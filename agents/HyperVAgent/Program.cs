using AgentCommon;

namespace HyperVAgent;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var configPath = args.FirstOrDefault()
            ?? Environment.GetEnvironmentVariable("HYPERV_AGENT_CONFIG")
            ?? @"C:\D2ROps\hyperv-agent.config.json";

        try
        {
            var config = ConfigLoader.Load<HyperVAgentConfig>(configPath);
            var operations = new HyperVOperations(config);
            var client = new AgentClient<HyperVAgentConfig>(config, "host", Console.WriteLine);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };

            await client.RunForeverAsync(operations.GetStatusAsync, operations.HandleCommandAsync, cts.Token);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
