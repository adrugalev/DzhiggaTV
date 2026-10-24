using L = DzhiggaTV.Localization.Texts;
namespace DzhiggaTV.Services;

/// <summary>Limits the bytes actually consumed, including decompressed data and chunked HTTP bodies.</summary>
public sealed class BoundedReadStream(Stream inner, long maximumBytes, bool leaveOpen = false, Action<long>? progress = null,
    CancellationToken readCancellationToken = default) : Stream
{
    public long BytesRead { get; private set; }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
    private int Count(int count) { BytesRead += count; if (BytesRead > maximumBytes) throw new InvalidDataException(L.T("Превышен допустимый размер данных.")); progress?.Invoke(BytesRead); return count; }
    public override int Read(byte[] buffer, int offset, int count) { readCancellationToken.ThrowIfCancellationRequested(); return Count(inner.Read(buffer, offset, count)); }
    public override int Read(Span<byte> buffer) { readCancellationToken.ThrowIfCancellationRequested(); return Count(inner.Read(buffer)); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // XmlReader does not pass a token to Stream.ReadAsync, so bind the import deadline here as well.
        using var linked = readCancellationToken.CanBeCanceled && cancellationToken.CanBeCanceled && readCancellationToken != cancellationToken
            ? CancellationTokenSource.CreateLinkedTokenSource(readCancellationToken, cancellationToken) : null;
        var token = linked?.Token ?? (readCancellationToken.CanBeCanceled ? readCancellationToken : cancellationToken);
        return Count(await inner.ReadAsync(buffer, token).ConfigureAwait(false));
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing && !leaveOpen) inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { if (!leaveOpen) await inner.DisposeAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }
}
