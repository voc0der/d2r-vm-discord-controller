using System.Text.Json;

namespace D2RAgent;

internal sealed class MenuCommandArgs
{
    public int? CharacterSlot { get; set; }
    public int? FriendRow { get; set; }
    public int? PartyPosition { get; set; }
    public string? GameName { get; set; }
    public string? Password { get; set; }
    public string? Difficulty { get; set; }
    public string? Fingerprint { get; set; }
    public bool? Append { get; set; }
    public long? FollowAutoRunId { get; set; }

    public static MenuCommandArgs From(JsonElement element)
    {
        var args = new MenuCommandArgs();
        if (element.ValueKind != JsonValueKind.Object)
        {
            return args;
        }

        args.CharacterSlot = TryGetInt(element, "characterSlot");
        args.FriendRow = TryGetInt(element, "friendRow");
        args.PartyPosition = TryGetInt(element, "partyPosition");
        args.GameName = TryGetString(element, "gameName");
        args.Password = TryGetString(element, "password");
        args.Difficulty = TryGetString(element, "difficulty");
        args.Fingerprint = TryGetString(element, "fingerprint");
        args.Append = TryGetBool(element, "append");
        args.FollowAutoRunId = TryGetLong(element, "followAutoRunId");
        return args;
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return value;
        }

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value))
        {
            return value;
        }

        return null;
    }

    private static long? TryGetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value))
        {
            return value;
        }

        if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out value))
        {
            return value;
        }

        return null;
    }
}
