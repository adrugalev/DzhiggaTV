using System.Net;

namespace DzhiggaTV.Services;

internal sealed class RemoteFiles : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    public RemoteFiles(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<HttpResponseMessage> OpenAsync(Uri source, bool allowInsecureHttp, CancellationToken cancellationToken)
    {
        var uri = source;
        bool usedPublicHttp = false;
        for (int redirects = 0; redirects < 8; redirects++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (uri.Scheme is not ("http" or "https")) throw new InvalidDataException("Поддерживаются только HTTP и HTTPS.");
            // EPG.ONE publishes public metadata and icons over HTTP, and its HTTPS entry point
            // redirects to that mirror. The exception is exact-host/default-port only: a redirect
            // must never grant a third party the mirror's HTTP permission.
            if (usedPublicHttp && !allowInsecureHttp && !IsPublicEpgOneServer(uri))
                throw new RemoteFileException("Сервер EPG.ONE перенаправил запрос за пределы разрешённых публичных серверов.");
            if (uri.Scheme == "http" && !allowInsecureHttp && !IsPublicEpgOneServer(uri))
            {
                if (redirects != 0) throw new RemoteFileException("Сторонний сервер требует незащищённый HTTP. Разрешите HTTP для сторонних источников EPG в настройках.");
                uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
            }
            usedPublicHttp |= uri.Scheme == "http" && !allowInsecureHttp;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("DzhiggaTV/1.0");
            using var headerDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            HttpResponseMessage response;
            try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerDeadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new RemoteFileException($"Сервер EPG или логотипов ({uri.IdnHost}) не ответил за 30 секунд."); }
            catch (HttpRequestException exception)
            { throw new RemoteFileException($"Не удалось подключиться к серверу EPG или логотипов ({uri.IdnHost}).", exception); }

            // Injected clients should disable automatic redirects, as the owned client does.
            // Still check their final address so a rejected HTTP result cannot enter the cache.
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            if ((!allowInsecureHttp && finalUri.Scheme == "http" && !IsPublicEpgOneServer(finalUri))
                || (usedPublicHttp && !allowInsecureHttp && !IsPublicEpgOneServer(finalUri)))
            { response.Dispose(); throw new RemoteFileException("Сервер перенаправил запрос на неразрешённый адрес EPG или логотипов."); }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new RemoteFileException("Сервер EPG или логотипов не указал адрес переадресации.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode) { var code = (int)response.StatusCode; response.Dispose(); throw new RemoteFileException($"Сервер EPG или логотипов вернул HTTP {code}."); }
            return response;
        }
        throw new RemoteFileException("Слишком много переадресаций EPG или логотипов.");
    }

    private static bool IsPublicEpgOneServer(Uri uri) => uri.Scheme is "http" or "https"
        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.IdnHost.Equals("epg.one", StringComparison.OrdinalIgnoreCase) || uri.IdnHost.Equals("ru.epg.one", StringComparison.OrdinalIgnoreCase));

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}

/// <summary>A transport failure whose text is safe to show without exposing source URL parameters.</summary>
internal sealed class RemoteFileException(string message, Exception? inner = null) : HttpRequestException(message, inner);
