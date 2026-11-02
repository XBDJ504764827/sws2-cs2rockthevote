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

namespace Sws2Cs2RockTheVote;

public partial class Sws2Cs2RockTheVote : BasePlugin
{
    private readonly object _sync = new();
    private RockTheVoteSettings _settings = new();
    private readonly Dictionary<ulong, DateTimeOffset> _requestCooldowns = [];
    private readonly HashSet<int> _requests = [];
    private readonly HashSet<int> _voters = [];
    private readonly Dictionary<string, HashSet<int>> _votes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MapInfo> _voteMaps = new(StringComparer.OrdinalIgnoreCase);

    private ServiceProvider? _provider;
    private IDisposable? _settingsSubscription;
    private Guid _rtvCommand;
    private CancellationTokenSource? _voteTimer;
    private CancellationTokenSource? _changeTimer;
    private IMenuService? _menus;
    private IMapManagerService? _mapManager;
    private DateTimeOffset _nextVoteAllowed = DateTimeOffset.MinValue;
    private bool _voteActive;
    private int _voteGeneration;
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

        _rtvCommand = Core.Command.RegisterCommand("rtv", OnRtvCommand, registerRaw: true, permission: "", helpText: "Request a map vote");
        Core.Event.OnClientDisconnected += OnClientDisconnected;
        Core.Event.OnMapUnload += OnMapUnload;
        _loaded = true;
        Core.Logger.LogInformation("[sws2-cs2rockthevote] Plugin loaded (hotReload: {HotReload})", hotReload);
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        if (_mapManager is not null)
        {
            _mapManager.MapChangeProgressed -= OnMapChangeProgressed;
        }

        _menus = interfaceManager.TryGetSharedInterface<IMenuService>(MenuServiceConstants.InterfaceKey, out var menus)
            ? menus
            : null;
        _mapManager = interfaceManager.TryGetSharedInterface<IMapManagerService>(IMapManagerService.InterfaceKey, out var maps)
            ? maps
            : null;

        if (_mapManager is not null)
        {
            _mapManager.MapChangeProgressed -= OnMapChangeProgressed;
            _mapManager.MapChangeProgressed += OnMapChangeProgressed;
        }
    }

    public override void Unload()
    {
        _loaded = false;
        Core.Command.UnregisterCommand(_rtvCommand);
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        Core.Event.OnMapUnload -= OnMapUnload;
        _settingsSubscription?.Dispose();
        _settingsSubscription = null;
        _voteTimer?.Cancel();
        _voteTimer = null;
        _changeTimer?.Cancel();
        _changeTimer = null;
        if (_mapManager is not null)
        {
            _mapManager.MapChangeProgressed -= OnMapChangeProgressed;
        }

        _mapManager = null;
        _menus = null;
        _provider?.Dispose();
        _provider = null;
    }

    private void ApplySettings(RockTheVoteSettings settings)
    {
        _settings = settings;
        Core.Logger.LogInformation("[sws2-cs2rockthevote] Configuration reloaded");
    }

    private void OnRtvCommand(ICommandContext context)
    {
        if (!context.IsSentByPlayer || context.Sender is not { IsValid: true } player || player.IsFakeClient)
        {
            context.Reply("This command can only be used by a player.");
            return;
        }

        if (!_settings.Enabled)
        {
            Reply(player, "rtv.disabled");
            return;
        }

        if (_menus is not { IsAvailable: true } || _mapManager is null)
        {
            Reply(player, "rtv.unavailable");
            return;
        }

        int connectedPlayers;
        int requiredRequests;
        DateTimeOffset nextVoteAllowed;
        lock (_sync)
        {
            connectedPlayers = GetEligiblePlayers().Count;
            if (_voteActive)
            {
                Reply(player, "rtv.vote_active");
                return;
            }

            nextVoteAllowed = _nextVoteAllowed;
            if (DateTimeOffset.UtcNow < nextVoteAllowed)
            {
                var seconds = Math.Ceiling((nextVoteAllowed - DateTimeOffset.UtcNow).TotalSeconds);
                Reply(player, "rtv.cooldown", seconds);
                return;
            }

            if (connectedPlayers < Math.Max(1, _settings.MinimumPlayers))
            {
                Reply(player, "rtv.minimum_players", _settings.MinimumPlayers);
                return;
            }

            if (_requestCooldowns.TryGetValue(player.SteamID, out var cooldownUntil)
                && cooldownUntil > DateTimeOffset.UtcNow)
            {
                Reply(player, "rtv.request_cooldown", Math.Ceiling((cooldownUntil - DateTimeOffset.UtcNow).TotalSeconds));
                return;
            }

            _requestCooldowns[player.SteamID] = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, _settings.RequestCooldownSeconds));
            _requests.Add(player.PlayerID);
            requiredRequests = RequiredRequests(connectedPlayers);

            if (_requests.Count >= requiredRequests)
            {
                StartVoteLocked();
                return;
            }
        }

        Reply(player, "rtv.request_recorded", GetRequestCount(), requiredRequests);
        Broadcast("rtv.requested", player.Name, GetRequestCount(), requiredRequests);
    }

    private void StartVoteLocked()
    {
        var players = GetEligiblePlayers();
        var currentMapName = Core.Engine.GlobalVars.MapName.ToString();
        var currentWorkshopId = Core.Engine.WorkshopId;
        var maps = _mapManager?.GetMaps()
            .Where(map => map.Kind == MapKind.Workshop
                ? !map.Value.Equals(currentWorkshopId, StringComparison.OrdinalIgnoreCase)
                : !map.Value.Equals(currentMapName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(_ => Random.Shared.Next())
            .Take(Math.Clamp(_settings.OptionCount, 2, 5))
            .ToArray() ?? [];

        if (players.Count < Math.Max(1, _settings.MinimumPlayers))
        {
            _requests.Clear();
            Broadcast("rtv.minimum_players_start");
            return;
        }

        if (maps.Length < 2 || _menus is not { IsAvailable: true })
        {
            _requests.Clear();
            Broadcast("rtv.no_maps");
            return;
        }

        _voteActive = true;
        _requests.Clear();
        _voters.Clear();
        _votes.Clear();
        _voteMaps.Clear();
        _voteGeneration++;
        var generation = _voteGeneration;
        _nextVoteAllowed = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, _settings.VoteCooldownSeconds));

        foreach (var map in maps)
        {
            _voteMaps[map.Id] = map;
            _votes[map.Id] = [];
        }

        Broadcast("rtv.started", Math.Clamp(_settings.VoteDurationSeconds, 5, 120));
        _voteTimer?.Cancel();
        _voteTimer = Core.Scheduler.DelayBySeconds(Math.Clamp(_settings.VoteDurationSeconds, 5, 120), () => FinishVote(generation));
        foreach (var player in players)
        {
            OpenBallot(player, generation);
        }
    }

    private void OpenBallot(IPlayer player, int generation)
    {
        if (_menus is not { IsAvailable: true })
        {
            return;
        }

        var localizer = Core.Translation.GetPlayerLocalizer(player);
        var builder = _menus.CreateMenu(localizer["rtv.vote.title"])
            .Id($"rtv.vote.{generation}.{player.PlayerID}")
                        .Comment(localizer["rtv.vote", GetTotalVotes()])
            .PageSize(5)
            .ShowClose()
            .CloseOnSelect();

        foreach (var map in _voteMaps.Values)
        {
            var mapInfo = map;
            builder.AddOption(option => option
                .Id($"map-{mapInfo.Id}")
                .Text(mapInfo.Name)
                .Comment(mapInfo.Kind == MapKind.Workshop ? $"Workshop ID: {mapInfo.Value}" : mapInfo.Value)
                .OnSelected(context => CastVote(context.Player, mapInfo, generation)));
        }

        var result = _menus.Open(player, builder.Build());
        if (result != MenuOpenResult.Opened)
        {
            Core.Logger.LogDebug("[sws2-cs2rockthevote] Could not open ballot for player {PlayerId}: {Result}", player.PlayerID, result);
            Reply(player, "rtv.vote_open_failed");
        }
    }

    private void CastVote(IPlayer player, MapInfo map, int generation)
    {
        lock (_sync)
        {
            if (!_voteActive || generation != _voteGeneration || !_voteMaps.ContainsKey(map.Id))
            {
                Reply(player, "rtv.vote_ended");
                return;
            }

            if (!_voters.Add(player.PlayerID))
            {
                Reply(player, "rtv.already_voted");
                return;
            }

            _votes[map.Id].Add(player.PlayerID);
        }

        Reply(player, "rtv.vote_recorded", map.Name);
    }

    private void FinishVote(int generation)
    {
        MapInfo? winner;
        int winningVotes;
        lock (_sync)
        {
            if (!_loaded || !_voteActive || generation != _voteGeneration)
            {
                return;
            }

            _voteActive = false;
            _voteTimer = null;
            var rankedMaps = _voteMaps.Values
                .Select(map => new { Map = map, Votes = _votes[map.Id].Count })
                .ToArray();
            winningVotes = rankedMaps.Max(entry => entry.Votes);
            winner = rankedMaps
                .Where(entry => entry.Votes == winningVotes)
                .OrderBy(_ => Random.Shared.Next())
                .FirstOrDefault()?.Map;
            _voters.Clear();
            _votes.Clear();
            _voteMaps.Clear();
        }

        if (winner is null || winningVotes == 0)
        {
            Broadcast("rtv.no_votes");
            return;
        }

        Broadcast("rtv.winner", winner.Name, winningVotes, _settings.ChangeDelaySeconds);
        _changeTimer = Core.Scheduler.DelayBySeconds(Math.Clamp(_settings.ChangeDelaySeconds, 0, 30), () =>
        {
            _changeTimer = null;
            if (_loaded)
            {
                RequestWinningMap(winner);
            }
        });
    }

    private void RequestWinningMap(MapInfo map)
    {
        if (!_loaded)
        {
            return;
        }

        if (_mapManager is null)
        {
            Broadcast("rtv.map_manager_unavailable");
            return;
        }

        var result = _mapManager.RequestChange(map.Id);
        switch (result.Status)
        {
            case MapChangeRequestStatus.ChangeStarted:
            case MapChangeRequestStatus.DownloadStarted:
                break;
            case MapChangeRequestStatus.DownloadAlreadyPending:
                Broadcast("rtv.download_pending", map.Name);
                break;
            case MapChangeRequestStatus.Busy:
                Broadcast("rtv.map_change_busy");
                break;
            default:
                Broadcast("rtv.map_change_failed", result.Error ?? map.Name);
                break;
        }
    }

    private void OnMapChangeProgressed(object? sender, MapChangeProgressEventArgs args)
    {
        (string? Key, string? Value) message = args.Progress switch
        {
            MapChangeProgress.DownloadStarted => ("rtv.download_started", args.Map.Name),
            MapChangeProgress.ChangeIssued => ("rtv.change_started", args.Map.Name),
            MapChangeProgress.Failed => ("rtv.map_change_failed", args.Error ?? args.Map.Name),
            _ => (null, null)
        };

        if (message.Key is not null)
        {
            Broadcast(message.Key, message.Value ?? string.Empty);
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        lock (_sync)
        {
            _requests.Remove(@event.PlayerId);
            if (_voters.Remove(@event.PlayerId))
            {
                foreach (var votes in _votes.Values)
                {
                    votes.Remove(@event.PlayerId);
                }
            }
        }
    }

    private void OnMapUnload(IOnMapUnloadEvent @event)
    {
        lock (_sync)
        {
            _voteTimer?.Cancel();
            _voteTimer = null;
            _changeTimer?.Cancel();
            _changeTimer = null;
            _voteActive = false;
            _requests.Clear();
            _voters.Clear();
            _voteMaps.Clear();
            _votes.Clear();
            _voteGeneration++;
            _nextVoteAllowed = DateTimeOffset.MinValue;
            _requestCooldowns.Clear();
        }
    }

    private List<IPlayer> GetEligiblePlayers()
    {
        return Core.PlayerManager.GetAllValidPlayers()
            .Where(player => !player.IsFakeClient && player.SteamID != 0)
            .ToList();
    }

    private int RequiredRequests(int players)
    {
        var ratio = double.IsFinite(_settings.RequestRatio)
            ? Math.Clamp(_settings.RequestRatio, 0.05, 1.0)
            : 0.6;
        var ratioThreshold = (int)Math.Ceiling(players * ratio);
        return Math.Clamp(Math.Max(Math.Max(1, _settings.MinimumRequests), ratioThreshold), 1, players);
    }

    private int GetRequestCount()
    {
        lock (_sync)
        {
            return _requests.Count;
        }
    }

    private int GetTotalVotes()
    {
        lock (_sync)
        {
            return _votes.Values.Sum(voters => voters.Count);
        }
    }

    private void Reply(IPlayer player, string key, params object[] args)
    {
        var localizer = Core.Translation.GetPlayerLocalizer(player);
        var message = args.Length == 0 ? localizer[key] : localizer[key, args];
        player.SendChat($"{localizer["rtv.prefix"]} {message}");
    }

    private void Broadcast(string key, params object[] args)
    {
        foreach (var player in Core.PlayerManager.GetAllValidPlayers().Where(player => !player.IsFakeClient))
        {
            Reply(player, key, args);
        }
    }
}
