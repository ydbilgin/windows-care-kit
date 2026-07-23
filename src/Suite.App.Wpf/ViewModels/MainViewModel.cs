using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WindowsCareKit.App.Mvvm;

namespace WindowsCareKit.App.ViewModels;

/// <summary>The shell: the navigation rail, the selected content, and the language selector.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<string, IWckModule> _modulesById;
    private readonly Dictionary<string, FrameworkElement> _moduleViews = new(StringComparer.OrdinalIgnoreCase);
    private NavItem _selectedNav = null!;
    private CancellationTokenSource? _navigationCts;
    private Task _activeNavigation = Task.CompletedTask;

    public MainViewModel(I18n i18n, IReadOnlyList<IWckModule> modules)
        : this(i18n, modules, EmptyServiceProvider.Instance)
    {
    }

    internal MainViewModel(I18n i18n, IReadOnlyList<IWckModule> modules, IServiceProvider services)
    {
        I18n = i18n;
        _modulesById = modules.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        // Glyphs are Segoe MDL2 Assets / Segoe Fluent Icons code points (delete / clean / save / migrate / restore / download / gear).
        Nav = new ObservableCollection<NavItem>(
            modules
                .OrderBy(m => m.IsSettings ? 1 : 0)
                .ThenBy(m => m.Order)
                .Select(m => new NavItem(
                    i18n,
                    m.Id,
                    m.TitleKey,
                    m.IconKey,
                    m.CreateContent(services),
                    m.DescKey,
                    m.IsSettings)));

        DismissFirstRunCommand = new RelayCommand(() => ShowFirstRun = false);
        if (Nav.Count > 0)
            SelectedNav = Nav[0];
    }

    public I18n I18n { get; }
    public ObservableCollection<NavItem> Nav { get; }
    public ICommand DismissFirstRunCommand { get; }

    private bool _showFirstRun = true;
    public bool ShowFirstRun { get => _showFirstRun; set => SetField(ref _showFirstRun, value); }

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (SetField(ref _selectedNav, value))
            {
                OnPropertyChanged(nameof(CurrentContent));
                DispatchNavigation(value);
            }
        }
    }

    /// <summary>
    /// Shell-owned navigation dispatch (P27). First cancels the previously-active navigation load — the
    /// concrete "navigate-away" signal in this shell, whose only navigation mechanism is this selection change.
    /// Then, if the newly-selected content wants a navigation-scoped load, starts it under a fresh shell-owned
    /// <see cref="CancellationTokenSource"/>, retains the returned task, and observes its completion/faults.
    /// The property setter is synchronous, so the load is launched-and-observed, never discarded (the module
    /// previously fired-and-forgot this call).
    /// </summary>
    private void DispatchNavigation(NavItem selected)
    {
        CancelActiveNavigation();

        if (selected.Content is IWckNavigationAware aware)
        {
            var cts = new CancellationTokenSource();
            _navigationCts = cts;
            _activeNavigation = ObserveNavigationAsync(aware, cts);
        }
    }

    /// <summary>Cancel and relinquish the currently-active navigation load, if any. Safe to call when nothing
    /// is active or when the active load already completed (its continuation disposed the CTS — cancel-after-
    /// dispose is caught). Reads and clears <see cref="_navigationCts"/> as one atomic
    /// <see cref="Interlocked.Exchange{T}(ref T, T)"/> instead of a separate read-then-assign, so a
    /// concurrently-completing older navigation's own retirement (see <see cref="RetireNavigation"/>) can never
    /// race this method into observing a value that is no longer current.</summary>
    private void CancelActiveNavigation()
    {
        CancellationTokenSource? active = Interlocked.Exchange(ref _navigationCts, null);
        if (active is null)
            return;

        try
        {
            active.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The load already completed and its continuation disposed the CTS — nothing left to cancel.
        }
    }

    /// <summary>Invoke the load and observe its outcome. A synchronous throw (a misbehaving implementer — the
    /// contract requires faults via the task) is contained here so it cannot escape into the WPF property-setter
    /// / binding path.</summary>
    private Task ObserveNavigationAsync(IWckNavigationAware aware, CancellationTokenSource cts)
    {
        Task load;
        try
        {
            load = aware.OnNavigatedToAsync(cts.Token) ?? Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Trace.TraceError("Navigation load threw synchronously: " + ex);
            RetireNavigation(cts);
            return Task.CompletedTask;
        }

        return AwaitNavigationAsync(load, cts);
    }

    private async Task AwaitNavigationAsync(Task load, CancellationTokenSource cts)
    {
        try
        {
            await load.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Expected when the user navigated away mid-load; the module resets its own retry state.
        }
        catch (Exception ex)
        {
            // Observed, not discarded (P27-R1); the module owns its own user-facing error state.
            Trace.TraceError("Navigation load failed: " + ex);
        }
        finally
        {
            RetireNavigation(cts);
        }
    }

    /// <summary>Test-only race-injection seam (see <see cref="RetireNavigation"/>): if set, invoked right after
    /// this retirement has genuinely observed (via <see cref="Volatile.Read{T}(ref T)"/>) that it still owns the
    /// CTS, but strictly before the atomic clear that acts on that observation. This lets a test force a
    /// competing navigation's CTS install into exactly the historical race window — after the "I still own it"
    /// read, before the clear takes effect. Always <see langword="null"/> in production — the call site below is
    /// a harmless no-op unless a test assigns it.</summary>
    internal Action? RaceTestHook_AfterRetireOwnershipRead;

    /// <summary>Sole disposer of a navigation CTS: clear the field only if this CTS is still the active one
    /// (a newer navigation may have replaced it — never clobber it), then dispose. Ownership is first observed
    /// with a plain <see cref="Volatile.Read{T}(ref T)"/>; if that read says this CTS is still current, the
    /// actual clear is the atomic <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>, not a separate
    /// <c>ReferenceEquals</c> check followed by a plain assignment: under genuinely concurrent/off-dispatcher
    /// completion (which this design tolerates by contract), a stale check-then-assign has a window where an
    /// older, delayed retirement could observe "this is still the active CTS" as true, then — after a newer
    /// navigation has already installed its own CTS in that window — clobber the NEWER reference with
    /// <c>null</c> instead of its own. <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> re-checks and
    /// clears in one indivisible operation: whenever it actually runs (even if deliberately delayed past a
    /// competing install, as <see cref="RaceTestHook_AfterRetireOwnershipRead"/> lets a test force right after
    /// the ownership observation above), it only ever nulls the field if it STILL references exactly this
    /// <paramref name="cts"/> at that atomic instant — a newer navigation's CTS can never be cleared by an older
    /// navigation's retirement. Dispose is idempotent, and cancel-after-dispose is guarded in
    /// <see cref="CancelActiveNavigation"/>.</summary>
    private void RetireNavigation(CancellationTokenSource cts)
    {
        CancellationTokenSource? observedOwner = Volatile.Read(ref _navigationCts);
        if (ReferenceEquals(observedOwner, cts))
        {
            RaceTestHook_AfterRetireOwnershipRead?.Invoke();
            Interlocked.CompareExchange(ref _navigationCts, null, cts);
        }

        cts.Dispose();
    }

    /// <summary>The most recently dispatched navigation load (<see cref="Task.CompletedTask"/> when none is or
    /// was active). Retained so the shell — and composition tests — observe completion instead of a discarded
    /// task (P27-R1).</summary>
    internal Task ActiveNavigationTask => _activeNavigation;

    public object CurrentContent => HostContent(_selectedNav);

    /// <summary>
    /// Opens a module by its short key (e.g. "migration" → the nav.migration tab). Used by the
    /// <c>--screen</c> startup deep-link so a specific screen can be shown on launch (handy for
    /// per-module screenshots and demos). Unknown keys are ignored; the default tab stays selected.
    /// </summary>
    public bool SelectNavByKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        string id = key.Trim().ToLowerInvariant();
        if (id.StartsWith("nav.", StringComparison.OrdinalIgnoreCase))
            id = id["nav.".Length..];

        foreach (NavItem item in Nav)
        {
            if (item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                SelectedNav = item;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Starts every opted-in module load and returns one observable task. The WPF startup boundary awaits this
    /// task so post-await module faults are logged instead of becoming unobserved fire-and-forget failures.
    /// </summary>
    public async Task OnShellStartupAsync()
    {
        var loads = new List<Task>();
        foreach (NavItem item in Nav)
            if (item.Content is IWckStartupAware aware)
                loads.Add(aware.OnShellStartupAsync());

        await Task.WhenAll(loads);
    }

    private object HostContent(NavItem item)
    {
        if (!_modulesById.TryGetValue(item.Id, out IWckModule? module))
            return item.Content;

        if (_moduleViews.TryGetValue(item.Id, out FrameworkElement? cached))
            return cached;

        FrameworkElement? view = module.CreateView();
        if (view is null)
            return item.Content;

        view.DataContext = item.Content;
        _moduleViews[item.Id] = view;
        return view;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        private EmptyServiceProvider()
        {
        }

        public object? GetService(Type serviceType) => null;
    }
}
