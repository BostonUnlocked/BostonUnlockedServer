using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Options;

namespace Shadowrun.LocalService.Supervisor;

internal sealed class DiscordBotHostedService(
    ILogger<DiscordBotHostedService> logger,
    IOptions<SupervisorOptions> options,
    SupervisorOperations operations,
    SupervisorAuditLogger auditLogger) : BackgroundService
{
    private const string DefaultSaveFileExportPath = @"C:\Windows\System32\config\systemprofile\AppData\Local\ShadowrunLocalService\data\localservice.sqlite";

    private readonly SupervisorOptions _settings = options.Value;
    private DiscordSocketClient? _client;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.DiscordBotToken))
        {
            logger.LogWarning("Discord bot token is not configured; supervisor command transport is disabled.");
            return;
        }

        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            AlwaysDownloadUsers = false,
            LogGatewayIntentWarnings = false
        };

        _client = new DiscordSocketClient(config);
        _client.Log += OnDiscordLog;
        _client.Ready += OnReady;
        _client.SlashCommandExecuted += OnSlashCommand;

        await _client.LoginAsync(TokenType.Bot, _settings.DiscordBotToken);
        await _client.StartAsync();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        finally
        {
            if (_client != null)
            {
                await _client.StopAsync();
                await _client.LogoutAsync();
                _client.Dispose();
            }
        }
    }

    private Task OnDiscordLog(LogMessage message)
    {
        logger.LogInformation("discord[{Severity}] {Source}: {Message}", message.Severity, message.Source, message.Message);
        return Task.CompletedTask;
    }

    private async Task OnReady()
    {
        if (_client == null)
        {
            return;
        }

        try
        {
            var commands = BuildCommands();
            if (_settings.DiscordGuildId.HasValue && _settings.DiscordGuildId.Value != 0)
            {
                var guild = _client.GetGuild(_settings.DiscordGuildId.Value);
                if (guild != null)
                {
                    await guild.BulkOverwriteApplicationCommandAsync(commands.ToArray());
                    logger.LogInformation("registered {Count} supervisor slash commands for guild {GuildId}", commands.Count, guild.Id);
                    return;
                }
            }

            await _client.BulkOverwriteGlobalApplicationCommandsAsync(commands.ToArray());
            logger.LogInformation("registered {Count} supervisor global slash commands", commands.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "failed to register slash commands");
        }
    }

    private async Task OnSlashCommand(SocketSlashCommand command)
    {
        var requesterId = command.User.Id.ToString();
        if (!IsAuthorized(requesterId))
        {
            auditLogger.Log("command-denied", new { requesterId, command = command.CommandName });
            await command.RespondAsync("You are not authorized for supervisor commands.", ephemeral: true);
            return;
        }

        if (string.Equals(command.CommandName, "exportsavefile", StringComparison.OrdinalIgnoreCase))
        {
            await HandleExportSaveFileCommandAsync(command, requesterId);
            return;
        }

        await command.DeferAsync(ephemeral: true);

        var timeoutSeconds = _settings.CommandTimeoutSeconds > 0 ? _settings.CommandTimeoutSeconds : 900;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            string response;
            switch (command.CommandName)
            {
                case "svc-status":
                    response = $"Service status: {operations.GetServiceStatus()}";
                    break;
                case "svc-restart":
                    response = await operations.RestartTargetAsync(requesterId, cts.Token);
                    break;
                case "svc-patch":
                {
                    var tag = GetStringOption(command, "tag");
                    response = await operations.ApplyPatchFromGitHubAsync(requesterId, tag, cts.Token);
                    break;
                }
                case "svc-logs":
                {
                    var root = GetStringOption(command, "root");
                    response = "Log snapshot created: " + operations.CreateLogSnapshot(requesterId, root);
                    break;
                }
                default:
                    response = "Unsupported command.";
                    break;
            }

            auditLogger.Log("command-completed", new { requesterId, command = command.CommandName, response });
            await command.ModifyOriginalResponseAsync(msg => msg.Content = response);
        }
        catch (Exception ex)
        {
            auditLogger.Log("command-failed", new { requesterId, command = command.CommandName, error = ex.Message });
            await command.ModifyOriginalResponseAsync(msg => msg.Content = "Command failed: " + ex.Message);
        }
    }

    private async Task HandleExportSaveFileCommandAsync(SocketSlashCommand command, string requesterId)
    {
        try
        {
            await command.DeferAsync(ephemeral: false);

            var saveFileExportPath = string.IsNullOrWhiteSpace(_settings.SaveFileExportPath)
                ? DefaultSaveFileExportPath
                : _settings.SaveFileExportPath;

            if (!File.Exists(saveFileExportPath))
            {
                auditLogger.Log("save-export-failed", new { requesterId, saveFilePath = saveFileExportPath, error = "file-not-found" });
                await command.FollowupAsync("Save file export failed: localservice.sqlite was not found.", ephemeral: false);
                return;
            }

            var timestamp = DateTimeOffset.UtcNow;
            var message = $"Save file export timestamp (UTC): {timestamp:yyyy-MM-dd HH:mm:ss} ({timestamp:O})";
            await command.FollowupWithFileAsync(saveFileExportPath, text: message, ephemeral: false);

            auditLogger.Log("save-export-completed", new
            {
                requesterId,
                saveFilePath = saveFileExportPath,
                timestampUtc = timestamp
            });
        }
        catch (Exception ex)
        {
            auditLogger.Log("save-export-failed", new { requesterId, saveFilePath = _settings.SaveFileExportPath ?? DefaultSaveFileExportPath, error = ex.Message });

            if (command.HasResponded)
            {
                await command.FollowupAsync("Save file export failed: " + ex.Message, ephemeral: false);
            }
            else
            {
                await command.RespondAsync("Save file export failed: " + ex.Message, ephemeral: false);
            }
        }
    }

    private bool IsAuthorized(string requesterId)
    {
        if (_settings.AllowedDiscordUserIds == null || _settings.AllowedDiscordUserIds.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < _settings.AllowedDiscordUserIds.Length; i++)
        {
            if (string.Equals(_settings.AllowedDiscordUserIds[i], requesterId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetStringOption(SocketSlashCommand command, string name)
    {
        var options = command.Data.Options;
        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options.ElementAt(i).Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return options.ElementAt(i).Value?.ToString();
            }
        }

        return null;
    }

    private static List<ApplicationCommandProperties> BuildCommands()
    {
        var status = new SlashCommandBuilder()
            .WithName("svc-status")
            .WithDescription("Show status of the managed local service")
            .Build();

        var restart = new SlashCommandBuilder()
            .WithName("svc-restart")
            .WithDescription("Restart the managed local service")
            .Build();

        var patch = new SlashCommandBuilder()
            .WithName("svc-patch")
            .WithDescription("Apply patch from configured GitHub release")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("tag")
                .WithDescription("Release tag (optional; defaults to latest)")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(false))
            .Build();

        var logs = new SlashCommandBuilder()
            .WithName("svc-logs")
            .WithDescription("Create a zip snapshot of structured logs")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("root")
                .WithDescription("Optional log root override")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(false))
            .Build();

        var exportSaveFile = new SlashCommandBuilder()
            .WithName("exportsavefile")
            .WithDescription("Upload the local service SQLite save file")
            .Build();

        return [status, restart, patch, logs, exportSaveFile];
    }
}
