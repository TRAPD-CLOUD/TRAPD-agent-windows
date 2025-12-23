using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Trapd.Agent.Service;
using Trapd.Agent.Service.Config;

var builder = Host.CreateApplicationBuilder(args);

// Logging - initial setup (will be reconfigured after loading config)
builder.Logging.ClearProviders();
builder.Logging.AddEventLog();
builder.Logging.AddConsole();

// ============================================================================
// STEP 1: Resolve Data Directory (service-safe)
// ============================================================================
var dataDir = DataDir.ResolveDataDir();

// Ensure directories exist
try
{
    DataDir.EnsureDirectories(dataDir);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: Cannot create data directory at '{dataDir}': {ex.Message}");
    Environment.Exit(1);
}

// ============================================================================
// STEP 2: Load Configuration from config.json
// ============================================================================
// Create a temporary logger for config loading (before DI is set up)
using var tempLoggerFactory = LoggerFactory.Create(b => b.AddConsole().AddEventLog());
var configLogger = tempLoggerFactory.CreateLogger("Config");

var trapdConfig = TrapdConfig.Load(dataDir, configLogger);

// ============================================================================
// STEP 3: Load API Key from DPAPI-encrypted file
// ============================================================================
string apiKey;
string apiKeySource;

// Check for ENV override first (for debugging only)
var envApiKey = Environment.GetEnvironmentVariable("TRAPD_API_KEY");
if (!string.IsNullOrWhiteSpace(envApiKey))
{
    apiKey = envApiKey;
    apiKeySource = "env:TRAPD_API_KEY";
    configLogger.LogWarning("Using API key from environment variable (debugging mode)");
}
else
{
    // Try to read from DPAPI-encrypted file
    try
    {
        apiKey = DpapiSecrets.ReadApiKey(dataDir);
        apiKeySource = "dpapi:" + DataDir.GetApiKeyPath(dataDir);
    }
    catch (FileNotFoundException ex)
    {
        configLogger.LogCritical(ex, "API key file not found. Cannot start service.");
        Console.Error.WriteLine($"FATAL: {ex.Message}");
        Environment.Exit(1);
        return; // unreachable, but helps compiler
    }
    catch (Exception ex)
    {
        configLogger.LogCritical(ex, "Failed to read/decrypt API key. Cannot start service.");
        Console.Error.WriteLine($"FATAL: Failed to read API key: {ex.Message}");
        Environment.Exit(1);
        return;
    }
}

// ============================================================================
// STEP 4: Apply ENV overrides for debugging
// ============================================================================
var apiUrl = Environment.GetEnvironmentVariable("TRAPD_API_URL");
string apiUrlSource;
if (!string.IsNullOrWhiteSpace(apiUrl))
{
    apiUrlSource = "env:TRAPD_API_URL";
    configLogger.LogWarning("Using API URL from environment variable: {ApiUrl}", apiUrl);
}
else
{
    apiUrl = trapdConfig.ApiUrl;
    apiUrlSource = "config.json";
}

var projectId = Environment.GetEnvironmentVariable("TRAPD_PROJECT_ID");
string projectIdSource;
if (!string.IsNullOrWhiteSpace(projectId))
{
    projectIdSource = "env:TRAPD_PROJECT_ID";
    configLogger.LogWarning("Using Project ID from environment variable: {ProjectId}", projectId);
}
else
{
    projectId = trapdConfig.ProjectId;
    projectIdSource = "config.json";
}

// Validate project_id is set
if (string.IsNullOrWhiteSpace(projectId))
{
    configLogger.LogCritical("project_id is not configured. Set it in config.json or via TRAPD_PROJECT_ID env var.");
    Console.Error.WriteLine("FATAL: project_id is not configured.");
    Environment.Exit(1);
    return;
}

// ============================================================================
// STEP 5: Configure paths based on DataDir
// ============================================================================
var dbPath = DataDir.GetQueueDbPath(dataDir);
var logPath = DataDir.GetLogPath(dataDir);
var deviceIdPath = DataDir.GetDeviceIdPath(dataDir);

// Store resolved values in configuration for later use
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["TRAPD_RESOLVED_DATA_DIR"] = dataDir,
    ["TRAPD_DB_PATH"] = dbPath,
    ["TRAPD_LOG_PATH"] = logPath,
    ["TRAPD_DEVICE_ID_PATH"] = deviceIdPath,
    ["TRAPD_API_URL"] = apiUrl,
    ["TRAPD_PROJECT_ID"] = projectId,
    ["TRAPD_INTERVAL_S"] = trapdConfig.IntervalSeconds.ToString(),
    ["TRAPD_BATCH_SIZE"] = trapdConfig.BatchSize.ToString(),
});

// ============================================================================
// STEP 6: Configure logging level from config
// ============================================================================
builder.Logging.SetMinimumLevel(trapdConfig.GetLogLevelEnum());

// ============================================================================
// STEP 7: Register Services
// ============================================================================
builder.Services.AddSingleton<InventoryCollector>();

// Register OfflineQueue
builder.Services.AddSingleton<OfflineQueue>(sp => 
    new OfflineQueue(dbPath));

// Register HttpClient and TrapdClient with resolved config
builder.Services.AddHttpClient("TrapdClient");
builder.Services.AddSingleton<TrapdClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var logger = sp.GetRequiredService<ILogger<TrapdClient>>();
    return new TrapdClient(
        factory.CreateClient("TrapdClient"),
        apiUrl,
        apiKey,
        logger
    );
});

// Register BatchSender
builder.Services.AddSingleton<BatchSender>(sp =>
{
    return new BatchSender(
        sp.GetRequiredService<OfflineQueue>(),
        sp.GetRequiredService<TrapdClient>(),
        logPath
    );
});

// Register Worker
builder.Services.AddHostedService<Worker>();

// Windows Service Lifetime
if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "TRAPD Agent";
    });
}

var host = builder.Build();

// ============================================================================
// Log startup info (never log API key)
// ============================================================================
var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("TRAPD Agent starting");
logger.LogInformation("  DataDir: {DataDir}", dataDir);
logger.LogInformation("  DB Path: {DbPath}", dbPath);
logger.LogInformation("  Log Path: {LogPath}", logPath);
logger.LogInformation("  API URL: {ApiUrl} (source: {Source})", apiUrl, apiUrlSource);
logger.LogInformation("  Project ID: {ProjectId} (source: {Source})", projectId, projectIdSource);
logger.LogInformation("  API Key: (source: {Source})", apiKeySource);
logger.LogInformation("  Interval: {Interval}s, BatchSize: {BatchSize}", trapdConfig.IntervalSeconds, trapdConfig.BatchSize);
logger.LogInformation("  Log Level: {LogLevel}", trapdConfig.LogLevel);

// ============================================================================
// Handle --once mode for integration testing
// ============================================================================
if (args.Contains("--once"))
{
    logger.LogInformation("Running in --once mode");
    
    var collector = host.Services.GetRequiredService<InventoryCollector>();
    var queue = host.Services.GetRequiredService<OfflineQueue>();
    var sender = host.Services.GetRequiredService<BatchSender>();
    var config = host.Services.GetRequiredService<IConfiguration>();

    var rawVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion 
                       ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() 
                       ?? "0.0.0";
    
    // Normalize to SemVer (MAJOR.MINOR.PATCH)
    var versionMatch = Regex.Match(rawVersion, @"^(\d+\.\d+\.\d+)");
    var agentVersion = versionMatch.Success ? versionMatch.Groups[1].Value : "0.0.0";

    // 1. Collect
    var inv = collector.Collect();
    
    // Resolve Sensor ID
    var envSensorId = Environment.GetEnvironmentVariable("TRAPD_SENSOR_ID");
    string sensorId;
    string source;

    if (!string.IsNullOrWhiteSpace(envSensorId))
    {
        sensorId = envSensorId;
        source = "env";
    }
    else
    {
        if (File.Exists(deviceIdPath))
        {
            try
            {
                sensorId = File.ReadAllText(deviceIdPath).Trim();
                source = "device_id_file";
            }
            catch
            {
                sensorId = Guid.NewGuid().ToString("N");
                source = "generated_fallback";
            }
        }
        else
        {
            sensorId = Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(deviceIdPath, sensorId);
                source = "generated_new";
            }
            catch
            {
                source = "generated_memory_only";
            }
        }
    }

    logger.LogInformation("Resolved SensorId={SensorId} Source={Source}", sensorId, source);

    var evt = new
    {
        sensor_id = sensorId,
        project_id = projectId,
        ts = DateTimeOffset.UtcNow.ToString("O"),
        kind = "heartbeat",
        message = "agent alive (once)",
        host = new
        {
            hostname = inv.Hostname,
            fqdn = inv.Fqdn,
            os = inv.Os,
            os_version = inv.OsVersion,
            arch = inv.Arch,
            primary_ip = inv.PrimaryIp,
            ip_addrs = inv.IpAddrs,
            domain = inv.Domain,
            joined = inv.Joined,
            aad_joined = inv.AadJoined
        },
        agent = new
        {
            version = agentVersion
        }
    };

    // 2. Enqueue
    var id = queue.Enqueue("heartbeat", evt);
    logger.LogInformation("Enqueued heartbeat id={Id}", id);

    // 3. Send
    await sender.RunOnceAsync(CancellationToken.None);
    logger.LogInformation("Send attempt complete.");
    
    return; // Exit
}

await host.RunAsync();