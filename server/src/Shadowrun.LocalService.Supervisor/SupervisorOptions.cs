namespace Shadowrun.LocalService.Supervisor;

internal sealed class SupervisorOptions
{
    public string TargetServiceName { get; set; } = "ShadowrunLocalService";
    public string? DiscordBotToken { get; set; }
    public string[] AllowedDiscordUserIds { get; set; } = [];
    public ulong? DiscordGuildId { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 900;
    public string? DefaultLogRootPath { get; set; }
    public string? SaveFileExportPath { get; set; }

    public string? GithubOwner { get; set; }
    public string? GithubRepo { get; set; }
    public string? GithubAssetName { get; set; } = "ServerPatch.zip";

    public string? LocalServiceBinPath { get; set; }
    public string? BackupRootPath { get; set; }
}
