using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace Lively.UI.WinUI.Helpers.Converters
{
    /// <summary>
    /// Converts our own path mini-language strings (optionally prefixed with F0/F1 fill rule)
    /// into a <see cref="Geometry"/> for PathIcon usage.
    /// </summary>
    public class StringToGeometryConverter : IValueConverter
    {
        private static readonly Dictionary<string, Geometry> cache = new();

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not string path || string.IsNullOrWhiteSpace(path))
                return null;

            if (cache.TryGetValue(path, out var cached))
                return cached;

            var geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path.Trim());
            cache[path] = geometry;
            return geometry;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
