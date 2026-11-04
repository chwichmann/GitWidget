using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using GitStatusWidget.Application.Credentials;

namespace GitStatusWidget.Infrastructure.Credentials;

/// <summary>
/// Linux Secret Service store (gnome-keyring, KWallet, KeePassXC) through libsecret's non-variadic
/// <c>secret_password_*v_sync</c> functions. Items use one string attribute <c>key</c> under the
/// schema <c>io.github.chwichmann.GitStatusWidget.Token</c>.
/// </summary>
/// <remarks>
/// <see cref="IsAvailable"/> probes once (library present, session bus present, a lookup succeeds
/// within a timeout) and never throws. Intended as a process-lifetime singleton: the native schema
/// is allocated once per instance and intentionally never freed, so in-flight calls stay safe.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe partial class LinuxSecretServiceCredentialStore : ICredentialStore, IProbedCredentialStore
{
    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGLib = "libglib-2.0.so.0";
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly SecretSchema* _schema;
    private readonly byte* _attributeName;
    private readonly Lazy<(bool Available, string? Reason)> _availability;

    /// <summary>Creates a store with the default schema name and probe timeout.</summary>
    public LinuxSecretServiceCredentialStore()
        : this(CredentialStoreNames.SecretSchema, DefaultProbeTimeout)
    {
    }

    /// <summary>Creates a store with a custom schema name.</summary>
    /// <param name="schemaName">libsecret schema name (reverse-DNS).</param>
    /// <param name="probeTimeout">Maximum time the availability probe may take.</param>
    public LinuxSecretServiceCredentialStore(string schemaName, TimeSpan probeTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        _attributeName = (byte*)Marshal.StringToCoTaskMemUTF8("key");
        _schema = (SecretSchema*)NativeMemory.AllocZeroed((nuint)sizeof(SecretSchema));
        _schema->Name = (byte*)Marshal.StringToCoTaskMemUTF8(schemaName);
        _schema->Flags = 0; // SECRET_SCHEMA_NONE
        _schema->Attributes[0] = new SecretSchemaAttribute { Name = _attributeName, Type = 0 }; // STRING; [1] zeroed = terminator
        _availability = new Lazy<(bool, string?)>(() => Probe(probeTimeout));
    }

    /// <inheritdoc />
    public bool IsAvailable => _availability.Value.Available;

    /// <summary>Why the Secret Service cannot be used, or <see langword="null"/> when it can.</summary>
    public string? UnavailableReason => _availability.Value.Reason;

    /// <inheritdoc />
    public string StoreName => "Secret Service (system keyring)";

    /// <inheritdoc />
    public Task<Secret?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        CredentialStoreNames.ValidateKey(key);
        EnsureAvailable();
        return Task.Run(() => LookupCore(key), cancellationToken);
    }

    /// <inheritdoc />
    public Task WriteAsync(string key, Secret secret, CancellationToken cancellationToken)
    {
        CredentialStoreNames.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(secret);
        EnsureAvailable();
        return Task.Run(() => StoreCore(key, secret), cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        CredentialStoreNames.ValidateKey(key);
        EnsureAvailable();
        return Task.Run(() => ClearCore(key), cancellationToken);
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new CredentialStoreException(UnavailableReason ?? "The Secret Service is not available.");
        }
    }

    private (bool, string?) Probe(TimeSpan timeout)
    {
        try
        {
            if (!NativeLibrary.TryLoad(LibSecret, typeof(LinuxSecretServiceCredentialStore).Assembly, null, out _)
                || !NativeLibrary.TryLoad(LibGLib, typeof(LinuxSecretServiceCredentialStore).Assembly, null, out _))
            {
                return (false, "libsecret is not installed (package libsecret-1-0 or libsecret).");
            }

            if (!HasSessionBus())
            {
                return (false, "No D-Bus session bus is available, so no system keyring can be reached.");
            }

            // Lookup of a key that never exists: no unlock prompt, but a D-Bus round-trip to the service.
            var probe = Task.Run(() => LookupCore("__availability-probe__"));
            if (!probe.Wait(timeout))
            {
                return (false, "The system keyring did not respond in time.");
            }

            return (true, null);
        }
        catch (AggregateException ex) when (ex.InnerException is CredentialStoreException inner)
        {
            return (false, $"No usable system keyring: {inner.Message}");
        }
        catch (AggregateException ex)
        {
            return (false, $"No usable system keyring ({ex.InnerException?.GetType().Name ?? "unknown error"}).");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return (false, "libsecret could not be loaded.");
        }
    }

    private static bool HasSessionBus()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        {
            return true;
        }

        string? runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(runtimeDir) && File.Exists(Path.Combine(runtimeDir, "bus"));
    }

    private Secret? LookupCore(string key)
    {
        return WithAttributes(key, attributes =>
        {
            GError* error = null;
            byte* password = secret_password_lookupv_sync(_schema, attributes, 0, &error);
            ThrowIfError(error, "lookup");
            if (password == null)
            {
                return null;
            }

            try
            {
                string? value = Marshal.PtrToStringUTF8((nint)password);
                return string.IsNullOrWhiteSpace(value) ? null : new Secret(value);
            }
            finally
            {
                secret_password_free(password); // Wipes the buffer.
            }
        });
    }

    private void StoreCore(string key, Secret secret)
    {
        WithAttributes<object?>(key, attributes =>
        {
            nint label = Marshal.StringToCoTaskMemUTF8($"GitStatusWidget token ({key})");
            string value = secret.Reveal();
            int byteCount = Encoding.UTF8.GetByteCount(value);
            byte* password = (byte*)NativeMemory.AllocZeroed((nuint)byteCount + 1);
            try
            {
                Encoding.UTF8.GetBytes(value, new Span<byte>(password, byteCount));
                GError* error = null;
                int ok = secret_password_storev_sync(_schema, attributes, null, (byte*)label, password, 0, &error);
                ThrowIfError(error, "store");
                if (ok == 0)
                {
                    throw new CredentialStoreException("libsecret store failed");
                }

                return null;
            }
            finally
            {
                NativeMemory.Clear(password, (nuint)byteCount);
                NativeMemory.Free(password);
                Marshal.FreeCoTaskMem(label);
            }
        });
    }

    private void ClearCore(string key)
    {
        WithAttributes<object?>(key, attributes =>
        {
            GError* error = null;
            _ = secret_password_clearv_sync(_schema, attributes, 0, &error); // FALSE without error = nothing deleted.
            ThrowIfError(error, "clear");
            return null;
        });
    }

    private T WithAttributes<T>(string key, Func<nint, T> body)
    {
        nint value = Marshal.StringToCoTaskMemUTF8(key);
        nint table = g_hash_table_new(GLibExports.StrHash, GLibExports.StrEqual); // No destroy funcs: we own key/value memory.
        if (table == 0)
        {
            Marshal.FreeCoTaskMem(value);
            throw new CredentialStoreException("g_hash_table_new failed");
        }

        try
        {
            g_hash_table_insert(table, _attributeName, (byte*)value);
            return body(table);
        }
        finally
        {
            g_hash_table_unref(table);
            Marshal.FreeCoTaskMem(value);
        }
    }

    private static void ThrowIfError(GError* error, string operation)
    {
        if (error == null)
        {
            return;
        }

        string message = Marshal.PtrToStringUTF8((nint)error->Message) ?? "unknown error";
        int code = error->Code;
        g_error_free(error);
        throw new CredentialStoreException($"libsecret {operation} failed: {message}", code);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchemaAttribute
    {
        public byte* Name;
        public int Type;
    }

    [InlineArray(32)]
    private struct SchemaAttributes
    {
        private SecretSchemaAttribute _element0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchema
    {
        public byte* Name;
        public int Flags;
        public SchemaAttributes Attributes;
        public int Reserved;
        public nint Reserved1;
        public nint Reserved2;
        public nint Reserved3;
        public nint Reserved4;
        public nint Reserved5;
        public nint Reserved6;
        public nint Reserved7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public byte* Message;
    }

    private static class GLibExports
    {
        private static readonly nint GLib = NativeLibrary.Load(LibGLib, typeof(LinuxSecretServiceCredentialStore).Assembly, null);
        public static readonly nint StrHash = NativeLibrary.GetExport(GLib, "g_str_hash");
        public static readonly nint StrEqual = NativeLibrary.GetExport(GLib, "g_str_equal");
    }

    [LibraryImport(LibSecret)]
    private static partial int secret_password_storev_sync(SecretSchema* schema, nint attributes, byte* collection, byte* label, byte* password, nint cancellable, GError** error);

    [LibraryImport(LibSecret)]
    private static partial byte* secret_password_lookupv_sync(SecretSchema* schema, nint attributes, nint cancellable, GError** error);

    [LibraryImport(LibSecret)]
    private static partial int secret_password_clearv_sync(SecretSchema* schema, nint attributes, nint cancellable, GError** error);

    [LibraryImport(LibSecret)]
    private static partial void secret_password_free(byte* password);

    [LibraryImport(LibGLib)]
    private static partial nint g_hash_table_new(nint hashFunc, nint keyEqualFunc);

    // Returns gboolean since GLib 2.40 (void before); the result is not needed.
    [LibraryImport(LibGLib)]
    private static partial void g_hash_table_insert(nint table, byte* key, byte* value);

    [LibraryImport(LibGLib)]
    private static partial void g_hash_table_unref(nint table);

    [LibraryImport(LibGLib)]
    private static partial void g_error_free(GError* error);
}
