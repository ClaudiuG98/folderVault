using System.IO.Pipes;
using System.Text;

namespace FolderVault.App;

/// <summary>
/// Keeps exactly one FolderVault process alive per user session.
///
/// This matters because every double-click on a locked folder launches a fresh process, but the
/// unlocked data keys and the auto-lock timers live in the first one. A second launch therefore
/// hands its command line to the running instance over a named pipe and exits, so the arriving
/// unlock request is served by the process that owns the session state.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Per-user names, so two people signed in at once each get their own instance.
    //
    // The suffix must be STABLE ACROSS PROCESSES, which rules out string.GetHashCode(): .NET
    // randomises string hashing per process, so every launch would derive a different mutex
    // name, every process would believe it was the primary, and each double-click on a locked
    // folder would start yet another copy holding its own keys and timers. SHA-256 is stable.
    private static readonly string Suffix = StableSuffix(Environment.UserName);

    private static readonly string MutexName = $@"Local\FolderVault.Instance.{Suffix}";
    private static readonly string PipeName = $"FolderVault.Pipe.{Suffix}";

    /// <summary>
    /// Derives the shared name from a user name. Must return the same value in every process,
    /// which is why it hashes explicitly instead of calling string.GetHashCode().
    /// </summary>
    internal static string StableSuffix(string userName) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(userName)))[..16];

    private readonly Mutex _mutex;
    private CancellationTokenSource? _listener;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Returns a handle if this process is the primary instance, otherwise null.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var isPrimary);
        if (isPrimary) return new SingleInstance(mutex);

        mutex.Dispose();
        return null;
    }

    /// <summary>Hands a command line to the already-running instance. False if it did not answer.</summary>
    public static bool SendToPrimary(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);

            var payload = Encoding.UTF8.GetBytes(string.Join('\n', args));
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts accepting forwarded command lines. <paramref name="onArgs"/> is raised on a
    /// background thread; callers marshal to the UI thread themselves.
    /// </summary>
    public void StartListening(Action<string[]> onArgs)
    {
        _listener = new CancellationTokenSource();
        var token = _listener.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var payload = await reader.ReadToEndAsync(token).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(payload))
                        onArgs(payload.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // A client vanished mid-handshake; keep serving the next one.
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _listener?.Cancel();
        _listener?.Dispose();

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owning thread at shutdown; the handle close below still frees the name.
        }

        _mutex.Dispose();
    }
}
