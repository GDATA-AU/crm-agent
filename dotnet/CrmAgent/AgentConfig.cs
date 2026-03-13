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

    /// <summary>
    /// Resolve a connection string from either a <c>connectionRef</c>
    /// (environment variable lookup) or a direct <c>connectionString</c> value.
    /// </summary>
    /// <remarks>
    /// Resolution order:
    /// <list type="number">
    ///   <item>If <paramref name="connectionRef"/> is provided, look up
    ///         <c>CONN_{REF}</c> in the environment (upper-cased, hyphens and
    ///         spaces replaced with underscores).</item>
    ///   <item>Fall back to <paramref name="connectionString"/>.</item>
    ///   <item>Throw if neither is available.</item>
    /// </list>
    /// </remarks>
    public static string ResolveConnectionString(string? connectionRef, string? connectionString)
    {
        if (!string.IsNullOrEmpty(connectionRef))
        {
            var envKey = "CONN_" + connectionRef.ToUpperInvariant().Replace('-', '_').Replace(' ', '_');
            var val = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrEmpty(val))
                return val;
            throw new InvalidOperationException(
                $"connectionRef \"{connectionRef}\" maps to env var \"{envKey}\" which is not set");
        }

        if (!string.IsNullOrEmpty(connectionString))
            return connectionString;

        throw new InvalidOperationException(
            "Job config must include either connectionRef or connectionString");
    }
}
