using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Radar.Views;

/// <summary>Число ↔ «выбран ли этот вариант» для сегментированных переключателей (RadioButton с номером варианта в параметре).</summary>
public sealed class EqConverter : IValueConverter
{
    public static readonly EqConverter Instance = new();

    private static int Param(object? p) => p switch
    {
        int i => i,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => -1,
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int v && v == Param(parameter);

    // снятие отметки с варианта не меняет значение — его задаст вариант, который отметили
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Param(parameter) : BindingOperations.DoNothing;
}
