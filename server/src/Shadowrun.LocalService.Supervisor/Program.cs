using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using System.ServiceProcess;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<SupervisorOptions>(builder.Configuration.GetSection("Supervisor"));
builder.Services.AddSingleton<SupervisorOperationLock>();
builder.Services.AddSingleton<SupervisorOperations>();
builder.Services.AddHostedService<SupervisorWorker>();

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ShadowrunLocalSupervisor";
});

var host = builder.Build();
await host.RunAsync();

internal sealed class SupervisorWorker(
    ILogger<SupervisorWorker> logger,
    IOptions<SupervisorOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        logger.LogInformation(
            "supervisor started; targetService={TargetService}; discordInterface={DiscordMode}; framework={Framework}",
            settings.TargetServiceName,
            string.IsNullOrWhiteSpace(settings.DiscordBotToken) ? "disabled" : "configured",
            ".NET 8");

        if (string.IsNullOrWhiteSpace(settings.DiscordBotToken))
        {
            logger.LogWarning("Discord bot token is not configured yet. Supervisor transport is currently disabled.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        logger.LogInformation("supervisor stopping");
    }
}

internal sealed class SupervisorOptions
{
    public string TargetServiceName { get; set; } = "ShadowrunLocalService";
    public string? DiscordBotToken { get; set; }
    public string[] AllowedDiscordUserIds { get; set; } = [];
    public string? GithubOwner { get; set; }
    public string? GithubRepo { get; set; }
    public string? GithubAssetName { get; set; }
    public string? LocalServiceBinPath { get; set; }
    public string? BackupRootPath { get; set; }
    public string[] PatchAllowList { get; set; } =
    [
        "Shadowrun.LocalService.Host.exe",
        "Shadowrun.LocalService.Host.pdb",
        "Shadowrun.LocalService.Core.dll",
        "Shadowrun.LocalService.Core.pdb"
    ];
}

internal sealed class SupervisorOperationLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new ReleaseHandle(_semaphore);
    }

    private sealed class ReleaseHandle(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose()
        {
            semaphore.Release();
        }
    }
}

internal sealed class SupervisorOperations(
    ILogger<SupervisorOperations> logger,
    IOptions<SupervisorOptions> options,
    SupervisorOperationLock operationLock)
{
    private readonly SupervisorOptions _settings = options.Value;

    public string GetServiceStatus()
    {
        try
        {
            using var service = new ServiceController(_settings.TargetServiceName);
            return service.Status.ToString();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to query service status");
            return "Unknown";
        }
    }

    public async Task<string> RestartTargetAsync(CancellationToken cancellationToken)
    {
        using var lease = await operationLock.AcquireAsync(cancellationToken);
        using var service = new ServiceController(_settings.TargetServiceName);

        if (service.Status != ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.StopPending)
        {
            service.Stop();
        }

        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMinutes(2));
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMinutes(2));

        return "Restart completed";
    }

    public async Task<string> ApplyPatchFromGitHubAsync(string releaseTag, CancellationToken cancellationToken)
    {
        using var lease = await operationLock.AcquireAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(_settings.GithubOwner)
            || string.IsNullOrWhiteSpace(_settings.GithubRepo)
            || string.IsNullOrWhiteSpace(_settings.GithubAssetName)
            || string.IsNullOrWhiteSpace(_settings.LocalServiceBinPath))
        {
            return "Patch aborted: Supervisor github/bin settings are incomplete.";
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "shadowrun-supervisor", Guid.NewGuid().ToString("N"));
        var backupRoot = string.IsNullOrWhiteSpace(_settings.BackupRootPath)
            ? Path.Combine(Path.GetTempPath(), "shadowrun-supervisor-backup")
            : _settings.BackupRootPath;

        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(backupRoot);

        var downloadPath = Path.Combine(tempRoot, _settings.GithubAssetName);

        var releaseApi = $"https://api.github.com/repos/{_settings.GithubOwner}/{_settings.GithubRepo}/releases/tags/{releaseTag}";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShadowrunLocalSupervisor", "1.0"));

        var releaseJson = await http.GetStringAsync(releaseApi, cancellationToken);
        using var doc = JsonDocument.Parse(releaseJson);

        var assets = doc.RootElement.GetProperty("assets").EnumerateArray();
        string? downloadUrl = null;
        foreach (var asset in assets)
        {
            if (asset.GetProperty("name").GetString() == _settings.GithubAssetName)
            {
                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return $"Patch aborted: asset '{_settings.GithubAssetName}' not found for tag '{releaseTag}'.";
        }

        await using (var remote = await http.GetStreamAsync(downloadUrl, cancellationToken))
        await using (var local = File.Create(downloadPath))
        {
            await remote.CopyToAsync(local, cancellationToken);
        }

        var unpackDir = Path.Combine(tempRoot, "unpacked");
        ZipFile.ExtractToDirectory(downloadPath, unpackDir);

        var allowList = new HashSet<string>(_settings.PatchAllowList, StringComparer.OrdinalIgnoreCase);

        using var service = new ServiceController(_settings.TargetServiceName);
        if (service.Status != ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.StopPending)
        {
            service.Stop();
        }

        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMinutes(2));

        var stampedBackup = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        Directory.CreateDirectory(stampedBackup);

        try
        {
            foreach (var allowedFile in allowList)
            {
                var sourcePath = Path.Combine(unpackDir, allowedFile);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                var destinationPath = Path.Combine(_settings.LocalServiceBinPath, allowedFile);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                if (File.Exists(destinationPath))
                {
                    var backupPath = Path.Combine(stampedBackup, allowedFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(destinationPath, backupPath, overwrite: true);
                }

                File.Copy(sourcePath, destinationPath, overwrite: true);
            }

            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMinutes(2));
            return "Patch completed";
        }
        catch
        {
            foreach (var backupFile in Directory.EnumerateFiles(stampedBackup, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(stampedBackup, backupFile);
                var restorePath = Path.Combine(_settings.LocalServiceBinPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(restorePath)!);
                File.Copy(backupFile, restorePath, overwrite: true);
            }

            try
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMinutes(2));
            }
            catch (Exception restoreEx)
            {
                logger.LogError(restoreEx, "failed to restart target service after rollback");
            }

            throw;
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    public string CreateLogSnapshot(string logRootPath)
    {
        if (!Directory.Exists(logRootPath))
        {
            return "Log snapshot failed: log root path does not exist.";
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var zipPath = Path.Combine(Path.GetTempPath(), $"shadowrun-logs-{stamp}.zip");

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var patterns = new[] { "events-*.jsonl", "diagnostics-*.jsonl", "player-bugs-*.jsonl" };

        foreach (var pattern in patterns)
        {
            foreach (var file in Directory.EnumerateFiles(logRootPath, pattern, SearchOption.TopDirectoryOnly))
            {
                archive.CreateEntryFromFile(file, Path.GetFileName(file));
            }
        }

        return zipPath;
    }
}
