using DzhiggaTV.Core;

namespace DzhiggaTV.Remote;

public sealed record RemoteOptions
{
    public bool AutoStart { get; init; }
    public string Address { get; init; } = "";
    public int Port { get; init; } = 8765;
    public bool AllowViewing { get; init; } = true;
    public int MaximumStreams { get; init; } = 2;
    public string Delivery { get; init; } = "proxy";
    public bool AllowDirectUrls { get; init; }
    public bool AllowControl { get; init; } = true;
    public bool AllowVolume { get; init; } = true;
    public bool AllowMultiscreen { get; init; } = true;
    public bool AllowFavorites { get; init; } = true;
    public bool AllowTranscoding { get; init; }
    public string FfmpegPath { get; init; } = "";
    public bool DependenciesPrepared { get; init; }
    public int TranscodeHeight { get; init; } = 720;
    public void Validate(bool requireFfmpeg = true)
    {
        if (Port is < 1024 or > 65535 || MaximumStreams is < 1 or > 4 ||
            Delivery is not ("proxy" or "auto" or "direct") || TranscodeHeight is not (480 or 720 or 1080))
            throw new RemoteException("Проверьте порт (1024–65535), число потоков (1–4) и режим передачи.");
        if (Delivery == "direct" && !AllowDirectUrls) throw new RemoteException("Для прямого режима разрешите передачу исходного адреса потока доверенному телефону.");
        if (requireFfmpeg && AllowTranscoding && (!File.Exists(FfmpegPath) || !Path.IsPathFullyQualified(FfmpegPath)))
            throw new RemoteException("Выберите установленный ffmpeg.exe, чтобы включить преобразование видео.");
    }
}

public sealed class RemoteException(string message, int status = 400) : Exception(message)
{ public int Status { get; } = status; }
public sealed record CatalogQuery(string Search = "", string Category = "", string Playlist = "", string Collection = "", string Sort = "playlist", int Offset = 0, int Limit = 70, string Ids = "");
public sealed record RemoteProgramme(string Title, DateTimeOffset Start, DateTimeOffset End, string? Description, string? Category)
{ public static RemoteProgramme? From(Programme? p) => p is null ? null : new(p.Title, p.Start, p.End, p.Description, p.Category); }
public sealed record RemoteChannel(string Id, string Name, string Category, bool Favorite, int Number, RemoteProgramme? Current, RemoteProgramme? Next, RemoteProgramme? Match = null);
public sealed record RemoteChoice(string Id, string Name);
public sealed record RemoteCatalog(IReadOnlyList<RemoteChannel> Channels, int Total, IReadOnlyList<string> Categories,
    IReadOnlyList<RemoteChoice> Playlists, IReadOnlyList<string> Collections, long Revision);
public sealed record RemoteTrack(long Id, string Label, bool Selected);
public sealed record RemoteMultiscreen(bool Enabled, bool Fullscreen, string? AudioId, string? ExpandedId, IReadOnlyList<string?> Channels);
public sealed record RemotePlayerState(string? ChannelId, string ChannelName, RemoteProgramme? Programme, string Status, double Volume,
    bool Muted, bool Paused, bool Fullscreen, IReadOnlyList<RemoteTrack> Audio, RemoteMultiscreen Multiscreen, long Revision);
public sealed record RemoteCommand(string Action, string? ChannelId = null, double? Value = null, string? Name = null, string?[]? Channels = null);
public sealed record StreamSource(string ChannelId, string Name, Uri Uri, IReadOnlyDictionary<string, string> Headers);
public sealed record RemotePolicy(bool Viewing, bool Control, bool Volume, bool Multiscreen, bool Favorites, bool DirectUrls, bool Transcoding, int MaximumStreams);
public sealed record DeviceInfo(string Id, string Name, DateTimeOffset Paired, DateTimeOffset LastSeen, bool Connected);
public sealed record PairRequest(string Id, string Name, string Verification, DateTimeOffset Expires);
public sealed record StreamInfo(string DeviceId, string ChannelId, string Mode, long Bytes, DateTimeOffset Started);

/// <summary>Implementations marshal WPF state to its dispatcher. Never return source URLs in catalog DTOs.</summary>
public interface IRemoteLibrary
{
    Task<RemoteCatalog> CatalogAsync(CatalogQuery query, CancellationToken token);
    Task<IReadOnlyList<RemoteProgramme>> GuideAsync(string channelId, DateTimeOffset from, CancellationToken token);
    Task<string?> LogoAsync(string channelId, CancellationToken token);
    Task<StreamSource> SourceAsync(string channelId, CancellationToken token);
    Task<RemotePlayerState> StateAsync(CancellationToken token);
    Task CommandAsync(RemoteCommand command, CancellationToken token);
}
