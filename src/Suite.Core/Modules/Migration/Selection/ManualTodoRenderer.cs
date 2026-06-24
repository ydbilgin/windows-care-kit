namespace WindowsCareKit.Core.Modules.Migration.Selection;

public sealed record ManualTodoEntry(string Code, string Tr, string En, bool IsCritical);

/// <summary>Builds the success-screen "do this by hand" checklist without claiming unsupported restore success.</summary>
public static class ManualTodoRenderer
{
    public static IReadOnlyList<ManualTodoEntry> Render(MigrationSelectionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var result = new List<ManualTodoEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string todo in item.Candidate.ManualTodo)
        {
            if (!string.IsNullOrWhiteSpace(todo) && seen.Add(todo.Trim()))
                result.Add(new ManualTodoEntry("recipe-manual-todo", todo.Trim(), todo.Trim(), false));
        }

        bool machineLocked = item.Badge.CoreBadge.Kind == BadgeKind.MachineLocked;
        if (item.Candidate.BackedUpButNotRestored && machineLocked)
        {
            result.Add(new ManualTodoEntry(
                "combined-honesty",
                "Yalnız kayıt için saklandı; otomatik geri-yüklenemez ve kopyalansa da yeni PC'de çalışmaz — yeniden giriş/yapılandırma gerekir.",
                "Saved for records only; it cannot be restored automatically and would not work on the new PC even if copied — re-login/reconfiguration is required.",
                true));
        }
        else if (machineLocked && result.Count == 0)
        {
            result.Add(new ManualTodoEntry(
                "machine-locked-remediation",
                "Bu öğe eski makineye bağlıdır; yeni PC'de yeniden giriş yap veya uygulamanın kendi dışa-aktarımını kullan.",
                "This item is tied to the old machine; sign in again or use the application's own export on the new PC.",
                true));
        }

        if (item.Candidate.RequiresRelogin)
        {
            result.Add(new ManualTodoEntry(
                "relogin-required",
                "Yeni PC'de yeniden giriş yap.",
                "Sign in again on the new PC.",
                true));
        }

        return result;
    }
}
