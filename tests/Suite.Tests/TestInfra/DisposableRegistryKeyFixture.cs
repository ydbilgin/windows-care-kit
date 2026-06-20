using System.Security.Principal;
using Microsoft.Win32;
using BclHive = Microsoft.Win32.RegistryHive;
using BclView = Microsoft.Win32.RegistryView;

namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// Self-provisions a throwaway registry key under <c>&lt;hive&gt;\SOFTWARE\WindowsCareKit.Tests\&lt;guid&gt;</c>
/// for a genuinely destructive Step 4 Tier B test, then deletes the whole subtree on <see cref="Dispose"/>.
/// Deleting under <c>HKLM</c> is machine-wide and requires elevation, so the ctor PRECHECKS that the process
/// is elevated and throws otherwise — Tier B already gates on a disposable machine where the harness guarantees
/// elevation, so the precheck only surfaces a misconfigured sandbox (never a silent vacuous pass).
/// </summary>
internal sealed class DisposableRegistryKeyFixture : IDisposable
{
    /// <summary>The core hive enum the product action will target (kept in sync with <see cref="BclHiveValue"/>).</summary>
    public WindowsCareKit.Core.Planning.RegistryHive Hive { get; }

    /// <summary>The 64-bit base key view used to provision/inspect the key (matches the action's default view).</summary>
    public BclHive BclHiveValue { get; }

    /// <summary>The GUID-suffixed subkey path, e.g. <c>SOFTWARE\WindowsCareKit.Tests\&lt;guid&gt;</c>.</summary>
    public string SubKeyPath { get; }

    public DisposableRegistryKeyFixture(WindowsCareKit.Core.Planning.RegistryHive hive)
    {
        Hive = hive;
        BclHiveValue = hive switch
        {
            WindowsCareKit.Core.Planning.RegistryHive.LocalMachine => BclHive.LocalMachine,
            WindowsCareKit.Core.Planning.RegistryHive.CurrentUser => BclHive.CurrentUser,
            _ => throw new ArgumentOutOfRangeException(nameof(hive), hive, "Only HKLM/HKCU are supported by this fixture."),
        };

        // HKLM writes are machine-wide → require elevation. Fail loudly (not a silent skip) so a sandbox that
        // forgot to elevate is visible rather than passing vacuously.
        if (BclHiveValue == BclHive.LocalMachine && !IsElevated())
            throw new InvalidOperationException(
                "DisposableRegistryKeyFixture(LocalMachine) requires an elevated process (machine-wide HKLM write).");

        SubKeyPath = $@"SOFTWARE\WindowsCareKit.Tests\{Guid.NewGuid():N}";

        using RegistryKey baseKey = RegistryKey.OpenBaseKey(BclHiveValue, BclView.Registry64);
        using RegistryKey created = baseKey.CreateSubKey(SubKeyPath, writable: true);
        created.SetValue("Marker", "to-be-deleted");
        using RegistryKey child = created.CreateSubKey("Child");
        child.SetValue("ChildVal", 1);
    }

    /// <summary>True when the chosen hive key currently exists in the 64-bit view.</summary>
    public bool KeyExists()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(BclHiveValue, BclView.Registry64);
        using RegistryKey? key = baseKey.OpenSubKey(SubKeyPath);
        return key is not null;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Dispose()
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(BclHiveValue, BclView.Registry64);
            baseKey.DeleteSubKeyTree(SubKeyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // Best-effort teardown: the test asserts the delete; a residual key must not fail an otherwise-passing run.
        }
    }
}
