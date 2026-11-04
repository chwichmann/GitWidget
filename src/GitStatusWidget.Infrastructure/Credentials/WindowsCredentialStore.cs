using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GitStatusWidget.Application.Credentials;

namespace GitStatusWidget.Infrastructure.Credentials;

/// <summary>
/// Windows Credential Manager store (generic credentials, <c>CRED_PERSIST_LOCAL_MACHINE</c> so the
/// token never roams with a roaming profile). The secret is stored as UTF-16LE without terminator.
/// Target names are <c>{service}:{key}</c>, for example <c>GitStatusWidget:GitStatusWidget/{guid}</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsCredentialStore : ICredentialStore, IProbedCredentialStore
{
    /// <summary><c>CRED_MAX_CREDENTIAL_BLOB_SIZE</c> = 5 × 512 bytes.</summary>
    public const int MaxSecretBytes = 5 * 512;

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int ErrorNoSuchLogonSession = 1312;

    private readonly string _targetPrefix;
    private readonly Lazy<(bool Available, string? Reason)> _availability;

    /// <summary>Creates a store using the default service prefix.</summary>
    public WindowsCredentialStore()
        : this(CredentialStoreNames.Service)
    {
    }

    /// <summary>Creates a store with a custom target-name prefix.</summary>
    /// <param name="service">Prefix of every target name.</param>
    public WindowsCredentialStore(string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        _targetPrefix = service + ":";
        _availability = new Lazy<(bool, string?)>(Probe);
    }

    /// <inheritdoc />
    public bool IsAvailable => _availability.Value.Available;

    /// <summary>Why the store is unavailable, or <see langword="null"/>.</summary>
    public string? UnavailableReason => _availability.Value.Reason;

    /// <inheritdoc />
    public string StoreName => "Windows Credential Manager";

    /// <inheritdoc />
    public Task<Secret?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            CredentialStoreNames.ValidateKey(key);
            return Task.FromResult(ReadCore(Target(key)));
        }
        catch (Exception ex) when (ex is CredentialStoreException or ArgumentException)
        {
            return Task.FromException<Secret?>(ex);
        }
    }

    /// <inheritdoc />
    public Task WriteAsync(string key, Secret secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            CredentialStoreNames.ValidateKey(key);
            ArgumentNullException.ThrowIfNull(secret);
            WriteCore(Target(key), secret.Reveal());
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is CredentialStoreException or ArgumentException)
        {
            return Task.FromException(ex);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            CredentialStoreNames.ValidateKey(key);
            if (!CredDelete(Target(key), CredTypeGeneric, 0))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != ErrorNotFound)
                {
                    throw new CredentialStoreException("CredDelete failed", error);
                }
            }

            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is CredentialStoreException or ArgumentException)
        {
            return Task.FromException(ex);
        }
    }

    private string Target(string key) => _targetPrefix + key;

    private static Secret? ReadCore(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out CREDENTIALW* credential))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new CredentialStoreException("CredRead failed", error);
        }

        try
        {
            int charCount = (int)(credential->CredentialBlobSize / sizeof(char));
            if (charCount == 0 || credential->CredentialBlob == null)
            {
                return null;
            }

            var value = new string((char*)credential->CredentialBlob, 0, charCount);
            return string.IsNullOrWhiteSpace(value) ? null : new Secret(value);
        }
        finally
        {
            CredFree(credential);
        }
    }

    private static void WriteCore(string target, string secret)
    {
        int byteCount = checked(secret.Length * sizeof(char));
        if (byteCount > MaxSecretBytes)
        {
            throw new ArgumentException(
                $"The secret is too large for Windows Credential Manager: {byteCount} bytes as UTF-16, the maximum is {MaxSecretBytes} bytes.",
                nameof(secret));
        }

        string user = Environment.UserName;
        const string comment = "GitStatusWidget access token";
        fixed (char* pTarget = target)
        fixed (char* pSecret = secret)
        fixed (char* pUser = user)
        fixed (char* pComment = comment)
        {
            var credential = new CREDENTIALW
            {
                Type = CredTypeGeneric,
                TargetName = pTarget,
                Comment = pComment,
                CredentialBlobSize = (uint)byteCount,
                CredentialBlob = (byte*)pSecret,
                Persist = CredPersistLocalMachine,
                UserName = pUser,
            };

            // CredWrite replaces an existing credential with the same TargetName + Type.
            if (!CredWrite(&credential, 0))
            {
                throw new CredentialStoreException("CredWrite failed", Marshal.GetLastPInvokeError());
            }
        }
    }

    private (bool, string?) Probe()
    {
        // A lookup of a key that never exists: ERROR_NOT_FOUND means the vault is reachable.
        if (CredRead(_targetPrefix + "__availability-probe__", CredTypeGeneric, 0, out CREDENTIALW* credential))
        {
            CredFree(credential);
            return (true, null);
        }

        int error = Marshal.GetLastPInvokeError();
        return error switch
        {
            ErrorNotFound => (true, null),
            ErrorNoSuchLogonSession => (false, "Windows Credential Manager is not available in this logon session."),
            _ => (false, $"Windows Credential Manager is not available (error {error})."),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public void* Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(CREDENTIALW* credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string targetName, uint type, uint flags, out CREDENTIALW* credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string targetName, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(void* buffer);
}
