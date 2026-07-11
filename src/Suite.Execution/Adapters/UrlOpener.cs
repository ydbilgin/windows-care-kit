using System.Diagnostics;
using WindowsCareKit.Core.Execution;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>Opens external HTTPS links after a direct user click in the UI.</summary>
public sealed class UrlOpener : IUrlOpener
{
    private readonly Func<ProcessStartInfo, Process?> _launch;

    public UrlOpener()
#pragma warning disable RS0030 // Sanctioned process launch (Suite.Execution): open a user-clicked HTTPS URL.
        : this(psi => Process.Start(psi))
#pragma warning restore RS0030
    {
    }

    internal UrlOpener(Func<ProcessStartInfo, Process?> launch)
    {
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
    }

    public void Open(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            return;

#pragma warning disable RS0030 // Sanctioned process handle ownership: dispose the handle returned by the launch seam.
        using IDisposable? processHandle = _launch(
            new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
#pragma warning restore RS0030
    }
}
