using Microsoft.Extensions.Hosting;
using Shadowrun.LocalService.Supervisor;

var configPath = GetConfigPathFromArgs(args);
var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "ShadowrunLocalSupervisor";
    })
    .ConfigureAppConfiguration((_, configuration) =>
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            configuration.AddJsonFile(configPath, optional: false, reloadOnChange: false);
        }
    })
    .ConfigureServices((context, services) =>
    {
        services.Configure<SupervisorOptions>(context.Configuration.GetSection("Supervisor"));
        services.AddSingleton<SupervisorOperationLock>();
        services.AddSingleton<SupervisorAuditLogger>();
        services.AddSingleton<SupervisorOperations>();
        services.AddHostedService<DiscordBotHostedService>();
    })
    .Build();

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
