using System.Diagnostics;
using WindowsCareKit.Core.Execution;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>Opens external HTTPS links after a direct user click in the UI.</summary>
public sealed class UrlOpener : IUrlOpener
{
    private readonly Action<ProcessStartInfo> _launch;

    public UrlOpener()
#pragma warning disable RS0030 // Sanctioned process launch (Suite.Execution): open a user-clicked HTTPS URL.
        : this(psi => Process.Start(psi))
#pragma warning restore RS0030
    {
    }

    internal UrlOpener(Action<ProcessStartInfo> launch)
    {
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
    }

    public void Open(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            return;

        _launch(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
