using System;
using System.Globalization;
using System.Windows.Data;

namespace ColorRamp
{
    public class GradientPositionConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length == 2 &&
                values[0] is double position &&
                values[1] is double totalWidth)
            {
                const double LeftPadding = 0;    // 左は0px！
                const double RightPadding = 8;  // 右は10px余白（お好みで8～12px）

                double effectiveWidth = totalWidth - LeftPadding - RightPadding;
                if (effectiveWidth <= 0) return 0.0;

                return LeftPadding + position * effectiveWidth;
            }
            return 0.0;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}