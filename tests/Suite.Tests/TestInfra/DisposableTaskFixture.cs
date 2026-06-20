using System.Diagnostics;

namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// Self-provisions a throwaway scheduled task under <c>\WindowsCareKit.Tests\WCK_&lt;guid&gt;</c> running a
/// harmless <c>whoami.exe</c> command, for a genuinely destructive Step 4 Tier B test, then removes it on
/// <see cref="Dispose"/> with <c>schtasks /Delete /F</c>. It NEVER touches <c>\Microsoft\Windows\**</c>.
///
/// <para>Provisioning uses <c>schtasks.exe</c> (the OS-supplied CLI) — this is TEST scaffolding, not the
/// product, which drives the COM Task Scheduler adapter. If the Task Scheduler service is unavailable the
/// task cannot be created; <see cref="Available"/> is then false so the test can surface a VISIBLE skip
/// rather than a misleading pass.</para>
/// </summary>
internal sealed class DisposableTaskFixture : IDisposable
{
    /// <summary>The full task path the product action targets, e.g. <c>\WindowsCareKit.Tests\WCK_&lt;guid&gt;</c>.</summary>
    public string TaskPath { get; }

    /// <summary>False when the task could not be provisioned (e.g. Task Scheduler service down) — test should skip-visibly.</summary>
    public bool Available { get; }

    /// <summary>Any stderr/diagnostic captured when provisioning failed, for a visible skip message.</summary>
    public string ProvisionDetail { get; }

    public DisposableTaskFixture()
    {
        string name = "WCK_" + Guid.NewGuid().ToString("N");
        TaskPath = $@"\WindowsCareKit.Tests\{name}";

        // /SC ONCE + a far-future start time so it never actually fires; /F overwrites; /RL LIMITED keeps it benign.
        var (exit, _, err) = RunSchTasks(
            $"/Create /TN \"{TaskPath}\" /TR \"{Environment.SystemDirectory}\\whoami.exe\" " +
            "/SC ONCE /ST 23:59 /SD 01/01/2099 /RL LIMITED /F");

        Available = exit == 0 && TaskExists();
        ProvisionDetail = Available ? "ok" : $"schtasks /Create exit={exit}: {err.Trim()}";
    }

    /// <summary>True when the task is currently registered.</summary>
    public bool TaskExists()
    {
        var (exit, _, _) = RunSchTasks($"/Query /TN \"{TaskPath}\"");
        return exit == 0;
    }

    /// <summary>True when the task exists AND its Scheduled-Task State is Disabled.</summary>
    public bool IsDisabled()
    {
        var (exit, output, _) = RunSchTasks($"/Query /TN \"{TaskPath}\" /V /FO LIST");
        if (exit != 0)
            return false;
        // The verbose LIST output carries a "Scheduled Task State:" / localized line; match the value "Disabled".
        foreach (string line in output.Split('\n'))
        {
            string l = line.Trim();
            if (l.EndsWith("Disabled", StringComparison.OrdinalIgnoreCase) &&
                l.Contains(':', StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static (int Exit, string Out, string Err) RunSchTasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process? p = Process.Start(psi);
            if (p is null)
                return (-1, string.Empty, "schtasks did not start");
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
        // Best-effort removal of the self-provisioned task (the test may already have deleted it).
        RunSchTasks($"/Delete /TN \"{TaskPath}\" /F");
    }
}
