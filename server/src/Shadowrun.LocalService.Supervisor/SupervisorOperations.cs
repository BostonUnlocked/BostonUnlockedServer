using System.IO.Compression;
using System.Net.Http.Headers;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Shadowrun.LocalService.Supervisor;

internal sealed class SupervisorOperations(
    ILogger<SupervisorOperations> logger,
    IOptions<SupervisorOptions> options,
    SupervisorOperationLock operationLock,
    SupervisorAuditLogger auditLogger)
{
    private static readonly string[] AllowedPatchFiles =
    [
        "Shadowrun.LocalService.Core.dll",
        "Shadowrun.LocalService.Core.pdb",
        "Shadowrun.LocalService.Host.exe",
        "Shadowrun.LocalService.Host.pdb"
    ];

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

    public async Task<string> RestartTargetAsync(string requesterId, CancellationToken cancellationToken)
    {
        using var lease = await operationLock.AcquireAsync(cancellationToken);
        auditLogger.Log("restart-started", new { requesterId, serviceName = _settings.TargetServiceName });

        using var service = new ServiceController(_settings.TargetServiceName);

        if (service.Status != ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.StopPending)
        {
            service.Stop();
        }

        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMinutes(2));
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMinutes(2));

        auditLogger.Log("restart-completed", new { requesterId, serviceName = _settings.TargetServiceName });
        return "Restart completed";
    }

    public async Task<string> ApplyPatchFromGitHubAsync(string requesterId, string? releaseTag, CancellationToken cancellationToken)
    {
        using var lease = await operationLock.AcquireAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(_settings.GithubOwner)
            || string.IsNullOrWhiteSpace(_settings.GithubRepo)
            || string.IsNullOrWhiteSpace(_settings.GithubAssetName)
            || string.IsNullOrWhiteSpace(_settings.LocalServiceBinPath))
        {
            return "Patch aborted: supervisor GitHub/bin settings are incomplete.";
        }

        var selectedTag = string.IsNullOrWhiteSpace(releaseTag)
            ? "latest"
            : releaseTag;

        auditLogger.Log("patch-started", new { requesterId, selectedTag, asset = _settings.GithubAssetName });

        var tempRoot = Path.Combine(Path.GetTempPath(), "shadowrun-supervisor", Guid.NewGuid().ToString("N"));
        var backupRoot = string.IsNullOrWhiteSpace(_settings.BackupRootPath)
            ? Path.Combine(Path.GetTempPath(), "shadowrun-supervisor-backup")
            : _settings.BackupRootPath;

        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(backupRoot);

        var downloadPath = Path.Combine(tempRoot, _settings.GithubAssetName);

        var releaseApi = selectedTag == "latest"
            ? $"https://api.github.com/repos/{_settings.GithubOwner}/{_settings.GithubRepo}/releases/latest"
            : $"https://api.github.com/repos/{_settings.GithubOwner}/{_settings.GithubRepo}/releases/tags/{selectedTag}";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShadowrunLocalSupervisor", "1.0"));

        try
        {
            var releaseJson = await http.GetStringAsync(releaseApi, cancellationToken);
            using var doc = JsonDocument.Parse(releaseJson);

            var resolvedTag = doc.RootElement.GetProperty("tag_name").GetString() ?? selectedTag;
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
                return $"Patch aborted: asset '{_settings.GithubAssetName}' not found for release '{resolvedTag}'.";
            }

            await using (var remote = await http.GetStreamAsync(downloadUrl, cancellationToken))
            await using (var local = File.Create(downloadPath))
            {
                await remote.CopyToAsync(local, cancellationToken);
            }

            var unpackDir = Path.Combine(tempRoot, "unpacked");
            ZipFile.ExtractToDirectory(downloadPath, unpackDir);

            var validatedFiles = BuildCopyPlanFromAllowList(unpackDir);
            if (validatedFiles.Count == 0)
            {
                return "Patch aborted: no allow-listed files were found in release payload.";
            }

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
                foreach (var file in validatedFiles)
                {
                    var sourcePath = Path.Combine(unpackDir, file.RelativePath);
                    var destinationPath = Path.Combine(_settings.LocalServiceBinPath, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                    if (File.Exists(destinationPath))
                    {
                        var backupPath = Path.Combine(stampedBackup, file.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                        File.Copy(destinationPath, backupPath, overwrite: true);
                    }

                    File.Copy(sourcePath, destinationPath, overwrite: true);
                }

                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMinutes(2));

                auditLogger.Log("patch-completed", new
                {
                    requesterId,
                    selectedTag = resolvedTag,
                    fileCount = validatedFiles.Count
                });

                return $"Patch completed (tag: {resolvedTag}, files: {validatedFiles.Count}).";
            }
            catch (Exception applyEx)
            {
                auditLogger.Log("patch-failed", new
                {
                    requesterId,
                    selectedTag = resolvedTag,
                    error = applyEx.Message
                });

                RollbackFromBackup(stampedBackup, _settings.LocalServiceBinPath);

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

    public string CreateLogSnapshot(string requesterId, string? logRootOverride)
    {
        var logRootPath = string.IsNullOrWhiteSpace(logRootOverride)
            ? _settings.DefaultLogRootPath
            : logRootOverride;

        if (string.IsNullOrWhiteSpace(logRootPath) || !Directory.Exists(logRootPath))
        {
            return "Log snapshot failed: log root path is not configured or does not exist.";
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

        auditLogger.Log("logs-snapshot-created", new { requesterId, logRootPath, zipPath });
        return zipPath;
    }

    private List<ValidatedPatchFile> BuildCopyPlanFromAllowList(string unpackDir)
    {
        var planned = new List<ValidatedPatchFile>();

        foreach (var candidate in AllowedPatchFiles)
        {
            var normalized = NormalizeRelativePath(candidate);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            var sourcePath = Path.Combine(unpackDir, normalized);
            if (!File.Exists(sourcePath))
            {
                throw new InvalidOperationException($"Required patch file '{normalized}' is missing from release payload.");
            }

            planned.Add(new ValidatedPatchFile(normalized));
        }

        return planned;
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var candidate = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(candidate))
        {
            return string.Empty;
        }

        var segments = candidate.Split(Path.DirectorySeparatorChar);
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                return string.Empty;
            }
        }

        return candidate.TrimStart(Path.DirectorySeparatorChar);
    }

    private static void RollbackFromBackup(string backupRoot, string destinationRoot)
    {
        foreach (var backupFile in Directory.EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(backupRoot, backupFile);
            var restorePath = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(restorePath)!);
            File.Copy(backupFile, restorePath, overwrite: true);
        }
    }

    private sealed class ValidatedPatchFile(string relativePath)
    {
        public string RelativePath { get; } = relativePath;
    }

}
