using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using Sws2Cs2MapManager.Contracts;
using Sws2Cs2Menus.Contracts;
using Sws2Cs2RockTheVote.Configuration;
using Sws2Cs2RockTheVote.Services;

namespace Sws2Cs2RockTheVote;

public partial class Sws2Cs2RockTheVote : BasePlugin
{
    private readonly object _sync = new();
    private readonly HashSet<int> _rtvVotes = [];
    private RockTheVoteSettings _settings = new();

    private ServiceProvider? _provider;
    private IDisposable? _settingsSubscription;
    private Guid _rtvCommand;
    private Guid _ydCommand;
    private CancellationTokenSource? _metadataShutdown;
    private WorkshopMapMetadataProvider? _metadataProvider;
    private IMenuService? _menus;
    private IMapManagerService? _mapManager;
    private MapInfo? _reservedMap;
    private MapInfo? _rtvTarget;
    private bool _changePending;
    private bool _loaded;

    public Sws2Cs2RockTheVote(ISwiftlyCore core) : base(core)
    {
    }

    public override void Load(bool hotReload)
    {
        Core.Configuration
            .InitializeWithTemplate("config.jsonc", "config.template.jsonc")
            .Configure(builder => builder.AddJsonFile("config.jsonc", optional: false, reloadOnChange: true));

        var services = new ServiceCollection();
        services.AddSwiftly(Core)
            .AddOptionsWithValidateOnStart<RockTheVoteSettings>()
            .BindConfiguration("sws2-cs2rockthevote");

        _provider = services.BuildServiceProvider();
        var options = _provider.GetRequiredService<IOptionsMonitor<RockTheVoteSettings>>();
        _settings = options.CurrentValue;
        _settingsSubscription = options.OnChange(settings => Core.Scheduler.NextTick(() => ApplySettings(settings)));

        _metadataShutdown = new CancellationTokenSource();
        _metadataProvider = new WorkshopMapMetadataProvider(Core.Logger, _settings.WorkshopMetadata);
        _rtvCommand = Core.Command.RegisterCommand(
            "rtv",
            OnRtvCommand,
            registerRaw: true,
            permission: "",
            helpText: "Agree to change to the reserved or next map");
        _ydCommand = Core.Command.RegisterCommand(
            "yd",
            OnYdCommand,
            registerRaw: true,
            permission: "",
            helpText: "Reserve the next map");

        Core.Event.OnClientDisconnected += OnClientDisconnected;
        Core.Event.OnMapUnload += OnMapUnload;
        _loaded = true;
        Core.Logger.LogInformation("[sws2-cs2rockthevote] Plugin loaded (hotReload: {HotReload})", hotReload);
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        var nextMapManager = interfaceManager.TryGetSharedInterface<IMapManagerService>(IMapManagerService.InterfaceKey, out var maps)
            ? maps
            : null;
        if (!ReferenceEquals(_mapManager, nextMapManager))
        {
            CancelOwnedChange();
            ResetRtvVotes();
        }
        if (_mapManager is not null)
        {
            _mapManager.MapChangeProgressed -= OnMapChangeProgressed;
            _mapManager.MapChangeControlChanged -= OnMapChangeControlChanged;
        }

        _menus = interfaceManager.TryGetSharedInterface<IMenuService>(MenuServiceConstants.InterfaceKey, out var menus)
            ? menus
            : null;
        _mapManager = nextMapManager;

        if (_mapManager is not null)
        {
            _mapManager.MapChangeProgressed -= OnMapChangeProgressed;
            _mapManager.MapChangeControlChanged -= OnMapChangeControlChanged;
            _mapManager.MapChangeProgressed += OnMapChangeProgressed;
            _mapManager.MapChangeControlChanged += OnMapChangeControlChanged;
            _ = WarmWorkshopMetadataAsync(_mapManager.GetMaps());
        }
    }

    public override void Unload()
    {
        _loaded = false;
        Core.Command.UnregisterCommand(_rtvCommand);
        Core.Command.UnregisterCommand(_ydCommand);
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        Core.Event.OnMapUnload -= OnMapUnload;
        _settingsSubscription?.Dispose();
        _settingsSubscription = null;
        _metadataShutdown?.Cancel();
        _metadataShutdown?.Dispose();
        _metadataShutdown = null;
        _metadataProvider?.Dispose();
        _metadataProvider = null;
        if (_mapManager is not null)
        {
            CancelOwnedChange();
            _mapManager.MapChangeProgressed -= OnMapChangeProgressed;
            _mapManager.MapChangeControlChanged -= OnMapChangeControlChanged;
        }

        _mapManager = null;
        _menus = null;
        _provider?.Dispose();
        _provider = null;
    }

    private void ApplySettings(RockTheVoteSettings settings)
    {
        if (!_loaded) return;
        _settings = settings;
        if (!settings.Enabled)
        {
            CancelOwnedChange();
            ResetRtvVotes();
        }
        _metadataProvider?.ApplySettings(settings.WorkshopMetadata);
        Core.Logger.LogInformation("[sws2-cs2rockthevote] Configuration reloaded");
    }

    private async Task WarmWorkshopMetadataAsync(IReadOnlyList<MapInfo> maps)
    {
        try
        {
            if (_metadataProvider is not null && _metadataShutdown is not null)
            {
                await _metadataProvider.GetAsync(maps, _metadataShutdown.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Core.Logger.LogWarning(exception, "[sws2-cs2rockthevote] Failed to warm Workshop map metadata");
        }
    }

    private void OnYdCommand(ICommandContext context)
    {
        if (!TryGetPlayer(context, out var player))
        {
            return;
        }

        if (!_settings.Enabled)
        {
            Reply(player, "rtv.disabled");
            return;
        }

        if (_mapManager is null)
        {
            Reply(player, "rtv.unavailable");
            return;
        }

        var maps = _mapManager.GetMaps();
        if (maps.Count == 0)
        {
            Reply(player, "rtv.no_maps");
            return;
        }

        if (context.Args.Length > 0)
        {
            var requestedName = string.Join(' ', context.Args).Trim();
            var requestedMap = FindRequestedMap(maps, requestedName);
            if (requestedMap is null)
            {
                Reply(player, "rtv.map_not_found", requestedName);
                return;
            }

            ReserveMap(player, requestedMap);
            return;
        }

        if (_menus is not { IsAvailable: true })
        {
            Reply(player, "rtv.menu_unavailable");
            return;
        }

        // Steam details are optional. Never delay opening the menu for a network request.
        var configuredMaps = maps.ToArray();
        var metadata = _metadataProvider?.GetCached(configuredMaps)
            ?? new Dictionary<string, WorkshopMapMetadata>(StringComparer.Ordinal);
        OpenReservationMenu(player, configuredMaps, metadata);
        _ = WarmWorkshopMetadataAsync(configuredMaps);
    }

    private void OpenReservationMenu(
        IPlayer player,
        IReadOnlyList<MapInfo> maps,
        IReadOnlyDictionary<string, WorkshopMapMetadata> metadata)
    {
        var menus = _menus;
        if (menus is not { IsAvailable: true })
        {
            Reply(player, "rtv.menu_unavailable");
            return;
        }

        var localizer = Core.Translation.GetPlayerLocalizer(player);
        var builder = menus.CreateMenu(localizer["rtv.reserve.title"])
            .Id($"rtv.reserve.{player.PlayerID}.{Guid.NewGuid():N}")
            .Layout(MenuLayout.MapPicker)
            .Comment(localizer["rtv.reserve.help"])
            .PageSize(4)
            .ShowBack(false)
            .ShowClose()
            .CloseOnSelect();

        foreach (var map in maps)
        {
            var selectedMap = map;
            metadata.TryGetValue(map.Value, out var details);
            var author = string.IsNullOrWhiteSpace(details?.Author)
                ? localizer["rtv.map_author_unknown"]
                : details.Author;
            builder.AddOption(option => option
                .Id($"map-{selectedMap.Id}")
                .Text(selectedMap.Name)
                .Comment(localizer["rtv.map_details", author, details?.Title ?? selectedMap.Name])
                .Image(details?.ImageUrl)
                .Tag(new MapMenuOptionAppearance(selectedMap.CkzNubTier, selectedMap.CkzProTier,
                    selectedMap.VnlNubTier, selectedMap.VnlProTier, selectedMap.Id))
                .OnSelected(context =>
                {
                    ReserveMap(context.Player, selectedMap);
                }));
        }

        var result = menus.Open(player, builder.Build());
        if (result != MenuOpenResult.Opened)
        {
            Core.Logger.LogDebug("[sws2-cs2rockthevote] Could not open map menu for player {PlayerId}: {Result}", player.PlayerID, result);
            Reply(player, "rtv.menu_open_failed");
        }
    }

    private bool ReserveMap(IPlayer player, MapInfo requestedMap)
    {
        if (!_settings.Enabled)
        {
            Reply(player, "rtv.disabled");
            return false;
        }

        var map = _mapManager?.GetMaps().FirstOrDefault(candidate =>
            candidate.Id.Equals(requestedMap.Id, StringComparison.OrdinalIgnoreCase));
        if (map is null)
        {
            Reply(player, "rtv.map_not_found", requestedMap.Name);
            return false;
        }

        lock (_sync)
        {
            if (_changePending)
            {
                Reply(player, "rtv.change_underway");
                return false;
            }

            _reservedMap = map;
            _rtvTarget = map;
            _rtvVotes.Clear();
        }

        Broadcast("rtv.reserved", player.Name, map.Name);
        return true;
    }

    private void OnRtvCommand(ICommandContext context)
    {
        if (!TryGetPlayer(context, out var player))
        {
            return;
        }

        if (!_settings.Enabled)
        {
            Reply(player, "rtv.disabled");
            return;
        }

        var mapManager = _mapManager;
        if (mapManager is null)
        {
            Reply(player, "rtv.unavailable");
            return;
        }

        var changeState = mapManager.GetChangeState();
        if (changeState.IsPaused)
        {
            Reply(player, "rtv.map_changes_paused");
            return;
        }

        if (changeState.PendingMap is not null)
        {
            Reply(player, "rtv.change_underway");
            return;
        }

        var maps = mapManager.GetMaps();
        if (maps.Count == 0)
        {
            Reply(player, "rtv.no_maps");
            return;
        }

        var playerCount = GetEligiblePlayers().Count;
        if (playerCount == 0)
        {
            return;
        }

        var nextMap = FindNextMap(maps);
        int votes;
        int requiredVotes;
        MapInfo? target;
        bool shouldChange = false;
        lock (_sync)
        {
            if (_changePending)
            {
                Reply(player, "rtv.change_underway");
                return;
            }

            target = _reservedMap ?? _rtvTarget ?? nextMap;
            if (target is null)
            {
                Reply(player, "rtv.no_next_map");
                return;
            }

            var configuredTarget = maps.FirstOrDefault(map =>
                map.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
            if (configuredTarget is null)
            {
                _reservedMap = null;
                _rtvTarget = nextMap;
                target = nextMap;
                _rtvVotes.Clear();
            }
            else
            {
                target = configuredTarget;
            }

            if (target is null)
            {
                Reply(player, "rtv.no_next_map");
                return;
            }

            _rtvTarget = target;
            if (!_rtvVotes.Add(player.PlayerID))
            {
                Reply(player, "rtv.already_voted");
                return;
            }

            votes = _rtvVotes.Count;
            requiredVotes = RequiredRtvVotes(playerCount);
            if (votes >= requiredVotes)
            {
                _changePending = true;
                shouldChange = true;
            }
        }

        Broadcast("rtv.approval", player.Name, target.Name, votes, requiredVotes);
        if (!shouldChange)
        {
            return;
        }

        var delay = Math.Clamp(_settings.ChangeDelaySeconds, 5, 30);
        var result = mapManager.ScheduleChange(target.Id, delay, "rtv");
        if (result.Status is not (MapChangeRequestStatus.Scheduled or MapChangeRequestStatus.ChangeStarted
            or MapChangeRequestStatus.DownloadStarted or MapChangeRequestStatus.DownloadAlreadyPending))
        {
            if (result.Status == MapChangeRequestStatus.Paused) Broadcast("rtv.map_changes_paused");
            else if (result.Status == MapChangeRequestStatus.Busy) Broadcast("rtv.map_change_busy");
            else if (result.Status == MapChangeRequestStatus.MapNotFound) Broadcast("rtv.map_not_found", target.Name);
            // Other failures are reported through MapChangeProgressed.
            ResetRtvVotes();
        }
        else
        {
            Broadcast("rtv.quorum", target.Name, delay);
        }
    }

    private void CancelOwnedChange()
    {
        if (_mapManager?.GetChangeState().Source == "rtv") _mapManager.CancelPendingChange();
    }

    private void ResetRtvVotes()
    {
        lock (_sync)
        {
            _changePending = false;
            _rtvVotes.Clear();
            _rtvTarget = _reservedMap;
        }
    }

    private void OnMapChangeControlChanged(object? sender, EventArgs args) => ResetRtvVotes();

    private void OnMapChangeProgressed(object? sender, MapChangeProgressEventArgs args)
    {
        (string? Key, string? Value) message = args.Progress switch
        {
            MapChangeProgress.DownloadStarted => ("rtv.download_started", args.Map.Name),
            MapChangeProgress.ChangeIssued => ("rtv.change_started", args.Map.Name),
            MapChangeProgress.Failed => ("rtv.map_change_failed", args.Error ?? args.Map.Name),
            _ => (null, null)
        };

        if (args.Progress is MapChangeProgress.Failed or MapChangeProgress.Cancelled)
        {
            lock (_sync)
            {
                if (_changePending)
                {
                    _changePending = false;
                    _rtvVotes.Clear();
                    _rtvTarget = _reservedMap;
                }
            }
        }

        if (message.Key is not null)
        {
            Broadcast(message.Key, message.Value ?? string.Empty);
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        lock (_sync)
        {
            _rtvVotes.Remove(@event.PlayerId);
        }
    }

    private void OnMapUnload(IOnMapUnloadEvent @event)
    {
        lock (_sync)
        {
            _changePending = false;
            _reservedMap = null;
            _rtvTarget = null;
            _rtvVotes.Clear();
        }
    }

    private List<IPlayer> GetEligiblePlayers()
    {
        return Core.PlayerManager.GetAllValidPlayers()
            .Where(player => !player.IsFakeClient)
            .ToList();
    }

    private int RequiredRtvVotes(int players)
    {
        return players switch
        {
            <= 1 => 1,
            2 => 2,
            3 => 2,
            _ => (int)Math.Ceiling(players * 0.8)
        };
    }

    private MapInfo? FindNextMap(IReadOnlyList<MapInfo> maps)
    {
        if (maps.Count == 0)
        {
            return null;
        }

        var currentMapName = Core.Engine.GlobalVars.MapName.ToString();
        var currentWorkshopId = Core.Engine.WorkshopId;
        var currentIndex = -1;
        for (var i = 0; i < maps.Count; i++)
        {
            if (IsCurrentMap(maps[i], currentMapName, currentWorkshopId))
            {
                currentIndex = i;
                break;
            }
        }

        for (var offset = 1; offset <= maps.Count; offset++)
        {
            var index = currentIndex >= 0
                ? (currentIndex + offset) % maps.Count
                : (offset - 1) % maps.Count;
            var candidate = maps[index];
            if (!IsCurrentMap(candidate, currentMapName, currentWorkshopId))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsCurrentMap(MapInfo map, string currentMapName, string currentWorkshopId)
    {
        return map.Kind == MapKind.Workshop
            ? map.Value.Equals(currentWorkshopId, StringComparison.OrdinalIgnoreCase)
            : map.Value.Equals(currentMapName, StringComparison.OrdinalIgnoreCase);
    }

    private static MapInfo? FindRequestedMap(IReadOnlyList<MapInfo> maps, string requestedName)
    {
        return maps.FirstOrDefault(map =>
            map.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase)
            || map.Id.Equals(requestedName, StringComparison.OrdinalIgnoreCase)
            || map.Value.Equals(requestedName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetPlayer(ICommandContext context, out IPlayer player)
    {
        if (context.IsSentByPlayer && context.Sender is { IsValid: true, IsFakeClient: false } sender)
        {
            player = sender;
            return true;
        }

        player = null!;
        context.Reply("This command can only be used by a player.");
        return false;
    }

    private void Reply(IPlayer player, string key, params object[] args)
    {
        var localizer = Core.Translation.GetPlayerLocalizer(player);
        var message = args.Length == 0 ? localizer[key] : localizer[key, args];
        player.SendChat($"{localizer["rtv.prefix"]} {message}");
    }

    private void Broadcast(string key, params object[] args)
    {
        foreach (var player in GetEligiblePlayers())
        {
            Reply(player, key, args);
        }
    }
}
