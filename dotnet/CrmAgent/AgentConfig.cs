namespace CrmAgent;

/// <summary>
/// Strongly-typed configuration loaded from environment variables / appsettings.
/// </summary>
public sealed class AgentConfig
{
    public const string SectionName = "Agent";

    public required string PortalUrl { get; init; }
    public required string AgentApiKey { get; init; }
    public required string AzureStorageConnectionString { get; init; }
    public int PollIntervalMs { get; init; } = 30_000;
    public int HeartbeatIntervalMs { get; init; } = 30_000;
    public string LogLevel { get; init; } = "Information";
}
