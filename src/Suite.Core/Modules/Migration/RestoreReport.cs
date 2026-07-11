using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules.Migration;

public enum RestoreDisposition
{
    Restored,
    NotRestored,
    ReinstallEnqueued,
    Manual,
}

/// <summary>One row in the honest restore success/report model.</summary>
public sealed record RestoreReportEntry(
    string Id,
    string RecipeId,
    RestoreDisposition Disposition,
    string Reason,
    string Note);

/// <summary>
/// Pure success-screen model. It deliberately keeps "config restored", "reinstall queued", and "manual" in
/// separate buckets so a restore cannot collapse to one green Done state.
/// </summary>
public sealed record RestoreReport(
    IReadOnlyList<RestoreReportEntry> Restored,
    IReadOnlyList<RestoreReportEntry> ReinstallEnqueued,
    IReadOnlyList<RestoreReportEntry> Manual)
{
    /// <summary>Restore targets whose action did NOT complete (Failed/Blocked/NotRun) — honest, never "Restored".</summary>
    public IReadOnlyList<RestoreReportEntry> NotRestored { get; init; } = Array.Empty<RestoreReportEntry>();

    public static RestoreReport FromPlan(MigrationRestorePlanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var restored = new List<RestoreReportEntry>();
        var reinstall = new List<RestoreReportEntry>();
        var manual = new List<RestoreReportEntry>();

        foreach ((string actionId, MigrationRestoreTarget target) in result.RestoreActionTargets)
        {
            restored.Add(new RestoreReportEntry(
                Id: target.EntryId,
                RecipeId: target.RecipeId,
                Disposition: RestoreDisposition.Restored,
                Reason: "config-copy",
                Note: $"EN: Config file placement is planned with .bak protection: {target.RelativePath}. TR: Ayar dosyasi .bak korumasiyla yerine konacak: {target.RelativePath}."));

            AddMetaManualRows(manual, target, suffix: actionId);
        }

        foreach ((string actionId, InstallEntry entry) in result.InstallActionEntries)
        {
            reinstall.Add(new RestoreReportEntry(
                Id: entry.Id,
                RecipeId: RecipeIdFromInstallEntry(entry),
                Disposition: RestoreDisposition.ReinstallEnqueued,
                Reason: entry.Method,
                Note: ReinstallNote(entry, actionId)));
        }

        foreach (RestoreSkip skip in result.Skipped)
        {
            manual.Add(new RestoreReportEntry(
                Id: skip.Target.EntryId,
                RecipeId: skip.Target.RecipeId,
                Disposition: RestoreDisposition.Manual,
                Reason: skip.Reason.ToString(),
                Note: RestoreSkipNotes.HumanNote(skip)));

            AddMetaManualRows(manual, skip.Target, suffix: skip.Reason.ToString());
        }

        foreach (InstallSkip skip in result.InstallSkipped)
        {
            if (skip.Reason is InstallSkipReason.AlreadyDone)
                continue;

            manual.Add(new RestoreReportEntry(
                Id: skip.Entry.Id,
                RecipeId: RecipeIdFromInstallEntry(skip.Entry),
                Disposition: RestoreDisposition.Manual,
                Reason: "install-" + skip.Reason,
                Note: $"EN: Reinstall step was not queued automatically: {skip.Note}. TR: Yeniden kurulum adimi otomatik siraya alinmadi: {skip.Note}."));
        }

        foreach (InstallEntry entry in result.InstallManualChecklist)
        {
            manual.Add(new RestoreReportEntry(
                Id: entry.Id + ":manual-checklist",
                RecipeId: RecipeIdFromInstallEntry(entry),
                Disposition: RestoreDisposition.Manual,
                Reason: "manual-install-checklist",
                Note: $"EN: Manual install/sign-in step remains: {entry.Description}. TR: Manuel kurulum/giris adimi kaliyor: {entry.Description}."));
        }

        return new RestoreReport(restored, reinstall, manual);
    }

    /// <summary>
    /// Build the honest post-execution report: a restore target is "Restored" ONLY when its action completed
    /// (MigrationRestoreActionStatus.Done). Failed / Blocked / NotRun targets go to <see cref="NotRestored"/>.
    /// Reinstall/manual/skip buckets are identical to <see cref="FromPlan"/>. (Preview still uses FromPlan.)
    /// </summary>
    public static RestoreReport FromExecution(
        MigrationRestorePlanResult result,
        IReadOnlyDictionary<string, MigrationRestoreActionStatus> statusByActionId)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(statusByActionId);

        RestoreReport planReport = FromPlan(result); // reuse reinstall + manual + skip buckets verbatim

        var restored = new List<RestoreReportEntry>();
        var notRestored = new List<RestoreReportEntry>();

        foreach ((string actionId, MigrationRestoreTarget target) in result.RestoreActionTargets)
        {
            bool done = statusByActionId.TryGetValue(actionId, out MigrationRestoreActionStatus status)
                        && status == MigrationRestoreActionStatus.Done;
            if (done)
            {
                restored.Add(new RestoreReportEntry(
                    Id: target.EntryId,
                    RecipeId: target.RecipeId,
                    Disposition: RestoreDisposition.Restored,
                    Reason: "config-copy",
                    Note: $"EN: Config file was restored with .bak protection: {target.RelativePath}. TR: Ayar dosyasi .bak korumasiyla geri yuklendi: {target.RelativePath}."));
            }
            else
            {
                string outcome = statusByActionId.TryGetValue(actionId, out var s) ? s.ToString() : "NotRun";
                notRestored.Add(new RestoreReportEntry(
                    Id: target.EntryId,
                    RecipeId: target.RecipeId,
                    Disposition: RestoreDisposition.NotRestored,
                    Reason: "restore-" + outcome.ToLowerInvariant(),
                    Note: $"EN: This item was NOT restored ({outcome}); redo it by hand. TR: Bu kalem geri yuklenmedi ({outcome}); elle yeniden yapin."));
            }
        }

        // FromPlan put every restore target in Restored; discard THAT bucket and use the execution-derived ones.
        // Keep FromPlan's ReinstallEnqueued and Manual buckets as-is.
        return new RestoreReport(restored, planReport.ReinstallEnqueued, planReport.Manual)
        {
            NotRestored = notRestored,
        };
    }

    private static void AddMetaManualRows(List<RestoreReportEntry> manual, MigrationRestoreTarget target, string suffix)
    {
        MigrationRecipeMeta? meta = target.MigrationMeta;
        if (meta is null)
            return;

        int i = 0;
        foreach (string todo in meta.ManualTodo)
        {
            manual.Add(new RestoreReportEntry(
                Id: $"{target.EntryId}:manual:{suffix}:{i++}",
                RecipeId: target.RecipeId,
                Disposition: RestoreDisposition.Manual,
                Reason: "recipe-manual-todo",
                Note: $"EN/TR: {todo}"));
        }

        if (meta.RequiresRelogin)
        {
            manual.Add(new RestoreReportEntry(
                Id: $"{target.EntryId}:relogin:{suffix}",
                RecipeId: target.RecipeId,
                Disposition: RestoreDisposition.Manual,
                Reason: "relogin-required",
                Note: "EN: Sign in again or re-activate after reinstall. TR: Yeniden kurulumdan sonra tekrar giris yapin veya etkinlestirin."));
        }

        if (meta.BackedUpButNotRestored)
        {
            manual.Add(new RestoreReportEntry(
                Id: $"{target.EntryId}:backed-up-not-restored:{suffix}",
                RecipeId: target.RecipeId,
                Disposition: RestoreDisposition.Manual,
                Reason: "backed-up-but-not-restored",
                Note: "EN: This was backed up or inventoried, but WCK cannot carry it automatically; redo it by hand. TR: Bu yedeklendi veya envantere alindi, fakat WCK otomatik tasiyamaz; elle yeniden yapin."));
        }
    }

    private static string ReinstallNote(InstallEntry entry, string actionId)
    {
        string locator = entry.Method switch
        {
            InstallMethod.Winget => entry.WingetId ?? entry.Id,
            InstallMethod.Npm => entry.NpmPackage ?? entry.Id,
            _ => entry.Id,
        };
        string admin = entry.RequiresAdmin ? " Requires admin." : string.Empty;
        string reboot = entry.RebootExpected ? " Reboot may be required." : string.Empty;
        return $"EN: Reinstall is queued through the gated install planner ({entry.Method}: {locator}).{admin}{reboot} Action={actionId}. TR: Yeniden kurulum guvenlik kapisindan gecen kurulum planiyla siraya alindi ({entry.Method}: {locator}).";
    }

    private static string RecipeIdFromInstallEntry(InstallEntry entry)
    {
        const string prefix = "migration:";
        const string suffix = ":install";
        if (entry.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && entry.Id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && entry.Id.Length > prefix.Length + suffix.Length)
        {
            return entry.Id[prefix.Length..^suffix.Length];
        }
        return entry.Id;
    }
}

public static class RestoreSkipNotes
{
    public static string HumanNote(RestoreSkip skip)
    {
        string detail = string.IsNullOrWhiteSpace(skip.Note) ? string.Empty : " Detail: " + skip.Note;
        return skip.Reason switch
        {
            RestoreSkipReason.MachineLocked =>
                "EN: Could not be carried automatically because this data is machine-locked or partial; reinstall, sign in, or export/import manually. TR: Bu veri makineye bagli veya kismi oldugu icin otomatik tasinamaz; yeniden kurun, giris yapin veya elle aktarim yapin." + detail,
            RestoreSkipReason.InventoryOnly =>
                "EN: Inventory/manual-only item; WCK lists it instead of restoring it. TR: Envanter/manuel kalem; WCK geri yuklemek yerine listeler." + detail,
            RestoreSkipReason.NonProfileRoot =>
                "EN: Non-profile data is outside the safe restore-write surface; re-add or merge by hand. TR: Profil disi veri guvenli geri-yazma yuzeyinin disindadir; elle yeniden ekleyin veya birlestirin." + detail,
            RestoreSkipReason.UnsupportedStrategy =>
                "EN: This restore strategy is not supported by the file-placement runner; redo it manually. TR: Bu geri yukleme stratejisi dosya-yerlestirme kosucusu tarafindan desteklenmez; elle yapin." + detail,
            RestoreSkipReason.SourceMissing =>
                "EN: The package bytes are missing; WCK cannot restore this item. TR: Paket icerigi eksik; WCK bu kalemi geri yukleyemez." + detail,
            RestoreSkipReason.PackageSourceRejected =>
                "EN: The package source path resolved outside the package; WCK refused this item before any write. TR: Paket kaynak yolu paketin disina cozuldu; WCK bu kalemi yazmadan once reddetti." + detail,
            RestoreSkipReason.RebindRejected =>
                "EN: Target path rebinding was rejected before any write. TR: Hedef yol yeniden baglama yazmadan once reddedildi." + detail,
            RestoreSkipReason.GateBlocked =>
                "EN: SafetyGate blocked the restore write. TR: Guvenlik kapisi geri yukleme yazimini engelledi." + detail,
            RestoreSkipReason.AlreadyDone =>
                "EN: Already completed in the checkpoint; skipped on resume. TR: Kontrol noktasinda tamamlanmis; devam ederken atlandi." + detail,
            RestoreSkipReason.NotAllowListed =>
                "EN: Legacy package was not trusted for inner-path-clean config placement. TR: Eski paket ic-yol-temiz ayar yerlestirme icin guvenilir degil." + detail,
            _ =>
                "EN: Manual follow-up required. TR: Elle takip gerekiyor." + detail,
        };
    }
}
