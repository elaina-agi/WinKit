using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinKit.Desktop.Infrastructure;

public sealed class BooleanVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class FileSizeFormatter
{
    public static string Format(long bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} 字节";
        var size = (double)bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        foreach (var unit in units)
        {
            size /= 1024;
            if (size < 1024 || unit == "TB") return $"{size:0.##} {unit}";
        }
        return $"{bytes:N0} 字节";
    }
}
