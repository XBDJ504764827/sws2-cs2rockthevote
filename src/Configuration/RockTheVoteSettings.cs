namespace Sws2Cs2RockTheVote.Configuration;

/// <summary>Rock the Vote configuration bound from config.jsonc.</summary>
public sealed class RockTheVoteSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Minimum connected human players required to request or start a vote.</summary>
    public int MinimumPlayers { get; set; } = 4;

    /// <summary>Minimum distinct player requests before an RTV vote starts.</summary>
    public int MinimumRequests { get; set; } = 3;

    /// <summary>Fraction of connected human players required to request an RTV vote.</summary>
    public double RequestRatio { get; set; } = 0.6;

    /// <summary>Number of random maps offered in a vote.</summary>
    public int OptionCount { get; set; } = 5;

    /// <summary>How long players have to vote.</summary>
    public int VoteDurationSeconds { get; set; } = 20;

    /// <summary>Per-player cooldown between RTV requests.</summary>
    public int RequestCooldownSeconds { get; set; } = 60;

    /// <summary>Cooldown after a vote finishes before another RTV vote can start.</summary>
    public int VoteCooldownSeconds { get; set; } = 180;

    /// <summary>Delay after a winning result before mapmanager is asked to change maps.</summary>
    public int ChangeDelaySeconds { get; set; } = 3;
}
