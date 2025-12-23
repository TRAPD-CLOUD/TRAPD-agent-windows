using System;
using System.IO;

namespace Trapd.Agent.Service.Config;

/// <summary>
/// Resolves the service-safe data directory for TRAPD Agent.
/// Priority: ENV(TRAPD_DATA_DIR) -> %ProgramData%\TRAPD
/// </summary>
public static class DataDir
{
    /// <summary>
    /// Resolves the data directory path.
    /// Uses TRAPD_DATA_DIR environment variable if set, otherwise falls back to ProgramData\TRAPD.
    /// </summary>
    /// <returns>The resolved data directory path (not guaranteed to exist yet).</returns>
    public static string ResolveDataDir()
    {
        // 1. Check environment variable override
        var envDir = Environment.GetEnvironmentVariable("TRAPD_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            return envDir;
        }

        // 2. Fall back to ProgramData\TRAPD (service-safe location)
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "TRAPD");
    }

    /// <summary>
    /// Ensures the data directory and required subdirectories exist.
    /// </summary>
    /// <param name="dataDir">The data directory path.</param>
    public static void EnsureDirectories(string dataDir)
    {
        ArgumentNullException.ThrowIfNull(dataDir);

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "secrets"));
    }

    /// <summary>
    /// Gets the path to config.json within the data directory.
    /// </summary>
    public static string GetConfigPath(string dataDir) => Path.Combine(dataDir, "config.json");

    /// <summary>
    /// Gets the path to the encrypted API key file within the data directory.
    /// </summary>
    public static string GetApiKeyPath(string dataDir) => Path.Combine(dataDir, "secrets", "api_key.dpapi");

    /// <summary>
    /// Gets the path to the queue database file within the data directory.
    /// </summary>
    public static string GetQueueDbPath(string dataDir) => Path.Combine(dataDir, "queue.db");

    /// <summary>
    /// Gets the path to the agent log file within the data directory.
    /// </summary>
    public static string GetLogPath(string dataDir) => Path.Combine(dataDir, "agent.log");

    /// <summary>
    /// Gets the path to the device ID file within the data directory.
    /// </summary>
    public static string GetDeviceIdPath(string dataDir) => Path.Combine(dataDir, "device_id.txt");
}
