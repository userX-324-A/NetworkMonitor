using System;
using System.Globalization;
using System.Windows.Data;

namespace NetworkMonitorUI
{
    /// <summary>
    /// Converts a boolean value to its inverse. True becomes False, False becomes True.
    /// Used in XAML bindings to invert boolean logic (e.g., for IsEnabled properties).
    /// </summary>
    [ValueConversion(typeof(bool), typeof(bool))]
    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool booleanValue)
            {
                return !booleanValue;
            }
            // Return false or DependencyProperty.UnsetValue if the input is not a boolean
            return false; 
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // ConvertBack is typically not needed for one-way bindings like IsEnabled
            if (value is bool booleanValue)
            {
                return !booleanValue;
            }
            return false;
        }
    }
} 