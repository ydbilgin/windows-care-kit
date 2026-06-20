using System.Diagnostics;
using System.Security.Principal;

namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// Self-provisions a benign, STOPPED Windows service named <c>WCK_Test_&lt;guid&gt;</c> for a genuinely
/// destructive Step 4 Tier B test, then removes it on <see cref="Dispose"/> with <c>sc delete</c>.
/// The service is created with a harmless binary path and is NEVER started — Step 4 deliberately does NOT
/// stop a running service. Creating/deleting a service is machine-wide and requires elevation, so the ctor
/// PRECHECKS elevation and throws otherwise (visible failure, never a vacuous pass).
///
/// <para>Provisioning uses <c>sc.exe</c> (the OS-supplied CLI) — this is TEST scaffolding, not the product,
/// which drives the SCM via advapi32 P/Invoke.</para>
/// </summary>
internal sealed class DisposableServiceFixture : IDisposable
{
    /// <summary>The service name the product action targets, e.g. <c>WCK_Test_&lt;guid&gt;</c>.</summary>
    public string ServiceName { get; }

    public DisposableServiceFixture()
    {
        if (!IsElevated())
            throw new InvalidOperationException(
                "DisposableServiceFixture requires an elevated process (machine-wide service create/delete).");

        ServiceName = "WCK_Test_" + Guid.NewGuid().ToString("N");

        // Benign binPath (cmd.exe), demand-start, NEVER started. Quoting per sc.exe's "key= value" grammar.
        var (exit, _, err) = RunSc(
            $"create {ServiceName} binPath= \"{Environment.SystemDirectory}\\cmd.exe /c rem WCK_TEST\" start= demand");
        if (exit != 0)
            throw new InvalidOperationException($"sc create failed (exit {exit}): {err.Trim()}");
    }

    /// <summary>True when the service is currently registered with the SCM.</summary>
    public bool ServiceExists()
    {
        var (exit, _, _) = RunSc($"query {ServiceName}");
        return exit == 0;
    }

    /// <summary>True when the service's configured start type is DISABLED (from <c>sc qc</c>).</summary>
    public bool IsDisabled()
    {
        var (exit, output, _) = RunSc($"qc {ServiceName}");
        if (exit != 0)
            return false;
        // START_TYPE line reports e.g. "4   DISABLED" when disabled.
        return output.Contains("DISABLED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static (int Exit, string Out, string Err) RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "sc.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process? p = Process.Start(psi);
            if (p is null)
                return (-1, string.Empty, "sc did not start");
            string output = p.StandardOutput.ReadToEnd();
            string error = p.StandardError.ReadToEnd();
            p.WaitForExit(20_000);
            return (p.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    public void Dispose()
    {
        // Best-effort removal of the self-provisioned service (the test may already have deleted it).
        RunSc($"delete {ServiceName}");
    }
}
