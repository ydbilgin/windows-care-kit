using System.ComponentModel;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;

namespace WindowsCareKit.App.ViewModels;

/// <summary>One module tab in the top navigation strip. <see cref="Title"/> and
/// <see cref="Descriptor"/> track the active language.</summary>
public sealed class NavItem : ObservableObject
{
    private readonly I18n _i18n;

    public NavItem(I18n i18n, string nameKey, string glyph, object content,
        string descriptorKey = "", bool isSettings = false)
    {
        _i18n = i18n;
        NameKey = nameKey;
        Glyph = glyph;
        Content = content;
        DescriptorKey = descriptorKey;
        IsSettings = isSettings;
        _i18n.PropertyChanged += OnI18nChanged;
    }

    public string NameKey { get; }
    public string Glyph { get; }
    public object Content { get; }
    public string DescriptorKey { get; }

    /// <summary>True for the quiet, far-right Settings tab (separated from the four modules).</summary>
    public bool IsSettings { get; }

    public string Title => _i18n[NameKey];

    /// <summary>One-line descriptor under the bold label; empty for the icon-only Settings tab.</summary>
    public string Descriptor => DescriptorKey.Length == 0 ? string.Empty : _i18n[DescriptorKey];

    private void OnI18nChanged(object? sender, PropertyChangedEventArgs e)
    {
        Raise(nameof(Title));
        Raise(nameof(Descriptor));
    }
}
