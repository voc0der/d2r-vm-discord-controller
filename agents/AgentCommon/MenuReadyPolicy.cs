using System.Text.Json;

namespace AgentCommon;

public static class MenuReadyPolicy
{
    public static bool ShouldRunReadyFirstFromStatusJson(bool connected, string? statusJson)
    {
        if (!connected)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return true;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(statusJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return true;
        }

        if (!TryGetBoolean(root, "d2rRunning", out var d2rRunning))
        {
            return true;
        }

        if (!d2rRunning)
        {
            return true;
        }

        if (TryGetString(root, "d2rVisibleState", out var visibleState))
        {
            return visibleState switch
            {
                "CharacterScreen" or "OfflineCharacterScreen" or "LobbyOrGame" or "InGame" => false,
                "NotRunning" or "Unknown" or "DiabloSplash" => true,
                _ => true
            };
        }

        if (!TryGetString(root, "d2rActivityState", out var activityState))
        {
            return true;
        }

        return !string.Equals(activityState, "CharacterScreenIdle", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(activityState, "LobbyOrGame", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetBoolean(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (root.TryGetProperty(propertyName, out var property)
            && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False))
        {
            value = property.GetBoolean();
            return true;
        }

        return false;
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = "";
        if (root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }
}
