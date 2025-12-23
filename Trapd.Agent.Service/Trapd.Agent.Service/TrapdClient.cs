using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Trapd.Agent.Service;

public sealed class TrapdClient
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly ILogger<TrapdClient> _logger;
    private bool _firstPayloadLogged;

    public TrapdClient(HttpClient http, string endpoint, string apiKey, ILogger<TrapdClient> logger)
    {
        _http = http;
        _endpoint = endpoint.TrimEnd('/');
        _logger = logger;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TRAPD-Agent/0.1");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<bool> SendBatchAsync(IReadOnlyList<QueuedItem> batch, CancellationToken ct)
    {
        // Payload: minimal, später TRAPD Schema / compression etc.
        var dto = batch.Select(x => new
        {
            id = x.Id,
            created_utc = x.CreatedUtc,
            type = x.Type,
            payload = JsonSerializer.Deserialize<JsonElement>(x.PayloadJson)
        });

        var json = JsonSerializer.Serialize(dto);

        // Log payload once per run (INFO), otherwise DEBUG
        if (!_firstPayloadLogged)
        {
            _logger.LogInformation("First Batch Payload (len={Len}): {Json}", json.Length, json);
            _firstPayloadLogged = true;
        }
        else
        {
            _logger.LogDebug("Batch Payload (len={Len}): {Json}", json.Length, json);
        }

        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.PostAsync($"{_endpoint}/api/v1/events/batch", content, ct);
        var body = await SafeReadBody(resp, ct);

        // Log full response body
        _logger.LogInformation("API Response {StatusCode} {Reason}: {Body}", resp.StatusCode, resp.ReasonPhrase, body);

        // 2xx => OK
        if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300)
            return true;

        // Alles andere => Fehler (nicht ack!)
        throw new HttpRequestException($"Batch upload failed: {(int)resp.StatusCode} {resp.ReasonPhrase}; body={body}");
    }

    private static async Task<string> SafeReadBody(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return "<unreadable>";
        }
    }
}
