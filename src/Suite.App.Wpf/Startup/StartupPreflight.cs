using System.IO;
using System.Text;
using WindowsCareKit.App.Deployment;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;

namespace WindowsCareKit.App.Startup;

/// <summary>Why startup may not proceed. <see cref="Ok"/> is the only non-fatal value.</summary>
internal enum StartupPreflightStatus
{
    /// <summary>The root is frozen and the base string table is present and well-formed.</summary>
    Ok,

    /// <summary>The app root could not be established (launched through a link/shim, or identity
    /// unobtainable). Nothing about the install can be stated, so nothing may be loaded from it.</summary>
    LayoutUndetermined,

    /// <summary>The root is known but the base string table is not there.</summary>
    BaseStringTableMissing,

    /// <summary>The base string table is there but could not be read, or is not the shape the live loader
    /// requires (<see cref="BaseStringTable"/>). Present-but-broken is a distinct statement from absent, and
    /// neither may degrade into "an empty table" (P20).</summary>
    BaseStringTableUnreadable,
}

/// <summary>
/// The outcome of <see cref="StartupPreflight"/>, in two shapes: a hardcoded-English message for the fatal
/// dialog and a single machine-readable line for <c>--verify-layout</c>.
/// </summary>
internal sealed class StartupPreflightResult
{
    internal StartupPreflightResult(
        StartupPreflightStatus status,
        AppLayout layout,
        IReadOnlyList<string> missingRequired,
        string? detail)
    {
        Status = status;
        Layout = layout;
        MissingRequired = missingRequired;
        Detail = detail;
    }

    public StartupPreflightStatus Status { get; }

    public AppLayout Layout { get; }

    /// <summary>Required resources found absent. Empty unless <see cref="Status"/> is
    /// <see cref="StartupPreflightStatus.BaseStringTableMissing"/>.</summary>
    public IReadOnlyList<string> MissingRequired { get; }

    /// <summary>The preserved cause for an undetermined layout or an unreadable table — never discarded, so a
    /// failure can be acted on rather than merely noticed (P26).</summary>
    public string? Detail { get; }

    public bool IsOk => Status == StartupPreflightStatus.Ok;

    /// <summary>Process exit code: 0 only when the app may start.</summary>
    public int ExitCode => Status switch
    {
        StartupPreflightStatus.Ok => StartupPreflight.ExitOk,
        StartupPreflightStatus.LayoutUndetermined => StartupPreflight.ExitLayoutUndetermined,
        _ => StartupPreflight.ExitResourcesUnusable,
    };

    /// <summary>
    /// One line, stable and greppable, for <c>--verify-layout</c>. Required resources report OK/MISSING;
    /// optional inventory reports its own presence with distinct tokens because absent-optional is a valid
    /// install, not a fault — it never affects the exit code.
    /// </summary>
    public string ToReportLine()
    {
        var sb = new StringBuilder(StartupPreflight.ReportPrefix);
        sb.Append(" status=").Append(Status);
        sb.Append(" root=").Append(Quote(Layout.IsDetermined ? Layout.Root : StartupPreflight.NoValue));

        if (!Layout.IsDetermined)
        {
            sb.Append(" processDir=").Append(Quote(Layout.ProcessDirectory ?? StartupPreflight.NoValue));
            sb.Append(" baseDir=").Append(Quote(Layout.BaseDirectory ?? StartupPreflight.NoValue));
        }

        foreach (AppResource required in StartupPreflight.RequiredResources)
        {
            sb.Append(' ').Append(required.RelativePath).Append('=');
            sb.Append(RequiredToken(required.RelativePath));
        }

        foreach (AppResource optional in StartupPreflight.OptionalResources)
        {
            sb.Append(' ').Append(optional.RelativePath).Append('=');
            sb.Append(!Layout.IsDetermined
                ? StartupPreflight.TokenUnknown
                : Layout.HasResource(optional)
                    ? StartupPreflight.TokenOptionalPresent
                    : StartupPreflight.TokenOptionalAbsent);
        }

        if (Detail is not null)
            sb.Append(" detail=").Append(Quote(Detail));

        return OneLine(sb.ToString());
    }

    /// <summary>
    /// The fatal dialog text. Hardcoded English on purpose: the string table is precisely what is unavailable,
    /// so routing this through <c>I18n</c> would print the raw keys it is meant to explain.
    /// </summary>
    public string ToFatalMessage()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Windows Care Kit cannot start because its installed files are not where the program is.")
          .AppendLine();

        switch (Status)
        {
            case StartupPreflightStatus.LayoutUndetermined:
                sb.AppendLine("The program folder could not be determined.").AppendLine();
                sb.Append("Resolved program directory: ")
                  .AppendLine(Layout.ProcessDirectory ?? StartupPreflight.NoValue);
                sb.Append("Runtime base directory:     ")
                  .AppendLine(Layout.BaseDirectory ?? StartupPreflight.NoValue);
                sb.AppendLine();
                sb.AppendLine(
                    "These must be the same folder. They differ when the program is started through a " +
                    "shortcut-style link or shim instead of from its own folder, which is not a supported " +
                    "way to run it. Run WindowsCareKit.exe from the folder it was installed or extracted to.");
                break;

            case StartupPreflightStatus.BaseStringTableMissing:
                sb.Append("Program folder: ").AppendLine(Layout.Root);
                sb.AppendLine();
                sb.AppendLine("These required files are missing:");
                foreach (string missing in MissingRequired)
                    sb.Append("  - ").AppendLine(Path.Combine(Layout.Root, missing));
                sb.AppendLine();
                sb.AppendLine(
                    "The installation is incomplete. Reinstall Windows Care Kit, or re-extract the release " +
                    "archive without leaving files out.");
                break;

            default:
                sb.Append("Program folder: ").AppendLine(Layout.Root);
                sb.AppendLine();
                sb.Append("This required file is present but unusable: ")
                  .AppendLine(Path.Combine(Layout.Root, BaseStringTable.RelativePath));
                if (Detail is not null)
                    sb.Append("Reason: ").AppendLine(Detail);
                sb.AppendLine();
                sb.AppendLine("Reinstall Windows Care Kit to replace the damaged file.");
                break;
        }

        sb.AppendLine();
        sb.AppendLine(
            "Starting anyway would show an interface whose every label is an internal key, which is worse " +
            "than this message.");
        return sb.ToString();
    }

    private string RequiredToken(string relativePath)
    {
        if (!Layout.IsDetermined)
            return StartupPreflight.TokenUnknown;
        if (MissingRequired.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
            return StartupPreflight.TokenMissing;
        return Status == StartupPreflightStatus.BaseStringTableUnreadable &&
               string.Equals(relativePath, BaseStringTable.RelativePath, StringComparison.OrdinalIgnoreCase)
            ? StartupPreflight.TokenUnreadable
            : StartupPreflight.TokenOk;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "'", StringComparison.Ordinal) + "\"";

    /// <summary>Collapses any newline a preserved exception message may carry: the report is one line by contract.</summary>
    private static string OneLine(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal)
             .Replace('\r', ' ')
             .Replace('\n', ' ');
}

/// <summary>
/// The check that must pass before anything is loaded from the app folder. It runs <b>before</b> module
/// discovery on purpose: <c>DirectoryModuleCatalog</c> executes third-party code from the root, so validating
/// the root afterwards would validate it after the fact.
/// <para>
/// The line it draws (ratified): the base string table is <b>fatal</b> when missing or malformed — it is the
/// shell's ability to say anything true, and a UI that renders raw keys explains nothing. Optional inventory
/// (<c>Modules\</c>, <c>manifests\</c>) absent is <b>never</b> fatal: the installer's supported "Base only"
/// type ships neither, so their absence is a legitimate install, not corruption.
/// </para>
/// </summary>
internal static class StartupPreflight
{
    /// <summary>Exit code when the layout verified and the app may start.</summary>
    public const int ExitOk = 0;

    /// <summary>Exit code when the app root could not be determined.</summary>
    public const int ExitLayoutUndetermined = 2;

    /// <summary>Exit code when the root is known but a required shipped resource is missing or unusable.</summary>
    public const int ExitResourcesUnusable = 3;

    internal const string ReportPrefix = "WCK-LAYOUT";
    internal const string NoValue = "<none>";
    internal const string TokenOk = "OK";
    internal const string TokenMissing = "MISSING";
    internal const string TokenUnreadable = "UNREADABLE";
    internal const string TokenUnknown = "UNKNOWN";
    internal const string TokenOptionalPresent = "OPTIONAL-PRESENT";
    internal const string TokenOptionalAbsent = "OPTIONAL-ABSENT";

    /// <summary>The base string table — shipped by every install type, and the one resource whose absence is
    /// fatal. Its path comes from the type that owns it; this is a required <em>file</em>, so a directory of
    /// that name does not satisfy it.</summary>
    public static readonly AppResource BaseStringTableResource =
        new(BaseStringTable.RelativePath, AppResourceKind.File);

    /// <summary>Resources every supported install must contain.</summary>
    public static readonly IReadOnlyList<AppResource> RequiredResources = [BaseStringTableResource];

    /// <summary>Component-provided inventory that a valid install may legitimately lack. Both are enumerated
    /// as directories by the code that consumes them, so both are required to BE directories. The module
    /// folder's name comes from the catalog that owns it, not from a second copy of the literal.</summary>
    public static readonly IReadOnlyList<AppResource> OptionalResources =
    [
        new(DirectoryModuleCatalog.ModulesFolderName, AppResourceKind.Directory),
        new(ManifestsFolderName, AppResourceKind.Directory),
    ];

    private const string ManifestsFolderName = "manifests";

    /// <summary>Production entry point: checks the frozen process layout against the real filesystem.</summary>
    public static StartupPreflightResult Run(AppLayout layout) => Run(layout, File.ReadAllText);

    /// <summary>
    /// Testable core. <paramref name="readBaseStringTable"/> is the only filesystem read this performs beyond
    /// the layout's own existence probe; it is handed to the shared loader, which is expected to throw on an
    /// unreadable file and keeps the cause.
    /// <para>
    /// This deliberately owns <b>no</b> rule about what a usable string table is. It asks
    /// <see cref="BaseStringTable"/> — the same loader <c>I18n</c> uses to build the live table — so the two
    /// cannot answer differently. When they did, an object-rooted file whose values were not strings passed
    /// this check and then produced a raw-key UI.
    /// </para>
    /// </summary>
    internal static StartupPreflightResult Run(AppLayout layout, Func<string, string> readBaseStringTable)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(readBaseStringTable);

        AppLayoutVerification verification = layout.Verify([.. RequiredResources]);
        switch (verification.Status)
        {
            case AppLayoutVerificationStatus.Undetermined:
                return new StartupPreflightResult(
                    StartupPreflightStatus.LayoutUndetermined, layout, [], verification.UndeterminedReason);

            case AppLayoutVerificationStatus.Missing:
                return new StartupPreflightResult(
                    StartupPreflightStatus.BaseStringTableMissing, layout, verification.MissingRelativePaths, null);
        }

        return FromBaseStringTable(
            layout,
            BaseStringTable.Load(layout.Resource(BaseStringTable.RelativePath), readBaseStringTable));
    }

    /// <summary>
    /// Translates a base-table load into the startup verdict. Shared with the shell's own first load so a
    /// table that goes missing between this check and that read (the same file, read twice) produces the
    /// identical explanation and exit code rather than a second, subtly different one.
    /// </summary>
    internal static StartupPreflightResult FromBaseStringTable(AppLayout layout, BaseStringTableResult table)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(table);

        return table.Status switch
        {
            BaseStringTableStatus.Loaded =>
                new StartupPreflightResult(StartupPreflightStatus.Ok, layout, [], null),
            BaseStringTableStatus.Missing =>
                new StartupPreflightResult(
                    StartupPreflightStatus.BaseStringTableMissing,
                    layout,
                    [BaseStringTable.RelativePath],
                    table.FailureDetail),
            _ =>
                new StartupPreflightResult(
                    StartupPreflightStatus.BaseStringTableUnreadable, layout, [], table.FailureDetail),
        };
    }
}
