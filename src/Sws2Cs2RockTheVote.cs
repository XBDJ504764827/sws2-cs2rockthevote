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
    private readonly Dictionary<int, DateTimeOffset> _requestCooldowns = [];
    private readonly HashSet<int> _requests = [];
    private readonly HashSet<int> _voters = [];
    private readonly Dictionary<string, HashSet<int>> _votes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MapInfo> _voteMaps = new(StringComparer.OrdinalIgnoreCase);

    private ServiceProvider? _provider;
    private IDisposable? _settingsSubscription;
    private Guid _rtvCommand;
    private CancellationTokenSource? _voteTimer;
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
        if (_mapManager is not null)
        {
            _mapManager.MapChangeProgressed -= OnMapChangeProgressed;
        }

        _mapManager = null;
        _menus = null;
        _provider?.Dispose();
        _mapManager = null;
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
            Reply(player, "Rock the Vote is disabled.");
            return;
        }

        if (_menus is not { IsAvailable: true } || _mapManager is null)
        {
            Reply(player, "RTV services are unavailable. Ensure sws2-cs2menus and sws2-cs2mapmanager are loaded.");
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
                Reply(player, "A map vote is already in progress.");
                return;
            }

            nextVoteAllowed = _nextVoteAllowed;
            if (DateTimeOffset.UtcNow < nextVoteAllowed)
            {
                var seconds = Math.Ceiling((nextVoteAllowed - DateTimeOffset.UtcNow).TotalSeconds);
                Reply(player, $"The next RTV vote can start in {seconds:0} seconds.");
                return;
            }

            if (connectedPlayers < Math.Max(1, _settings.MinimumPlayers))
            {
                Reply(player, $"At least {_settings.MinimumPlayers} players are required to request a vote.");
                return;
            }

            if (_requestCooldowns.TryGetValue(player.PlayerID, out var cooldownUntil)
                && cooldownUntil > DateTimeOffset.UtcNow)
            {
                Reply(player, $"You can request RTV again in {Math.Ceiling((cooldownUntil - DateTimeOffset.UtcNow).TotalSeconds):0} seconds.");
                return;
            }

            _requestCooldowns[player.PlayerID] = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, _settings.RequestCooldownSeconds));
            _requests.Add(player.PlayerID);
            requiredRequests = RequiredRequests(connectedPlayers);

            if (_requests.Count >= requiredRequests)
            {
                StartVoteLocked();
                return;
            }
        }

        Reply(player, $"RTV request recorded ({GetRequestCount()}/{requiredRequests}).");
        Broadcast($"{player.Name} requested a map vote ({GetRequestCount()}/{requiredRequests}).");
    }

    private void StartVoteLocked()
    {
        var players = GetEligiblePlayers();
        var maps = _mapManager?.GetMaps()
            .OrderBy(_ => Random.Shared.Next())
            .Take(Math.Clamp(_settings.OptionCount, 2, 5))
            .ToArray() ?? [];

        if (players.Count < Math.Max(1, _settings.MinimumPlayers))
        {
            _requests.Clear();
            Broadcast("There are not enough players to start a map vote.");
            return;
        }

        if (maps.Length < 2 || _menus is not { IsAvailable: true })
        {
            _requests.Clear();
            Broadcast("A map vote could not start: at least two configured maps and the menu service are required.");
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

        Broadcast($"Map vote started. Use !rtv to open the ballot. Voting ends in {_settings.VoteDurationSeconds} seconds.");
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

        var current = Core.Translation.GetPlayerLocalizer(player);
        var builder = _menus.CreateMenu("Rock the Vote")
            .Id($"rtv.vote.{generation}.{player.PlayerID}")
            .Comment($"Vote for the next map ({_votes.Values.Sum(voters => voters.Count)} votes)")
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
        }
    }

    private void CastVote(IPlayer player, MapInfo map, int generation)
    {
        lock (_sync)
        {
            if (!_voteActive || generation != _voteGeneration || !_voteMaps.ContainsKey(map.Id))
            {
                Reply(player, "This map vote has ended.");
                return;
            }

            if (!_voters.Add(player.PlayerID))
            {
                Reply(player, "You have already voted in this map vote.");
                return;
            }

            _votes[map.Id].Add(player.PlayerID);
        }

        Reply(player, $"Your vote for {map.Name} has been recorded.");
    }

    private void FinishVote(int generation)
    {
        MapInfo? winner;
        int winningVotes;
        lock (_sync)
        {
            if (!_voteActive || generation != _voteGeneration)
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
            Broadcast("Map vote ended without votes. The current map will remain.");
            return;
        }

        Broadcast($"Map vote winner: {winner.Name} ({winningVotes} vote(s)). Changing map in {_settings.ChangeDelaySeconds} seconds.");
        Core.Scheduler.DelayBySeconds(Math.Clamp(_settings.ChangeDelaySeconds, 0, 30), () => RequestWinningMap(winner));
    }

    private void RequestWinningMap(MapInfo map)
    {
        if (_mapManager is null)
        {
            Broadcast("Map change failed: sws2-cs2mapmanager is not loaded.");
            return;
        }

        var result = _mapManager.RequestChange(map.Id);
        switch (result.Status)
        {
            case MapChangeRequestStatus.ChangeStarted:
            case MapChangeRequestStatus.DownloadStarted:
                break;
            case MapChangeRequestStatus.DownloadAlreadyPending:
                Broadcast($"The Workshop map {map.Name} is already downloading.");
                break;
            case MapChangeRequestStatus.Busy:
                Broadcast("Map change failed: another map change is already in progress.");
                break;
            default:
                Broadcast($"Map change failed: {result.Error ?? map.Name}.");
                break;
        }
    }

    private void OnMapChangeProgressed(object? sender, MapChangeProgressEventArgs args)
    {
        var message = args.Progress switch
        {
            MapChangeProgress.DownloadStarted => $"Downloading Workshop map {args.Map.Name}; the server will change when it is ready.",
            MapChangeProgress.ChangeIssued => $"Changing map to {args.Map.Name}.",
            MapChangeProgress.Failed => $"Map change failed: {args.Error ?? args.Map.Name}.",
            _ => null
        };

        if (message is not null)
        {
            Broadcast(message);
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
            _voteActive = false;
            _requests.Clear();
            _voters.Clear();
            _votes.Clear();
            _voteMaps.Clear();
            _voteGeneration++;
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
        var ratioThreshold = (int)Math.Ceiling(players * Math.Clamp(_settings.RequestRatio, 0.05, 1.0));
        return Math.Clamp(Math.Max(Math.Max(1, _settings.MinimumRequests), ratioThreshold), 1, players);
    }

    private int GetRequestCount()
    {
        lock (_sync)
        {
            return _requests.Count;
        }
    }

    private void Reply(IPlayer player, string message)
    {
        var localizer = Core.Translation.GetPlayerLocalizer(player);
        player.SendChat($"{localizer["rtv.prefix"]} {message}");
    }

    private void Broadcast(string message)
    {
        foreach (var player in Core.PlayerManager.GetAllValidPlayers().Where(player => !player.IsFakeClient))
        {
            Reply(player, message);
        }
    }
}
