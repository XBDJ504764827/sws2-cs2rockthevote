namespace Sws2Cs2RockTheVote.Configuration;

/// <summary>Rock the Vote configuration bound from config.jsonc.</summary>
public sealed class RockTheVoteSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Reserved-map countdown after quorum; clamped to 5–30 seconds.</summary>
    public int ChangeDelaySeconds { get; set; } = 5;

    /// <summary>Maximum time to select maps when none is reserved; clamped to 5–120 seconds.</summary>
    public int MapVoteDurationSeconds { get; set; } = 30;

    /// <summary>Optional Steam title, author and preview lookups. Never required for voting.</summary>
    public WorkshopMetadataSettings WorkshopMetadata { get; set; } = new();
}

public sealed class WorkshopMetadataSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Timeout for each Steam HTTP request, clamped to 1-120 seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
