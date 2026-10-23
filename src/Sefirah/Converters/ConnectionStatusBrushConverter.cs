using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Sefirah.Data.Models;
using Windows.UI;

namespace Sefirah.Converters;

public sealed class ConnectionStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        new SolidColorBrush(value switch
        {
            Connected => Color.FromArgb(255, 73, 171, 119),
            Connecting => Color.FromArgb(255, 201, 148, 55),
            _ => Color.FromArgb(255, 140, 140, 148)
        });

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
