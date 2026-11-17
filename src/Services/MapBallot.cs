using Sws2Cs2MapManager.Contracts;

namespace Sws2Cs2RockTheVote.Services;

/// <summary>A single electorate and catalog snapshot; callers serialize access on the game thread.</summary>
internal sealed class MapBallot(IEnumerable<int> players, IEnumerable<MapInfo> maps)
{
    private readonly HashSet<int> _players = players.ToHashSet();
    private readonly Dictionary<string, MapInfo> _maps = maps.ToDictionary(map => map.Id, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _votes = [];

    public Guid Id { get; } = Guid.NewGuid();
    public IReadOnlyCollection<MapInfo> Maps => _maps.Values;
    public int PlayerCount => _players.Count;
    public int VoteCount => _votes.Count;
    public bool IsComplete => VoteCount == PlayerCount;
    public bool CanVote(int playerId) => _players.Contains(playerId) && !_votes.ContainsKey(playerId);

    public bool TryVote(int playerId, string mapId)
    {
        if (!CanVote(playerId) || !_maps.ContainsKey(mapId)) return false;
        return _votes.TryAdd(playerId, mapId);
    }

    public void RemovePlayer(int playerId)
    {
        _players.Remove(playerId);
        _votes.Remove(playerId);
    }

    public MapInfo? SelectWinner(Random random)
    {
        if (_votes.Count == 0) return null;
        var counts = _votes.Values.GroupBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        var highest = counts.Max(group => group.Count());
        var leaders = counts.Where(group => group.Count() == highest).Select(group => _maps[group.Key]).ToArray();
        return leaders[random.Next(leaders.Length)];
    }
}
