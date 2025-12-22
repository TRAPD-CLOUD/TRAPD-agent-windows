using System.IO;

namespace Trapd.Agent.Service;

public sealed class BatchSender
{
    private readonly OfflineQueue _queue;
    private readonly TrapdClient _client;
    private readonly string _logPath;

    // Einfacher Backoff-Status
    private int _failures = 0;

    public BatchSender(OfflineQueue queue, TrapdClient client, string logPath)
    {
        _queue = queue;
        _client = client;
        _logPath = logPath;
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var batch = _queue.LeaseBatch(batchSize: 100, leaseFor: TimeSpan.FromMinutes(5));
        if (batch.Count == 0)
            return;

        try
        {
            await File.AppendAllTextAsync(_logPath,
                $"upload attempt count={batch.Count} ids=[{string.Join(",", batch.Select(x => x.Id))}]{Environment.NewLine}", ct);

            var ok = await _client.SendBatchAsync(batch, ct);

            if (ok)
            {
                _queue.MarkSent(batch.Select(x => x.Id));
                _failures = 0;

                await File.AppendAllTextAsync(_logPath,
                    $"upload ok, marked sent ids=[{string.Join(",", batch.Select(x => x.Id))}]{Environment.NewLine}", ct);
            }
        }
        catch (Exception ex)
        {
            _failures++;

            await File.AppendAllTextAsync(_logPath,
                $"upload failed (failures={_failures}): {ex.Message}{Environment.NewLine}", ct);

            // Backoff: 1s,2s,4s,... max 60s
            var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(_failures, 6))));
            await File.AppendAllTextAsync(_logPath,
                $"backoff {delay.TotalSeconds:0}s{Environment.NewLine}", ct);

            await Task.Delay(delay, ct);
            // KEIN MarkSent => bleibt in Queue und wird später erneut versucht
        }
    }
}
