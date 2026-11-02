namespace Sws2Cs2RockTheVote.Configuration;

/// <summary>Rock the Vote configuration bound from config.jsonc.</summary>
public sealed class RockTheVoteSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after enough players agree before mapmanager is asked to change maps.</summary>
    public int ChangeDelaySeconds { get; set; } = 3;
}
