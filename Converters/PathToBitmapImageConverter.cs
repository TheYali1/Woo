using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Woo_.Converters;

public sealed class PathToBitmapImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var path = value?.ToString()?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(path) &&
            Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            var pngPath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "icon.png");
            if (File.Exists(pngPath))
            {
                path = pngPath;
            }
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "Assets", "Woo!.png");
        }

        try
        {
            return new BitmapImage(new Uri(Path.GetFullPath(path), UriKind.Absolute));
        }
        catch
        {
            return new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Woo!.png")));
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
