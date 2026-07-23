namespace WindowsCareKit.App.Modules;

/// <summary>
/// Optional: nav content that wants a load kicked off when it becomes the selected navigation target.
/// The shell OWNS the returned task and the supplied token (P27): it retains the task so completion and
/// faults are observed rather than discarded, and it cancels the token when the user navigates away (or when
/// navigation to this content is superseded), so a background load started here does not keep mutating a
/// now-hidden cached view. Implementers MUST surface faults through the returned task (be <c>async</c>-shaped),
/// not throw synchronously, and SHOULD observe <paramref name="cancellationToken"/> to stop promptly on
/// deactivation.
/// </summary>
public interface IWckNavigationAware
{
    Task OnNavigatedToAsync(CancellationToken cancellationToken);
}
