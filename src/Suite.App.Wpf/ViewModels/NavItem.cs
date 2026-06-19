using System.ComponentModel;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;

namespace WindowsCareKit.App.ViewModels;

/// <summary>One entry in the left navigation rail. <see cref="Title"/> tracks the active language.</summary>
public sealed class NavItem : ObservableObject
{
    private readonly I18n _i18n;

    public NavItem(I18n i18n, string nameKey, string glyph, object content)
    {
        _i18n = i18n;
        NameKey = nameKey;
        Glyph = glyph;
        Content = content;
        _i18n.PropertyChanged += OnI18nChanged;
    }

    public string NameKey { get; }
    public string Glyph { get; }
    public object Content { get; }
    public string Title => _i18n[NameKey];

    private void OnI18nChanged(object? sender, PropertyChangedEventArgs e) => Raise(nameof(Title));
}
