using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Trapd.Agent.Service.Config;

/// <summary>
/// Configuration DTO for TRAPD Agent. Loaded from config.json in the data directory.
/// </summary>
public sealed class TrapdConfig
{
    /// <summary>
    /// The base URL of the TRAPD API (e.g., "https://api.trapd.io").
    /// </summary>
    [JsonPropertyName("api_url")]
    public string ApiUrl { get; set; } = "https://api.trapd.io";

    /// <summary>
    /// The project ID to associate events with.
    /// </summary>
    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Heartbeat interval in seconds (10-3600).
    /// </summary>
    [JsonPropertyName("interval_s")]
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Number of events to send in a single batch (1-1000).
    /// </summary>
    [JsonPropertyName("batch_size")]
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Log level for the agent (Trace, Debug, Information, Warning, Error, Critical).
    /// </summary>
    [JsonPropertyName("log_level")]
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// Loads configuration from config.json in the specified data directory.
    /// Falls back to defaults on parse/validation errors.
    /// </summary>
    /// <param name="dataDir">The data directory path.</param>
    /// <param name="logger">Logger for error reporting (can be null).</param>
    /// <returns>A validated TrapdConfig instance.</returns>
    public static TrapdConfig Load(string dataDir, ILogger? logger = null)
    {
        var configPath = DataDir.GetConfigPath(dataDir);
        var config = new TrapdConfig();

        if (!File.Exists(configPath))
        {
            logger?.LogWarning("Config file not found at {ConfigPath}. Using defaults.", configPath);
            return config;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            var loaded = JsonSerializer.Deserialize<TrapdConfig>(json, options);
            if (loaded != null)
            {
                config = loaded;
            }
        }
        catch (JsonException ex)
        {
            logger?.LogError(ex, "Failed to parse config.json at {ConfigPath}. Using defaults.", configPath);
            return new TrapdConfig();
        }
        catch (IOException ex)
        {
            logger?.LogError(ex, "Failed to read config.json at {ConfigPath}. Using defaults.", configPath);
            return new TrapdConfig();
        }

        // Validate and apply defaults for invalid values
        config = Validate(config, logger);

        return config;
    }

    /// <summary>
    /// Validates the configuration and returns a corrected instance.
    /// </summary>
    private static TrapdConfig Validate(TrapdConfig config, ILogger? logger)
    {
        var validated = new TrapdConfig
        {
            ApiUrl = config.ApiUrl,
            ProjectId = config.ProjectId,
            IntervalSeconds = config.IntervalSeconds,
            BatchSize = config.BatchSize,
            LogLevel = config.LogLevel
        };

        // Validate api_url
        if (string.IsNullOrWhiteSpace(validated.ApiUrl))
        {
            logger?.LogWarning("api_url is empty. Using default: https://api.trapd.io");
            validated.ApiUrl = "https://api.trapd.io";
        }

        // Validate project_id - warn but don't override (may come from env)
        if (string.IsNullOrWhiteSpace(validated.ProjectId))
        {
            logger?.LogWarning("project_id is empty in config.json. Must be set via config or TRAPD_PROJECT_ID env var.");
        }

        // Validate interval_s (10-3600)
        if (validated.IntervalSeconds < 10 || validated.IntervalSeconds > 3600)
        {
            logger?.LogWarning("interval_s={IntervalS} is out of range (10-3600). Using default: 60", validated.IntervalSeconds);
            validated.IntervalSeconds = 60;
        }

        // Validate batch_size (1-1000)
        if (validated.BatchSize < 1 || validated.BatchSize > 1000)
        {
            logger?.LogWarning("batch_size={BatchSize} is out of range (1-1000). Using default: 100", validated.BatchSize);
            validated.BatchSize = 100;
        }

        // Validate log_level
        var validLevels = new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical" };
        if (!Array.Exists(validLevels, l => l.Equals(validated.LogLevel, StringComparison.OrdinalIgnoreCase)))
        {
            logger?.LogWarning("log_level={LogLevel} is invalid. Using default: Information", validated.LogLevel);
            validated.LogLevel = "Information";
        }

        return validated;
    }

    /// <summary>
    /// Parses the LogLevel string to a LogLevel enum value.
    /// </summary>
    public Microsoft.Extensions.Logging.LogLevel GetLogLevelEnum()
    {
        return LogLevel?.ToLowerInvariant() switch
        {
            "trace" => Microsoft.Extensions.Logging.LogLevel.Trace,
            "debug" => Microsoft.Extensions.Logging.LogLevel.Debug,
            "information" => Microsoft.Extensions.Logging.LogLevel.Information,
            "warning" => Microsoft.Extensions.Logging.LogLevel.Warning,
            "error" => Microsoft.Extensions.Logging.LogLevel.Error,
            "critical" => Microsoft.Extensions.Logging.LogLevel.Critical,
            _ => Microsoft.Extensions.Logging.LogLevel.Information
        };
    }
}
