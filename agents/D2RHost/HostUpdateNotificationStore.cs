using System.Text.Json;

namespace D2RHost;

public sealed class HostUpdateNotificationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly ILogger<HostUpdateNotificationStore> _logger;

    public HostUpdateNotificationStore(HostRuntimeOptions runtime, ILogger<HostUpdateNotificationStore> logger)
    {
        _path = GetPath(runtime.ConfigPath);
        _logger = logger;
    }

    public static string GetPath(string configPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        return Path.Combine(directory, "pending-update-notifications.jsonl");
    }

    public string[] ReadPendingMessages()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var messages = new List<string>();
        foreach (var line in File.ReadAllLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var marker = JsonSerializer.Deserialize<HostUpdateMarker>(line, JsonOptions);
                if (marker is not null)
                {
                    messages.Add(FormatMessage(marker));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not parse pending update notification marker: {Line}", line);
            }
        }

        return messages.ToArray();
    }

    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear pending update notification marker {Path}.", _path);
        }
    }

    private static string FormatMessage(HostUpdateMarker marker)
    {
        var appName = string.IsNullOrWhiteSpace(marker.AppName) ? "D2ROps" : marker.AppName;
        var versions = !string.IsNullOrWhiteSpace(marker.CurrentVersion)
            && !string.IsNullOrWhiteSpace(marker.LatestVersion)
                ? $" {marker.CurrentVersion} -> {marker.LatestVersion}"
                : "";
        var completed = marker.CompletedUtc is { } completedUtc
            ? $" at {completedUtc.ToLocalTime():G}"
            : "";
        var log = string.IsNullOrWhiteSpace(marker.LogPath)
            ? ""
            : $"\nLog: `{marker.LogPath}`";

        return $"{appName} update completed{versions}{completed}.{log}";
    }

    private sealed record HostUpdateMarker(
        string? AppName,
        string? CurrentVersion,
        string? LatestVersion,
        string? LogPath,
        DateTimeOffset? CompletedUtc);
}
