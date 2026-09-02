namespace FolderVault.Core.Ops;

/// <summary>Progress of a lock or unlock, for the UI. Fast mode reports only the coarse steps.</summary>
public sealed record OperationProgress(string Step, long BytesDone = 0, long BytesTotal = 0)
{
    public double? Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : null;
}

/// <summary>
/// Raised when an operation cannot proceed but has left the vault in a consistent state.
/// The message is written to be shown directly to the user.
/// </summary>
public sealed class VaultOperationException : Exception
{
    public VaultOperationException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Raised specifically when a file inside the folder is open in another program, which is the
/// most common reason a lock fails. Callers retry this rather than treating it as fatal.
/// </summary>
public sealed class PayloadInUseException : Exception
{
    public PayloadInUseException(string message, Exception? inner = null) : base(message, inner) { }
}
