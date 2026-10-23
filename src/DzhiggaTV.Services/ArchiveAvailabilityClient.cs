using DzhiggaTV.Core;

namespace DzhiggaTV.Services;

/// <summary>Small bounded read before replacing native playback. Uncertain network/format results defer to libmpv.</summary>
public sealed class ArchiveAvailabilityClient
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3) }) { Timeout = Timeout.InfiniteTimeSpan };
    public async Task<ArchiveProbeResult> CheckAsync(Channel channel, string url, CancellationToken token)
    {
        var parts = url.Split('|', 2); if (!Uri.TryCreate(parts[0], UriKind.Absolute, out var initial) || initial.Scheme is not ("http" or "https")) return new(ArchiveAvailability.Unknown);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in channel.Attributes ?? new Dictionary<string, string>())
        {
            var name = key.ToLowerInvariant() switch { "http-user-agent" or "user-agent" => "User-Agent", "http-referrer" or "http-referer" or "referer" => "Referer", "http-origin" => "Origin", _ => null };
            if (name is not null && !value.Contains('\r') && !value.Contains('\n')) headers[name] = value;
        }
        if (parts.Length > 1) foreach (var pair in parts[1].Split('&'))
        {
            var values = pair.Split('=', 2); if (values.Length != 2) continue;
            var name = values[0].ToLowerInvariant() switch { "user-agent" => "User-Agent", "referer" => "Referer", "origin" => "Origin", "cookie" => "Cookie", "authorization" => "Authorization", _ => null };
            string value = Uri.UnescapeDataString(values[1]);
            if (name is not null && !value.Contains('\r') && !value.Contains('\n')) headers[name] = value;
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await ArchiveProbe.CheckAsync(initial, async (uri, cancellation) =>
            {
                for (int redirects = 0; redirects < 5; redirects++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("DzhiggaTV/Archive"); request.Headers.ConnectionClose = true;
                    foreach (var (name, value) in headers)
                        if (uri.GetLeftPart(UriPartial.Authority) == initial.GetLeftPart(UriPartial.Authority) || name is not ("Cookie" or "Authorization" or "cookie" or "authorization"))
                        { request.Headers.Remove(name); request.Headers.TryAddWithoutValidation(name, value); }
                    using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                    if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location && Uri.TryCreate(uri, location, out var next) && next.Scheme is "http" or "https") { uri = next; continue; }
                    return await ArchiveProbe.ReadResponseAsync(response, uri, cancellation).ConfigureAwait(false);
                }
                return new(uri, 0, null, false);
            }, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(ArchiveAvailability.Unknown); }
        catch (Exception e) when (e is HttpRequestException or IOException) { return new(ArchiveAvailability.Unknown); }
    }
}
