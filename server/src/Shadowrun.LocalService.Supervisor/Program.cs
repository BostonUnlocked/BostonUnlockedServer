using Shadowrun.LocalService.Supervisor;

var configPath = GetConfigPathFromArgs(args);
var builder = Host.CreateApplicationBuilder(args);

if (!string.IsNullOrWhiteSpace(configPath))
{
    builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: false);
}

builder.Services.Configure<SupervisorOptions>(builder.Configuration.GetSection("Supervisor"));
builder.Services.AddSingleton<SupervisorOperationLock>();
builder.Services.AddSingleton<SupervisorAuditLogger>();
builder.Services.AddSingleton<SupervisorOperations>();
builder.Services.AddHostedService<DiscordBotHostedService>();

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ShadowrunLocalSupervisor";
});

var host = builder.Build();
await host.RunAsync();

static string? GetConfigPathFromArgs(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            return args[i + 1];
        }
    }

    return null;
}
