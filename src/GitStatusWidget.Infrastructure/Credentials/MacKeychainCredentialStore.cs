using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using GitStatusWidget.Application.Credentials;

namespace GitStatusWidget.Infrastructure.Credentials;

/// <summary>
/// macOS login keychain store (Security.framework <c>SecItem*</c>, <c>kSecClassGenericPassword</c>).
/// Service = <c>GitStatusWidget</c> (<c>GitStatusWidget.Debug</c> in Debug builds), account = credential key.
/// Calls run on the thread pool because the keychain can show a blocking access prompt.
/// </summary>
/// <remarks>
/// The item ACL trusts the creating binary by code signature. Ad-hoc signed builds change their
/// signature on every rebuild, so the first read after an update may prompt (see
/// <c>docs/reference/platform-integration.md</c> §1.3).
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed partial class MacKeychainCredentialStore : ICredentialStore
{
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;
    private const int ErrSecUserCanceled = -128;
    private const int ErrSecAuthFailed = -25293;
    private const int ErrSecInteractionNotAllowed = -25308;

    private readonly string _service;

    /// <summary>Creates a store using the default service name.</summary>
    public MacKeychainCredentialStore()
        : this(CredentialStoreNames.Service)
    {
    }

    /// <summary>Creates a store for a custom keychain service name.</summary>
    /// <param name="service">Value of <c>kSecAttrService</c>.</param>
    public MacKeychainCredentialStore(string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        _service = service;
    }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string StoreName => "macOS Keychain";

    /// <summary>Keychain service name (<c>kSecAttrService</c>).</summary>
    public string Service => _service;

    /// <inheritdoc />
    public Task<Secret?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        CredentialStoreNames.ValidateKey(key);
        return Task.Run(() => ReadCore(key), cancellationToken);
    }

    /// <inheritdoc />
    public Task WriteAsync(string key, Secret secret, CancellationToken cancellationToken)
    {
        CredentialStoreNames.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(secret);
        return Task.Run(() => WriteCore(key, secret), cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        CredentialStoreNames.ValidateKey(key);
        return Task.Run(() => DeleteCore(key), cancellationToken);
    }

    private unsafe Secret? ReadCore(string key)
    {
        using var scope = new CfScope();
        nint query = scope.Dictionary(
            (Native.KSecClass, Native.KSecClassGenericPassword),
            (Native.KSecAttrService, scope.String(_service)),
            (Native.KSecAttrAccount, scope.String(key)),
            (Native.KSecReturnData, Native.KCFBooleanTrue),
            (Native.KSecMatchLimit, Native.KSecMatchLimitOne));

        int status = SecItemCopyMatching(query, out nint data);
        if (status == ErrSecItemNotFound)
        {
            return null;
        }

        Check(status, "SecItemCopyMatching");
        try
        {
            if (data == 0)
            {
                return null;
            }

            var bytes = new ReadOnlySpan<byte>((void*)CFDataGetBytePtr(data), checked((int)CFDataGetLength(data)));
            string value = Encoding.UTF8.GetString(bytes);
            return string.IsNullOrWhiteSpace(value) ? null : new Secret(value);
        }
        finally
        {
            if (data != 0)
            {
                CFRelease(data); // "Copy" rule: the caller owns the returned CFData.
            }
        }
    }

    private void WriteCore(string key, Secret secret)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(secret.Reveal());
        try
        {
            using var scope = new CfScope();
            nint value = scope.Data(bytes);
            nint query = scope.Dictionary(
                (Native.KSecClass, Native.KSecClassGenericPassword),
                (Native.KSecAttrService, scope.String(_service)),
                (Native.KSecAttrAccount, scope.String(key)));
            nint update = scope.Dictionary((Native.KSecValueData, value));

            int status = SecItemUpdate(query, update);
            if (status == ErrSecItemNotFound)
            {
                nint add = scope.Dictionary(
                    (Native.KSecClass, Native.KSecClassGenericPassword),
                    (Native.KSecAttrService, scope.String(_service)),
                    (Native.KSecAttrAccount, scope.String(key)),
                    (Native.KSecAttrLabel, scope.String($"{_service} ({key})")),
                    (Native.KSecValueData, value));
                status = SecItemAdd(add, 0);
                if (status == ErrSecDuplicateItem)
                {
                    status = SecItemUpdate(query, update); // Lost a race with another writer.
                }
            }

            Check(status, "SecItemAdd/SecItemUpdate");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void DeleteCore(string key)
    {
        using var scope = new CfScope();
        nint query = scope.Dictionary(
            (Native.KSecClass, Native.KSecClassGenericPassword),
            (Native.KSecAttrService, scope.String(_service)),
            (Native.KSecAttrAccount, scope.String(key)));
        int status = SecItemDelete(query);
        if (status != ErrSecItemNotFound)
        {
            Check(status, "SecItemDelete");
        }
    }

    private static void Check(int status, string operation)
    {
        switch (status)
        {
            case ErrSecSuccess:
                return;
            case ErrSecUserCanceled:
                throw new CredentialStoreException($"{operation}: keychain access was denied or cancelled by the user", status);
            case ErrSecAuthFailed:
                throw new CredentialStoreException($"{operation}: keychain authentication failed", status);
            case ErrSecInteractionNotAllowed:
                throw new CredentialStoreException($"{operation}: the keychain is locked and user interaction is not allowed", status);
            default:
                throw new CredentialStoreException($"{operation} failed", status);
        }
    }

    private const string SecurityLibrary = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint KCFStringEncodingUtf8 = 0x08000100;

    [LibraryImport(CoreFoundationLibrary)]
    private static partial void CFRelease(nint cf);

    [LibraryImport(CoreFoundationLibrary)]
    private static unsafe partial nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, nint numValues, nint keyCallBacks, nint valueCallBacks);

    [LibraryImport(CoreFoundationLibrary)]
    private static unsafe partial nint CFDataCreate(nint allocator, byte* bytes, nint length);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial nint CFDataGetBytePtr(nint data);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial nint CFDataGetLength(nint data);

    [LibraryImport(CoreFoundationLibrary)]
    private static unsafe partial nint CFStringCreateWithBytes(nint allocator, byte* bytes, nint numBytes, uint encoding, [MarshalAs(UnmanagedType.U1)] bool isExternalRepresentation);

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemAdd(nint attributes, nint result);

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemCopyMatching(nint query, out nint result);

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemUpdate(nint query, nint attributesToUpdate);

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemDelete(nint query);

    /// <summary>CoreFoundation/Security constants, resolved on first use (only ever touched on macOS).</summary>
    private static class Native
    {
        private static readonly nint Security = NativeLibrary.Load(SecurityLibrary);
        private static readonly nint CoreFoundation = NativeLibrary.Load(CoreFoundationLibrary);

        public static readonly nint KSecClass = SecurityConstant("kSecClass");
        public static readonly nint KSecClassGenericPassword = SecurityConstant("kSecClassGenericPassword");
        public static readonly nint KSecAttrService = SecurityConstant("kSecAttrService");
        public static readonly nint KSecAttrAccount = SecurityConstant("kSecAttrAccount");
        public static readonly nint KSecAttrLabel = SecurityConstant("kSecAttrLabel");
        public static readonly nint KSecValueData = SecurityConstant("kSecValueData");
        public static readonly nint KSecReturnData = SecurityConstant("kSecReturnData");
        public static readonly nint KSecMatchLimit = SecurityConstant("kSecMatchLimit");
        public static readonly nint KSecMatchLimitOne = SecurityConstant("kSecMatchLimitOne");
        public static readonly nint KCFBooleanTrue = Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreFoundation, "kCFBooleanTrue"));

        // The callback tables are structs: pass the export address itself, do not dereference.
        public static readonly nint KCFTypeDictionaryKeyCallBacks = NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryKeyCallBacks");
        public static readonly nint KCFTypeDictionaryValueCallBacks = NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryValueCallBacks");

        // CFStringRef globals: the export is the address of the pointer.
        private static nint SecurityConstant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Security, name));
    }

    /// <summary>Owns every CoreFoundation object created for one call and releases them together.</summary>
    private sealed class CfScope : IDisposable
    {
        private readonly List<nint> _owned = [];

        public unsafe nint String(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            fixed (byte* p = bytes)
            {
                return Own(CFStringCreateWithBytes(0, p, bytes.Length, KCFStringEncodingUtf8, false));
            }
        }

        public unsafe nint Data(byte[] bytes)
        {
            fixed (byte* p = bytes)
            {
                return Own(CFDataCreate(0, p, bytes.Length));
            }
        }

        public unsafe nint Dictionary(params ReadOnlySpan<(nint Key, nint Value)> pairs)
        {
            nint* keys = stackalloc nint[pairs.Length];
            nint* values = stackalloc nint[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
            {
                keys[i] = pairs[i].Key;
                values[i] = pairs[i].Value;
            }

            return Own(CFDictionaryCreate(0, keys, values, pairs.Length, Native.KCFTypeDictionaryKeyCallBacks, Native.KCFTypeDictionaryValueCallBacks));
        }

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                CFRelease(_owned[i]);
            }

            _owned.Clear();
        }

        private nint Own(nint pointer)
        {
            if (pointer == 0)
            {
                throw new CredentialStoreException("CoreFoundation allocation failed");
            }

            _owned.Add(pointer);
            return pointer;
        }
    }
}
