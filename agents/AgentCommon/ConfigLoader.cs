using System.Text.Json;

namespace AgentCommon;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static T Load<T>(string path) where T : AgentConfig
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Agent config was not found: {path}", path);
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Agent config was empty or invalid: {path}");

        if (string.IsNullOrWhiteSpace(config.AgentId))
        {
            throw new InvalidOperationException("agentId is required.");
        }

        if (string.IsNullOrWhiteSpace(config.ControllerUrl))
        {
            throw new InvalidOperationException("controllerUrl is required.");
        }

        if (string.IsNullOrWhiteSpace(config.SharedSecret))
        {
            throw new InvalidOperationException("sharedSecret is required.");
        }

        return config;
    }
}
