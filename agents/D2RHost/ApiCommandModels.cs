namespace D2RHost;

/// <summary>
/// Collects what a command handler would have said to Discord, so an authenticated HTTP caller
/// can be told exactly the same thing.
/// </summary>
/// <remarks>
/// Handlers write here without knowing they are: the bot's response helpers route to this sink
/// whenever the context has no interaction to answer. That is the whole point of the abstraction -
/// the API gets real parity with <c>/d2r</c> because it runs the same handlers, rather than a
/// second implementation that drifts from them.
/// </remarks>
internal sealed class ApiCommandSink
{
    private readonly object _sync = new();
    private readonly List<string> _details = new();
    private string? _message;
    private ApiCommandFile? _file;

    /// <summary>
    /// The primary reply. A handler may set this more than once - Discord's own flow is "defer,
    /// then edit the original response" - and the last one written is the answer, exactly as the
    /// operator would see it in Discord.
    /// </summary>
    public void SetMessage(string content)
    {
        lock (_sync)
        {
            _message = content;
        }
    }

    /// <summary>
    /// A follow-up. Background fan-outs report per-account failures this way long after the
    /// initial reply, so these are kept in order alongside the primary message rather than
    /// overwriting it.
    /// </summary>
    public void AddDetail(string content)
    {
        lock (_sync)
        {
            _details.Add(content);
        }
    }

    public void AttachFile(string fileName, string contentType, byte[] content)
    {
        lock (_sync)
        {
            _file = new ApiCommandFile(fileName, contentType, Convert.ToBase64String(content));
        }
    }

    public ApiCommandResult ToResult(bool ok, string? error = null)
    {
        lock (_sync)
        {
            return new ApiCommandResult(
                ok,
                _message ?? (ok ? "Command completed." : error ?? "Command failed."),
                _details.ToArray(),
                _file,
                error);
        }
    }
}

public sealed record ApiCommandFile(string FileName, string ContentType, string Base64);

public sealed record ApiCommandResult(
    bool Ok,
    string Message,
    IReadOnlyList<string> Details,
    ApiCommandFile? File,
    string? Error);

/// <summary>
/// Why an API command could not even be attempted. These are the cases that must not look like an
/// ordinary command failure, because the caller has to do something different about each.
/// </summary>
public enum ApiCommandRejection
{
    None = 0,

    /// <summary>The named command or group is not part of the /d2r surface.</summary>
    UnknownCommand,

    /// <summary>The command needs a Discord channel for its live monitor and none is available.</summary>
    NeedsDiscordChannel
}

/// <summary>The live state of a <c>/d2r dclone</c> park, for <c>GET /api/dclone</c>.</summary>
/// <remarks>
/// <see cref="Total"/> grows while the park runs: VMs that connect later are admitted, up to
/// <see cref="MaxBots"/> when the park was started with a <c>bots</c> cap (null means no cap).
/// </remarks>
public sealed record DcloneParkStatus(
    bool Running,
    DateTimeOffset? StartedUtc,
    string? Difficulty,
    int Parked,
    int Total,
    int? MaxBots,
    IReadOnlyList<DcloneParkGame> Games);

public sealed record DcloneParkGame(
    string AccountKey,
    string? GameName,
    string? Password,
    string State,
    string Detail,
    int Reparks);
