using System;
using System.IO;
using System.Threading;

namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// Best-effort recursive temp-dir teardown that tolerates the transient Windows
/// "The process cannot access the file ... because it is being used by another process"
/// race (AV/indexer/just-released handle). Retries a few times with a short backoff, then
/// gives up silently — a test whose assertions already passed must never fail in cleanup.
/// Use this instead of a bare Directory.Delete(path, recursive: true) in test teardown/finally.
/// </summary>
internal static class TestFs
{
    public static void DeleteResilient(string? path, int attempts = 6, int backoffMs = 120)
    {
        if (string.IsNullOrEmpty(path))
            return;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return; // gone (or never existed) — done
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i == attempts - 1)
                    return; // best-effort: swallow the final failure, never fail a passed test on cleanup
                Thread.Sleep(backoffMs);
            }
        }
    }
}
