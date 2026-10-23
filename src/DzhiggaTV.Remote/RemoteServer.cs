using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DzhiggaTV.Remote;

public sealed class RemoteServer : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly IRemoteLibrary _library;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Start, int Count)> _rates = [];
    private readonly ConcurrentDictionary<string, int> _sockets = [];
    private readonly Func<string, RemoteAddress> _select;
    private RemoteAddress? _address;
    private DateTimeOffset _profileChecked;
    private bool _profilePrivate;
    private readonly SemaphoreSlim _profileGate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public RemoteOptions Options { get; private set; } = new();
    public DeviceRegistry Devices { get; }
    public RemoteStreams Streams { get; }
    public string? Url { get; private set; }
    public bool Running => _app is not null;
    public string Status { get; private set; } = "Remote выключен";
    public string? LastPlaybackError { get; private set; }
    public event Action? Changed;

    public RemoteServer(IRemoteLibrary library, string directory, Func<string, RemoteAddress>? select = null, SourcePolicy? sourcePolicy = null)
    {
        _library = library; _select = select ?? RemoteNetwork.Select;
        Devices = new(directory); Streams = new(library, () => Options, directory, sourcePolicy);
        Devices.Changed += () => { foreach (var stream in Streams.Active) if (!Devices.Exists(stream.DeviceId)) Streams.Stop(stream.DeviceId); Changed?.Invoke(); };
    }
    public async Task StartAsync(RemoteOptions options, CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            await StopCoreAsync(); options.Validate();
            _address = await Task.Run(() => _select(options.Address), token); Options = options;
            if (!_address.Private) throw new RemoteException("Запуск Remote в публичной или неизвестной сети запрещён. Выберите домашнюю частную сеть.");
            _profilePrivate = _address.Private; _profileChecked = DateTimeOffset.UtcNow;
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(RemoteServer).Assembly.GetName().Name, ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders(); // Request paths, pairing codes and upstream URLs are deliberately never logged.
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(IPAddress.Parse(_address.Address), options.Port);
                k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 16384; k.Limits.MaxConcurrentConnections = 64; k.Limits.MaxConcurrentUpgradedConnections = 24;
                k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10); k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(40);
            });
            var app = builder.Build(); Url = $"http://{_address.Address}:{options.Port}";
            app.UseWebSockets(new() { KeepAliveInterval = TimeSpan.FromSeconds(10) });
            app.Use(HandleAsync); Map(app);
            try { await app.StartAsync(token); _app = app; Status = "Remote работает"; }
            catch { await app.DisposeAsync(); Url = null; throw new RemoteException("Не удалось открыть порт. Выберите свободный порт и проверьте выбранный сетевой адаптер."); }
        }
        catch (RemoteException e) { Status = e.Message; throw; }
        finally { _lifecycle.Release(); Changed?.Invoke(); }
    }
    public string PairingUrl() => Running ? Url + "/#pair=" + Devices.RotateCode() : throw new RemoteException("Сначала включите Remote.");
    public async Task StopAsync() { await _lifecycle.WaitAsync(); try { await StopCoreAsync(); } finally { _lifecycle.Release(); Changed?.Invoke(); } }
    private async Task StopCoreAsync()
    {
        var app = _app; _app = null; Devices.ClearPending(); Streams.Stop(); Url = null; _rates.Clear();
        if (app is not null) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4)); try { await app.StopAsync(timeout.Token); } catch (OperationCanceledException) { } await app.DisposeAsync(); }
        Status = "Remote выключен";
    }
    private async Task HandleAsync(HttpContext c, RequestDelegate next)
    {
        c.Response.Headers.CacheControl = "no-store";
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        c.Response.Headers["Referrer-Policy"] = "no-referrer";
        c.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        c.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; media-src 'self' http: https: blob:; connect-src 'self' ws:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        try
        {
            if (_address is null || c.Connection.RemoteIpAddress is not { } ip || !_address.Contains(ip) || c.Request.Host.Value != new Uri(Url!).Authority) throw new RemoteException("Доступ разрешён только из выбранной домашней сети.", 403);
            await CheckProfileAsync();
            if (!_profilePrivate) throw new RemoteException("Сеть Windows больше не является частной. Остановите Remote и проверьте сетевой профиль.", 403);
            string path = c.Request.Path;
            var origin = c.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && origin != Url || c.Request.Headers["Sec-Fetch-Site"] == "cross-site") throw new RemoteException("Запрос с другого сайта запрещён.", 403);
            bool mutation = !HttpMethods.IsGet(c.Request.Method) && !HttpMethods.IsHead(c.Request.Method);
            if (mutation && (origin != Url || !c.Request.HasJsonContentType())) throw new RemoteException("Не удалось проверить источник команды. Откройте Remote по адресу с QR-кода.", 403);
            Rate(ip + ":all", 6000);
            if (path == "/api/pair" && mutation) Rate(ip + ":pair", 8);
            if (path == "/api/command") Rate(ip + ":command", 90);
            if (path == "/api/catalog" || path.StartsWith("/api/guide/", StringComparison.Ordinal)) Rate(ip + ":library", 180);
            if (path == "/api/stream") Rate(ip + ":stream", 40);
            if (path.StartsWith("/encoder/", StringComparison.Ordinal))
            {
                var parts = path.Split('/');
                if (!ip.Equals(c.Connection.LocalIpAddress) || parts.Length != 4 || Streams.EncoderDevice(parts[2], c.Request.Headers["X-DzhiggaTV-Encoder"].ToString()) is not { } encoderDevice || !Devices.Exists(encoderDevice)) throw new RemoteException("Доступ к преобразованию не подтверждён.", 401);
                c.Items["encoderDevice"] = encoderDevice;
            }
            bool requiresAuth = path.StartsWith("/api/", StringComparison.Ordinal) && path is not ("/api/status" or "/api/pair" or "/api/pair/status") || path.StartsWith("/media/", StringComparison.Ordinal) || path.StartsWith("/converted/", StringComparison.Ordinal) || path == "/events";
            if (requiresAuth)
            {
                var device = Devices.Authenticate(c.Request.Cookies[DeviceRegistry.CookieName]) ?? throw new RemoteException("Подключите устройство с помощью QR-кода на компьютере.", 401);
                c.Items["device"] = device;
                if (mutation && c.Request.Headers["X-DzhiggaTV-CSRF"] != device.Csrf) throw new RemoteException("Обновите страницу Remote и повторите команду.", 403);
                if (path == "/events" && origin != Url) throw new RemoteException("Источник подключения не подтверждён.", 403);
            }
            await next(c);
        }
        catch (Exception e) when (!c.Response.HasStarted)
        {
            c.Response.StatusCode = e is RemoteException known ? known.Status : e is OperationCanceledException ? 408 : 502;
            var error = e is RemoteException re ? re.Message : "Не удалось выполнить запрос. Проверьте подключение к компьютеру и доступность IPTV-провайдера.";
            if (c.Request.Path.StartsWithSegments("/media") || c.Request.Path.StartsWithSegments("/converted") || c.Request.Path == "/api/stream") LastPlaybackError = $"{DateTime.Now:HH:mm}: {error}";
            await c.Response.WriteAsJsonAsync(new { error }, CancellationToken.None);
        }
        catch (Exception) { c.Abort(); }
    }
    private async Task CheckProfileAsync()
    {
        if (DateTimeOffset.UtcNow - _profileChecked < TimeSpan.FromSeconds(15)) return;
        await _profileGate.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow - _profileChecked < TimeSpan.FromSeconds(15)) return;
            try { _profilePrivate = (await Task.Run(() => _select(_address!.Address))).Private; } catch { _profilePrivate = false; }
            _profileChecked = DateTimeOffset.UtcNow;
            if (!_profilePrivate) { Streams.Stop(); Status = "Доступ остановлен: сеть больше не частная"; Changed?.Invoke(); }
        }
        finally { _profileGate.Release(); }
    }
    private void Map(WebApplication app)
    {
        app.MapGet("/api/status", () => new { application = "DzhiggaTV Remote", version = "2.0", pairing = true });
        app.MapPost("/api/pair", async (HttpContext c) =>
        {
            var body = await c.Request.ReadFromJsonAsync<PairBody>(c.RequestAborted) ?? throw new RemoteException("Пустой запрос.");
            return Devices.Begin(body.Code, body.Name);
        });
        app.MapGet("/api/pair/status", (HttpContext c) =>
        {
            var result = Devices.Poll(c.Request.Query["id"].ToString());
            if (result.Credential is { } credential) c.Response.Cookies.Append(DeviceRegistry.CookieName, credential, new() { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = c.Request.IsHttps, Path = "/", MaxAge = TimeSpan.FromDays(30), IsEssential = true });
            return new { status = result.Status };
        });
        app.MapGet("/api/session", (HttpContext c) => new { name = Device(c).Name, csrf = Device(c).Csrf, policy = Policy(), serverTime = DateTimeOffset.UtcNow });
        app.MapGet("/api/catalog", async (HttpContext c) => { return Results.Ok(await _library.CatalogAsync(new(c.Request.Query["search"].ToString()[..Math.Min(120, c.Request.Query["search"].ToString().Length)], c.Request.Query["category"].ToString(), c.Request.Query["playlist"].ToString(), c.Request.Query["collection"].ToString(), c.Request.Query["sort"].ToString(), Number(c, "offset", 0, 0, 100000), Number(c, "limit", 70, 1, 100), c.Request.Query["ids"].ToString()), c.RequestAborted)); });
        app.MapGet("/api/guide/{id}", (string id, HttpContext c) =>
        {
            var at = DateTimeOffset.TryParse(c.Request.Query["from"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date : DateTimeOffset.UtcNow;
            if (Math.Abs((DateTimeOffset.UtcNow - at).TotalDays) > 14) throw new RemoteException("Выберите дату в пределах двух недель.");
            return _library.GuideAsync(id, at, c.RequestAborted);
        });
        app.MapGet("/api/logo/{id}", async (string id, HttpContext c) =>
        {
            var path = await _library.LogoAsync(id, c.RequestAborted);
            if (path is null || !File.Exists(path)) return Results.NotFound();
            var type = await RemoteImage.ContentTypeAsync(path, c.RequestAborted);
            if (type is null) return Results.NotFound();
            c.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(path, type);
        });
        app.MapGet("/api/state", async (HttpContext c) => { return Results.Ok(await _library.StateAsync(c.RequestAborted)); });
        app.MapPost("/api/command", async (HttpContext c) =>
        {
            var command = await c.Request.ReadFromJsonAsync<RemoteCommand>(c.RequestAborted) ?? throw new RemoteException("Пустая команда.");
            Authorize(command);
            if (!await _commands.WaitAsync(0, c.RequestAborted)) throw new RemoteException("Предыдущая команда ещё выполняется.", 409);
            try { await _library.CommandAsync(command, c.RequestAborted); } finally { _commands.Release(); }
            return await _library.StateAsync(c.RequestAborted);
        });
        app.MapPost("/api/stream", async (HttpContext c) =>
        {
            var body = await c.Request.ReadFromJsonAsync<StreamBody>(c.RequestAborted) ?? throw new RemoteException("Выберите канал.");
            return await Streams.StartAsync(Device(c).Id, body.ChannelId, body.Transcode, Url!, c.RequestAborted, body.Compatible, body.ProgrammeStart);
        });
        app.MapPost("/api/stream/stop", (HttpContext c) => { Streams.Stop(Device(c).Id); return Results.Ok(); });
        app.MapPost("/api/stream/ping", (HttpContext c) => { Streams.Touch(Device(c).Id); return Results.Ok(); });
        app.MapPost("/api/logout", (HttpContext c) => { Devices.Revoke(Device(c).Id); c.Response.Cookies.Delete(DeviceRegistry.CookieName); return Results.Ok(); });
        app.MapGet("/media/{id}/{resource}", (string id, string resource, HttpContext c) => Streams.ProxyAsync(c, Device(c).Id, id, resource));
        app.MapGet("/encoder/{id}/{resource}", (string id, string resource, HttpContext c) => Streams.ProxyAsync(c, (string)c.Items["encoderDevice"]!, id, resource, true));
        app.MapGet("/converted/{id}/{file}", (string id, string file, HttpContext c) => Streams.ConvertedAsync(c, Device(c).Id, id, file));
        app.MapGet("/events", EventsAsync);
        app.MapGet("/{**path}", (string? path) => Asset(path ?? "index.html"));
    }
    private void Authorize(RemoteCommand command)
    {
        bool favorites = command.Action is "favorite" or "collection.add" or "collection.remove";
        if (favorites ? !Options.AllowFavorites : !Options.AllowControl) throw new RemoteException("Эта функция отключена в настройках Remote на ПК.", 403);
        if (command.Action is "volume" or "mute" && !Options.AllowVolume) throw new RemoteException("Управление звуком отключено на ПК.", 403);
        if (command.Action.StartsWith("multiscreen.", StringComparison.Ordinal) && !Options.AllowMultiscreen) throw new RemoteException("Управление мультиэкраном отключено на ПК.", 403);
    }
    private RemotePolicy Policy() => new(Options.AllowViewing, Options.AllowControl, Options.AllowVolume, Options.AllowMultiscreen, Options.AllowFavorites, Options.AllowDirectUrls, Options.AllowTranscoding, Options.MaximumStreams);
    private async Task EventsAsync(HttpContext c)
    {
        var id = Device(c).Id;
        if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
        if (_sockets.AddOrUpdate(id, 1, (_, n) => n + 1) > 2) { _sockets.AddOrUpdate(id, 0, (_, n) => n - 1); throw new RemoteException("Закройте лишние вкладки Remote.", 429); }
        using var socket = await c.WebSockets.AcceptWebSocketAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted);
        var credential = c.Request.Cookies[DeviceRegistry.CookieName];
        var receive = ReceiveAsync(socket, () => Devices.Authenticate(credential), lifetime.Token);
        try
        {
            string? previous = null;
            while (socket.State == WebSocketState.Open && !receive.IsCompleted && Devices.Exists(id) && Running)
            {
                await CheckProfileAsync(); if (!_profilePrivate) break;
                if (Devices.Devices.FirstOrDefault(d => d.Id == id) is not { } device || DateTimeOffset.UtcNow - device.LastSeen > TimeSpan.FromSeconds(40)) break;
                var state = JsonSerializer.Serialize(await _library.StateAsync(lifetime.Token), Json);
                if (state != previous) { await socket.SendAsync(Encoding.UTF8.GetBytes(state), WebSocketMessageType.Text, true, lifetime.Token); previous = state; }
                await Task.Delay(1000, lifetime.Token);
            }
            if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Подключение завершено", CancellationToken.None);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
        finally { lifetime.Cancel(); try { await receive; } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { } _sockets.AddOrUpdate(id, 0, (_, n) => Math.Max(0, n - 1)); }
    }
    private static async Task ReceiveAsync(WebSocket socket, Action touch, CancellationToken token)
    {
        var buffer = new byte[128];
        while (socket.State == WebSocketState.Open)
        {
            var message = await socket.ReceiveAsync(buffer, token); if (message.MessageType == WebSocketMessageType.Close) break;
            if (message.MessageType != WebSocketMessageType.Text || !message.EndOfMessage || message.Count != 4 || !buffer.AsSpan(0, 4).SequenceEqual("ping"u8)) throw new WebSocketException("Only heartbeat messages are accepted.");
            touch();
        }
    }
    private void Rate(string key, int maximum)
    {
        var now = DateTimeOffset.UtcNow;
        var value = _rates.AddOrUpdate(key, (now, 1), (_, p) => now - p.Start > TimeSpan.FromMinutes(1) ? (now, 1) : (p.Start, p.Count + 1));
        if (_rates.Count > 1024) foreach (var p in _rates.Where(p => now - p.Value.Start > TimeSpan.FromMinutes(1))) _rates.TryRemove(p.Key, out _);
        if (value.Count > maximum) throw new RemoteException("Слишком много запросов. Подождите минуту.", 429);
    }
    private static DeviceRegistry.Device Device(HttpContext c) => (DeviceRegistry.Device)c.Items["device"]!;
    private static int Number(HttpContext c, string key, int fallback, int min, int max) => int.TryParse(c.Request.Query[key], out var value) ? Math.Clamp(value, min, max) : fallback;
    private static IResult Asset(string path)
    {
        if (path.Length == 0) path = "index.html";
        var name = "DzhiggaTV.Remote.Web.dist." + path.Replace('/', '.');
        var stream = typeof(RemoteServer).Assembly.GetManifestResourceStream(name);
        if (stream is null) return Results.NotFound();
        var type = Path.GetExtension(path) switch { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8", ".json" or ".webmanifest" => "application/manifest+json", ".png" => "image/png", ".svg" => "image/svg+xml", ".mp4" => "video/mp4", _ => "application/octet-stream" };
        return Results.Stream(stream, type, enableRangeProcessing: type == "video/mp4");
    }
    public async ValueTask DisposeAsync() { await StopAsync(); Streams.Dispose(); }
    private sealed record PairBody(string Code, string Name);
    private sealed record StreamBody(string ChannelId, bool Transcode = false, bool Compatible = false, DateTimeOffset? ProgrammeStart = null);
}
