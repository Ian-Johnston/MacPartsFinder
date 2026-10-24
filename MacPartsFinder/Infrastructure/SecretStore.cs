using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace MacPartsFinder;

/// <summary>
/// Where API keys are kept - never in settings.json. Each person's keys stay on their own computer:
/// the macOS Keychain on a Mac, DPAPI-encrypted (current Windows user only) on Windows.
/// </summary>
public interface ISecretStore
{
    /// <summary>The saved value, or "" if there isn't one (or it can't be read).</summary>
    string Get(string name);

    /// <summary>Saves the value; "" removes it.</summary>
    void Set(string name, string value);
}

public static class SecretStore
{
    public static ISecretStore ForThisPlatform(string settingsFolder)
    {
        if (OperatingSystem.IsMacOS()) return new MacKeychainStore();
        if (OperatingSystem.IsWindows()) return new WindowsDpapiStore(Path.Combine(settingsFolder, "keys.json"));
        return new NoSecretStore();
    }
}

/// <summary>
/// macOS Keychain, as "MacPartsFinder" generic passwords (one per key, visible in the Keychain Access app).
/// Uses the classic SecKeychain calls - simple to call from .NET and still supported by macOS.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychainStore : ISecretStore
{
    private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("MacPartsFinder");

    public string Get(string name)
    {
        var account = Encoding.UTF8.GetBytes(name);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                                                    out var length, out var data, out var item);
        try
        {
            if (status != 0 || data == IntPtr.Zero) return ""; // not saved yet, or access refused
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public void Set(string name, string value)
    {
        var account = Encoding.UTF8.GetBytes(name);
        var password = Encoding.UTF8.GetBytes(value ?? "");
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                                                    out _, out var data, out var item);
        try
        {
            if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (status == ErrSecItemNotFound)
            {
                if (password.Length == 0) return; // nothing to remove
                status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                                                       (uint)password.Length, password, IntPtr.Zero);
            }
            else if (status == 0)
            {
                status = password.Length == 0
                    ? SecKeychainItemDelete(item)
                    : SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password);
            }
            if (status != 0) throw new InvalidOperationException($"Could not save {name} in the macOS Keychain (error {status}).");
        }
        finally
        {
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    [DllImport(SecurityLib)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
                                                             uint accountNameLength, byte[] accountName,
                                                             out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
                                                            uint accountNameLength, byte[] accountName,
                                                            uint passwordLength, byte[] passwordData, IntPtr itemRef);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundationLib)]
    private static extern void CFRelease(IntPtr cf);
}

/// <summary>
/// Windows (for trying the app out on a PC): keys encrypted with DPAPI for the current Windows user,
/// in keys.json next to settings.json - the same protection WinPartsFinder uses.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiStore : ISecretStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _encrypted = new();

    public WindowsDpapiStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                _encrypted = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch
        {
            // Unreadable - the user can re-enter their keys in Settings.
        }
    }

    public string Get(string name)
    {
        if (!_encrypted.TryGetValue(name, out var enc) || enc.Length == 0) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return ""; // e.g. file copied from another Windows user
        }
    }

    public void Set(string name, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (!_encrypted.Remove(name)) return;
        }
        else
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            _encrypted[name] = Convert.ToBase64String(bytes);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonConvert.SerializeObject(_encrypted, Formatting.Indented));
    }
}

/// <summary>Other systems (Linux): keys work while the app is open but aren't saved.</summary>
internal sealed class NoSecretStore : ISecretStore
{
    public string Get(string name) => "";

    public void Set(string name, string value)
    {
    }
}
