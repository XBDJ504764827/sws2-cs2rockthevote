namespace Sws2Cs2RockTheVote.Configuration;

/// <summary>Rock the Vote configuration bound from config.jsonc.</summary>
public sealed class RockTheVoteSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after enough players agree before mapmanager is asked to change maps.</summary>
    public int ChangeDelaySeconds { get; set; } = 3;

    /// <summary>Optional Steam title, author and preview lookups. Never required for voting.</summary>
    public WorkshopMetadataSettings WorkshopMetadata { get; set; } = new();
}

public sealed class WorkshopMetadataSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Timeout for each Steam HTTP request, clamped to 1-120 seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
