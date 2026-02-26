using System;
using System.IO;

namespace Shadowrun.LocalService.Core
{
public sealed class LocalServiceOptions
{
    // Dev toggle: flip this constant to enable local AI decision execution by default.
    public const bool DefaultEnableAiLogic = true;

    private string _dataDir;

    public LocalServiceOptions()
    {
        Host = "0.0.0.0";
        Port = 80;
        APlayPort = 5055;
        PhotonPort = 4530;
        WorkspaceRoot = Directory.GetCurrentDirectory();

        EnableAiLogic = DefaultEnableAiLogic;

        // Defaults for a typical Steam install; can be overridden by setting these properties.
        GameRootDir = "d:\\SteamLibrary\\steamapps\\common\\ShadowrunChronicles";

        // Prefer a portable bundle layout if present.
        var portableStreamingAssets = TryGetPortableSubdir("StreamingAssets");
        StreamingAssetsDir = !IsNullOrWhiteSpace(portableStreamingAssets)
            ? portableStreamingAssets
            : Path.Combine(Path.Combine(GameRootDir, "Shadowrun_Data"), "StreamingAssets");

        // Keep persistence outside build output by default.
        _dataDir = TryGetDefaultPersistentDataDir();
        ChatAdminConfigPath = Path.Combine(_dataDir, "chat-admins.json");
    }

    public string Host { get; set; }
    public int Port { get; set; }
    public int APlayPort { get; set; }
    public int PhotonPort { get; set; }
    public string WorkspaceRoot { get; set; }

    public string GameRootDir { get; set; }
    public string StreamingAssetsDir { get; set; }
    public string ChatAdminConfigPath { get; set; }

    /// <summary>
    /// When true, the server simulation will attempt to run the local AI decision engine
    /// for AI-controlled teams instead of always issuing an end-turn command.
    /// </summary>
    public bool EnableAiLogic { get; set; }

    /// <summary>
    /// When true, the host will not write request logs to files.
    /// </summary>
    public bool DisableFileLogs { get; set; }

    public string DataDir
    {
        get { return _dataDir; }
        set { _dataDir = value; }
    }

    public string LocalServiceRoot
    {
        get
        {
            // If the host is started from the repo's 'localservice' folder, don't duplicate it.
            // Expected structure for localservice root: contains 'config' and 'static-data' directories.
            try
            {
                var configDir = Path.Combine(WorkspaceRoot, "config");
                var staticDataDir = Path.Combine(WorkspaceRoot, "static-data");

                // In the repo layout we often run from ./server which has config/ and static-data/,
                // while logs/ live alongside the built executable.
                if (Directory.Exists(configDir) && Directory.Exists(staticDataDir))
                {
                    return WorkspaceRoot;
                }
            }
            catch
            {
                // Fall through to default behavior.
            }

            return Path.Combine(WorkspaceRoot, "localservice");
        }
    }

    public string ConfigDir
    {
        get
        {
            var portable = TryGetPortableSubdir("config");
            return !IsNullOrWhiteSpace(portable) ? portable : Path.Combine(LocalServiceRoot, "config");
        }
    }

    public string StaticDataDir
    {
        get
        {
            var portable = TryGetPortableSubdir("static-data");
            return !IsNullOrWhiteSpace(portable) ? portable : Path.Combine(LocalServiceRoot, "static-data");
        }
    }

    public string LogDir
    {
        get
        {
            // Logs are always generated alongside the executable for portable builds.
            var baseDir = SafeGetBaseDirectory();
            if (!IsNullOrWhiteSpace(baseDir))
            {
                return Path.Combine(baseDir, "logs");
            }
            return Path.Combine(LocalServiceRoot, "logs");
        }
    }

    public string RequestLogPath { get { return Path.Combine(LogDir, "requests-csharp.log"); } }
    public string RequestLowLogPath { get { return Path.Combine(LogDir, "requests-csharp-low.log"); } }
    public string AiLogPath { get { return Path.Combine(LogDir, "requests-csharp-ai.log"); } }
    public string AdminLogPath { get { return Path.Combine(LogDir, "requests-csharp-admin.log"); } }

    private string TryGetPortableSubdir(string name)
    {
        try
        {
            var baseDir = SafeGetBaseDirectory();
            if (IsNullOrWhiteSpace(baseDir))
            {
                return null;
            }

            // Portable layout: <exeDir>/Resources/<name>
            var resourcesDir = Path.Combine(baseDir, "Resources");
            var candidate = Path.Combine(resourcesDir, name);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        catch
        {
        }
        return null;
    }

    private static string SafeGetBaseDirectory()
    {
        try
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }
        catch
        {
            return null;
        }
    }

    private string TryGetDefaultPersistentDataDir()
    {
        // Prefer an existing ./data next to the current working directory for dev convenience.
        try
        {
            if (!IsNullOrWhiteSpace(WorkspaceRoot))
            {
                var local = Path.Combine(WorkspaceRoot, "data");
                if (Directory.Exists(local))
                {
                    return local;
                }
            }
        }
        catch
        {
        }

        // Otherwise keep data outside build output so it persists across rebuilds.
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!IsNullOrWhiteSpace(appData))
            {
                return Path.Combine(Path.Combine(appData, "ShadowrunLocalService"), "data");
            }
        }
        catch
        {
        }

        // Last resort: relative to current working directory.
        return Path.Combine(Directory.GetCurrentDirectory(), "data");
    }

    private static bool IsNullOrWhiteSpace(string value)
    {
        return value == null || value.Trim().Length == 0;
    }
}

}
