using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using GitStatusWidget.Application.Platform;

namespace GitStatusWidget.Infrastructure.Platform;

/// <summary>
/// Per-user single-instance ownership with a named <see cref="Mutex"/> and activation signalling
/// over a named pipe (a Unix domain socket on macOS/Linux). The first process becomes primary and,
/// after <see cref="StartListening"/>, raises <see cref="ActivationRequested"/> when a later
/// process calls <see cref="SignalPrimary"/> / <see cref="SignalPrimaryAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Primacy is decided by <c>createdNew</c> alone; the mutex is never acquired, so there is no thread
/// affinity (releasing a mutex after an <c>await</c> throws) and nothing to release. Process exit, a
/// crash included, drops the handle.
/// </para>
/// <para>
/// Scope: Windows uses the logon session (<c>Local\</c>) and the session id in the pipe name. On
/// macOS/Linux the runtime's "session" is the POSIX session (<c>getsid</c>), which differs between a
/// terminal and Finder/launchd, so the mutex is created machine-wide there and made per-user by
/// <see cref="NamedWaitHandleOptions.CurrentUserOnly"/> plus a user hash in the name.
/// </para>
/// <para>
/// Protocol: the client sends one whitelisted line (<c>show</c> or <c>dashboard</c>) and the primary
/// answers <c>ok</c> before raising the event; anything else is ignored without an answer. Pipes are
/// created with <see cref="PipeOptions.CurrentUserOnly"/>.
/// </para>
/// </remarks>
public sealed class SingleInstanceCoordinator : ISingleInstanceGuard, IAsyncDisposable, IDisposable
{
    /// <summary>Default application id (separate value in Debug builds).</summary>
    public const string DefaultApplicationId = PlatformIdentity.SingleInstanceId;

    /// <summary>Maximum application id length; keeps the Unix socket path below the 104-byte limit.</summary>
    public const int MaxApplicationIdLength = 24;

    private const int MaxCommandBytes = 64;
    private const string ShowCommand = "show";
    private const string DashboardCommand = "dashboard";
    private const string AckReply = "ok";
    private static readonly TimeSpan ClientReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ListenerStopTimeout = TimeSpan.FromSeconds(2);

    private readonly Mutex? _mutex;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private Task? _listener;
    private bool _disposed;

    /// <summary>Acquires (or detects) the single-instance lock for <see cref="DefaultApplicationId"/>.</summary>
    public SingleInstanceCoordinator()
        : this(DefaultApplicationId)
    {
    }

    /// <summary>Acquires (or detects) the single-instance lock for an application id.</summary>
    /// <param name="applicationId">Id of 1–24 characters from <c>[A-Za-z0-9._-]</c>.</param>
    public SingleInstanceCoordinator(string applicationId)
    {
        ValidateApplicationId(applicationId);
        string scope = $"{applicationId}.{UserHash()}";
        PipeName = OperatingSystem.IsWindows() ? $"{scope}.{CurrentSessionId()}" : scope;

        try
        {
            _mutex = new Mutex(
                initiallyOwned: false,
                scope,
                new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = OperatingSystem.IsWindows() },
                out bool createdNew);
            IsPrimary = createdNew;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Name squatted with a foreign ACL or an unusable lock directory: fail open rather than never starting.
            _mutex = null;
            IsPrimary = true;
            HasLock = false;
            return;
        }

        HasLock = true;
    }

    /// <inheritdoc />
    public bool IsPrimary { get; }

    /// <summary>
    /// <see langword="false"/> when the OS lock could not be created and this instance runs as primary
    /// without single-instance protection.
    /// </summary>
    public bool HasLock { get; }

    /// <summary>Name of the activation pipe (diagnostics and tests).</summary>
    internal string PipeName { get; }

    /// <inheritdoc />
    public event EventHandler<ActivationRequestedEventArgs>? ActivationRequested;

    /// <summary>
    /// Starts the activation listener in the primary instance. Subscribe to
    /// <see cref="ActivationRequested"/> first. Calling it again has no effect.
    /// </summary>
    /// <exception cref="InvalidOperationException">This instance is not primary.</exception>
    public void StartListening()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsPrimary)
            {
                throw new InvalidOperationException("Only the primary instance can listen for activation requests.");
            }

            _listener ??= Task.Run(() => ListenAsync(_cts.Token));
        }
    }

    /// <summary>Asks the primary instance to activate (blocking; for <c>Program.Main</c> before the UI starts).</summary>
    /// <param name="command">The request.</param>
    /// <param name="timeout">Maximum time to wait for the primary instance.</param>
    /// <returns><see langword="true"/> when the primary instance acknowledged the request.</returns>
    /// <remarks>Runs the asynchronous version to completion; call it only where no synchronization context is installed.</remarks>
    public bool SignalPrimary(ActivationCommand command, TimeSpan timeout) =>
        SignalPrimaryAsync(command, timeout, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Asks the primary instance to activate and waits for its acknowledgement.</summary>
    /// <param name="command">The request.</param>
    /// <param name="timeout">Maximum time to wait for the primary instance.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><see langword="true"/> when the primary instance acknowledged the request.</returns>
    public async Task<bool> SignalPrimaryAsync(ActivationCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await using (client.ConfigureAwait(false))
            {
                await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
                await client.WriteAsync(Encode(command), timeoutCts.Token).ConfigureAwait(false);
                await client.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

                // Without an acknowledgement a write can succeed into a socket that is being torn down.
                string? reply = await ReadLineAsync(client, timeoutCts.Token).ConfigureAwait(false);
                return reply == AckReply;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // Timed out.
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Stops the listener (bounded wait) and releases the lock.</summary>
    public void Dispose()
    {
        Task? listener = BeginDispose();
        if (listener is not null)
        {
            try
            {
                listener.Wait(ListenerStopTimeout);
            }
            catch (AggregateException)
            {
                // The listener only ends by cancellation; nothing to report.
            }
        }

        EndDispose();
    }

    /// <summary>Stops the listener (bounded wait) and releases the lock.</summary>
    /// <returns>A task that completes when disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        Task? listener = BeginDispose();
        if (listener is not null)
        {
            try
            {
                await listener.WaitAsync(ListenerStopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Bounded shutdown: do not hang on exit.
            }
        }

        EndDispose();
    }

    internal static bool TryParseCommand(string text, out ActivationCommand command)
    {
        switch (text.TrimEnd('\r', '\n'))
        {
            case ShowCommand:
                command = ActivationCommand.Show;
                return true;
            case DashboardCommand:
                command = ActivationCommand.OpenDashboard;
                return true;
            default:
                command = default;
                return false;
        }
    }

    private Task? BeginDispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            _disposed = true;
        }

        _cts.Cancel();
        return _listener;
    }

    private void EndDispose()
    {
        _mutex?.Dispose(); // Never owned, so no ReleaseMutex (thread affinity); closing the handle frees the name.
        _cts.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        // The next server instance is created before the current one is disposed. On macOS/Linux all
        // instances share one listening socket that is closed when the last instance goes away; a
        // client connecting in that gap would otherwise be accepted by the kernel and then dropped.
        NamedPipeServerStream? next = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream? server = next ?? TryCreateServer();
                next = null;
                if (server is null)
                {
                    // Name busy (for example a previous instance still shutting down): retry later.
                    if (!await DelayAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                await using (server.ConfigureAwait(false))
                {
                    try
                    {
                        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                        next = TryCreateServer();
                        await HandleClientAsync(server, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (IOException)
                    {
                        // Client vanished mid-message: wait for the next one.
                    }
                }
            }
        }
        finally
        {
            if (next is not null)
            {
                await next.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private NamedPipeServerStream? TryCreateServer()
    {
        try
        {
            return new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 2,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        clientCts.CancelAfter(ClientReadTimeout);
        try
        {
            string? text = await ReadLineAsync(server, clientCts.Token).ConfigureAwait(false);
            if (text is null || !TryParseCommand(text, out var command))
            {
                return; // Whitelist; ignore anything else and send no acknowledgement.
            }

            await server.WriteAsync(Encoding.UTF8.GetBytes(AckReply + "\n"), clientCts.Token).ConfigureAwait(false);
            await server.FlushAsync(clientCts.Token).ConfigureAwait(false);
            Raise(command);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Silent or slow client: drop it.
        }
    }

    /// <summary>Reads one short line (up to <see cref="MaxCommandBytes"/>); <see langword="null"/> on EOF without data.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaxCommandBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (Array.IndexOf(buffer, (byte)'\n', 0, total) >= 0)
            {
                break;
            }
        }

        if (total == 0)
        {
            return null;
        }

        int end = Array.IndexOf(buffer, (byte)'\n', 0, total);
        return Encoding.UTF8.GetString(buffer, 0, end >= 0 ? end : total).TrimEnd('\r');
    }

    private void Raise(ActivationCommand command)
    {
        EventHandler<ActivationRequestedEventArgs>? handler = ActivationRequested;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new ActivationRequestedEventArgs(command));
        }
#pragma warning disable CA1031 // A faulty subscriber must not stop the listener for the rest of the session.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"Single-instance activation handler failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static byte[] Encode(ActivationCommand command) => Encoding.UTF8.GetBytes(command switch
    {
        ActivationCommand.OpenDashboard => DashboardCommand,
        _ => ShowCommand,
    } + "\n");

    private static void ValidateApplicationId(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (applicationId.Length > MaxApplicationIdLength
            || !applicationId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new ArgumentException(
                $"The application id must be 1-{MaxApplicationIdLength} characters from [A-Za-z0-9._-].",
                nameof(applicationId));
        }
    }

    private static int CurrentSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    /// <summary>Short stable hash of the user identity, so names are per user without leaking or length issues.</summary>
    private static string UserHash()
    {
        string identity = $"{Environment.UserDomainName}\\{Environment.UserName}".ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }
}
