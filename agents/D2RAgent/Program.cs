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
            if (await SelfUpdater.CheckAndOfferUpdateAsync(SelfUpdateOptions.D2RAgent(args)))
            {
                return 0;
            }

            var config = VmAgentConfigWizard.LoadOrCreate(configPath);
            config = await VmAgentConfigWizard.EnsureConnectsAsync(
                configPath,
                config,
                async (candidate, cancellationToken) =>
                {
                    var probeOperations = new VmOperations(candidate);
                    var probeClient = new AgentClient<VmAgentConfig>(candidate, "vm", Console.WriteLine);
                    await probeClient.ProbeConnectionAsync(
                        probeOperations.GetStatusAsync,
                        TimeSpan.FromSeconds(6),
                        cancellationToken);
                },
                CancellationToken.None);

            var operations = new VmOperations(config);
            var client = new AgentClient<VmAgentConfig>(config, "vm", Console.WriteLine);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };

            var idleMonitorTask = operations.RunIdleMonitorAsync(Console.WriteLine, cts.Token);
            try
            {
                await client.RunForeverAsync(operations.GetStatusAsync, operations.HandleCommandAsync, cts.Token);
            }
            finally
            {
                cts.Cancel();
                try
                {
                    await idleMonitorTask;
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown.
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (ConsolePrompt.CanPrompt)
            {
                Console.WriteLine();
                Console.WriteLine("Press Enter to exit.");
                Console.ReadLine();
            }

            return 1;
        }
    }
}
