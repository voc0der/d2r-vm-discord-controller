using System.Text.Json;

namespace AgentCommon;

public sealed record CommandRequest(string CommandId, string Command, JsonElement Args);

public sealed record CommandResult(bool Ok, string Message, object? Data = null, bool ExitAfterResult = false)
{
    public static CommandResult Success(string message, object? data = null, bool exitAfterResult = false) =>
        new(true, message, data, exitAfterResult);

    public static CommandResult Failure(string message, object? data = null) => new(false, message, data);
}

internal sealed record ControllerCommandEnvelope(
    string Type,
    string CommandId,
    string Command,
    JsonElement Args);
