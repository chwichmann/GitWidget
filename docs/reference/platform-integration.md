# GitWidget – Platform Integration Reference

Developer reference for the cross-platform desktop shell of GitWidget (.NET 10 + Avalonia + CommunityToolkit.Mvvm + Microsoft.Extensions.Hosting). It covers the pieces the architecture document ([../ARCHITECTURE-GitStatusWidget.md](../ARCHITECTURE-GitStatusWidget.md), sections 16–19, 23, 25–26) leaves as "platform work": credentials, tray, windows, motion, autostart, single instance, power and network events, URLs, paths, packaging and CI.

**Researched:** 2026-11-04. **Environment used for checks:** macOS 27.0.1 on Apple Silicon (Retina), .NET SDK 10.0.400 (runtime 10.0.11).

**What the labels mean:**

- **Verified (local):** compiled and/or run in a scratch project on the Mac above.
- **Verified (compile):** compiled with `IsAotCompatible=true` and no warnings, but not run because it targets another OS.
- **Verified (docs/NuGet):** checked against the cited official page or NuGet metadata on the research date.
- **Unverified:** taken from general knowledge or secondary sources. Check it before you rely on it.

---

## 0. Versions and the Avalonia 11 vs 12 decision

### 0.1 Package matrix (Verified (NuGet), 2026-11-04)

| Package | Version | Notes |
|---|---|---|
| `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` | **12.1.5** (2026-11-02) | 12.0.0 went stable 2026-04-07; 12.1.0 on 2026-07-09 |
| `Avalonia.Headless.XUnit` | **12.1.5** | depends on `xunit.v3.extensibility.core` ≥ 3.2.2. **Breaks with xunit.v3 4.x** (see §12) |
| `Avalonia.Wayland` | 12.1.5 | Experimental native Wayland backend, opt-in via `UseWayland()` (§3.6) |
| Avalonia 11 line (for reference) | 11.3.22 (2026-09-11) | Latest 11.x. Still gets patches |
| `CommunityToolkit.Mvvm` | **8.4.2** | Partial-property `[ObservableProperty]` works on .NET 10 / C# 14 (Verified (local)) |
| `Microsoft.Extensions.Hosting` | **10.0.14** | The 11.0.0 RCs exist; stay on 10.0.x LTS |
| `Microsoft.Extensions.Http.Resilience` | 10.11.0 | |
| `Microsoft.Extensions.TimeProvider.Testing` | 10.11.0 | `FakeTimeProvider` |
| `Microsoft.Win32.SystemEvents` | 10.0.14 | Optional, Windows-only (§8) |
| `xunit.v3` | **3.2.2** (pin it) | 4.0.2 exists but is incompatible with Avalonia.Headless.XUnit 12.1.5 |
| `xunit.runner.visualstudio` | 3.1.5 | Only for VSTest mode |
| `Microsoft.NET.Test.Sdk` | 18.11.0 | Only for VSTest mode |
| `WireMock.Net` | 2.21.0 | |
| `FluentAssertions` | 8.11.0 | **v8+ is under a commercial (Xceed) license.** Use `AwesomeAssertions` 9.6.0 (fork, Apache-2.0, Unverified: license) or stay on FluentAssertions 7.x |
| `Meziantou.Framework.Win32.CredentialManager` | 3.0.8 | Windows only, net10/net11, no deps (§1.5) |
| `Devlooped.CredentialManager` | 2.7.0 (2026-03-04) | GCM-derived, 3 OS (§1.5) |
| `KeySharp` | 1.0.5 (2022-05-13) | Stale. Do not use |
| `NReco.Logging.File` / `Karambolo.Extensions.Logging.File` | 1.4.0 / 4.1.1 | Optional file loggers (§11.6) |

### 0.2 Use Avalonia 12, not 11

Avalonia 12 is stable and on its sixth-plus patch (12.1.5). GitWidget is a new port (WPF → Avalonia) with no Avalonia 11 code to migrate, so start on **12.1.x**. Why:

- It requires .NET 8+ and the docs recommend .NET 10, which matches our target.
- Compiled bindings are the default (`AvaloniaUseCompiledBindingsByDefault=true`), which is what trimming and AOT need anyway.
- `ExtendClientAreaToDecorationsHint` is fixed on Windows. The Linux drag-and-drop and the Wayland backend (experimental) arrived in 12.1.
- Style animations stop on hidden controls by default (`PlaybackBehavior`), which helps idle CPU (§4).
- Our tray, window, screen and lifetime APIs exist in 12.1.5. They were checked by reflection (Verified (local)).

Avalonia 12 renames you will hit while following older (v11) samples and blog posts:

| Avalonia 11 | Avalonia 12 |
|---|---|
| `SystemDecorations="None"` | `WindowDecorations="None"` (the old property is `[Obsolete]`, the `SystemDecorations` enum is gone) |
| `ExtendClientAreaChromeHints` | removed. Use `WindowDecorations` + `ExtendClientAreaToDecorationsHint` |
| `TopLevel.PlatformSettings` | removed. Use `Application.Current.PlatformSettings` or `visual.GetPlatformSettings()` |
| `NativeMenuItemToggleType` | `MenuItemToggleType` (`None`, `CheckBox`, `Radio`) |
| `Screen.PixelDensity` / `Screen.Primary` | `Screen.Scaling` / `Screen.IsPrimary`. `Screen` is abstract and lives in `Avalonia.Platform` |
| `Avalonia.Diagnostics` + `AttachDevTools()` | removed. Use `AvaloniaUI.DiagnosticsSupport` + `AttachDeveloperTools()` (Dev Tools require Avalonia Plus, a paid tier) |
| `{Binding}` = reflection binding when the flag was unset | `{Binding}` = compiled binding. `x:DataType` is required |
| xUnit v2 headless | xUnit **v3** headless |

Build-time telemetry: every Avalonia build runs `Avalonia.BuildServices` (11.3.2), which sends anonymous build data. It is opt-out: set `AVALONIA_TELEMETRY_OPTOUT=1` in your shell profile and in CI (Verified (local): package README). Nothing is collected at runtime.

Sources: https://docs.avaloniaui.net/docs/avalonia12-breaking-changes, https://avaloniaui.net/blog/release-12-1, https://www.nuget.org/packages/Avalonia

---

## 1. Secure credential storage

### 1.1 Contract and key conventions

```csharp
public interface ICredentialStore
{
    Task<string?> Read(string key);          // null = not found
    Task Write(string key, string secret);   // create or overwrite
    Task Delete(string key);                 // idempotent: deleting a missing key is not an error
}

public sealed class CredentialStoreException(string message, int code) : Exception($"{message} (code {code})")
{
    public int Code { get; } = code;         // Win32 error / OSStatus / GError code
}
```

- **Key:** the credential reference from settings, `{configuration-guid}` (architecture §18.1). The store adds the app prefix itself.
  - Windows `TargetName` = `GitWidget/{guid}`
  - macOS `kSecAttrService` = bundle id (`com.example.GitWidget`, a placeholder: pick the real reverse-DNS id once) and `kSecAttrAccount` = `{guid}`
  - libsecret attribute `key={guid}` under schema `com.example.GitWidget.Token`
- **Use a different prefix/service in Debug builds** (`GitWidget-dev`, `com.example.GitWidget.dev`). Dev builds then never touch production tokens, and the macOS ACL issue in §1.3 stays contained.
- **Every call may block.** macOS can show a keychain prompt, and libsecret makes D-Bus round-trips and may show an unlock prompt. Run the work off the UI thread (`Task.Run`). The sketches below already do this on macOS and Linux.
- **Threat model.** All three stores protect secrets at rest and from other OS users. On Windows and Linux (an unlocked Secret Service collection), **any process running as the same user can read them**. Only the macOS keychain has per-application ACLs. This is acceptable for a personal widget. Never log secrets, and redact them (architecture §18.2).
- **Linux fallback.** Register an `InMemoryCredentialStore` (`ConcurrentDictionary`) for unit tests and as the "session-only" fallback (§1.4).

### 1.2 Windows: Credential Manager (advapi32)

Facts (Verified (docs): `CREDENTIALW`):

- `Type = CRED_TYPE_GENERIC (1)`. Only the app reads the blob.
- `TargetName`: at most `CRED_MAX_GENERIC_TARGET_NAME_LENGTH` = 32767 chars. It is **case-insensitive**. Microsoft recommends a company/app prefix. `TargetName`+`Type` is the identity of the credential.
- `CredentialBlobSize` ≤ `CRED_MAX_CREDENTIAL_BLOB_SIZE` = **5 × 512 = 2560 bytes**. For generic credentials the app defines the format. Use **UTF-16LE without a terminator** (the Windows convention, and what Credential Manager's UI and `cmdkey` expect), so the limit is 1280 UTF-16 chars. GitHub and GitLab tokens are well under 100 chars.
- `Persist`:
  - `CRED_PERSIST_SESSION (1)`: gone at logoff.
  - **`CRED_PERSIST_LOCAL_MACHINE (2)`: persists for this user on this computer only. Use this.**
  - `CRED_PERSIST_ENTERPRISE (3)`: also visible to this user's logon sessions **on other computers**, meaning it **roams with the roaming profile**. On the work PC this would copy the token to every domain machine you log on to and to the profile servers. Do **not** use it. If the account has no roaming profile, it falls back to local, but don't depend on that.
  - Unverified: enterprise credentials are stored under `%APPDATA%\Microsoft\Credentials` (roaming) and local-machine credentials under `%LOCALAPPDATA%\Microsoft\Credentials`.
- `UserName` is ignored for generic credentials but appears in the Credential Manager UI. Set it to `Environment.UserName` or to the provider account name.
- **Errors:** `CredRead`/`CredDelete` fail with `ERROR_NOT_FOUND = 1168` when there is no entry. Map that to `null` / no-op. Common others: `ERROR_NO_SUCH_LOGON_SESSION (1312)` (service or no profile loaded), `ERROR_INVALID_PARAMETER (87)` (blob too large).
- Memory returned by `CredReadW` must be freed with `CredFree`.

Implementation (`LibraryImport`, blittable struct, trimming/AOT safe). Verified (compile): no warnings with `IsAotCompatible=true`. `sizeof(CREDENTIALW)` = 80, which matches the native x64/arm64 layout (Verified (local)). Not run on Windows.

```csharp
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsCredentialStore(string prefix) : ICredentialStore
{
    const uint CRED_TYPE_GENERIC = 1;
    const uint CRED_PERSIST_LOCAL_MACHINE = 2;           // per user, this machine only, does not roam
    const int CRED_MAX_CREDENTIAL_BLOB_SIZE = 5 * 512;   // 2560 bytes
    const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential)]
    struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public FILETIME LastWritten;      // ignored on write
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public void* Attributes;          // PCREDENTIAL_ATTRIBUTEW, unused
        public char* TargetAlias;
        public char* UserName;
    }
    [StructLayout(LayoutKind.Sequential)] struct FILETIME { public uint Low, High; }

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

    string Target(string key) => $"{prefix}/{key}";        // e.g. "GitWidget/3f2a…"

    public Task<string?> Read(string key)
    {
        if (!CredRead(Target(key), CRED_TYPE_GENERIC, 0, out var cred))
        {
            int err = Marshal.GetLastPInvokeError();
            if (err == ERROR_NOT_FOUND) return Task.FromResult<string?>(null);
            throw new CredentialStoreException("CredRead failed", err);
        }
        try
        {   // UTF-16LE, no terminator: byte count / 2 = char count
            return Task.FromResult<string?>(new string((char*)cred->CredentialBlob, 0, (int)cred->CredentialBlobSize / sizeof(char)));
        }
        finally { CredFree(cred); }
    }

    public Task Write(string key, string secret)
    {
        int byteCount = secret.Length * sizeof(char);
        if (byteCount > CRED_MAX_CREDENTIAL_BLOB_SIZE)
            throw new ArgumentException($"Secret exceeds {CRED_MAX_CREDENTIAL_BLOB_SIZE} bytes as UTF-16.", nameof(secret));
        string target = Target(key), user = Environment.UserName;
        fixed (char* pTarget = target) fixed (char* pSecret = secret) fixed (char* pUser = user)
        {
            var cred = new CREDENTIALW
            {
                Type = CRED_TYPE_GENERIC, TargetName = pTarget,
                CredentialBlobSize = (uint)byteCount, CredentialBlob = (byte*)pSecret,
                Persist = CRED_PERSIST_LOCAL_MACHINE, UserName = pUser,
            };
            if (!CredWrite(&cred, 0))       // CredWrite overwrites an existing TargetName+Type
                throw new CredentialStoreException("CredWrite failed", Marshal.GetLastPInvokeError());
        }
        return Task.CompletedTask;
    }

    public Task Delete(string key)
    {
        if (!CredDelete(Target(key), CRED_TYPE_GENERIC, 0))
        {
            int err = Marshal.GetLastPInvokeError();
            if (err != ERROR_NOT_FOUND) throw new CredentialStoreException("CredDelete failed", err);
        }
        return Task.CompletedTask;
    }
}
```

Manual check on Windows: `cmdkey /list:GitWidget/*` should list the entry. You can also open Control Panel → Credential Manager → Windows Credentials → Generic Credentials.

Sources: https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw, https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew, https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation

### 1.3 macOS: Keychain via Security.framework

Approach: use the `SecItem*` API with `kSecClassGenericPassword`. Build the query dictionaries with CoreFoundation (`CFDictionaryCreate`, `CFStringCreateWithBytes`, `CFDataCreate`). Read constants such as `kSecClass` from the framework exports with `NativeLibrary.GetExport` + `Marshal.ReadIntPtr`, because they are `CFStringRef` globals.

**Don't dereference `kCFTypeDictionaryKeyCallBacks`/`ValueCallBacks`.** They are structs, so you pass the export address itself.

**Verified (local):** add, read (including non-ASCII), update, delete, a second delete (no-op), and a read after delete (null) all work. Run under both `dotnet run` and a published Native AOT build. No keychain prompt appeared for the creating binary.

OSStatus values you will see:

| Code | Name | Meaning / handling |
|---|---|---|
| 0 | `errSecSuccess` | |
| -25300 | `errSecItemNotFound` | Read → `null`. Delete → no-op. Update → fall back to Add |
| -25299 | `errSecDuplicateItem` | Add raced another writer → Update |
| -128 | `errSecUserCanceled` | The user clicked Deny/Cancel on the access prompt → show "access denied", don't retry in a loop (Unverified: exact code) |
| -25293 | `errSecAuthFailed` | Wrong keychain password in the prompt (Unverified: exact code) |
| -34018 | `errSecMissingEntitlement` | You asked for the data-protection keychain without entitlements (see below) |

```csharp
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

[SupportedOSPlatform("macos")]
public sealed partial class MacKeychainCredentialStore(string service) : ICredentialStore
{
    const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
    const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const int errSecSuccess = 0, errSecItemNotFound = -25300, errSecDuplicateItem = -25299;
    const uint kCFStringEncodingUTF8 = 0x08000100;

    [LibraryImport(CoreFoundationLib)] private static partial void CFRelease(nint cf);
    [LibraryImport(CoreFoundationLib)] private static unsafe partial nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, nint numValues, nint keyCallBacks, nint valueCallBacks);
    [LibraryImport(CoreFoundationLib)] private static unsafe partial nint CFDataCreate(nint allocator, byte* bytes, nint length);
    [LibraryImport(CoreFoundationLib)] private static partial nint CFDataGetBytePtr(nint data);
    [LibraryImport(CoreFoundationLib)] private static partial nint CFDataGetLength(nint data);
    [LibraryImport(CoreFoundationLib)] private static unsafe partial nint CFStringCreateWithBytes(nint alloc, byte* bytes, nint numBytes, uint encoding, [MarshalAs(UnmanagedType.U1)] bool isExternalRepresentation);

    [LibraryImport(SecurityLib)] private static partial int SecItemAdd(nint attributes, nint result);
    [LibraryImport(SecurityLib)] private static partial int SecItemCopyMatching(nint query, out nint result);
    [LibraryImport(SecurityLib)] private static partial int SecItemUpdate(nint query, nint attributesToUpdate);
    [LibraryImport(SecurityLib)] private static partial int SecItemDelete(nint query);

    static readonly nint Sec = NativeLibrary.Load(SecurityLib);
    static readonly nint Cf = NativeLibrary.Load(CoreFoundationLib);
    static nint SecConst(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Sec, name));   // CFStringRef globals
    static nint CfConst(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Cf, name));

    static readonly nint kSecClass = SecConst("kSecClass");
    static readonly nint kSecClassGenericPassword = SecConst("kSecClassGenericPassword");
    static readonly nint kSecAttrService = SecConst("kSecAttrService");
    static readonly nint kSecAttrAccount = SecConst("kSecAttrAccount");
    static readonly nint kSecAttrLabel = SecConst("kSecAttrLabel");
    static readonly nint kSecValueData = SecConst("kSecValueData");
    static readonly nint kSecReturnData = SecConst("kSecReturnData");
    static readonly nint kSecMatchLimit = SecConst("kSecMatchLimit");
    static readonly nint kSecMatchLimitOne = SecConst("kSecMatchLimitOne");
    static readonly nint kCFBooleanTrue = CfConst("kCFBooleanTrue");
    // Callback tables are structs: pass the export ADDRESS, do not dereference.
    static readonly nint kCFTypeDictionaryKeyCallBacks = NativeLibrary.GetExport(Cf, "kCFTypeDictionaryKeyCallBacks");
    static readonly nint kCFTypeDictionaryValueCallBacks = NativeLibrary.GetExport(Cf, "kCFTypeDictionaryValueCallBacks");

    // SecItem calls can block on a user prompt: never run them on the UI thread.
    public Task<string?> Read(string key) => Task.Run(() => ReadCore(key));
    public Task Write(string key, string secret) => Task.Run(() => WriteCore(key, secret));
    public Task Delete(string key) => Task.Run(() => DeleteCore(key));

    string? ReadCore(string key)
    {
        using var s = new CfScope();
        var q = s.Dict((kSecClass, kSecClassGenericPassword), (kSecAttrService, s.Str(service)), (kSecAttrAccount, s.Str(key)),
                       (kSecReturnData, kCFBooleanTrue), (kSecMatchLimit, kSecMatchLimitOne));
        int rc = SecItemCopyMatching(q, out nint data);
        if (rc == errSecItemNotFound) return null;
        Check(rc, "SecItemCopyMatching");
        try
        {
            unsafe { return Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)CFDataGetBytePtr(data), checked((int)CFDataGetLength(data)))); }
        }
        finally { CFRelease(data); }                 // "Copy" rule: we own the returned CFData
    }

    void WriteCore(string key, string secret)
    {
        using var s = new CfScope();
        nint value = s.Data(Encoding.UTF8.GetBytes(secret));
        var q = s.Dict((kSecClass, kSecClassGenericPassword), (kSecAttrService, s.Str(service)), (kSecAttrAccount, s.Str(key)));
        int rc = SecItemUpdate(q, s.Dict((kSecValueData, value)));
        if (rc == errSecItemNotFound)
        {
            var add = s.Dict((kSecClass, kSecClassGenericPassword), (kSecAttrService, s.Str(service)), (kSecAttrAccount, s.Str(key)),
                             (kSecAttrLabel, s.Str($"{service} ({key})")), (kSecValueData, value));
            rc = SecItemAdd(add, 0);
            if (rc == errSecDuplicateItem) rc = SecItemUpdate(q, s.Dict((kSecValueData, value)));   // lost a race
        }
        Check(rc, "SecItemAdd/Update");
    }

    void DeleteCore(string key)
    {
        using var s = new CfScope();
        int rc = SecItemDelete(s.Dict((kSecClass, kSecClassGenericPassword), (kSecAttrService, s.Str(service)), (kSecAttrAccount, s.Str(key))));
        if (rc != errSecItemNotFound) Check(rc, "SecItemDelete");
    }

    static void Check(int rc, string op) { if (rc != errSecSuccess) throw new CredentialStoreException($"{op} failed", rc); }

    /// Owns every CF object created for one call and releases them together.
    sealed class CfScope : IDisposable
    {
        readonly List<nint> _owned = [];
        nint Own(nint p) { if (p == 0) throw new OutOfMemoryException("CF allocation failed"); _owned.Add(p); return p; }
        public unsafe nint Str(string s) { var b = Encoding.UTF8.GetBytes(s); fixed (byte* p = b) return Own(CFStringCreateWithBytes(0, p, b.Length, kCFStringEncodingUTF8, false)); }
        public unsafe nint Data(byte[] b) { fixed (byte* p = b) return Own(CFDataCreate(0, p, b.Length)); }
        public unsafe nint Dict(params ReadOnlySpan<(nint Key, nint Value)> pairs)
        {
            nint* k = stackalloc nint[pairs.Length]; nint* v = stackalloc nint[pairs.Length];
            for (int i = 0; i < pairs.Length; i++) { k[i] = pairs[i].Key; v[i] = pairs[i].Value; }
            return Own(CFDictionaryCreate(0, k, v, pairs.Length, kCFTypeDictionaryKeyCallBacks, kCFTypeDictionaryValueCallBacks));
        }
        public void Dispose() { for (int i = _owned.Count - 1; i >= 0; i--) CFRelease(_owned[i]); }
    }
}
```

**Pitfalls (important for an unsigned or ad-hoc-signed personal app):**

1. **ACL is bound to the code signature.** Without `kSecUseDataProtectionKeychain`, `SecItemAdd` writes to the file-based login keychain. The creating app goes on the item's trusted-application list, identified by its *designated requirement* (DR).
   - For an **ad-hoc signature the DR is the cdhash**. Verified (local): `codesign -d -r-` on the published apphost prints `designated => cdhash H"c921…"`.
   - The cdhash changes on every rebuild. After each update, the first read triggers the "GitWidget wants to use your confidential information stored in … in your keychain" prompt, and **Always Allow** only lasts until the next build. (Unverified locally: the prompt was not triggered on purpose because it blocks.)
   - **Fix:** create a self-signed "Code Signing" certificate once (Keychain Access → Certificate Assistant → Create a Certificate…, type Code Signing) and sign every build with it (`codesign -s "GitWidget Dev" …`). The DR then becomes `identifier "com.example.GitWidget" and certificate leaf = H"…"`, which stays the same across rebuilds (Unverified: no re-prompt after rebuild).
   - A Developer ID certificate gives the same stability.
2. **`dotnet run` / `dotnet GitWidget.dll` runs as the `dotnet` host**, which Microsoft signs (TeamIdentifier `UBF8T346G9`, Verified (local)). Items created that way are trusted for *every* app launched through the `dotnet` host. This is why the separate Debug service name from §1.1 matters.
3. **Data-protection keychain (`kSecUseDataProtectionKeychain = true`, iOS-style)** needs a `keychain-access-groups` entitlement backed by a provisioning profile / Team ID. An ad-hoc build cannot have one and gets `errSecMissingEntitlement (-34018)`. Stay on the login keychain.
4. In the login keychain `kSecAttrAccessible` has no effect (Unverified). Locking the keychain (screen-lock settings) makes reads prompt for the keychain password.
5. Run calls on a background thread (done above). Treat `errSecUserCanceled` as "user said no". Set credential status to *Authentication required* and don't re-prompt on every poll.

**Why not shell out to `/usr/bin/security`?**

- `security add-generic-password -s svc -a acct -w TOKEN -U` puts the token on the **command line**. Any local process can see it via `ps`/`proc_pidinfo` while it runs, and it can end up in process-accounting logs and crash reports.
- If you omit the `-w` value, `security` reads the password from a TTY, and a GUI app has none.
- Items created by `security` trust `/usr/bin/security` (Apple-signed). Then **any** process that can run `security find-generic-password -w` reads them silently, which is weaker than an ACL on your own binary.
- Parsing stdout/stderr and exit codes is brittle.
- Reading via `security find-generic-password -s … -a … -w` doesn't expose the secret in argv, but you still get the ACL and parsing problems.
- Conclusion: P/Invoke. It is about 120 lines and was verified working.

Sources: https://developer.apple.com/documentation/security/secitemadd(_:_:), https://developer.apple.com/documentation/security/keychain-services, https://developer.apple.com/documentation/security/errsecitemnotfound

### 1.4 Linux: Secret Service via libsecret

- **Use the non-variadic `…v_sync` functions.** The classic `secret_password_store_sync(schema, collection, label, password, cancellable, error, "key", value, NULL)` is **C-variadic**. .NET `LibraryImport` has no varargs support, and calling a variadic function through a fixed signature is undefined on some ABIs.
- The `v` variants take a `GHashTable*` of string→string attributes:
  - `gboolean secret_password_storev_sync(const SecretSchema*, GHashTable* attributes, const gchar* collection /*NULL = default*/, const gchar* label, const gchar* password, GCancellable*, GError**)`
  - `gchar* secret_password_lookupv_sync(schema, attributes, cancellable, error)`: NULL with no error = not found. Free the result with `secret_password_free` (it wipes the memory).
  - `gboolean secret_password_clearv_sync(schema, attributes, cancellable, error)`: FALSE with no error = nothing deleted.
  - Store **updates** an existing item with matching attributes, so it acts as an upsert. The calls may block indefinitely, so keep them off the UI thread.
- `SecretSchema` layout:
  - `{ const gchar* name; SecretSchemaFlags flags; SecretSchemaAttribute attributes[32]; gint reserved; gpointer reserved1..7; }`
  - `SecretSchemaAttribute = { const gchar* name; SecretSchemaAttributeType type; }`
  - That is 592 bytes on 64-bit (Verified (local): `sizeof` of the struct below = 592). The attribute list ends at the first entry with `name == NULL`. The schema must stay at a fixed address, so allocate it natively.
- Libraries: `libsecret-1.so.0` (Debian/Ubuntu package `libsecret-1-0`, Fedora `libsecret`) and `libglib-2.0.so.0`.

Verified (compile) only. Not run on Linux. Treat it as a sketch to test on a GNOME VM.

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("linux")]
public sealed unsafe partial class LibSecretCredentialStore : ICredentialStore, IDisposable
{
    const string LibSecret = "libsecret-1.so.0";
    const string LibGLib = "libglib-2.0.so.0";

    [StructLayout(LayoutKind.Sequential)] struct SecretSchemaAttribute { public byte* Name; public int Type; } // 0 = STRING
    [InlineArray(32)] struct SchemaAttributes { SecretSchemaAttribute _e0; }
    [StructLayout(LayoutKind.Sequential)]
    struct SecretSchema
    {
        public byte* Name;
        public int Flags;                       // SECRET_SCHEMA_NONE
        public SchemaAttributes Attributes;
        public int Reserved;
        public nint Reserved1, Reserved2, Reserved3, Reserved4, Reserved5, Reserved6, Reserved7;
    }
    [StructLayout(LayoutKind.Sequential)] struct GError { public uint Domain; public int Code; public byte* Message; }

    [LibraryImport(LibSecret)] private static partial int secret_password_storev_sync(SecretSchema* schema, nint attributes, byte* collection, byte* label, byte* password, nint cancellable, GError** error);
    [LibraryImport(LibSecret)] private static partial byte* secret_password_lookupv_sync(SecretSchema* schema, nint attributes, nint cancellable, GError** error);
    [LibraryImport(LibSecret)] private static partial int secret_password_clearv_sync(SecretSchema* schema, nint attributes, nint cancellable, GError** error);
    [LibraryImport(LibSecret)] private static partial void secret_password_free(byte* password);
    [LibraryImport(LibGLib)] private static partial nint g_hash_table_new(nint hashFunc, nint keyEqualFunc);
    [LibraryImport(LibGLib)] private static partial int g_hash_table_insert(nint table, byte* key, byte* value);
    [LibraryImport(LibGLib)] private static partial void g_hash_table_unref(nint table);
    [LibraryImport(LibGLib)] private static partial void g_error_free(GError* error);

    static readonly nint GLib = NativeLibrary.Load(LibGLib);
    static readonly nint g_str_hash = NativeLibrary.GetExport(GLib, "g_str_hash");
    static readonly nint g_str_equal = NativeLibrary.GetExport(GLib, "g_str_equal");

    readonly SecretSchema* _schema;
    readonly byte* _schemaName, _attrKey;

    public LibSecretCredentialStore(string schemaName = "com.example.GitWidget.Token")
    {
        _schemaName = (byte*)Marshal.StringToCoTaskMemUTF8(schemaName);
        _attrKey = (byte*)Marshal.StringToCoTaskMemUTF8("key");
        _schema = (SecretSchema*)NativeMemory.AllocZeroed((nuint)sizeof(SecretSchema));
        _schema->Name = _schemaName;
        _schema->Attributes[0] = new SecretSchemaAttribute { Name = _attrKey, Type = 0 };   // [1] zeroed = terminator
    }

    public Task<string?> Read(string key) => Task.Run(() => WithAttributes(key, attrs =>
    {
        GError* err = null;
        byte* pw = secret_password_lookupv_sync(_schema, attrs, 0, &err);
        ThrowIf(err, "lookup");
        if (pw == null) return null;
        try { return Marshal.PtrToStringUTF8((nint)pw); } finally { secret_password_free(pw); }
    }));

    public Task Write(string key, string secret) => Task.Run(() => WithAttributes<object?>(key, attrs =>
    {
        nint label = Marshal.StringToCoTaskMemUTF8($"GitWidget token ({key})");
        nint pw = Marshal.StringToCoTaskMemUTF8(secret);
        try
        {
            GError* err = null;
            int ok = secret_password_storev_sync(_schema, attrs, null, (byte*)label, (byte*)pw, 0, &err);
            ThrowIf(err, "store");
            if (ok == 0) throw new CredentialStoreException("secret_password_storev_sync returned FALSE", 0);
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(label);
            NativeMemory.Clear((void*)pw, (nuint)System.Text.Encoding.UTF8.GetByteCount(secret));   // best-effort wipe
            Marshal.FreeCoTaskMem(pw);
        }
    }));

    public Task Delete(string key) => Task.Run(() => WithAttributes<object?>(key, attrs =>
    {
        GError* err = null;
        secret_password_clearv_sync(_schema, attrs, 0, &err);   // FALSE without error = nothing to delete
        ThrowIf(err, "clear");
        return null;
    }));

    T WithAttributes<T>(string key, Func<nint, T> body)
    {
        nint value = Marshal.StringToCoTaskMemUTF8(key);
        nint table = g_hash_table_new(g_str_hash, g_str_equal);  // no destroy funcs: we own key/value memory
        try { g_hash_table_insert(table, _attrKey, (byte*)value); return body(table); }
        finally { g_hash_table_unref(table); Marshal.FreeCoTaskMem(value); }
    }

    static void ThrowIf(GError* err, string op)
    {
        if (err == null) return;
        string msg = Marshal.PtrToStringUTF8((nint)err->Message) ?? "unknown";
        int code = err->Code;
        g_error_free(err);
        throw new CredentialStoreException($"libsecret {op} failed: {msg}", code);
    }

    public void Dispose()
    {
        NativeMemory.Free(_schema);
        Marshal.FreeCoTaskMem((nint)_schemaName);
        Marshal.FreeCoTaskMem((nint)_attrKey);
    }
}
```

**When there is no keyring daemon (headless, minimal WM, SSH session, WSL, CI)**, the failures look like this:

- `DllNotFoundException` / `TypeInitializationException`: libsecret isn't installed.
- `GError`, for example `org.freedesktop.DBus.Error.ServiceUnknown`: nothing owns `org.freedesktop.secrets`.
- The default collection is locked and no prompter is available.

Providers for the Secret Service: gnome-keyring, KWallet in recent Plasma releases, and KeePassXC with Secret Service integration enabled (Unverified: exact KWallet version).

Policy:

1. **Probe once at startup** (`NativeLibrary.TryLoad("libsecret-1.so.0", …)` plus a lookup of a sentinel key) and expose `CredentialStoreAvailability { Available, Unavailable(reason) }` to the settings UI.
2. **Default fallback: session-only.** Use `InMemoryCredentialStore`. The token works until the process exits, and the UI says "No system keyring found. Token will not be saved." This matches the architecture rule to never persist tokens in JSON.
3. **Optional for headless/CI: environment variable** `GITWIDGET_TOKEN_<GUID>` (read-only store), documented as the user's responsibility.
4. **Never write a silent plaintext fallback.** If someone really needs it, offer an explicit opt-in "insecure file store": a file at mode 0600 under `$XDG_CONFIG_HOME/GitWidget/`, with a warning banner, like GCM's `plaintext` store. Do not build this for the MVP.

Sources: https://gnome.pages.gitlab.gnome.org/libsecret/func.password_storev_sync.html, https://gnome.pages.gitlab.gnome.org/libsecret/, https://specifications.freedesktop.org/secret-service-spec/latest/ (Unverified URL)

### 1.5 Library or own code?

| Option | Platforms | Status (NuGet 2026-11-04) | Notes |
|---|---|---|---|
| **Own minimal implementation (above)** | Win / macOS / Linux | n/a | About 350 LOC across 3 files. **Zero dependencies.** `LibraryImport`, trimming/AOT clean (Verified (compile) for all three; macOS run-tested) |
| `Devlooped.CredentialManager` 2.7.0 | Win / macOS / Linux | updated 2026-03-04 | Packages Git Credential Manager's stores. `netstandard2.0` (not AOT-annotated, Unverified trim behaviour). Deps: `DotNetConfig`, `System.Security.Cryptography.ProtectedData`, `System.Text.Json 8.0.5`. **No default store on Linux**: GCM needs `GCM_CREDENTIAL_STORE`/`credential.credentialStore` (secretservice, gpg, cache, plaintext). The API is URL+account shaped. **Open Source Maintenance Fee**: revenue-generating users are asked to pay via GitHub Sponsors |
| `Meziantou.Framework.Win32.CredentialManager` 3.0.8 | Windows only | very active (net10/net11 TFMs, no deps) | The best off-the-shelf Windows wrapper. You'd still need macOS + Linux code |
| `KeySharp` 1.0.5 | Win / macOS / Linux | last release 2022-05 | Stale, `netstandard2.1`. Reject |
| "TextCopy"-like libraries | – | – | Clipboard, not credentials. Not relevant |

**Recommendation: write the minimal implementation yourself** (the three classes above, plus `InMemoryCredentialStore` and an environment-variable reader). Reasons:

- Architecture §23.7 ("keep package dependencies minimal").
- The code is small and already verified on the primary dev OS.
- It stays AOT/trim clean.
- It avoids GCM's Linux store configuration and the maintenance-fee question on a work PC.

If you don't want to own the Windows interop, `Meziantou.Framework.Win32.CredentialManager` is a drop-in for that one platform.

Wiring:

```csharp
services.AddSingleton<ICredentialStore>(_ =>
    OperatingSystem.IsWindows() ? new WindowsCredentialStore(AppIds.CredentialPrefix) :
    OperatingSystem.IsMacOS()   ? new MacKeychainCredentialStore(AppIds.BundleId) :
    OperatingSystem.IsLinux() && LibSecretProbe.IsAvailable() ? new LibSecretCredentialStore() :
    new InMemoryCredentialStore());
```

Sources: https://www.nuget.org/packages/Devlooped.CredentialManager, https://github.com/git-ecosystem/git-credential-manager/blob/main/docs/credstores.md, https://www.nuget.org/packages/Meziantou.Framework.Win32.CredentialManager, https://www.nuget.org/packages/KeySharp

---

## 2. Tray and menu bar

### 2.1 Declaration in `App.axaml`

Verified (local), Avalonia 12.1.5 on macOS: this compiles with compiled bindings, runs, shows the icon, builds the dynamic submenu and switches the icon.

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:GitWidget.Desktop.ViewModels"
             x:Class="GitWidget.Desktop.App"
             x:DataType="vm:TrayViewModel"
             RequestedThemeVariant="Default">
  <Application.Styles>
    <FluentTheme />
  </Application.Styles>
  <TrayIcon.Icons>
    <TrayIcons>
      <!-- FallbackValue is REQUIRED for ToolTipText, see 2.3 -->
      <TrayIcon Icon="{Binding TrayIcon}"
                ToolTipText="{Binding TrayToolTip, FallbackValue=GitWidget}"
                Command="{Binding ShowWidgetCommand}">
        <TrayIcon.Menu>
          <NativeMenu>
            <NativeMenuItem Header="Show widget" Command="{Binding ShowWidgetCommand}" />
            <NativeMenuItem Header="Open dashboard" Command="{Binding OpenDashboardCommand}" />
            <NativeMenuItem Header="Refresh now" Command="{Binding RefreshCommand}" />
            <NativeMenuItemSeparator />
            <NativeMenuItem Header="Repository">
              <NativeMenu />                       <!-- filled from code, see 2.2 -->
            </NativeMenuItem>
            <NativeMenuItem Header="Always on top" ToggleType="CheckBox"
                            IsChecked="{Binding AlwaysOnTop}" Command="{Binding ToggleAlwaysOnTopCommand}" />
            <NativeMenuItem Header="Start at login" ToggleType="CheckBox"
                            IsChecked="{Binding StartAtLogin}" Command="{Binding ToggleStartAtLoginCommand}" />
            <NativeMenuItem Header="Settings…" Command="{Binding OpenSettingsCommand}" />
            <NativeMenuItemSeparator />
            <NativeMenuItem Header="Exit" Command="{Binding ExitCommand}" />
          </NativeMenu>
        </TrayIcon.Menu>
      </TrayIcon>
    </TrayIcons>
  </TrayIcon.Icons>
</Application>
```

- The tray icon belongs to the `Application`, not a window, so it survives when every window is hidden.
- Set `DataContext` on the `Application` in `OnFrameworkInitializationCompleted` (the bindings then resolve).
- The "Start with Windows" label from architecture §17 becomes "Start at login" (or set the label per OS).

APIs present in 12.1.5 (Verified (local) by reflection):

- `TrayIcon`: `Icon` (WindowIcon), `ToolTipText`, `Menu`, `IsVisible`, `Command`, `CommandParameter`, `Clicked` event, `TrayIcon.GetIcons(app)`.
- `NativeMenuItem`: `Header`, `Command`, `CommandParameter`, `ToggleType` (`MenuItemToggleType`), `IsChecked`, `IsEnabled`, `IsVisible`, `Gesture`, `Icon` (Bitmap), `ToolTip`, `Menu` (submenu), `Click`.
- `NativeMenu`: `Items`, `Opening`, `Closed`, `NeedsUpdate`.
- `MacOSProperties.IsTemplateIcon` (attached, macOS).

### 2.2 Dynamic menus (submenu per repository, check/radio items)

**`x:Name` does not work on `NativeMenu`.** It fails to compile with `AVLN2000 Unable to resolve … property Name on type NativeMenu` (Verified (local)). Get the menu from code instead:

```csharp
// App.OnFrameworkInitializationCompleted, after DataContext is set
var tray = TrayIcon.GetIcons(this)![0];
var repoMenu = ((NativeMenuItem)tray.Menu!.Items[4]).Menu!;   // or search by Header

void RebuildRepositoryMenu(IReadOnlyList<RepositoryItem> repos, string? selectedId)
{
    Dispatcher.UIThread.VerifyAccess();
    repoMenu.Items.Clear();
    foreach (var r in repos)
        repoMenu.Items.Add(new NativeMenuItem(r.DisplayName)
        {
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = r.Id == selectedId,
            Command = vm.SelectRepositoryCommand,
            CommandParameter = r.Id,
            // per-repo badge in text, not just colour (architecture §17)
            ToolTip = r.StatusText,
        });
    if (repos.Count == 0) repoMenu.Items.Add(new NativeMenuItem("No repositories configured") { IsEnabled = false });
}
```

- Rebuild only when the repository list or selection changes, not on every poll. To refresh lazily, rebuild in `repoMenu.NeedsUpdate`/`Opening`.
- A per-repository *submenu* (for example `owner/repo ▸ Open in browser / Refresh / Remove`) is a `NativeMenuItem` whose `Menu` is another `NativeMenu`.
- Always change items on the UI thread.

### 2.3 Changing the icon at runtime (state badges) and tooltip

```csharp
public partial class TrayViewModel : ObservableObject
{
    static readonly Dictionary<TrayState, WindowIcon> Icons = new()
    {   // load ONCE; don't create WindowIcons per poll
        [TrayState.Normal] = Load("tray-normal"),  [TrayState.Failed]  = Load("tray-failed"),
        [TrayState.Stale]  = Load("tray-stale"),   [TrayState.Offline] = Load("tray-offline"),
    };
    static WindowIcon Load(string n) => new(AssetLoader.Open(new Uri($"avares://GitWidget.Desktop/Assets/Tray/{n}.png")));

    [ObservableProperty] public partial WindowIcon TrayIcon { get; set; } = Icons[TrayState.Normal];
    [ObservableProperty] public partial string TrayToolTip { get; set; } = "GitWidget";

    public void Apply(AggregateHealth h)
    {
        TrayIcon = Icons[h.State];
        TrayToolTip = Truncate($"GitWidget – {h.Summary}", 120);   // e.g. "1 build failed (owner/repo)"
    }
}
```

- **Pitfall (Verified (local), 12.1.5, macOS):** if `ToolTipText` evaluates to `null` when the tray icon attaches, the app crashes at startup with `ArgumentNullException` in `Avalonia.Native.TrayIconImpl.SetToolTipText`. This happens because XAML loads before `DataContext` is set. Fix: use `FallbackValue=…` on the binding (shown above) or a non-null literal, and never set the property to `null`.
- Tooltip length: Windows `NOTIFYICONDATA.szTip` holds 128 chars including the terminator, so keep it ≤ 127. Longer text gets truncated (Unverified: whether Avalonia truncates or fails). Truncate yourself.
- **Don't encode state only by colour** (architecture §17). Draw a shape into the badge (✕ = failed, clock = stale, slash = offline) and put the text in the tooltip.
- **macOS menu bar convention:** monochrome *template* images that the system tints for light/dark. Use `MacOSProperties.IsTemplateIcon="True"` on the `TrayIcon`. A template icon loses colour, which is another reason to use shape badges. Without the template flag, coloured icons stay as they are. Provide 18×18 pt PNGs at @1x/@2x (Unverified: whether Avalonia picks @2x).
- **Windows:** provide 16/20/24/32 px images (100–200 % DPI). A multi-size `.ico` is best. On Windows 11 the icon may land in the overflow flyout ("Show hidden icons") until the user pins it. Apps cannot force pinning.

### 2.4 Per-OS behaviour

| | Windows | macOS | Linux |
|---|---|---|---|
| Backing | Shell notification area (Shell_NotifyIcon) | `NSStatusItem` in the menu bar | `StatusNotifierItem` (KDE/freedesktop SNI) over D-Bus, with the menu exported via `com.canonical.dbusmenu` |
| Left click | `Command` / `Clicked` | opens the menu (Avalonia docs) | depends on the host: often `Activate` → `Command`, sometimes the menu (Unverified) |
| Right click | menu | menu | menu |
| Needs | – | – | A running SNI host. **KDE Plasma:** built in. **GNOME:** needs the *AppIndicator and KStatusNotifierItem Support* extension (Ubuntu ships it enabled). **XFCE:** status notifier plugin. Without a host the icon is invisible. Offer a fallback, for example opening the widget at start |
| Tooltip | yes | yes (hover) | host-dependent |

Avalonia's docs say it is "confirmed on Ubuntu". There is `X11PlatformOptions.UseDBusMenu` for D-Bus menus (Verified (local): property exists).

### 2.5 macOS: no Dock icon, lifetime without a main window

- **Bundled app:** put `<key>LSUIElement</key><true/>` in `Info.plist` (§11.4). The app becomes an "agent": no Dock icon, no app menu, no Cmd-Tab entry.
- **Unbundled / `dotnet run`:** `AppBuilder.Configure<App>().With(new MacOSPlatformOptions { ShowInDock = false })` sets the accessory activation policy at runtime (Verified (local): property exists, app started without a Dock icon). Set both; they don't conflict.
- Agent apps don't automatically come to the front. When the user opens the dashboard from the tray, call `window.Show(); window.Activate();`.
- **Lifetime:** set `desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown` in `OnFrameworkInitializationCompleted` (Verified (local)). Only the tray Exit command (`desktop.Shutdown()`) or OS logout ends the process (architecture §26).
- **Reopen:** `Application.Current.TryGetFeature<IActivatableLifetime>()` raises `Activated` with `ActivationKind.Reopen` when the user launches the already-running bundle again from Finder/Spotlight. LaunchServices activates the existing process instead of starting a second one, so treat Reopen as "show widget" (Verified (local): API exists; behaviour Unverified).

```csharp
public override void OnFrameworkInitializationCompleted()
{
    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DataContext = _services.GetRequiredService<TrayViewModel>();
        desktop.ShutdownRequested += (_, e) => { /* OS logout / Cmd-Q: persist window state, flush logs */ };
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime act)
            act.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) _windows.ShowWidget(); };
    }
    base.OnFrameworkInitializationCompleted();
}
```

Hosting integration (architecture §25/§26). Avalonia owns the main thread, and the Generic Host runs around it:

```csharp
[STAThread]
public static int Main(string[] args)
{
    // Main stays synchronous: an async Main loses [STAThread] after the first await.
    var single = new SingleInstance(AppIds.AppId);                        // §7, step 1
    try
    {
        if (!single.IsPrimary) { SingleInstance.SignalPrimaryAsync(AppIds.AppId, "show", TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); return 0; }

        var builder = Host.CreateApplicationBuilder(args);                // step 2
        builder.Services.AddGitWidget();                                  // settings, stores, pollers, VMs
        using IHost host = builder.Build();
        App.Services = host.Services;                                     // simple static handoff to App
        host.StartAsync().GetAwaiter().GetResult();                       // hosted services wait for a "UI ready" signal before polling (step 7)
        // App.OnFrameworkInitializationCompleted calls single.StartListening(cmd => Dispatcher.UIThread.InvokeAsync(...))

        int code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);  // blocks until Shutdown()

        host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); // §26: cancel + bounded wait
        return code;
    }
    finally { single.DisposeAsync().AsTask().GetAwaiter().GetResult(); } // §26 step 7
}
public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .With(new MacOSPlatformOptions { ShowInDock = false })
    .LogToTrace();
```

(`SingleInstance` is the class from §7.)

Sources: https://docs.avaloniaui.net/controls/navigation/trayicon, https://docs.avaloniaui.net/controls/menus/nativemenu, https://developer.apple.com/documentation/bundleresources/information-property-list/lsuielement, https://www.freedesktop.org/wiki/Specifications/StatusNotifierItem/

---

## 3. Widget, dashboard and toast windows

### 3.1 Borderless, transparent, topmost widget

Verified (local), 12.1.5 macOS. This renders, and `ActualTransparencyLevel` was `AcrylicBlur`.

```xml
<Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:GitWidget.Desktop.ViewModels"
        x:Class="GitWidget.Desktop.Views.WidgetWindow" x:DataType="vm:WidgetViewModel"
        Width="360" Height="190" CanResize="False"
        WindowDecorations="None"
        ShowInTaskbar="False"
        Topmost="{Binding AlwaysOnTop}"
        TransparencyLevelHint="AcrylicBlur, Blur, Transparent"
        Background="Transparent"
        Title="GitWidget">
  <Border CornerRadius="12" Background="{DynamicResource WidgetBackgroundBrush}" Padding="12"
          PointerPressed="OnDragAreaPointerPressed">
    …
  </Border>
</Window>
```

- `WindowDecorations` (12.x): `None` | `BorderOnly` | `Full`. Use `None` for the widget and toasts.
- The dashboard can use `Full`, or `ExtendClientAreaToDecorationsHint="True"` to keep native caption buttons with a custom title area. That works on Windows/macOS, with limited support on Linux.
- `TransparencyLevelHint` is an ordered fallback list (`None`, `Transparent`, `Blur`, `AcrylicBlur`, `Mica`). Check `ActualTransparencyLevel` at runtime and set `TransparencyBackgroundFallback` / an opaque `Background` when it comes back `None`. That happens on Linux without a compositor, and on Windows when "Transparency effects" is off.
  - Avalonia's window-management page says macOS supports only `Transparent`. On 12.1.5 the probe got `AcrylicBlur` back on macOS. Don't depend on either; design for the fallback.
  - `Mica` is Windows 11 only.
- `Background="Transparent"` on the `Window` plus a semi-transparent `Border` with `CornerRadius` gives a rounded floating card. Set the card brush per theme (§5.4).
- `Topmost` comes from `WindowBase`. Avalonia 12 also has a built-in non-client drag region, `Avalonia.Controls.Chrome.WindowDecorationProperties.ElementRole="TitleBar"` (role `WindowDecorationsElementRole.TitleBar`, "initiates a platform window move"). Whether it works with `WindowDecorations="None"` is Unverified, so use `BeginMoveDrag`.

### 3.2 Dragging, hide-on-close

```csharp
public partial class WidgetWindow : Window
{
    public WidgetWindow() => InitializeComponent();

    void OnDragAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            BeginMoveDrag(e);                       // native move loop (Win32 / NSWindow / xdg_toplevel.move)
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // User close (Alt+F4, Cmd+W, close button) → hide to tray. App/OS shutdown → really close.
        if (e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
```

`WindowCloseReason` values (12.1.5): `Undefined`, `WindowClosing`, `OwnerWindowClosing`, `ApplicationShutdown`, `OSShutdown`. Together with `IsProgrammatic` they separate "user closed it" from shutdown (Verified (local): compiles; the hide path was not run interactively).

- `PositionChanged` fires continuously during a drag. Debounce persistence (save 500 ms after the last change) and save again on shutdown.
- Don't start a drag from interactive children: handle `PointerPressed` only on the background/header element, or check `e.Source`.

### 3.3 Positioning: Screens, scaling, multi-monitor, clamping

What to know about units (Verified (local) on macOS, docs for Windows):

- `Window.Position`, `Screen.Bounds` and `Screen.WorkingArea` are `PixelPoint`/`PixelRect` in **desktop coordinates**. `Width`/`Height`/`ClientSize` are DIPs.
- **Windows:** desktop coordinates are physical pixels, and `Screen.Scaling` = monitor DPI / 96 (for example 1.5 at 144 DPI).
- **macOS:** desktop coordinates are **points**. On a Retina 2560×1600 panel ("looks like 1680×1050") the probe returned:
  - `Screen.Bounds` = 0,0,1680,1050
  - `WorkingArea` = 50,30,1630,1020 (Dock on the left, menu bar on top)
  - `Screen.Scaling` = 1, `Window.DesktopScaling` = 1, but `RenderScaling` = 2
- **Rule:** convert DIP sizes to desktop units with **`Window.DesktopScaling` (or the target `Screen.Scaling`), never `RenderScaling`**.

```csharp
public sealed record SavedBounds(int X, int Y, double WidthDip, double HeightDip, string? ScreenName);

public static class Placement
{
    /// Returns a position that keeps the window fully visible on some current working area.
    public static PixelPoint Clamp(Window w, SavedBounds saved)
    {
        var screens = w.Screens;
        var wanted = new PixelPoint(saved.X, saved.Y);
        Screen target =
            screens.All.FirstOrDefault(s => s.DisplayName == saved.ScreenName && s.WorkingArea.Contains(wanted))
            ?? screens.ScreenFromPoint(wanted)
            ?? screens.Primary
            ?? screens.All[0];
        PixelRect area = target.WorkingArea;
        int wPx = (int)Math.Ceiling(saved.WidthDip * target.Scaling);
        int hPx = (int)Math.Ceiling(saved.HeightDip * target.Scaling);
        int x = Math.Clamp(wanted.X, area.X, Math.Max(area.X, area.Right - wPx));
        int y = Math.Clamp(wanted.Y, area.Y, Math.Max(area.Y, area.Bottom - hPx));
        return new PixelPoint(x, y);
    }

    /// Bottom-right corner of the working area of the screen the widget is on (toast anchor).
    public static PixelPoint BottomRight(Window toast, Screen screen, double marginDip = 12)
    {
        var a = screen.WorkingArea; double s = screen.Scaling;
        return new PixelPoint((int)(a.Right - (toast.Width + marginDip) * s), (int)(a.Bottom - (toast.Height + marginDip) * s));
    }
}
```

- Set `WindowStartupLocation="Manual"` and assign `Position` **before** `Show()` to avoid a visible jump. Re-clamp in `Screens.Changed` (monitor unplugged, dock/undock, resolution change).
- Persist `Position` (desktop units), the size in DIPs, `Screen.DisplayName` and the compact/expanded state (architecture §16.5).
- `Screens.ScreenFromWindow(window)` gives the current monitor. On Windows, mixed-DPI setups put monitors in one pixel space with different scalings, which is why the clamp uses the target screen's `Scaling`.
- macOS menu bar at the top: `WorkingArea.Y` > 0 already accounts for it.

### 3.4 Non-activating toasts

```csharp
var toast = new ToastWindow                   // WindowDecorations=None, SizeToContent=WidthAndHeight
{
    DataContext = vm,
    ShowActivated = false,                     // don't steal focus when shown
    ShowInTaskbar = false,
    Topmost = true,
    WindowStartupLocation = WindowStartupLocation.Manual,
};
toast.Position = Placement.BottomRight(toast, widget.Screens.ScreenFromWindow(widget) ?? widget.Screens.Primary!);
toast.Opened += (_, _) =>
{
    if (OperatingSystem.IsWindows() && toast.TryGetPlatformHandle() is { } h)
        Win32WindowStyles.MakeNonActivatingToolWindow(h.Handle);   // no focus on click, not in Alt+Tab
};
toast.Show();
DispatcherTimer.RunOnce(toast.Close, TimeSpan.FromSeconds(4));     // short-lived (architecture §16.1)
```

```csharp
[SupportedOSPlatform("windows")]
public static partial class Win32WindowStyles
{
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static partial nint GetWindowLongPtr(nint hWnd, int nIndex);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint value);

    public static void MakeNonActivatingToolWindow(nint hwnd)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }
}
```

- `ShowActivated=false` only covers *showing*. On Windows a click would still activate the toast, which `WS_EX_NOACTIVATE` prevents (Verified (compile); Avalonia does not expose this style).
- `GetWindowLongPtrW` exists only on 64-bit, which is fine for win-x64/arm64.
- macOS: for an agent app (`LSUIElement`), `ShowActivated=false` windows don't activate the app (Unverified: whether a click activates).
- Close toasts (don't hide them) and reuse at most one or two instances. Queue events instead of stacking dozens of windows.

### 3.5 Hide/show and taskbar summary

| Window | Decorations | Taskbar | Topmost | Close button |
|---|---|---|---|---|
| Compact widget | `None` | no | optional (setting) | hides |
| Expanded dashboard | `Full` (or extended client area) | yes (Windows/Linux) so Alt+Tab finds it | no | hides |
| Toast | `None` | no | yes | auto-closes |
| Settings | `Full` | yes | no | closes (dialog) |

### 3.6 Linux / Wayland limitations

- `UsePlatformDetect()` uses the **X11 backend**. On a Wayland session that runs through XWayland, and most of the above works: position, Topmost (EWMH `_NET_WM_STATE_ABOVE`, if the WM honours it), and SNI tray.
- The native Wayland backend (`Avalonia.Wayland` + `.UseWayland()`, new in 12.1, **experimental**) is subject to Wayland's rules:
  - **Clients cannot set or read global window position.** `Position` is ignored, so saved placement and bottom-right toasts don't work.
  - **There is no "always on top" in xdg-shell** (`Topmost` ignored).
  - There is no stealth non-activation guarantee.
  - `BeginMoveDrag` *does* work (`xdg_toplevel.move`).
  - (Unverified for the Avalonia implementation; these are protocol-level limitations.)
- **Recommendation:** stay on X11/XWayland (default) for GitWidget. Linux is best-effort.
- Transparency on X11 needs a compositing WM. Check `ActualTransparencyLevel`.

Sources: https://docs.avaloniaui.net/docs/app-development/window-management, https://docs.avaloniaui.net/controls/primitives/window, https://docs.avaloniaui.net/docs/avalonia12-breaking-changes, https://avaloniaui.net/blog/release-12-1, https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles

---

## 4. Animations

Avalonia has three mechanisms:

1. **Transitions:** implicit; they animate a property whenever it changes. Good for count badges and colour changes.
2. **Keyframe style animations:** they run while a style selector matches, for example when a class is added. Good for one-shot effects.
3. **`Animation.RunAsync(control, ct)`:** imperative, awaitable, cancellable. **This is the best fit for "animate only newly derived events"** (architecture §16.4), because the event pipeline decides and the view gets a clear call.

The XAML below compiles and renders on 12.1.5 (Verified (local)). Transform properties (`TranslateTransform.X`, `ScaleTransform.ScaleX`) are animated through Avalonia's transform animator on `RenderTransform`.

```xml
<Styles xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <!-- Blue slide-in for a new issue row -->
  <Style Selector="Border.issue-new">
    <Style.Animations>
      <Animation Duration="0:0:0.35" Easing="CubicEaseOut" FillMode="Forward" IterationCount="1">
        <KeyFrame Cue="0%">
          <Setter Property="Opacity" Value="0" />
          <Setter Property="TranslateTransform.X" Value="24" />
        </KeyFrame>
        <KeyFrame Cue="100%">
          <Setter Property="Opacity" Value="1" />
          <Setter Property="TranslateTransform.X" Value="0" />
        </KeyFrame>
      </Animation>
    </Style.Animations>
  </Style>

  <!-- Failed build: ONE red pulse, never infinite -->
  <Style Selector="Border.build-failed-pulse">
    <Style.Animations>
      <Animation Duration="0:0:0.6" IterationCount="1" FillMode="None">
        <KeyFrame Cue="0%"><Setter Property="Background" Value="Transparent" /></KeyFrame>
        <KeyFrame Cue="40%"><Setter Property="Background" Value="#66E53935" /></KeyFrame>
        <KeyFrame Cue="100%"><Setter Property="Background" Value="Transparent" /></KeyFrame>
      </Animation>
    </Style.Animations>
  </Style>

  <!-- Issue closed: green check "pop" -->
  <Style Selector="PathIcon.closed-check">
    <Setter Property="Foreground" Value="{DynamicResource SuccessBrush}" />
    <Style.Animations>
      <Animation Duration="0:0:0.45" Easing="BackEaseOut" FillMode="Forward" IterationCount="1">
        <KeyFrame Cue="0%">
          <Setter Property="ScaleTransform.ScaleX" Value="0.2" /><Setter Property="ScaleTransform.ScaleY" Value="0.2" />
          <Setter Property="Opacity" Value="0" />
        </KeyFrame>
        <KeyFrame Cue="100%">
          <Setter Property="ScaleTransform.ScaleX" Value="1" /><Setter Property="ScaleTransform.ScaleY" Value="1" />
          <Setter Property="Opacity" Value="1" />
        </KeyFrame>
      </Animation>
    </Style.Animations>
  </Style>

  <!-- Implicit transitions for counters/badges; disabled under reduced motion -->
  <Style Selector="Border.count-badge">
    <Setter Property="Transitions">
      <Transitions>
        <BrushTransition Property="Background" Duration="0:0:0.25" />
        <DoubleTransition Property="Opacity" Duration="0:0:0.2" />
      </Transitions>
    </Setter>
  </Style>
  <Style Selector=":is(Control).reduced-motion Border.count-badge">
    <Setter Property="Transitions" Value="{x:Null}" />
  </Style>
</Styles>
```

Triggering from the event pipeline:

```csharp
public sealed class EventAnimator(IMotionPreference motion)
{
    public async Task PlayAsync(Control target, DomainEventKind kind, CancellationToken ct)
    {
        string cls = kind switch
        {
            DomainEventKind.IssueClosed => "closed-check",
            DomainEventKind.IssueOpened => "issue-new",
            DomainEventKind.BuildFailed => "build-failed-pulse",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (motion.ReduceMotion) return;                    // final state is the normal state: just show it
        target.Classes.Remove(cls);                         // re-trigger if it was already applied
        target.Classes.Add(cls);
        await Task.Delay(700, ct);                          // longest animation
        target.Classes.Remove(cls);                         // the style no longer matches, so nothing keeps ticking
    }
}
```

Performance rules for a widget that is idle almost all the time:

- **No `IterationCount="Infinite"`**, no animated "live" indicators, no blinking.
  - The "subtle pending refresh indicator" (architecture §16.2) should be a static glyph change, or an indeterminate `ProgressBar` that has `IsVisible=false` outside refresh.
  - In Avalonia 12, style animations on **hidden** controls stop ticking by default (`PlaybackBehavior.Auto`/`OnlyIfVisible`). Don't set `PlaybackBehavior="Always"`.
- Remove the trigger class after the animation (above) so no style animation stays attached.
- Animate `Opacity` and `RenderTransform` (compositor-friendly) instead of `Width`/`Margin` (layout every frame). For heavier effects use composition animations (render thread).
- Update relative times ("updated 2 min ago") with one shared 30–60 s `DispatcherTimer`, not per item.
- Never animate during baseline creation or during bulk cache load (architecture §16.4). The pipeline simply does not call `PlayAsync`.
- To check idle CPU: with the widget visible and no events, the process should sit at ~0 % CPU (Activity Monitor / Task Manager). A non-zero steady state points to a running animation or timer.

Sources: https://docs.avaloniaui.net/docs/graphics-animation/keyframe-animations, https://docs.avaloniaui.net/docs/graphics-animation/control-transitions, https://docs.avaloniaui.net/docs/graphics-animation/animation-settings, https://docs.avaloniaui.net/docs/graphics-animation/composition-animations, https://docs.avaloniaui.net/docs/avalonia12-breaking-changes

---

## 5. Reduced motion and theme detection

### 5.1 What Avalonia provides

**Nothing for reduced motion.** `IPlatformSettings` in 12.1.5 has only these members (Verified (local) by reflection):

- `GetTapSize`, `GetDoubleTapSize`, `GetDoubleTapTime`, `HoldWaitDuration`, `HotkeyConfiguration`
- `GetColorValues()` → `PlatformColorValues { ThemeVariant, ContrastPreference, AccentColor1..3 }`
- `ColorValuesChanged`

There is no animation or motion flag. The docs say the same. Read the OS setting yourself:

```csharp
public interface IMotionPreference { bool ReduceMotion { get; } }

public sealed class OsMotionPreference : IMotionPreference
{
    bool _cached; long _expires;                          // cheap TTL cache (Linux spawns processes)
    public bool ReduceMotion
    {
        get
        {
            if (Environment.TickCount64 < _expires) return _cached;
            _cached = (OperatingSystem.IsWindows() ? ReducedMotionWindows.ShouldReduceMotion()
                     : OperatingSystem.IsMacOS()   ? ReducedMotionMac.ShouldReduceMotion()
                     : OperatingSystem.IsLinux()   ? ReducedMotionLinux.ShouldReduceMotion()
                     : null) ?? false;
            _expires = Environment.TickCount64 + 60_000;
            return _cached;
        }
    }
}
```

Combine this with an app setting `Animations: System | On | Off`. Apply the `reduced-motion` class on window roots when the preference is on (§4).

### 5.2 Windows: `SPI_GETCLIENTAREAANIMATION`

`SPI_GETCLIENTAREAANIMATION = 0x1042`. `pvParam` points to a `BOOL` that receives TRUE when animations are enabled. It maps to Settings → Accessibility → Visual effects → **Animation effects** (Verified (docs); Verified (compile)).

```csharp
[SupportedOSPlatform("windows")]
static unsafe partial class ReducedMotionWindows
{
    const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint uiAction, uint uiParam, void* pvParam, uint fWinIni);

    public static bool? ShouldReduceMotion()
    {
        int enabled = 1;                                   // Win32 BOOL
        return SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, &enabled, 0) ? enabled == 0 : null;
    }
}
```

(`SPI_GETANIMATION` / `ANIMATIONINFO` is about minimize/restore animations only. Don't use it for this. WinRT `UISettings.AnimationsEnabled` is equivalent but needs the Windows SDK projection and a `-windows` TFM, which we avoid.)

### 5.3 macOS: `NSWorkspace.accessibilityDisplayShouldReduceMotion`

Call it through the Objective-C runtime (`objc_msgSend`). Verified (local): returns `False` with Reduce Motion off. AppKit is already loaded inside an Avalonia app; the explicit load is for console tests.

```csharp
[SupportedOSPlatform("macos")]
static partial class ReducedMotionMac
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial nint objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial nint sel_registerName(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint MsgSendPtr(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.U1)] private static partial bool MsgSendBool(nint receiver, nint selector);

    public static bool? ShouldReduceMotion()
    {
        NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out _);
        nint cls = objc_getClass("NSWorkspace");
        if (cls == 0) return null;
        nint ws = MsgSendPtr(cls, sel_registerName("sharedWorkspace"));
        return ws == 0 ? null : MsgSendBool(ws, sel_registerName("accessibilityDisplayShouldReduceMotion"));
    }
}
```

- On arm64, typed `objc_msgSend` imports with no varargs (as above) are correct.
- Changes are announced by `NSWorkspaceAccessibilityDisplayOptionsDidChangeNotification`. Observing that needs an Objective-C block or selector target, so re-query (TTL cache) instead.
- Alternative `defaults read com.apple.universalaccess reduceMotion`: prints `1`/`0`, but **exits with an error when the key was never set** (Verified (local): "Could not find key 'reduceMotion'…"). It also spawns a process. Use it only as a fallback.

### 5.4 Linux

Precedence:

1. **XDG Desktop Portal** `org.freedesktop.portal.Settings.ReadOne("org.freedesktop.appearance", "reduced-motion")` → `u`: 0 = no preference, 1 = reduced (Verified (docs): key documented in interface v2). Which portal backends implement it is Unverified.
   CLI fallback: `gdbus call --session --dest org.freedesktop.portal.Desktop --object-path /org/freedesktop/portal/desktop --method org.freedesktop.portal.Settings.ReadOne org.freedesktop.appearance reduced-motion` → `(<<uint32 1>>,)`.
   In-process: `Tmds.DBus.Protocol` already ships with Avalonia.FreeDesktop (no extra dependency), but there is more code to write. Unverified.
2. **GNOME:** `gsettings get org.gnome.desktop.interface enable-animations` → `true`/`false` (false = reduce).
3. Otherwise `false`.

```csharp
[SupportedOSPlatform("linux")]
static class ReducedMotionLinux
{
    public static bool? ShouldReduceMotion()
    {
        var portal = Run("gdbus", "call", "--session", "--dest", "org.freedesktop.portal.Desktop",
                         "--object-path", "/org/freedesktop/portal/desktop",
                         "--method", "org.freedesktop.portal.Settings.ReadOne", "org.freedesktop.appearance", "reduced-motion");
        if (portal is not null && portal.Contains("uint32 ")) return portal.Contains("uint32 1");
        var g = Run("gsettings", "get", "org.gnome.desktop.interface", "enable-animations");
        return g?.Trim() switch { "false" => true, "true" => false, _ => null };
    }
    static string? Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(1500) && p.ExitCode == 0 ? o : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }   // tool not installed
    }
}
```

### 5.5 Theme (dark/light), contrast, accent

- Set `RequestedThemeVariant="Default"` on `Application`, so it follows the OS. Then read `Application.Current.ActualThemeVariant` (`ThemeVariant.Light`/`Dark`) and subscribe to `ActualThemeVariantChanged` (Verified (local): the probe reported `Dark` on a dark-mode Mac).
- Use `ThemeDictionaries` in resources, keyed `Light`/`Dark`, for widget brushes (card background, semantic colours from architecture §16.3). Don't add code-behind switches.
- `Application.Current.PlatformSettings.GetColorValues()` → `ThemeVariant`, **`ContrastPreference`** (`NoPreference`/`High`: use it to drop transparency and raise contrast), and accent colours. Verified (local): `ThemeVariant = Dark, ContrastPreference = NoPreference, AccentColor1 = #ff007aff`. Change event: `PlatformSettings.ColorValuesChanged`.
- In Avalonia 12, `TopLevel.PlatformSettings` is gone. Use `Application.PlatformSettings` or `control.GetPlatformSettings()`.
- A user override ("Light/Dark/System") = set `Application.RequestedThemeVariant`.

Sources: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow, https://developer.apple.com/documentation/appkit/nsworkspace/accessibilitydisplayshouldreducemotion, https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Settings.html, https://docs.avaloniaui.net/docs/services/platform-settings, https://docs.avaloniaui.net/docs/styling/theme-variants

---

## 6. Start at login

One interface, three implementations (Verified (compile); the macOS plist writer was not exercised against launchd):

```csharp
public interface ILoginItem { bool IsEnabled { get; } void Enable(); void Disable(); }
```

**Always register the published executable path** (`Environment.ProcessPath`). Refuse, with a UI message, when `Path.GetFileNameWithoutExtension(Environment.ProcessPath)` is `dotnet`, because under `dotnet run` the path points at the SDK host. Also refuse when the path is inside a temporary or build folder. Pass `--autostart` so the app knows it was started at login (start in tray, add jitter before the first refresh).

### 6.1 Windows: HKCU Run key

```csharp
[SupportedOSPlatform("windows")]
public sealed class WindowsRunKeyLoginItem(string valueName, string exePath) : ILoginItem
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public bool IsEnabled { get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(valueName) is string; } }
    public void Enable()  { using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true); k.SetValue(valueName, $"\"{exePath}\" --autostart", RegistryValueKind.String); }
    public void Disable() { using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true); k?.DeleteValue(valueName, throwOnMissingValue: false); }
}
```

- HKCU, so no admin is needed. **Quote the path** (spaces, and the OneDrive path in your case).
- `Microsoft.Win32.Registry` is part of the `net10.0` shared framework (Windows-only at runtime; guarded by `[SupportedOSPlatform]` and the CA1416 analyzer).
- Task Manager → Startup apps "Disabled" leaves the Run value in place and writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run\<valueName>` (REG_BINARY, first byte `0x02` = enabled, `0x03` = disabled). This is undocumented, so Unverified. Read it so the checkbox reflects reality, and don't silently re-enable.
- Group Policy can disable Run keys on managed work PCs.

### 6.2 macOS: LaunchAgent plist (recommended) vs SMAppService

```csharp
[SupportedOSPlatform("macos")]
public sealed class MacLaunchAgentLoginItem(string label, string exePath) : ILoginItem
{
    string PlistPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", label + ".plist");
    public bool IsEnabled => File.Exists(PlistPath);
    public void Enable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        File.WriteAllText(PlistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{SecurityElement.Escape(label)}</string>
              <key>ProgramArguments</key>
              <array>
                <string>{SecurityElement.Escape(exePath)}</string>
                <string>--autostart</string>
              </array>
              <key>RunAtLoad</key><true/>
              <key>ProcessType</key><string>Interactive</string>
              <key>LimitLoadToSessionType</key><string>Aqua</string>
            </dict>
            </plist>
            """, new UTF8Encoding(false));
    }
    public void Disable() => File.Delete(PlistPath);   // takes effect at next login
}
```

- `exePath` = `/Applications/GitWidget.app/Contents/MacOS/GitWidget`. Don't point at a build output folder. No `KeepAlive`: if the user quits, the app should stay quit.
- launchd loads `~/Library/LaunchAgents` at login. Running `launchctl bootstrap gui/$(id -u) <plist>` is unnecessary, because the app is already running when the user ticks the box.
- macOS 13+ shows a "Background Items Added" notification. The user can toggle the agent in System Settings → General → Login Items & Extensions. The plist stays on disk when disabled there, so `IsEnabled` only means "registered" (Unverified: how to detect a user-disabled state).
- **`SMAppService.mainApp.register()` (macOS 13+)** is the modern API and shows the app under "Open at Login". It must be called from inside a proper `.app` bundle with a stable bundle id. It is Objective-C/Swift-only, so .NET would need `objc_msgSend` interop into ServiceManagement. Apple's docs expect a signed app, and behaviour with ad-hoc signatures is Unverified. **Use the LaunchAgent for now.** Switch to SMAppService if the app gets a Developer ID signature.

### 6.3 Linux: XDG autostart

```csharp
[SupportedOSPlatform("linux")]
public sealed class XdgAutostartLoginItem(string appId, string displayName, string exePath) : ILoginItem
{
    static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x && Path.IsPathRooted(x)
        ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    string FilePath => Path.Combine(ConfigHome, "autostart", appId + ".desktop");
    public bool IsEnabled => File.Exists(FilePath);
    public void Enable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, $"""
            [Desktop Entry]
            Type=Application
            Name={displayName}
            Exec={QuoteExecArg(exePath)} --autostart
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """);
    }
    public void Disable() => File.Delete(FilePath);

    // Desktop Entry "Exec": wrap in "...", backslash-escape " ` $ \ inside quotes, double '%'.
    // The .desktop string format also unescapes '\', so each backslash is written twice.
    static string QuoteExecArg(string arg)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in arg)
        {
            if (c is '"' or '`' or '$') sb.Append("\\\\").Append(c);
            else if (c == '\\') sb.Append("\\\\\\\\");
            else if (c == '%') sb.Append("%%");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
```

- Per the autostart spec, a file in `$XDG_CONFIG_HOME/autostart` with the same name as one in `$XDG_CONFIG_DIRS/autostart` overrides it, and `Hidden=true` disables it. For our own entry, deleting the file is enough.
- Most desktops honour the spec (GNOME, KDE, XFCE). Pure WMs may not.

Sources: https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys, https://developer.apple.com/library/archive/documentation/MacOSX/Conceptual/BPSystemStartup/Chapters/CreatingLaunchdJobs.html, https://developer.apple.com/documentation/servicemanagement/smappservice, https://specifications.freedesktop.org/autostart-spec/latest/, https://specifications.freedesktop.org/desktop-entry-spec/latest/exec-variables.html

---

## 7. Single instance and "focus the existing instance"

- **Named `Mutex` works cross-process on all three OSes in .NET.** On Unix the runtime implements it with lock files. The probe created them under `/tmp/.dotnet/` (Verified (local): a second process got `createdNew=false` while the first was alive and `true` after it exited).
- **.NET 10 adds `NamedWaitHandleOptions { CurrentUserOnly, CurrentSessionOnly }`** (Verified (local): new `Mutex` constructors exist and work on macOS). Use both flags instead of hand-crafting `Local\`/`Global\` prefixes.
  - `CurrentSessionOnly` corresponds to `Local\` on Windows.
  - `CurrentUserOnly` adds an ACL/ownership check, so another user can't squat the name.
  - The probe also accepted the classic `Local\`/`Global\` prefixes on macOS.
- **Signal the running instance with `NamedPipeServerStream`/`ClientStream`**. They are portable: real named pipes on Windows, Unix domain sockets on macOS/Linux. Use `PipeOptions.CurrentUserOnly`. Accept only whitelisted commands.
- **Pitfall (Verified (local)): `Mutex` has thread affinity.** Calling `ReleaseMutex()` after an `await` (a different thread) throws `ApplicationException: Object synchronization method was called from an unsynchronized block of code`. With the `createdNew` pattern you don't need to release at all: dispose the handle at shutdown, and process exit drops ownership either way.
- A lock file with `FileShare.None` (`new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)`) also works. Use it only if the mutex causes trouble, for example with sandboxing (`/tmp` differs inside an App Sandbox; GitWidget isn't sandboxed).

Verified (local): primary receives `show`, secondary gets `signalled=True`, and a restart after the primary exits works.

```csharp
public sealed class SingleInstance : IAsyncDisposable
{
    readonly Mutex _mutex;
    readonly string _pipeName;
    readonly CancellationTokenSource _cts = new();
    Task? _server;
    public bool IsPrimary { get; }

    public SingleInstance(string appId)
    {
        string user = Environment.UserName;                 // per-user names (RDP, fast user switching)
        _pipeName = $"{appId}.{user}.ipc";
        _mutex = new Mutex(initiallyOwned: true, $"{appId}.{user}",
                           new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true },
                           out bool createdNew);
        IsPrimary = createdNew;
    }

    public void StartListening(Func<string, Task> onCommand) => _server = Task.Run(async () =>
    {
        while (!_cts.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await server.WaitForConnectionAsync(_cts.Token); } catch (OperationCanceledException) { break; }
            using var reader = new StreamReader(server, Encoding.UTF8);
            var line = await reader.ReadLineAsync(_cts.Token);
            if (line is "show" or "dashboard") await onCommand(line);      // whitelist; ignore anything else
        }
    });

    public static async Task<bool> SignalPrimaryAsync(string appId, string command, TimeSpan timeout)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", $"{appId}.{Environment.UserName}.ipc",
                PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync(cts.Token);
            await client.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_server is not null) await _server;
        _mutex.Dispose();          // no ReleaseMutex(): thread affinity, see pitfall above
    }
}
```

- In the primary, `onCommand` must marshal to the UI thread: `Dispatcher.UIThread.InvokeAsync(() => windows.ShowWidget())`.
- On Windows, showing a window from a background trigger may only flash the taskbar (foreground lock). For a topmost widget that is acceptable. Unverified whether Avalonia's `Activate()` gets around it.
- macOS: launching the **bundle** a second time doesn't start a process (see Reopen in §2.5). Launching the **binary** directly does, and the mutex + pipe handles that case.

Sources: https://learn.microsoft.com/en-us/dotnet/api/system.threading.namedwaithandleoptions, https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex, https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions

---

## 8. Sleep/resume and network changes

### 8.1 Resume detection: gap detection with `TimeProvider`

- `Microsoft.Win32.SystemEvents.PowerModeChanged` (package `Microsoft.Win32.SystemEvents` 10.0.14) is **Windows-only**. It creates a hidden window on its own thread.
- macOS (`NSWorkspaceDidWakeNotification`) and Linux (logind `org.freedesktop.login1.Manager.PrepareForSleep(bool)` over D-Bus) need native or D-Bus observers.
- Monotonic clocks differ per OS in whether they count suspended time:
  - Linux `CLOCK_MONOTONIC` excludes suspend.
  - macOS uptime clocks exclude sleep.
  - Windows QPC behaviour around sleep is not clearly documented.
  - (Unverified for the exact clocks .NET's `Stopwatch` uses per OS.)
- **Don't rely on monotonic-clock semantics.**

Portable approach: a periodic timer doesn't fire while the machine sleeps. Compare the **wall clock** between ticks. A gap much larger than the tick interval means "resumed from sleep" (or the clock jumped), and either way the right reaction is the same: refresh soon, with jitter.

```csharp
public sealed class ResumeDetector(TimeProvider time, TimeSpan tick, TimeSpan tolerance) : IAsyncDisposable
{
    readonly CancellationTokenSource _cts = new();
    Task? _loop;
    public event Action<TimeSpan>? Resumed;               // observed gap

    public void Start() => _loop = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(tick, time);
        DateTimeOffset last = time.GetUtcNow();
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                DateTimeOffset now = time.GetUtcNow();
                TimeSpan gap = now - last; last = now;
                if (gap > tick + tolerance || gap < TimeSpan.Zero) Resumed?.Invoke(gap);
            }
        }
        catch (OperationCanceledException) { }
    });

    public async ValueTask DisposeAsync() { _cts.Cancel(); if (_loop is not null) await _loop; _cts.Dispose(); }
}
// new ResumeDetector(TimeProvider.System, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30))
```

- Verified (local) with `FakeTimeProvider` 10.11.0: four normal 30 s ticks raised no event, and a 45 min jump (`SetUtcNow` or one `Advance(45min)`) raised exactly one `Resumed(00:45:00)`. This is also the unit-test pattern.
- On `Resumed`:
  - mark snapshots "possibly stale" (UI freshness)
  - cancel in-flight requests (sockets are probably dead)
  - schedule a refresh with 5–30 s jitter (the network may not be up yet)
  - let the resilience pipeline retry
- Set `SocketsHttpHandler.PooledConnectionLifetime` (for example 5 min) so pooled connections from before sleep are not reused for long. `IHttpClientFactory` handler rotation also helps.
- Optional on Windows: add `SystemEvents.PowerModeChanged` (`PowerModes.Resume`) for an immediate signal. Gap detection stays the cross-platform baseline.

### 8.2 `NetworkChange`

- `NetworkChange.NetworkAvailabilityChanged` fires when "any network interface is up and not loopback/tunnel" changes. **It is a weak signal.**
  - Virtual adapters (Hyper-V, WSL, Docker, VPN TAP) keep it `true` while the real network is down.
  - Captive portals and DNS failures don't affect it.
  - It never tells you whether `api.github.com` or the company GitLab is reachable.
  - Docs don't promise equal behaviour across OSes (Unverified details).
- `NetworkChange.NetworkAddressChanged` fires on any address change (Wi-Fi switch, VPN connect). This is **the more useful trigger**: debounce it (2–5 s) and schedule a refresh.
- **Offline state comes from actual request outcomes** (architecture §20: classify DNS/connect/timeout as offline, keep the last snapshot, show freshness). Network events only shorten the wait before the next attempt.
- Subscribe once (static events) and unsubscribe at shutdown. Handlers run on thread-pool threads.

Sources: https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview, https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.systemevents.powermodechanged, https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.networkchange.networkavailabilitychanged, https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.pooledconnectionlifetime

---

## 9. Opening URLs safely

Architecture §23.3: allow only `https` (plus `http` for explicitly configured internal GitLab hosts). Provider URLs are untrusted.

Two routes:

- **Avalonia `ILauncher`:** `TopLevel.GetTopLevel(control)!.Launcher.LaunchUriAsync(uri)` returns `Task<bool>`, supported on Windows/macOS/Linux. It needs a `TopLevel`, so it fails in tray-only mode with no window open. The underlying mechanism per OS is Unverified.
- **Own launcher:** works without a window. Validate first, then never go through a shell, and pass the URL as a single argv element.

```csharp
public static class UrlLauncher
{
    public static bool TryOpen(Uri uri, bool allowHttp = false)
    {
        if (!uri.IsAbsoluteUri) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;           // no credentials in URLs
        // optional: host allow-list = configured provider hosts (github.com, gitlab.company.tld)
        string url = uri.AbsoluteUri;                                      // normalized, percent-encoded, no spaces/quotes
        ProcessStartInfo psi =
            OperatingSystem.IsWindows() ? new ProcessStartInfo(url) { UseShellExecute = true }   // ShellExecuteEx → default https handler
          : OperatingSystem.IsMacOS()   ? new ProcessStartInfo("/usr/bin/open") { ArgumentList = { url } }
          :                               new ProcessStartInfo("xdg-open") { ArgumentList = { url } };
        psi.UseShellExecute = OperatingSystem.IsWindows();
        try { using var p = Process.Start(psi); return true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}
```

- Verified (local): `javascript:` and `file:` URIs are rejected; real opening was not exercised.
- Why this is safe:
  - `ArgumentList` passes exactly one argv entry, with no shell or quoting.
  - A validated `https://…` absolute URI can't start with `-`, so `open`/`xdg-open` can't misread it as an option.
  - `Uri.AbsoluteUri` percent-encodes spaces and quotes.
  - On Windows, `UseShellExecute` with a string that is guaranteed to be an `https:` URL can only reach the registered https handler, never an executable path.
- **Never** do `Process.Start("cmd", "/c start " + url)` or `sh -c "xdg-open " + url`. On Windows `start` parses `&`, `^` and `|`, which is a classic injection.
- Build provider URLs from components (`new Uri(baseUri, $"{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{number}")`) rather than trusting `html_url` from the payload. Or validate `html_url`'s host against the configured provider host.

Sources: https://docs.avaloniaui.net/docs/services/launcher, https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist, https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.useshellexecute

---

## 10. App data and log paths

What `Environment.GetFolderPath` returns:

| SpecialFolder | Windows | macOS (.NET 8+) | Linux |
|---|---|---|---|
| `LocalApplicationData` | `%LOCALAPPDATA%` (`C:\Users\u\AppData\Local`) | **`~/Library/Application Support`** (Verified (local), .NET 10.0.11) | `$XDG_DATA_HOME` or `~/.local/share` |
| `ApplicationData` | `%APPDATA%` (Roaming) | `~/Library/Application Support` (Verified (local)) | `$XDG_CONFIG_HOME` or `~/.config` |
| `UserProfile` | `C:\Users\u` | `/Users/u` | `/home/u` |

Notes:

- Before .NET 8, macOS `LocalApplicationData` returned `~/.local/share`. .NET 8 changed it to `~/Library/Application Support`.
- On macOS `LocalApplicationData` and `ApplicationData` are the same folder, so don't use the pair to separate "machine-local" from "roaming" there.
- Linux values follow .NET's XDG handling (Unverified exact fallback rules).

GitWidget layout. It keeps architecture §19 and only changes the root per OS:

| Purpose | Windows | macOS | Linux |
|---|---|---|---|
| settings, window-state, cache/ (snapshots, event ledger) | `%LOCALAPPDATA%\GitWidget\` | `~/Library/Application Support/GitWidget/` | `${XDG_DATA_HOME:-~/.local/share}/GitWidget/` (Unverified: settings could go to `$XDG_CONFIG_HOME`; keeping one root is simpler) |
| logs | `%LOCALAPPDATA%\GitWidget\logs\` | `~/Library/Logs/GitWidget/` (Apple convention; Console.app shows it) | `${XDG_STATE_HOME:-~/.local/state}/GitWidget/logs/` (basedir spec 0.8: state = logs, history) |

```csharp
public static class AppPaths
{
    public static string DataRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "GitWidget");
    public static string Logs { get; } =
        OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Logs", "GitWidget") :
        OperatingSystem.IsLinux() ? Path.Combine(XdgOr("XDG_STATE_HOME", Path.Combine(Home, ".local", "state")), "GitWidget", "logs") :
        Path.Combine(DataRoot, "logs");
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string XdgOr(string v, string fallback) => Environment.GetEnvironmentVariable(v) is { Length: > 0 } p && Path.IsPathRooted(p) ? p : fallback;

    public static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);                // inherits user-only ACL of the profile
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);   // 0700
    }
}
```

- The snapshot cache and event ledger are not regenerable without side effects: losing the ledger re-triggers animations. Keep them in Application Support, not in `~/Library/Caches` or `$XDG_CACHE_HOME`, which the OS or cleanup tools may purge.
- Write JSON atomically: write a temp file in the same directory, then `File.Move(tmp, path, overwrite: true)`.

Sources: https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/8.0/getfolderpath-unix, https://learn.microsoft.com/en-us/dotnet/api/system.environment.specialfolder, https://specifications.freedesktop.org/basedir-spec/latest/, https://developer.apple.com/library/archive/documentation/FileManagement/Conceptual/FileSystemProgrammingGuide/MacOSXDirectories/MacOSXDirectories.html (Unverified URL)

---

## 11. Publishing and packaging

### 11.1 Keep the solution WPF-free so it builds everywhere

- All projects `TargetFramework=net10.0`. **No `net10.0-windows`, no `UseWPF`/`UseWindowsForms`.** Remove `H.NotifyIcon.Wpf` and use the Avalonia `TrayIcon`.
- Put platform code behind interfaces (`ICredentialStore`, `ILoginItem`, `IMotionPreference`) with `[SupportedOSPlatform("…")]` implementations and `OperatingSystem.IsWindows()` guards. Let analyzer **CA1416** flag unguarded calls (promote it to an error).
- Interop uses `LibraryImport` and needs `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in the project that contains it (for example `GitWidget.Platform`).
- Add `<IsAotCompatible>true</IsAotCompatible>` to every library. It enables the trim/AOT analyzers, and the sketches in this doc produce zero warnings with it (Verified (compile)).
- Use central package management (`Directory.Packages.props`) with the versions from §0.1.
- The repo lives in OneDrive. Redirect build output with `<ArtifactsPath>` (or `UseArtifactsOutput`) in `Directory.Build.props` to a non-synced folder, so OneDrive doesn't churn on `bin/obj`. Unverified whether the path can point outside the repo in every tool.

### 11.2 `dotnet publish` per RID

```bash
export AVALONIA_TELEMETRY_OPTOUT=1
P=src/GitWidget.Desktop/GitWidget.Desktop.csproj
# Windows (primary)
dotnet publish $P -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=true -o artifacts/win-x64
dotnet publish $P -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=true -o artifacts/win-arm64
# macOS: keep native dylibs next to the exe (inside the .app they get signed with the bundle)
dotnet publish $P -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -o artifacts/osx-arm64
# Linux
dotnet publish $P -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=true -o artifacts/linux-x64
```

- `IncludeNativeLibrariesForSelfExtract=true` packs Skia/HarfBuzz/ANGLE into the exe, and they are **extracted at first run** to a per-user folder. That's fine on Windows/Linux. On macOS, leave the 3 dylibs next to the executable instead (extracted copies are outside the bundle signature). Unverified whether Gatekeeper objects to extracted copies.
- Measured sizes for a minimal Avalonia 12.1.5 tray app (tray, 1 window, Fluent theme, CommunityToolkit.Mvvm), osx-arm64, Verified (local):

| Variant | Output | Zipped |
|---|---|---|
| self-contained single-file, untrimmed | 104 MB exe | – |
| self-contained single-file, **trimmed** | **37 MB** exe | 16 MB |
| **Native AOT** | 14 MB exe + `libSkiaSharp` 15 MB + `libHarfBuzzSharp` 2.8 MB + `libAvaloniaNative` 1.5 MB (≈ 34 MB) + 45 MB `.dSYM` (don't ship it) | 14.7 MB |

  Both the trimmed and the AOT builds ran correctly with **zero trim/AOT warnings**. Expect a few MB more with Hosting, HttpClient, resilience and System.Text.Json.
- The SDK 10.0.400 used here bundles runtime 10.0.11. AOT publish showed `NU1903: Package 'Microsoft.DotNet.ILCompiler' 10.0.11 has a known high severity vulnerability`. **Keep the SDK on the latest 10.0 patch** (Microsoft.Extensions.* are at 10.0.14). Which patch fixes it is Unverified.

### 11.3 Trimming / Native AOT status

- Avalonia supports trimming and Native AOT (`PublishAot=true` in the exe, `IsAotCompatible=true` everywhere). Requirements:
  - compiled bindings: default in 12, `x:DataType` on every view
  - no runtime XAML loading
  - assets as `AvaloniaResource`
  - DI registered at startup without reflection scanning
  - add a `TrimmerRootAssembly` for anything that still uses reflection
  - `BuiltInComInteropSupport=false` was needed only before 12.0
- Our stack:
  - **System.Text.Json:** use `JsonSerializerContext` source generation for settings, snapshots and provider DTOs. Set `<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>` to catch misses at dev time.
  - **Microsoft.Extensions.Configuration binding** (`services.Configure<T>(section)`) uses reflection. Set `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>`.
  - **CommunityToolkit.Mvvm** source generators are AOT-safe (Verified (local): no warnings).
  - **ReflectionBinding** / `{Binding}` with an `object` DataType breaks under trimming. Keep compiled bindings.
- Native AOT can't cross-compile between OSes. Each RID is built on its own OS (the CI matrix does this). Windows AOT needs the MSVC C++ build tools; the hosted runners have them (Unverified).
- **Recommendation:** ship the MVP as **self-contained + single-file + trimmed**. Enable Native AOT later if startup time or memory matters. The code base is already set up for it.

### 11.4 macOS `.app` bundle

```text
GitWidget.app/
  Contents/
    Info.plist
    MacOS/
      GitWidget                 # published executable (CFBundleExecutable)
      libSkiaSharp.dylib
      libHarfBuzzSharp.dylib
      libAvaloniaNative.dylib
    Resources/
      GitWidget.icns
```

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>com.example.GitWidget</string>
  <key>CFBundleName</key><string>GitWidget</string>
  <key>CFBundleDisplayName</key><string>GitWidget</string>
  <key>CFBundleExecutable</key><string>GitWidget</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>CFBundleIconFile</key><string>GitWidget.icns</string>
  <key>LSMinimumSystemVersion</key><string>15.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
```

- `LSMinimumSystemVersion 15.0` follows the .NET 10 supported-OS list, which gives macOS 15 as the minimum.
- `CFBundleName` should be ≤ 15 characters.

```bash
APP=artifacts/GitWidget.app
rm -rf "$APP" && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R artifacts/osx-arm64/. "$APP/Contents/MacOS/"
rm -f "$APP/Contents/MacOS/"*.pdb
cp packaging/macos/Info.plist "$APP/Contents/Info.plist"
cp packaging/macos/GitWidget.icns "$APP/Contents/Resources/"
# ad-hoc (identity "-"): sign nested code first, then the bundle; no --deep, no hardened runtime needed
for f in "$APP/Contents/MacOS/"*.dylib; do codesign --force --sign - "$f"; done
codesign --force --sign - --identifier com.example.GitWidget "$APP"
codesign --verify --strict --verbose=2 "$APP"
# for stable Keychain ACLs use a self-signed cert instead of "-":  codesign --force --sign "GitWidget Dev" …
```

- Apple Silicon refuses to run unsigned arm64 code. The SDK ad-hoc signs the apphost automatically (Verified (local): `Signature=adhoc`; the AOT binary is `linker-signed`). **Any change to the bundle afterwards needs a re-sign.**
- Hardened runtime (`--options runtime`) is only needed for notarization. If you enable it, .NET needs the entitlement `com.apple.security.cs.allow-jit`, per Avalonia's docs ("the only one that is actually needed").
- Notarization needs a paid Developer ID: `xcrun notarytool submit … --wait`, then `xcrun stapler staple`. Zip with `ditto -c -k --sequesterRsrc --keepParent`.
- **Gatekeeper/quarantine:**
  - A locally built bundle has no `com.apple.quarantine` attribute and just runs.
  - A bundle **downloaded** in a browser (for example a CI artifact) is quarantined. Ad-hoc signed and not notarized, it is blocked ("Apple could not verify…").
  - To allow it: System Settings → Privacy & Security → "Open Anyway", or `xattr -dr com.apple.quarantine GitWidget.app`.
  - Recent macOS versions removed the Control-click → Open bypass (Unverified for the exact version).
- Avalonia recommends their **Parcel** tool for packaging. Optional; the script above is enough for personal use.

### 11.5 Windows notes (SmartScreen, work PC)

- An unsigned exe **downloaded** from the internet (it carries Mark-of-the-Web) triggers SmartScreen "Windows protected your PC" → "More info" → "Run anyway". Locally built output, or files you unblock (`Unblock-File`), carry no MOTW.
- On the **work PC**, AppLocker/WDAC/Smart App Control or EDR may block unsigned binaries running from user-writable paths like `%LOCALAPPDATA%`. Check with IT early.
- Signing options: an OV/EV certificate, or Microsoft's cloud signing service (Azure Trusted Signing; current product name/eligibility Unverified). This matches architecture §23.8 ("sign release binaries when moving beyond personal development").
- A Windows `app.manifest` declaring Windows 10/11 `supportedOS` keeps DPI/version behaviour correct. The Avalonia templates include one, and per-monitor DPI awareness is set by Avalonia (`Win32PlatformOptions.DpiAwareness`).

### 11.6 Logging with minimal dependencies

Options (all plug into `Microsoft.Extensions.Logging`):

- **Tiny own `ILoggerProvider` (~100 LOC, recommended).** Use a `Channel<string>` and one background writer, a daily file `git-status-widget-yyyyMMdd.log`, 14-day retention, size cap, and **central redaction** (architecture §18.2/§22). No dependencies.
- `NReco.Logging.File` 1.4.0 (MIT; deps: `Microsoft.Extensions.Logging(.Configuration)`, `Options.ConfigurationExtensions`, which Hosting brings anyway). Rolling by size/date.
- `Karambolo.Extensions.Logging.File` 4.1.1 (MIT; same plus `FileProviders.Physical`).
- Avoid the Serilog stack for this app (several packages; `Serilog.Extensions.Logging.File` 3.0.0 is old).

```csharp
public sealed class FileLoggerProvider : ILoggerProvider
{
    readonly Channel<string> _ch = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    readonly Task _writer;
    readonly string _dir;
    public FileLoggerProvider(string dir, int retainDays = 14)
    {
        _dir = dir; AppPaths.EnsurePrivateDirectory(dir);
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("git-status-widget-*.log").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-retainDays))) f.Delete();
        _writer = Task.Run(WriteLoop);
    }
    public ILogger CreateLogger(string category) => new FileLogger(category, _ch.Writer);
    async Task WriteLoop()
    {
        await foreach (var line in _ch.Reader.ReadAllAsync())
        {
            var path = Path.Combine(_dir, $"git-status-widget-{DateTime.UtcNow:yyyyMMdd}.log");
            await File.AppendAllTextAsync(path, line + Environment.NewLine);   // simple; batch if volume grows
        }
    }
    public void Dispose() { _ch.Writer.TryComplete(); _writer.Wait(TimeSpan.FromSeconds(2)); }   // §26 "flush logs"

    sealed class FileLogger(string category, ChannelWriter<string> w) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            w.TryWrite($"{DateTimeOffset.UtcNow:O} [{level}] {category}: {Redactor.Redact(fmt(state, ex))}{(ex is null ? "" : " | " + Redactor.Redact(ex.ToString()))}");
        }
    }
}
// builder.Logging.ClearProviders().AddProvider(new FileLoggerProvider(AppPaths.Logs)); (+ AddDebug() in Debug)
```

`Redactor` masks `Authorization`, `PRIVATE-TOKEN`, cookies, `ghp_…`/`github_pat_…`/`glpat-…` patterns and secret-bearing query parameters (architecture §18.2).

Sources: https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview, https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/, https://docs.avaloniaui.net/docs/deployment/native-aot, https://docs.avaloniaui.net/docs/deployment/macos, https://docs.avaloniaui.net/docs/data-binding/compiled-bindings, https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md, https://www.nuget.org/packages/NReco.Logging.File, https://www.nuget.org/packages/Karambolo.Extensions.Logging.File

---

## 12. GitHub Actions

Current major versions (Verified (GitHub API), 2026-11-04):

| Action | Major (latest) |
|---|---|
| `actions/checkout` | **v7** (v7.0.1) |
| `actions/setup-dotnet` | **v6** (v6.1.0, node24; inputs `dotnet-version`, `cache`, `cache-dependency-path`, `global-json-file`) |
| `actions/upload-artifact` | **v7** (v7.0.2). The brief's "v4" still runs, but v6+ is node24 |
| `actions/download-artifact` | v8 (v8.0.2) |
| `actions/cache` | **v6** (v6.1.0) |

### 12.1 Test projects: xUnit v3 + Avalonia headless

Verified (local) on macOS with Avalonia.Headless.XUnit 12.1.5:

| xunit.v3 | Runner mode | Result |
|---|---|---|
| **3.2.2** | VSTest (`xunit.runner.visualstudio` 3.1.5 + `Microsoft.NET.Test.Sdk` 18.11.0) | **2/2 passed** |
| **3.2.2** | Microsoft.Testing.Platform (global.json `"test": {"runner": "Microsoft.Testing.Platform"}`) | **2/2 passed** |
| 4.0.2 | VSTest | fails at build: "Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK" |
| 4.0.2 | MTP | `[AvaloniaFact]` fails: `MissingMethodException … Xunit.v3.TestIntrospectionHelper…` in `AvaloniaFactDiscoverer`. **Incompatible** |

→ **Pin `xunit.v3` 3.2.2** until Avalonia ships a headless package built against xUnit v4.

```xml
<!-- tests/GitWidget.Desktop.Tests/GitWidget.Desktop.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>                <!-- xUnit v3 test projects are executables -->
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia.Headless.XUnit" Version="12.1.5" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="12.1.5" />
    <PackageReference Include="xunit.v3" Version="3.2.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.11.0" />
  </ItemGroup>
</Project>
```

```csharp
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
public class TestApp : Application { public override void Initialize() => Styles.Add(new FluentTheme()); }
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
public class WidgetTests
{
    [AvaloniaFact]                                   // runs on the Avalonia UI thread
    public void Widget_shows_open_count()
    {
        var vm = new WidgetViewModel(/* fakes */);
        var w = new WidgetWindow { DataContext = vm };
        w.Show();
        Assert.Equal("3", w.FindControl<TextBlock>("OpenCount")!.Text);
    }
}
```

- Default isolation is per test (`AvaloniaTestIsolationLevel.PerTest`).
- Plain ViewModel tests need no headless platform: use `[Fact]`, with `FakeTimeProvider` for timing.
- Headless needs no display server, so it runs on `ubuntu-latest` without Xvfb. Unverified on Linux CI; it is designed for this.
- Credential store integration tests: tag them `[Trait("Category","OsIntegration")]` and skip them in CI. Hosted macOS runners' keychains and the absent Linux Secret Service make them flaky. Run them manually on the dev machines, and use `InMemoryCredentialStore` everywhere else.

### 12.2 Workflow

```yaml
name: ci
on:
  push: { branches: [main] }
  pull_request:
permissions:
  contents: read
env:
  DOTNET_NOLOGO: "1"
  DOTNET_CLI_TELEMETRY_OPTOUT: "1"
  AVALONIA_TELEMETRY_OPTOUT: "1"
  NUGET_PACKAGES: ${{ github.workspace }}/.nuget/packages

jobs:
  build:
    strategy:
      fail-fast: false
      matrix:
        include:
          - os: windows-latest
            rids: "win-x64 win-arm64"
          - os: macos-latest            # Apple Silicon runner (Unverified: current image)
            rids: "osx-arm64"
          - os: ubuntu-latest
            rids: "linux-x64"
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v7

      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x        # or global-json-file: global.json
          cache: true                   # needs packages.lock.json (RestorePackagesWithLockFile=true)
          cache-dependency-path: "**/packages.lock.json"

      - run: dotnet restore --locked-mode
      - run: dotnet build -c Release --no-restore
      - run: dotnet test -c Release --no-build --filter "Category!=OsIntegration" --logger "trx;LogFilePrefix=${{ runner.os }}" --results-directory TestResults

      - uses: actions/upload-artifact@v7
        if: always()
        with:
          name: test-results-${{ runner.os }}
          path: TestResults/
          retention-days: 7

      - name: Publish
        shell: bash
        run: |
          for rid in ${{ matrix.rids }}; do
            extra="-p:IncludeNativeLibrariesForSelfExtract=true"
            [[ $rid == osx-* ]] && extra=""
            dotnet publish src/GitWidget.Desktop/GitWidget.Desktop.csproj -c Release -r $rid --self-contained \
              -p:PublishSingleFile=true -p:PublishTrimmed=true $extra -o artifacts/$rid
          done

      - name: Bundle + ad-hoc sign (macOS)
        if: runner.os == 'macOS'
        run: ./packaging/macos/make-app.sh artifacts/osx-arm64 artifacts/GitWidget.app   # script from §11.4

      - uses: actions/upload-artifact@v7
        with:
          name: gitwidget-${{ runner.os }}
          path: |
            artifacts/*
            !artifacts/**/*.pdb
            !artifacts/**/*.dSYM
          retention-days: 14
```

- **Caching:** `setup-dotnet`'s `cache: true` caches the global packages folder keyed on `packages.lock.json`. Turn on `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` in `Directory.Build.props` and commit the lock files. Without lock files, use `actions/cache@v6` on `${{ env.NUGET_PACKAGES }}` keyed on `hashFiles('**/*.csproj', 'Directory.Packages.props')`.
- **macOS artifacts:** `upload-artifact` zips everything, which may drop the executable bit and symlinks inside the `.app` (Unverified for v7). Zipping the bundle yourself first is safer: `ditto -c -k --keepParent artifacts/GitWidget.app artifacts/GitWidget-osx-arm64.zip`. Downloaded artifacts are quarantined (§11.4).
- If a step must run in MTP mode instead of VSTest, add `global.json` with `"test": { "runner": "Microsoft.Testing.Platform" }` and drop `Microsoft.NET.Test.Sdk`/`xunit.runner.visualstudio`. Both work with xunit.v3 3.2.2 (§12.1).
- Pin actions to commit SHAs if you want supply-chain hardening (setup-dotnet's own repo does this as of v5.4).

Sources: https://github.com/actions/setup-dotnet, https://github.com/actions/upload-artifact, https://github.com/actions/cache, https://github.com/actions/checkout, https://docs.avaloniaui.net/docs/testing/headless-xunit, https://xunit.net/docs/getting-started/v3/whats-new (Unverified URL)

---

## 13. Changes to the architecture document implied by this reference

| Architecture item | Change |
|---|---|
| §5 Desktop UI "WPF", Tray "H.NotifyIcon.Wpf" | Avalonia **12.1.x** `TrayIcon`/`NativeMenu` (§2) |
| §5 Credentials "Windows Credential Manager" | `ICredentialStore` with Windows Credential Manager / macOS Keychain / libsecret, own implementation (§1) |
| §5 Local settings "JSON under %LocalAppData%" | `LocalApplicationData/GitWidget` per OS. Logs in OS-conventional log folders (§10) |
| §5 Tests "FluentAssertions" | Licensing check (v8 is commercial). Consider AwesomeAssertions. Pin xunit.v3 3.2.2 |
| §16.4 "Respect the Windows reduced-motion preference" | Respect the OS preference on all three platforms (§5) |
| §16.5 / §17 "Start with Windows" | "Start at login" via `ILoginItem` (§6) |
| §23.2 "Render titles as plain WPF text" | Plain Avalonia `TextBlock.Text`. Never markup, `InlineCollection` parsing or HTML |
| §25 step 1 single instance | `Mutex` with `NamedWaitHandleOptions` + named-pipe "show" (§7) |
| §26 step 6 "Dispose tray icon explicitly" | `TrayIcon.Dispose()` for each icon in `TrayIcon.GetIcons(app)` during shutdown (Windows otherwise may leave a ghost icon until mouse-over; Unverified whether Avalonia already handles this) |

---

## Appendix A: What was verified locally

All checks ran in the session scratchpad (not in this repo) on macOS 27.0.1 / Apple Silicon / .NET SDK 10.0.400:

1. Reflection over Avalonia 12.1.5 assemblies:
   - `IPlatformSettings` (no reduced-motion member)
   - `TrayIcon`, `NativeMenuItem` (`MenuItemToggleType`), `Window`/`WindowBase` (`WindowDecorations`, `ShowActivated`, `ShowInTaskbar`, `Topmost`, `BeginMoveDrag`, `ClosingBehavior`)
   - `MacOSPlatformOptions.ShowInDock`, `Screens`/`Screen`, `ILauncher`, `IActivatableLifetime`/`ActivationKind.Reopen`
   - `WindowCloseReason`, `Animation.PlaybackBehavior`, `MacOSProperties.IsTemplateIcon`
2. A minimal Avalonia 12.1.5 tray app (App.axaml tray + dynamic radio submenu + borderless acrylic topmost widget + keyframe styles):
   - builds with compiled bindings
   - runs on macOS
   - reported theme, colour values, screen geometry (points, `DesktopScaling` 1 vs `RenderScaling` 2) and `ActualTransparencyLevel=AcrylicBlur`
3. Found: `x:Name` on `NativeMenu` → AVLN2000. A `null` `ToolTipText` at attach crashes `Avalonia.Native`.
4. Publish osx-arm64: single-file 104 MB, trimmed 37 MB, Native AOT 14 MB + 19 MB dylibs. All ran with zero trim/AOT warnings. Ad-hoc signature with a cdhash designated requirement.
5. `MacKeychainCredentialStore`: full CRUD round trip (also under AOT).
6. `WindowsCredentialStore`, `LibSecretCredentialStore`, Windows SPI, Win32 window styles, login items, URL launcher: compile with `IsAotCompatible=true` and no warnings. Struct sizes match native x64 (80 / 592 bytes).
7. `NSWorkspace.accessibilityDisplayShouldReduceMotion` via `objc_msgSend`. `defaults read … reduceMotion` fails when the key is unset.
8. `Environment.GetFolderPath` on macOS .NET 10.0.11.
9. Named mutex across processes + `NamedWaitHandleOptions`. Named-pipe "show" signalling. `ReleaseMutex` thread-affinity exception after `await`.
10. `ResumeDetector` with `FakeTimeProvider`.
11. Avalonia.Headless.XUnit 12.1.5 with xunit.v3 3.2.2 (VSTest + MTP pass) and 4.0.2 (fails).

Not verified (needs a Windows or Linux machine): runtime behaviour of the Windows and libsecret stores, the Windows toast style, Run/StartupApproved keys, LaunchAgent loading, XDG autostart, Linux tray hosts, Wayland behaviour, and the keychain re-prompt after a rebuild.
