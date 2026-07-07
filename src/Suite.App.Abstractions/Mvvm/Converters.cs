using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WindowsCareKit.App.Mvvm;

/// <summary>Int 0 (e.g. an empty collection's Count) → Visible; anything else → Collapsed.
/// Used to show "nothing found" placeholders.</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is int i && i == 0) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Int &gt; 0 → Visible; 0 → Collapsed.</summary>
public sealed class PositiveToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is int i && i > 0) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>False → Visible; True → Collapsed.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps an int value to <c>true</c> when it equals the converter parameter (a stringified int), used to bind a
/// set of mutually-exclusive RadioButtons to a single int (e.g. the wizard scan-depth selector). ConvertBack
/// returns the parameter int only for a <c>true</c> (checked) state, so the bound int is set when the user
/// picks an option; the unchecked event yields <see cref="Binding.DoNothing"/> so the index is not clobbered.
/// </summary>
public sealed class IndexEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && int.TryParse(parameter?.ToString(), out int p) && i == p;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b && int.TryParse(parameter?.ToString(), out int p))
            ? p
            : Binding.DoNothing;
}
