namespace WindowsCareKit.App.Modules;

/// <summary>Optional: nav content that wants a background load kicked off once the shell window is shown.</summary>
public interface IWckStartupAware
{
    Task OnShellStartupAsync();
}
