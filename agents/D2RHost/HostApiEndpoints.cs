using System.Text.Json;
using AgentCommon;

namespace D2RHost;

/// <summary>
/// The authenticated HTTP command surface: everything <c>/d2r</c> can do, reachable from anything
/// on the trusted network that holds the key.
/// </summary>
/// <remarks>
/// Mapped on a master only. A worker relays commands for its own VMs but has no view of the fleet,
/// so an API there could answer for part of the estate while looking like it answered for all of
/// it - worse than not being there. On a worker these routes simply do not exist.
///
/// The pre-existing unauthenticated reads (<c>/healthz</c>, <c>/agents</c>, <c>/nodes</c>,
/// <c>/config/accounts</c>) are deliberately left alone. They were open before this existed,
/// monitoring is likely pointed at them, and enabling a command API is not a reason to break that.
/// </remarks>
internal static class HostApiEndpoints
{
    public static void MapHostApi(this WebApplication app, HostConfig config)
    {
        if (!config.IsMaster)
        {
            return;
        }

        var api = app.MapGroup("/api");

        api.MapGet("/commands", (HttpContext http, HostConfig hostConfig) =>
            Authorize(http, hostConfig)
                ?? Results.Json(new
                {
                    commands = DiscordSlashCommandCatalog.All.Select(descriptor => new
                    {
                        path = descriptor.Path,
                        group = descriptor.Group,
                        command = descriptor.Command,
                        description = descriptor.Description,
                        options = descriptor.Options.Select(option => new
                        {
                            name = option.Name,
                            type = option.Type,
                            required = option.Required,
                            description = option.Description,
                            choices = option.Choices
                        })
                    })
                }));

        api.MapGet("/dclone", (HttpContext http, HostConfig hostConfig, DiscordBot bot) =>
            Authorize(http, hostConfig) ?? Results.Json(bot.GetDcloneParkStatus()));

        // One envelope endpoint rather than a route per command: the command list comes from
        // DiscordSlashCommands.Build(), so a route table here would be a second surface to keep in
        // step with it, and would silently lag every command added to Discord.
        api.MapPost("/command", async (
            HttpContext http,
            HostConfig hostConfig,
            DiscordBot bot,
            ApiCommandRequest? request,
            CancellationToken cancellationToken) =>
        {
            if (Authorize(http, hostConfig) is { } denied)
            {
                return denied;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Command))
            {
                return Results.BadRequest(new { ok = false, error = "command is required." });
            }

            var result = await bot.ExecuteApiCommandAsync(
                BlankToNull(request.Group),
                request.Command.Trim(),
                ToOptionValues(request.Options),
                cancellationToken);
            return Results.Json(result, statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        });

        // The same thing addressed as a path, for callers (and curl one-liners) that would rather
        // put the command in the URL and the options in the body.
        api.MapPost("/d2r/{command}", (
                HttpContext http, HostConfig hostConfig, DiscordBot bot, string command,
                Dictionary<string, JsonElement>? options, CancellationToken cancellationToken) =>
            ExecutePathCommandAsync(http, hostConfig, bot, group: null, command, options, cancellationToken));

        api.MapPost("/d2r/{group}/{command}", (
                HttpContext http, HostConfig hostConfig, DiscordBot bot, string group, string command,
                Dictionary<string, JsonElement>? options, CancellationToken cancellationToken) =>
            ExecutePathCommandAsync(http, hostConfig, bot, group, command, options, cancellationToken));
    }

    private static async Task<IResult> ExecutePathCommandAsync(
        HttpContext http,
        HostConfig config,
        DiscordBot bot,
        string? group,
        string command,
        Dictionary<string, JsonElement>? options,
        CancellationToken cancellationToken)
    {
        if (Authorize(http, config) is { } denied)
        {
            return denied;
        }

        var result = await bot.ExecuteApiCommandAsync(
            BlankToNull(group),
            command,
            ToOptionValues(options),
            cancellationToken);
        return Results.Json(result, statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Returns null when the caller may proceed, or the response to send back when it may not.
    /// </summary>
    /// <remarks>
    /// "Turned off" and "wrong key" are deliberately different answers. An operator debugging their
    /// own automation on their own LAN needs to be able to tell "I never ran /d2r config api" from
    /// "my key is wrong", and this is not a surface where hiding that distinction from an attacker
    /// who is already inside the trusted network buys anything.
    /// </remarks>
    private static IResult? Authorize(HttpContext http, HostConfig config)
    {
        var decision = HostApiAuthorizationPolicy.Evaluate(
            config.Api.Enabled,
            config.Api.KeyHash,
            http.Request.Headers.Authorization.ToString(),
            http.Request.Headers["X-API-Key"].ToString());

        return decision switch
        {
            HostApiAuthorization.Allowed => null,
            HostApiAuthorization.Disabled => Results.Json(
                new
                {
                    ok = false,
                    error = "The HTTP command API is disabled on this host. Enable it from Discord with "
                        + "/d2r config api enabled:true."
                },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Json(
                new
                {
                    ok = false,
                    error = "A valid API key is required. Send it as `X-API-Key: <key>` or "
                        + "`Authorization: Bearer <key>`."
                },
                statusCode: StatusCodes.Status401Unauthorized)
        };
    }

    /// <summary>
    /// Flattens the JSON option object into the loosely typed values the command handlers read.
    /// </summary>
    /// <remarks>
    /// Numbers come back as long and booleans as bool so that Convert.ToInt32/ToBoolean behave the
    /// same as they do on Discord's own option values; a JSON string is left as a string, which
    /// also means "7" works where 7 was meant, the way a person typing curl would expect.
    /// </remarks>
    private static Dictionary<string, object?> ToOptionValues(Dictionary<string, JsonElement>? options)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, element) in options ?? new Dictionary<string, JsonElement>())
        {
            values[name] = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => element.TryGetInt64(out var number) ? number : element.GetDouble(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => element.ToString()
            };
        }

        return values;
    }

    private static string? BlankToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public sealed record ApiCommandRequest(
    string? Group,
    string? Command,
    Dictionary<string, JsonElement>? Options);
