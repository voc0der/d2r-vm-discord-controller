using AgentCommon;

namespace D2RAgent;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var configPath = args.FirstOrDefault()
            ?? Environment.GetEnvironmentVariable("D2R_AGENT_CONFIG")
            ?? @"C:\D2ROps\vm-agent.config.json";

        try
        {
            var config = ConfigLoader.Load<VmAgentConfig>(configPath);
            var operations = new VmOperations(config);
            var client = new AgentClient<VmAgentConfig>(config, "vm", Console.WriteLine);

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
