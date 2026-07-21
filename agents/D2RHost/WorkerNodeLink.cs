using AgentCommon;

namespace D2RHost;

/// <summary>
/// Maintains the worker's authenticated outbound WebSocket connection to the master.
/// </summary>
public sealed class WorkerNodeLink
{
    private const int MinimumHeartbeatSeconds = 5;
    private const int MaximumHeartbeatSeconds = 300;

    private readonly AgentClient<WorkerLinkAgentConfig> _client;
    private readonly WorkerNodeOperations _operations;

    public WorkerNodeLink(
        HostConfig config,
        WorkerNodeOperations operations,
        ILogger<WorkerNodeLink> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(logger);

        _operations = operations;
        var clientConfig = BuildClientConfig(config);
        _client = new AgentClient<WorkerLinkAgentConfig>(
            clientConfig,
            "host",
            message => logger.LogInformation("Worker link: {Message}", message));
    }

    public Task RunForeverAsync(CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task<object>> statusFactory = async statusCancellationToken =>
            await _operations.GetStatusAsync(statusCancellationToken);

        return _client.RunForeverAsync(
            statusFactory,
            _operations.HandleCommandAsync,
            cancellationToken);
    }

    private static WorkerLinkAgentConfig BuildClientConfig(HostConfig config)
    {
        var nodeId = RequireValue(config.NodeId, "nodeId");
        var masterUrl = RequireValue(config.MasterUrl, "masterUrl");
        var masterSharedSecret = RequireValue(config.MasterSharedSecret, "masterSharedSecret");

        if (!Uri.TryCreate(masterUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeWs && uri.Scheme != Uri.UriSchemeWss))
        {
            throw new InvalidOperationException(
                "Worker mode masterUrl must be an absolute ws:// or wss:// URL.");
        }

        var endpoint = new UriBuilder(uri);
        if (string.IsNullOrWhiteSpace(endpoint.Path) || endpoint.Path == "/")
        {
            endpoint.Path = "/node";
        }

        return new WorkerLinkAgentConfig
        {
            AgentId = nodeId,
            ControllerUrl = endpoint.Uri.AbsoluteUri,
            SharedSecret = masterSharedSecret,
            HeartbeatSeconds = Math.Clamp(
                config.NodeHeartbeatSeconds,
                MinimumHeartbeatSeconds,
                MaximumHeartbeatSeconds)
        };
    }

    private static string RequireValue(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Worker mode requires {propertyName}.");
        }

        return value.Trim();
    }

    private sealed class WorkerLinkAgentConfig : AgentConfig
    {
    }
}
