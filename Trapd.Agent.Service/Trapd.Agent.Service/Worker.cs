using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Trapd.Agent.Service;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly InventoryCollector _collector;
    private readonly OfflineQueue _queue;
    private readonly BatchSender _sender;
    private readonly IConfiguration _config;
    private readonly string _logPath;
    private readonly string _deviceIdPath;
    private readonly Random _jitter = new();

    public Worker(
        ILogger<Worker> logger,
        InventoryCollector collector,
        OfflineQueue queue,
        BatchSender sender,
        IConfiguration config)
    {
        _logger = logger;
        _collector = collector;
        _queue = queue;
        _sender = sender;
        _config = config;
        _logPath = _config["TRAPD_LOG_PATH"]!;
        _deviceIdPath = _config["TRAPD_DEVICE_ID_PATH"] 
            ?? Path.Combine(_config["TRAPD_RESOLVED_DATA_DIR"]!, "device_id.txt");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ensure log directory exists
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            await File.AppendAllTextAsync(_logPath, $"TRAPD Agent started at: {DateTimeOffset.Now}\n", stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write to log file at {LogPath}. Continuing without file logging.", _logPath);
        }

        var intervalSeconds = _config.GetValue<int>("TRAPD_INTERVAL_S");
        if (intervalSeconds <= 0) intervalSeconds = 60;

        var projectId = _config["TRAPD_PROJECT_ID"] ?? "unknown";
        
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
            if (File.Exists(_deviceIdPath))
            {
                try
                {
                    sensorId = File.ReadAllText(_deviceIdPath).Trim();
                    source = "device_id_file";
                }
                catch
                {
                    // Fallback if read fails
                    sensorId = Guid.NewGuid().ToString("N");
                    source = "generated_fallback";
                }
            }
            else
            {
                sensorId = Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(_deviceIdPath, sensorId);
                    source = "generated_new";
                }
                catch
                {
                    source = "generated_memory_only";
                }
            }
        }

        _logger.LogInformation("Resolved SensorId={SensorId} Source={Source}", sensorId, source);

        var rawVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion 
                           ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() 
                           ?? "0.0.0";
        
        // Normalize to SemVer (MAJOR.MINOR.PATCH)
        var versionMatch = Regex.Match(rawVersion, @"^(\d+\.\d+\.\d+)");
        var agentVersion = versionMatch.Success ? versionMatch.Groups[1].Value : "0.0.0";

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("TRAPD Agent heartbeat: {time}", DateTimeOffset.Now);

                // 1. Collect Inventory
                var inv = _collector.Collect();
                
                // 2. Build Event Payload with enhanced fields
                var evt = new
                {
                    sensor_id = sensorId,
                    project_id = projectId,
                    ts = DateTimeOffset.UtcNow.ToString("O"),
                    kind = "heartbeat",
                    message = "agent alive",
                    host = new
                    {
                        hostname = inv.Hostname,
                        fqdn = inv.Fqdn,
                        os = inv.Os,
                        os_version = inv.OsVersion,
                        os_build = inv.OsBuild,
                        arch = inv.Arch,
                        primary_ip = inv.PrimaryIp,
                        ip_addrs = inv.IpAddrs,
                        mac_addrs = inv.MacAddrs,
                        timezone = inv.Timezone,
                        boot_time = inv.BootTime,
                        uptime_seconds = inv.SystemUptimeSeconds
                    },
                    agent = new
                    {
                        version = agentVersion,
                        uptime_seconds = inv.AgentUptimeSeconds,
                        last_restart = inv.AgentLastRestart
                    },
                    hardware = inv.Hardware != null ? new
                    {
                        cpu_model = inv.Hardware.CpuModel,
                        cpu_cores = inv.Hardware.CpuCores,
                        ram_total_gb = inv.Hardware.RamTotalGb,
                        disk_total_gb = inv.Hardware.DiskTotalGb,
                        disk_free_gb = inv.Hardware.DiskFreeGb
                    } : null,
                    identity = new
                    {
                        domain = inv.Domain,
                        joined = inv.Joined,
                        aad_joined = inv.AadJoined
                    }
                };

                // 3. Enqueue
                var id = _queue.Enqueue("heartbeat", evt);
                await SafeAppendLogAsync($"enqueued id={id} kind=heartbeat{Environment.NewLine}", stoppingToken);

                // 4. Attempt to Send (Drain Queue)
                await _sender.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error in worker loop");
                await SafeAppendLogAsync($"error: {ex.Message}{Environment.NewLine}", stoppingToken);
                
                // Backoff on error to avoid tight loop
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }

            // Wait for next interval with jitter +/- 10%
            var jitterPercent = (_jitter.NextDouble() * 0.2) - 0.1; // -0.1 to +0.1
            var delaySeconds = intervalSeconds * (1 + jitterPercent);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }

    private async Task SafeAppendLogAsync(string message, CancellationToken ct)
    {
        try
        {
            await File.AppendAllTextAsync(_logPath, message, ct);
        }
        catch
        {
            // Ignore logging errors
        }
    }
}