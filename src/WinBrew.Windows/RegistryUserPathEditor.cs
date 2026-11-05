using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinBrew.Application.Ports;
using WinBrew.Core;

namespace WinBrew.Windows;

/// <summary>
/// HKCU\Environment\Path (A12). <paramref name="subKey"/> defaults to the real
/// "Environment" key; tests pass a disposable key under HKCU\Software so the
/// real user PATH is never touched. The string rules live in
/// <see cref="PathList"/>; this class adds the registry semantics: raw reads
/// (DoNotExpandEnvironmentNames), the original value kind kept, a missing
/// value created as REG_EXPAND_SZ, and the WM_SETTINGCHANGE broadcast.
/// </summary>
public sealed class RegistryUserPathEditor(string subKey = RegistryUserPathEditor.EnvironmentKey) : IUserPathEditor
{
    public const string EnvironmentKey = "Environment";
    private const string ValueName = "Path";

    public bool IsSupported => OperatingSystem.IsWindows();

    public Task<PathChangeResult> AddAsync(string entry, CancellationToken ct)
    {
        EnsureSupported();
        using var key = OpenWritable();
        var (raw, kind) = ReadRaw(key);

        var result = PathList.TryAppend(raw, entry);
        if (result.AlreadyPresent)
        {
            return Task.FromResult(new PathChangeResult(Changed: false, AlreadyPresent: true, Warning: null));
        }

        if (result.ExceedsLimit)
        {
            throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                $"Adding '{entry}' would make the user PATH longer than {PathList.HardLimit} characters; refused (A12).");
        }

        key.SetValue(ValueName, result.NewValue, kind);
        Broadcast();

        var warning = result.ShouldWarn
            ? $"The user PATH is now {result.NewValue.Length} characters (> {PathList.WarnLimit}); some tools truncate long PATH values (A12)."
            : null;
        return Task.FromResult(new PathChangeResult(Changed: true, AlreadyPresent: false, warning));
    }

    public Task<bool> RemoveAsync(string entry, CancellationToken ct)
    {
        EnsureSupported();
        using var key = OpenWritable();
        var (raw, kind) = ReadRaw(key);

        var updated = PathList.TryRemove(raw, entry);
        if (updated is null)
        {
            return Task.FromResult(false);
        }

        key.SetValue(ValueName, updated, kind);
        Broadcast();
        return Task.FromResult(true);
    }

    public IReadOnlyList<string> EntriesBefore(string entry)
    {
        if (!IsSupported)
        {
            return [];
        }

        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        if (key is null)
        {
            return [];
        }

        var (raw, _) = ReadRaw(key);
        var entries = PathList.Split(raw);
        var target = PathList.NormalizeForCompare(entry);
        var before = new List<string>();
        foreach (var e in entries)
        {
            if (PathList.NormalizeForCompare(e) == target)
            {
                break;
            }

            before.Add(e);
        }

        return before;
    }

    private void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new WinBrewException(WinBrewErrorCode.GeneralFailure,
                "PATH integration needs Windows (HKCU\\Environment, A12).");
        }
    }

    private RegistryKey OpenWritable() =>
        Registry.CurrentUser.CreateSubKey(subKey, writable: true)
        ?? throw new WinBrewException(WinBrewErrorCode.GeneralFailure, $"Cannot open HKCU\\{subKey} for writing.");

    private static (string? Raw, RegistryValueKind Kind) ReadRaw(RegistryKey key)
    {
        var raw = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (raw is null)
        {
            return (null, RegistryValueKind.ExpandString); // A12: a missing value is created as REG_EXPAND_SZ
        }

        var kind = key.GetValueKind(ValueName);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                $"HKCU\\Environment\\Path has unexpected type {kind}; WinBrew changes only REG_SZ/REG_EXPAND_SZ values (A12).");
        }

        return (raw, kind);
    }

    private void Broadcast()
    {
        if (!string.Equals(subKey, EnvironmentKey, StringComparison.OrdinalIgnoreCase))
        {
            return; // test hive: nothing to announce
        }

        _ = SendMessageTimeoutW(HwndBroadcast, WmSettingChange, UIntPtr.Zero, "Environment", SmtoAbortIfHung, 5000, out _);
    }

    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
}
