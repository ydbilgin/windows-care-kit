using Windows.ApplicationModel;
using Windows.Management.Deployment;
using WindowsCareKit.Core.Modules.Uninstall;

namespace WindowsCareKit.Win32;

/// <summary>
/// Lists the current user's UWP/AppX packages via <see cref="PackageManager"/>. Read-only and
/// best-effort: any package that throws while being read is skipped rather than crashing the scan.
/// v1 is per-user only; provisioned/all-users removal is out of scope (spec §1.1).
/// </summary>
public sealed class Win32AppxReader : IAppxReader
{
    public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages()
    {
        var result = new List<InstalledAppx>();

        PackageManager manager;
        try
        {
            manager = new PackageManager();
        }
        catch (Exception)
        {
            return result; // packaging APIs unavailable on this SKU
        }

        IEnumerable<Package> packages;
        try
        {
            // Empty user SID => the current user's packages (no admin needed).
            packages = manager.FindPackagesForUser(string.Empty);
        }
        catch (Exception)
        {
            return result;
        }

        foreach (Package package in packages)
        {
            InstalledAppx? entry = TryMap(package);
            if (entry is not null)
                result.Add(entry);
        }

        return result;
    }

    private static InstalledAppx? TryMap(Package package)
    {
        try
        {
            string fullName = package.Id.FullName;
            string name = package.Id.Name;

            string display = Safe(() => package.DisplayName);
            if (string.IsNullOrWhiteSpace(display))
                display = name;

            string? publisher = Safe(() => package.PublisherDisplayName);
            if (string.IsNullOrWhiteSpace(publisher))
                publisher = package.Id.Publisher;

            var v = package.Id.Version;
            string version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";

            string? location = SafeNullable(() => package.InstalledLocation?.Path);

            bool frameworkOrSystem = false;
            try
            {
                frameworkOrSystem = package.IsFramework
                    || package.SignatureKind == PackageSignatureKind.System
                    || package.IsResourcePackage;
            }
            catch (Exception)
            {
                // leave as false
            }

            return new InstalledAppx
            {
                PackageFullName = fullName,
                PackageFamilyName = package.Id.FamilyName,
                DisplayName = display,
                PublisherDisplayName = publisher,
                Version = version,
                InstallLocation = location,
                IsFrameworkOrSystem = frameworkOrSystem,
            };
        }
        catch (Exception)
        {
            return null; // a package that cannot be read is skipped
        }
    }

    private static string Safe(Func<string> getter)
    {
        try { return getter() ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    private static string? SafeNullable(Func<string?> getter)
    {
        try { return getter(); }
        catch (Exception) { return null; }
    }
}
