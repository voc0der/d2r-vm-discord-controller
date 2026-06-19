using D2RHost;

var configPath = args.FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("CONFIG_PATH")
    ?? @"C:\D2ROps\d2r-host.config.json";

var config = HostConfigLoader.Load(configPath);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{config.HttpPort}");

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<AppDb>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<HyperVOperations>();
builder.Services.AddSingleton<DiscordBot>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/healthz", (AgentRegistry registry) =>
{
    return Results.Json(new
    {
        ok = true,
        agents = registry.Snapshot().Count
    });
});

app.MapGet("/agents", (AgentRegistry registry) =>
{
    return Results.Json(registry.Snapshot());
});

app.MapGet("/config/accounts", () =>
{
    return Results.Json(config.Accounts.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray());
});

app.Map("/agent", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket required.");
        return;
    }

    var registry = context.RequestServices.GetRequiredService<AgentRegistry>();
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await registry.HandleWebSocketAsync(socket, context.RequestAborted);
});

var bot = app.Services.GetRequiredService<DiscordBot>();
await bot.StartAsync();
app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        bot.StopAsync().GetAwaiter().GetResult();
    }
    catch
    {
        // Shutdown best effort.
    }
});

await app.RunAsync();
