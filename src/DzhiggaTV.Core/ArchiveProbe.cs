using System.Text;

namespace DzhiggaTV.Core;

public enum ArchiveAvailability { Unknown, Available, Empty, Missing, Denied }
public sealed record ArchiveProbeResponse(Uri Uri, int Status, string? Playlist, bool Complete);
public sealed record ArchiveProbeResult(ArchiveAvailability Availability)
{
    public bool Unavailable => Availability is ArchiveAvailability.Empty or ArchiveAvailability.Missing or ArchiveAvailability.Denied;
    public string Message => Availability switch
    {
        ArchiveAvailability.Empty => "Провайдер вернул пустой архив за выбранное время. Запись могла ещё не появиться или отсутствовать. Попробуйте более раннее время.",
        ArchiveAvailability.Missing => "Провайдер не нашёл запись за выбранное время. Заявленная глубина архива не гарантирует наличие каждой передачи.",
        ArchiveAvailability.Denied => "Провайдер отклонил запрос архива. Проверьте доступность записи и лимит подключений.",
        _ => ""
    };
}
public sealed class ArchiveUnavailableException(string message) : Exception(message);

public static class ArchiveProbe
{
    public static async Task<ArchiveProbeResult> CheckAsync(Uri uri, Func<Uri, CancellationToken, Task<ArchiveProbeResponse>> open, CancellationToken token)
    {
        bool alternativeVariants = false;
        for (int depth = 0; depth < 3; depth++)
        {
            if (uri.Scheme is not ("http" or "https")) return new(ArchiveAvailability.Unknown);
            var response = await open(uri, token).ConfigureAwait(false);
            if (response.Status is 401 or 403 or 429) return new(alternativeVariants ? ArchiveAvailability.Unknown : ArchiveAvailability.Denied);
            if (response.Status is 404 or 410) return new(alternativeVariants ? ArchiveAvailability.Unknown : ArchiveAvailability.Missing);
            if (response.Status is < 200 or >= 300 || response.Playlist is null) return new(ArchiveAvailability.Unknown);
            var text = response.Playlist.TrimStart('\uFEFF', '\r', '\n', ' ');
            if (!text.StartsWith("#EXTM3U", StringComparison.Ordinal)) return new(ArchiveAvailability.Unknown);
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            int master = Array.FindIndex(lines, l => l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal));
            if (master >= 0)
            {
                alternativeVariants |= lines.Count(l => l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) > 1;
                if (master + 1 >= lines.Length || lines[master + 1].StartsWith('#') || !Uri.TryCreate(response.Uri, lines[master + 1], out uri!)) return new(ArchiveAvailability.Unknown);
                continue;
            }
            if (lines.Any(l => !l.StartsWith('#'))) return new(ArchiveAvailability.Available);
            if (lines.Any(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) || l.StartsWith("#EXT-X-I-FRAME-STREAM-INF:", StringComparison.Ordinal))) return new(ArchiveAvailability.Unknown);
            return new(response.Complete && !alternativeVariants ? ArchiveAvailability.Empty : ArchiveAvailability.Unknown);
        }
        return new(ArchiveAvailability.Unknown);
    }

    public static async Task<ArchiveProbeResponse> ReadResponseAsync(HttpResponseMessage response, Uri fallback, CancellationToken token)
    {
        var uri = response.RequestMessage?.RequestUri ?? fallback;
        if (!response.IsSuccessStatusCode) return new(uri, (int)response.StatusCode, null, false);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[131072]; int count = await stream.ReadAtLeastAsync(buffer.AsMemory(0, 32), 8, false, token).ConfigureAwait(false);
        if (!Encoding.UTF8.GetString(buffer, 0, count).TrimStart('\uFEFF', '\r', '\n', ' ').StartsWith("#EXTM3U", StringComparison.Ordinal)) return new(uri, (int)response.StatusCode, null, false);
        bool complete = false;
        while (count < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) { complete = true; break; } count += read;
        }
        return new(uri, (int)response.StatusCode, Encoding.UTF8.GetString(buffer, 0, count), complete);
    }
}
