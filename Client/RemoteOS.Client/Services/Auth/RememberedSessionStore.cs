using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Identity;
using RemoteOS.Protocol.Workspace;

namespace Client.Services.Auth;

/// <summary>Stores opted-in credentials in the platform's encrypted credential vault.</summary>
public interface IRememberedSessionStore
{
    Task<RememberedSession?> LoadAsync(CancellationToken ct = default);
    Task<bool> SaveAsync(RememberedSession session, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>Everything in this record, including Password, is written only to encrypted OS credential storage.</summary>
public sealed record RememberedSession(
    string ServerUrl,
    AuthTokens Tokens,
    UserDto User,
    WorkspaceDto Workspace,
    SessionDto Session,
    DeviceDto Device,
    DeviceRole AssignedRole,
    string? Password)
{
    public static RememberedSession From(string serverUrl, LoginResponse response, string password)
        => new(
            serverUrl,
            response.Tokens,
            response.User,
            response.Workspace,
            response.Session,
            response.Device,
            response.AssignedRole,
            password);

    public static RememberedSession From(string serverUrl, AuthSession session, string? password)
        => new(
            serverUrl,
            session.Tokens!,
            session.CurrentUser!,
            session.CurrentWorkspace!,
            session.CurrentSession!,
            session.CurrentDevice!,
            session.AssignedRole,
            password);

    public LoginResponse ToLoginResponse(AuthTokens tokens)
        => new(User, Workspace, Session, Device, tokens, AssignedRole);
}

/// <summary>
/// Uses DPAPI on Windows, Keychain on macOS, and the Secret Service API on Linux.
/// No unencrypted credential file is created on any platform.
/// </summary>
public sealed class RememberedSessionStore : IRememberedSessionStore
{
    private static readonly byte[] Entropy = "RemoteOS.RememberedSession.v2"u8.ToArray();
    private readonly string _windowsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteOS",
        "remembered-session.bin");

    public async Task<RememberedSession?> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var payload = OperatingSystem.IsWindows()
                ? await LoadWindowsAsync(ct)
                : OperatingSystem.IsMacOS()
                    ? MacKeychain.TryRead(out var macValue) ? macValue : null
                    : OperatingSystem.IsLinux()
                        ? LinuxSecretService.TryRead(out var linuxValue) ? linuxValue : null
                        : null;
            return payload is null ? null : Deserialize(payload);
        }
        catch (CryptographicException)
        {
            await ClearAsync(ct);
            return null;
        }
        catch (JsonException)
        {
            await ClearAsync(ct);
            return null;
        }
        catch (FormatException)
        {
            await ClearAsync(ct);
            return null;
        }
    }

    public async Task<bool> SaveAsync(RememberedSession session, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var payload = Serialize(session);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                await SaveWindowsAsync(payload, ct);
                return true;
            }

            if (OperatingSystem.IsMacOS())
                return MacKeychain.TryWrite(payload);

            if (OperatingSystem.IsLinux())
                return LinuxSecretService.TryWrite(payload);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(_windowsFilePath)) File.Delete(_windowsFilePath);
            }
            else if (OperatingSystem.IsMacOS())
            {
                MacKeychain.TryClear();
            }
            else if (OperatingSystem.IsLinux())
            {
                LinuxSecretService.TryClear();
            }
        }
        catch (IOException)
        {
            // A stale credential is harmless; the next successful login will replace it.
        }
        catch (UnauthorizedAccessException)
        {
            // A stale credential is harmless; the next successful login will replace it.
        }

        await Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private async Task<string?> LoadWindowsAsync(CancellationToken ct)
    {
        if (!File.Exists(_windowsFilePath)) return null;
        var protectedBytes = await File.ReadAllBytesAsync(_windowsFilePath, ct);
        var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    [SupportedOSPlatform("windows")]
    private async Task SaveWindowsAsync(string payload, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_windowsFilePath)!);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(payload), Entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_windowsFilePath, protectedBytes, ct);
    }

    private static string Serialize(RememberedSession session)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(session, RemoteOsJsonOptions.Default));

    private static RememberedSession? Deserialize(string payload)
        => JsonSerializer.Deserialize<RememberedSession>(Convert.FromBase64String(payload), RemoteOsJsonOptions.Default);
}

internal static class MacKeychain
{
    private const int Success = 0;
    private const int ItemNotFound = -25300;
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("RemoteOS.Client.RememberedSession");
    private static readonly byte[] Account = Encoding.UTF8.GetBytes("default");

    public static bool TryRead(out string? value)
    {
        value = null;
        IntPtr data = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        try
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)Service.Length, Service, (uint)Account.Length, Account,
                out var length, out data, out item);
            if (status == ItemNotFound) return true;
            if (status != Success) return false;

            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            value = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        finally
        {
            if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public static bool TryWrite(string value)
    {
        var password = Encoding.UTF8.GetBytes(value);
        IntPtr data = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        try
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)Service.Length, Service, (uint)Account.Length, Account,
                out var length, out data, out item);
            if (status == Success)
                return SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password) == Success;
            if (status != ItemNotFound) return false;

            return SecKeychainAddGenericPassword(
                IntPtr.Zero, (uint)Service.Length, Service, (uint)Account.Length, Account,
                (uint)password.Length, password, out item) == Success;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        finally
        {
            if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public static bool TryClear()
    {
        IntPtr data = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        try
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)Service.Length, Service, (uint)Account.Length, Account,
                out _, out data, out item);
            return status == ItemNotFound || (status == Success && SecKeychainItemDelete(item) == Success);
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        finally
        {
            if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength,
        out IntPtr passwordData, out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength,
        byte[] passwordData, out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);
}

internal static class LinuxSecretService
{
    private const string SchemaName = "com.remoteos.client.remembered-session";
    private const string AttributeName = "application";
    private const string AttributeValue = "RemoteOS.Client";
    private static readonly GlibHashFunction HashFunction = Hash;
    private static readonly GlibEqualFunction EqualFunction = Equal;
    private static readonly IntPtr HashFunctionPointer = Marshal.GetFunctionPointerForDelegate(HashFunction);
    private static readonly IntPtr EqualFunctionPointer = Marshal.GetFunctionPointerForDelegate(EqualFunction);

    public static bool TryRead(out string? value)
    {
        value = null;
        try
        {
            using var attributes = new SecretAttributes();
            var password = secret_password_lookupv_sync(ref Schema.Value, attributes.Handle, IntPtr.Zero, out var error);
            try
            {
                if (error != IntPtr.Zero) return false;
                if (password == IntPtr.Zero) return true;
                value = Marshal.PtrToStringUTF8(password);
                return true;
            }
            finally
            {
                if (password != IntPtr.Zero) secret_password_free(password);
                FreeError(error);
            }
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static bool TryWrite(string value)
    {
        try
        {
            using var attributes = new SecretAttributes();
            var saved = secret_password_storev_sync(
                ref Schema.Value, IntPtr.Zero, "RemoteOS remembered login", value,
                attributes.Handle, IntPtr.Zero, out var error);
            var hasError = error != IntPtr.Zero;
            FreeError(error);
            return saved && !hasError;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static bool TryClear()
    {
        try
        {
            using var attributes = new SecretAttributes();
            var cleared = secret_password_clearv_sync(ref Schema.Value, attributes.Handle, IntPtr.Zero, out var error);
            var hasError = error != IntPtr.Zero;
            FreeError(error);
            return cleared && !hasError;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static void FreeError(IntPtr error)
    {
        if (error != IntPtr.Zero) g_error_free(error);
    }

    private static class Schema
    {
        public static SecretSchema Value = new()
        {
            Name = SchemaName,
            Flags = 0,
            Attributes = CreateAttributes(),
        };

        private static SecretSchemaAttribute[] CreateAttributes()
        {
            var attributes = new SecretSchemaAttribute[32];
            attributes[0] = new SecretSchemaAttribute { Name = AttributeName, Type = 0 };
            return attributes;
        }
    }

    private sealed class SecretAttributes : IDisposable
    {
        private readonly IntPtr _key;
        private readonly IntPtr _value;
        public IntPtr Handle { get; }

        public SecretAttributes()
        {
            Handle = g_hash_table_new(HashFunctionPointer, EqualFunctionPointer);
            _key = Marshal.StringToCoTaskMemUTF8(AttributeName);
            _value = Marshal.StringToCoTaskMemUTF8(AttributeValue);
            g_hash_table_insert(Handle, _key, _value);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) g_hash_table_destroy(Handle);
            Marshal.FreeCoTaskMem(_key);
            Marshal.FreeCoTaskMem(_value);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchema
    {
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string Name;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public SecretSchemaAttribute[] Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchemaAttribute
    {
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Name;
        public int Type;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GlibHashFunction(IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool GlibEqualFunction(IntPtr first, IntPtr second);

    private static uint Hash(IntPtr value)
    {
        uint hash = 5381;
        for (var index = 0; ; index++)
        {
            var current = Marshal.ReadByte(value, index);
            if (current == 0) return hash;
            hash = (hash << 5) + hash + current;
        }
    }

    private static bool Equal(IntPtr first, IntPtr second)
    {
        var index = 0;
        while (true)
        {
            var left = Marshal.ReadByte(first, index);
            var right = Marshal.ReadByte(second, index);
            if (left != right) return false;
            if (left == 0) return true;
            index++;
        }
    }

    [DllImport("libsecret-1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr secret_password_lookupv_sync(
        ref SecretSchema schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport("libsecret-1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool secret_password_storev_sync(
        ref SecretSchema schema, IntPtr collection, string label, string password,
        IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport("libsecret-1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool secret_password_clearv_sync(
        ref SecretSchema schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport("libsecret-1.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc);

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool g_hash_table_insert(IntPtr hashTable, IntPtr key, IntPtr value);

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_hash_table_destroy(IntPtr hashTable);

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_error_free(IntPtr error);
}
