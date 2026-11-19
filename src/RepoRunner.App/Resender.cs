using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;

namespace RepoRunner.App;

/// <summary>What came back when a captured call was sent again.</summary>
public sealed record ResendResult(DateTime At, int? Status, string? ContentType, string? Body, double Ms, string? Error, string? SentBody, bool Edited);

/// <summary>
/// Sends a captured call again, to the address it originally went to, with the headers it was recorded with.
/// The hosts are the app's own development services, often addressed by IP with a certificate that cannot match,
/// so certificates are not checked here, as the stand-in does for replay.
/// </summary>
public sealed class Resender : IDisposable
{
    private const int MaxBody = 2_000_000;

    private readonly HttpClient _http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        AllowAutoRedirect = false,
        UseCookies = false,
    }) { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<ResendResult> SendAsync(string method, string url, string? body, string? contentType,
        IReadOnlyDictionary<string, string>? headers, bool edited, CancellationToken cancel = default)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            var recordedType = headers?.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrEmpty(body))
            {
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
                var type = recordedType ?? (contentType is null ? "application/json" : $"{contentType}; charset=utf-8");
                if (MediaTypeHeaderValue.TryParse(type, out var parsed)) request.Content.Headers.ContentType = parsed;
            }
            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            {
                if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
            var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxBody));
            return new ResendResult(DateTime.Now, (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType,
                text, clock.Elapsed.TotalMilliseconds, null, body, edited);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            var reason = ex is TaskCanceledException && !cancel.IsCancellationRequested ? "no answer within 60 seconds" : ex.Message;
            return new ResendResult(DateTime.Now, null, null, null, clock.Elapsed.TotalMilliseconds, reason, body, edited);
        }
    }

    public void Dispose() => _http.Dispose();
}
