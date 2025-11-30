using System;
using System.Globalization;
using System.Windows.Data;

namespace ColorRamp
{
    public class PercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double d)
            {
                // 0.5 -> 50, 小数点1桁まで
                return Math.Round(d * 100, 1);
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s && double.TryParse(s, out double result))
            {
                return Math.Clamp(result / 100.0, 0.0, 1.0);
            }
            if (value is double d)
            {
                return Math.Clamp(d / 100.0, 0.0, 1.0);
            }
            return 0.0;
        }
    }
}