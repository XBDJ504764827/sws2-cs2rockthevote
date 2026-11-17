using Sws2Cs2MapManager.Contracts;
using Sws2Cs2RockTheVote.Services;

var maps = new[]
{
    new MapInfo("a", "Map A", MapKind.Local, "a"),
    new MapInfo("b", "Map B", MapKind.Local, "b"),
    new MapInfo("c", "Map C", MapKind.Local, "c"),
    new MapInfo("d", "Unselected map", MapKind.Local, "d")
};
var checks = 0;

var solo = new MapBallot([1], maps);
Check(!solo.IsComplete && solo.SelectWinner(new Random(1)) is null, "No automatic map before a choice");
Check(solo.TryVote(1, "b") && solo.IsComplete, "One player's choice completes immediately");
Check(solo.SelectWinner(new Random(1))?.Id == "b", "Solo selection wins regardless of catalog order");

var pair = new MapBallot([1, 2], maps);
Check(pair.TryVote(1, "a") && !pair.IsComplete, "Two players must both select before early completion");
Check(!pair.TryVote(1, "b") && pair.VoteCount == 1, "Duplicate selection cannot replace or add a vote");
Check(!pair.TryVote(3, "c") && !pair.TryVote(2, "unknown"), "Late arrivals and unknown maps cannot vote");
Check(pair.TryVote(2, "b") && pair.IsComplete, "Second selection completes pair");
CheckWinners(pair, ["a", "b"], "Pair tie selects only the two chosen maps and allows either to win");

var threeWay = new MapBallot([1, 2, 3], maps);
threeWay.TryVote(1, "a");
threeWay.TryVote(2, "b");
Check(!threeWay.IsComplete, "Three-player ballot includes player who did not request RTV");
threeWay.TryVote(3, "c");
Check(threeWay.IsComplete, "Three selections complete three-player ballot");
CheckWinners(threeWay, ["a", "b", "c"], "Three-way tie allows all and only chosen maps");

var majority = new MapBallot([1, 2, 3], maps);
majority.TryVote(1, "B");
majority.TryVote(2, "b");
majority.TryVote(3, "a");
CheckWinners(majority, ["b"], "Two votes beat one, including case-insensitive map IDs");

var partial = new MapBallot([1, 2, 3], maps);
Check(partial.SelectWinner(new Random(1)) is null, "No submitted ballots means no winner on timeout");
partial.TryVote(1, "c");
Check(!partial.IsComplete && partial.SelectWinner(new Random(1))?.Id == "c", "Timeout can resolve submitted votes without waiting for abstentions");
partial.RemovePlayer(2);
Check(!partial.IsComplete, "One remaining abstention still keeps ballot open");
partial.RemovePlayer(3);
Check(partial.IsComplete && partial.SelectWinner(new Random(1))?.Id == "c", "Disconnecting abstainers can complete ballot");
partial.RemovePlayer(1);
Check(partial.IsComplete && partial.VoteCount == 0 && partial.SelectWinner(new Random(1)) is null, "All disconnecting clears votes and leaves no winner");
Check(!partial.TryVote(1, "a"), "Disconnected player's reused slot cannot join old ballot");

var removal = new MapBallot([1, 2, 3], maps);
removal.TryVote(1, "a");
removal.TryVote(2, "a");
removal.TryVote(3, "b");
removal.RemovePlayer(1);
CheckWinners(removal, ["a", "b"], "Disconnect removes an already submitted vote from the tally");

var next = new MapBallot([1], maps);
Check(next.Id != solo.Id && next.VoteCount == 0 && next.CanVote(1), "Each session has a fresh ID and independent selections");

Console.WriteLine($"Passed {checks} map vote checks.");

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

void CheckWinners(MapBallot ballot, string[] expected, string message)
{
    // A fixed seed makes the check repeatable while exercising random tie resolution.
    var random = new Random(12345);
    var winners = Enumerable.Range(0, 200).Select(_ => ballot.SelectWinner(random)!.Id).ToHashSet();
    Check(winners.SetEquals(expected), message);
}
