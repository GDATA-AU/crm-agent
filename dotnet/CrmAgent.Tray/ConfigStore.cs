using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrmAgent.Tray;

/// <summary>
/// Reads and writes the agent configuration stored in
/// %ProgramData%\LGA CRM Agent\appsettings.json.
/// The Worker Service is configured to layer this file on top of
/// its own appsettings.json so credentials are kept out of Program Files.
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LGA CRM Agent");

    public static readonly string ConfigPath = Path.Combine(ConfigDirectory, "appsettings.json");

    public sealed record AgentSettings(
        string PortalUrl,
        string AgentApiKey,
        string AzureStorageConnectionString);

    public static bool IsConfigured()
    {
        var s = Load();
        return s is not null
            && !string.IsNullOrWhiteSpace(s.PortalUrl)
            && !string.IsNullOrWhiteSpace(s.AgentApiKey)
            && !string.IsNullOrWhiteSpace(s.AzureStorageConnectionString);
    }

    public static AgentSettings? Load()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            using var stream = File.OpenRead(ConfigPath);
            var root = JsonNode.Parse(stream);
            var agent = root?["Agent"];
            if (agent is null) return null;
            return new AgentSettings(
                agent["PortalUrl"]?.GetValue<string>() ?? "",
                agent["AgentApiKey"]?.GetValue<string>() ?? "",
                agent["AzureStorageConnectionString"]?.GetValue<string>() ?? "");
        }
        catch { return null; }
    }

    public static void Save(AgentSettings settings)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var root = new JsonObject
        {
            ["Agent"] = new JsonObject
            {
                ["PortalUrl"] = settings.PortalUrl,
                ["AgentApiKey"] = settings.AgentApiKey,
                ["AzureStorageConnectionString"] = settings.AzureStorageConnectionString,
                ["PollIntervalMs"] = 30000,
                ["HeartbeatIntervalMs"] = 30000
            }
        };
        File.WriteAllText(ConfigPath, root.ToJsonString(WriteOptions));
    }
}
