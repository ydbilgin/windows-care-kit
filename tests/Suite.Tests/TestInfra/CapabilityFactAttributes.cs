using Xunit;

namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// Discovery-time STATIC-skip <see cref="FactAttribute"/>s for tests that require a host privilege/feature
/// (creating an NTFS junction, creating a symbolic link, or 8.3 short-name generation). Modeled EXACTLY on
/// <see cref="DisposableFactAttribute"/>: the capability is probed ONCE at type-load time and, if unavailable,
/// <see cref="FactAttribute.Skip"/> is set so the runner reports the test as SKIPPED — never FAILED.
///
/// <para>Why static (not <c>Assert.Skip</c>): this project pins xUnit <b>2.9.2</b>, where runtime/dynamic skip
/// (<c>SkipException</c>) is NOT honored by the runner and surfaces as a FAILURE. A discovery-time
/// <see cref="FactAttribute.Skip"/> keeps CI GREEN on no-privilege hosts. These attributes REPLACE the old
/// silent <c>return</c>-on-unavailable pattern, so a test now either runs its real assertion or is visibly
/// skipped with a reason — it can never report a silent vacuous pass.</para>
/// </summary>
internal static class HostCapabilities
{
    /// <summary>True when an NTFS directory junction can be created here (no elevation needed — <c>mklink /J</c>).</summary>
    public static readonly bool JunctionSupported = ProbeJunction();

    /// <summary>True when a symbolic link can be created here (needs admin OR Windows Developer Mode).</summary>
    public static readonly bool SymlinkSupported = ProbeSymlink();

    /// <summary>True when 8.3 short-name generation is enabled on the temp volume (8dot3name on).</summary>
    public static readonly bool ShortNameSupported = ProbeShortName();

    private static bool ProbeJunction()
    {
        string root = Path.Combine(Path.GetTempPath(), "wck-cap-junc-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "target");
        string link = Path.Combine(root, "link");
        try
        {
            Directory.CreateDirectory(target);
            bool ok = JunctionHelper.TryCreateJunction(link, target);
            JunctionHelper.CleanupWithJunction(root, link);
            return ok;
        }
        catch
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* ignore */ }
            return false;
        }
    }

    private static bool ProbeSymlink()
    {
        string root = Path.Combine(Path.GetTempPath(), "wck-cap-sym-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "target");
        string link = Path.Combine(root, "link");
        try
        {
            Directory.CreateDirectory(target);
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return false;
            }
            return Directory.Exists(link)
                   && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    private static bool ProbeShortName()
    {
        // Create a long-named dir and ask the OS for its 8.3 short name; if none (or identical), 8dot3name is off.
        string longDir = Path.Combine(Path.GetTempPath(), "wck-cap-83-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(longDir);
            string? shortDir = ShortNameInterop.TryGetShortPathName(longDir);
            return shortDir is not null && !string.Equals(shortDir, longDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (Directory.Exists(longDir)) Directory.Delete(longDir); } catch { /* ignore */ }
        }
    }
}

/// <summary>STATIC-skips a test unless NTFS junctions can be created on this host (no silent vacuous pass).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class FactRequiresJunctionAttribute : FactAttribute
{
    public FactRequiresJunctionAttribute()
    {
        if (!HostCapabilities.JunctionSupported)
            Skip = "requires NTFS junction support (mklink /J unavailable on this host)";
    }
}

/// <summary>STATIC-skips a test unless symbolic links can be created on this host (admin / Developer Mode).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class FactRequiresSymlinkAttribute : FactAttribute
{
    public FactRequiresSymlinkAttribute()
    {
        if (!HostCapabilities.SymlinkSupported)
            Skip = "requires symbolic-link privilege (admin or Windows Developer Mode)";
    }
}

/// <summary>STATIC-skips a test unless 8.3 short-name generation is enabled on the temp volume (8dot3name).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class FactRequires8Dot3Attribute : FactAttribute
{
    public FactRequires8Dot3Attribute()
    {
        if (!HostCapabilities.ShortNameSupported)
            Skip = "requires 8.3 short-name generation (8dot3name disabled on this volume)";
    }
}
