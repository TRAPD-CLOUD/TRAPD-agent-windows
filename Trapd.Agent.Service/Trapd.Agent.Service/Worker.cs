using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Trapd.Agent.Service;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    public Worker(ILogger<Worker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var logPath = @"C:\ProgramData\TRAPD\agent.log";
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var dbPath = @"C:\ProgramData\TRAPD\queue.db";
        var queue = new OfflineQueue(dbPath);

        // Batch-Sender (simuliert erstmal nur "Senden" + ack)
        var endpoint = "http://localhost:8080";
        var apiKey = "trapd_dev_agent_key_local";

        using var http = new HttpClient();
        var client = new TrapdClient(http, endpoint, apiKey);

        var sender = new BatchSender(queue, client, logPath);

        _logger.LogInformation("TRAPD Agent started at: {time}", DateTimeOffset.Now);
        await File.AppendAllTextAsync(logPath, $"started {DateTimeOffset.Now:O}{Environment.NewLine}", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("TRAPD Agent heartbeat: {time}", DateTimeOffset.Now);
            await File.AppendAllTextAsync(logPath, $"heartbeat {DateTimeOffset.Now:O}{Environment.NewLine}", stoppingToken);

            // Event erzeugen und in Offline-Queue schreiben
            var evt = new
            {
                sensor_id = Environment.MachineName,
                project_id = "p-1",
                ts = DateTimeOffset.UtcNow.ToString("O"),
                kind = "heartbeat",
                message = "agent alive"
            };


            var id = queue.Enqueue("heartbeat", evt);
            await File.AppendAllTextAsync(logPath, $"enqueued id={id}{Environment.NewLine}", stoppingToken);

            // Batch aus Queue holen + "senden" simulieren + als sent markieren
            await sender.RunOnceAsync(stoppingToken);

            // Pause am Ende, damit enqueue + dequeue direkt passieren
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
