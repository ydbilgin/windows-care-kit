namespace WindowsCareKit.Core.Modules;

/// <summary>A top-level feature area in the shell (Sil / Temizle / Yedekle / Kur).</summary>
public interface IModule
{
    /// <summary>Stable identifier, e.g. <c>uninstall</c>.</summary>
    string Name { get; }

    /// <summary>i18n resource key for the display title, e.g. <c>module.uninstall.title</c>.</summary>
    string DisplayNameKey { get; }
}

/// <summary>
/// A module that participates in post-format restore, ordered by <see cref="RestoreOrder"/>
/// (driver → winget → core tools → … → tasks). Used by the Kur module in a later PR (spec §1.4).
/// </summary>
public interface IRestoreParticipant
{
    string Name { get; }
    int RestoreOrder { get; }
}
