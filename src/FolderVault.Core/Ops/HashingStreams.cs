using System.Security.Cryptography;

namespace FolderVault.Core.Ops;

/// <summary>
/// Wraps a source stream and hashes everything read through it, so a file can be encrypted and
/// fingerprinted in a single pass rather than being read from disk twice.
/// </summary>
internal sealed class HashingReadStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public string Hex { get; private set; } = string.Empty;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0) _hash.AppendData(buffer, offset, read);
        return read;
    }

    /// <summary>Finalises the hash. Valid once the stream has been read to the end.</summary>
    public string Finish() => Hex = Convert.ToHexString(_hash.GetHashAndReset());

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A write sink that hashes and counts bytes without storing them. Used by the verify pass to
/// decrypt a blob and confirm it matches, without needing space for a second copy on disk.
/// </summary>
internal sealed class HashingSinkStream : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _written;

    public override void Write(byte[] buffer, int offset, int count)
    {
        _hash.AppendData(buffer, offset, count);
        _written += count;
    }

    public string Finish() => Convert.ToHexString(_hash.GetHashAndReset());

    public override long Length => _written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Position { get => _written; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}
