using System.ComponentModel;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;

namespace WindowsCareKit.App.ViewModels;

/// <summary>A not-yet-built module tab (Temizle / Yedekle / Kur / Ayarlar).</summary>
public sealed class PlaceholderViewModel : ObservableObject
{
    public PlaceholderViewModel(I18n i18n, string titleKey)
    {
        I18n = i18n;
        TitleKey = titleKey;
        I18n.PropertyChanged += OnI18nChanged;
    }

    public I18n I18n { get; }
    public string TitleKey { get; }
    public string Title => I18n[TitleKey];

    private void OnI18nChanged(object? sender, PropertyChangedEventArgs e) => Raise(nameof(Title));
}
