using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Woo_.Converters;

public sealed class LogSeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value?.ToString() switch
        {
            "Black" => new SolidColorBrush(ColorHelper.FromArgb(255, 12, 12, 12)),
            "DarkRed" => new SolidColorBrush(ColorHelper.FromArgb(255, 197, 15, 31)),
            "DarkGreen" => new SolidColorBrush(ColorHelper.FromArgb(255, 19, 161, 14)),
            "DarkYellow" => new SolidColorBrush(ColorHelper.FromArgb(255, 193, 156, 0)),
            "DarkBlue" => new SolidColorBrush(ColorHelper.FromArgb(255, 0, 55, 218)),
            "DarkMagenta" => new SolidColorBrush(ColorHelper.FromArgb(255, 136, 23, 152)),
            "DarkCyan" => new SolidColorBrush(ColorHelper.FromArgb(255, 58, 150, 221)),
            "Gray" => new SolidColorBrush(ColorHelper.FromArgb(255, 204, 204, 204)),
            "DarkGray" => new SolidColorBrush(ColorHelper.FromArgb(255, 118, 118, 118)),
            "Red" => new SolidColorBrush(ColorHelper.FromArgb(255, 231, 72, 86)),
            "Green" => new SolidColorBrush(ColorHelper.FromArgb(255, 22, 198, 12)),
            "Yellow" => new SolidColorBrush(ColorHelper.FromArgb(255, 249, 241, 165)),
            "Blue" => new SolidColorBrush(ColorHelper.FromArgb(255, 59, 120, 255)),
            "Magenta" => new SolidColorBrush(ColorHelper.FromArgb(255, 180, 0, 158)),
            "Cyan" => new SolidColorBrush(ColorHelper.FromArgb(255, 97, 214, 214)),
            "White" => new SolidColorBrush(ColorHelper.FromArgb(255, 242, 242, 242)),
            _ => new SolidColorBrush(ColorHelper.FromArgb(255, 232, 235, 240))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
