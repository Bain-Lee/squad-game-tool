using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SquadTools
{
    // Shared palette brushes also serve code-created controls and retained drawings.
    // Their Color bindings prevent WPF from freezing a brush while sealing a style.
    internal static class UiTheme
    {
        private sealed class ColorSource : DependencyObject
        {
            internal static readonly DependencyProperty ColorProperty = DependencyProperty.Register("Color", typeof(Color), typeof(ColorSource));
            public Color Color { get { return (Color)GetValue(ColorProperty); } set { SetValue(ColorProperty, value); } }
        }

        private static readonly ResourceDictionary resources = new ResourceDictionary();
        private static readonly Dictionary<string, ColorSource> sources = new Dictionary<string, ColorSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> lightColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "#101720", "#F6F4EF" }, { "#101B24", "#FAF8F3" }, { "#111820", "#FCFAF6" },
            { "#111A24", "#FCFAF6" }, { "#141D27", "#FCFAF6" }, { "#141F2B", "#F8F6F1" },
            { "#171F29", "#FCFAF6" }, { "#1B2531", "#EAECEB" }, { "#1D2733", "#FCFAF6" },
            { "#1D2835", "#F0EFEA" }, { "#202B38", "#F8F6F1" }, { "#242D39", "#FCFAF6" },
            { "#253548", "#EDF3FB" }, { "#26313F", "#E5ECF5" }, { "#283E55", "#DCEBFC" },
            { "#2A3441", "#D6DFE9" }, { "#2C3745", "#DEE5ED" }, { "#2D3A4A", "#E7F0FC" },
            { "#304A64", "#D4E5FA" }, { "#35404E", "#C8D3E0" }, { "#365679", "#C4DDF8" },
            { "#394454", "#CBD5E1" }, { "#3B628D", "#D4E5FA" }, { "#596A80", "#8797AB" },
            { "#65758A", "#64748B" }, { "#718398", "#62748A" }, { "#7E91A7", "#5C7088" },
            { "#8192A5", "#5B6D82" }, { "#8196AD", "#5B7088" }, { "#8F9EAF", "#586B82" },
            { "#91A6BE", "#516A85" }, { "#96A5B6", "#5C6F85" }, { "#98A8BB", "#53677F" },
            { "#9FAEC0", "#586C84" }, { "#A4B4C8", "#556B86" }, { "#A6B2C1", "#51657E" },
            { "#A9BDCF", "#526C85" }, { "#AAB9CB", "#536B86" }, { "#BAC4D0", "#425872" },
            { "#DCE4EE", "#26384E" }, { "#E9EDF2", "#22344A" }, { "#EDF1F7", "#22344A" },
            { "#EDF4FF", "#244C7F" }, { "#FFFFFF", "#22344A" },
            { "#8BB8F4", "#3D77C3" }, { "#94C9FF", "#2369B2" }, { "#9CBDE5", "#386AA7" },
            { "#ADCDF9", "#2B67B3" }, { "#E4B6A4", "#A6573D" }, { "#EDC084", "#97651B" },
            { "#FFA88C", "#AB4D30" }, { "#EBC487", "#96621C" }, { "#B8EB7E", "#4A7B29" },
            { "#A0AEBD", "#5D6F83" }, { "#A3AFBC", "#607084" }, { "#A7B8C8", "#526A82" },
            { "#92A4B7", "#60758C" }, { "#94A7BB", "#5C7189" }, { "#263746", "#E2E8F0" },
            { "#2C3C4C", "#D8E1EB" }, { "#314638", "#DFEBD8" }, { "#91AC87", "#769264" },
            { "#778899", "#5E7188" }, { "#B0C4DE", "#436A9C" }, { "#FA8072", "#AB493D" },
            { "#192A36", "#FCFAF6" }, { "#182630", "#F5F2EB" },
            { "#E8101B24", "#E8FAF8F3" }, { "#E9101B24", "#E9FAF8F3" },
            { "#C4D0D6", "#586973" }, { "#BECDD5", "#586973" },
            { "#D9E8CF", "#4E6D3C" }, { "#D4F58C", "#6C8B37" }, { "#E9AF89", "#926139" }, { "#8196A5", "#60717B" }
        };

        public static bool IsLight { get; private set; }

        public static void Attach(FrameworkElement element)
        {
            if (!element.Resources.MergedDictionaries.Contains(resources)) element.Resources.MergedDictionaries.Add(resources);
        }

        public static string Xaml(string xaml)
        {
            return Regex.Replace(xaml, "(?<q>['\"])(?<c>#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)\\k<q>", delegate(Match match) {
                string hex = match.Groups["c"].Value.ToUpperInvariant();
                Brush(hex);
                return match.Groups["q"].Value + "{DynamicResource " + Key(hex) + "}" + match.Groups["q"].Value;
            });
        }

        public static SolidColorBrush Brush(string darkHex)
        {
            string hex = darkHex.ToUpperInvariant();
            ColorSource source;
            if (sources.TryGetValue(hex, out source)) return (SolidColorBrush)resources[Key(hex)];
            source = new ColorSource { Color = ColorFor(hex) };
            var brush = new SolidColorBrush();
            BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new Binding("Color") { Source = source, Mode = BindingMode.OneWay });
            sources.Add(hex, source);
            resources[Key(hex)] = brush;
            return brush;
        }

        public static void Apply(bool light)
        {
            IsLight = light;
            foreach (var pair in sources) pair.Value.Color = ColorFor(pair.Key);
        }

        private static string Key(string hex) { return "Theme" + hex.Substring(1); }
        private static Color ColorFor(string hex)
        {
            string alternate;
            return (Color)ColorConverter.ConvertFromString(IsLight && lightColors.TryGetValue(hex, out alternate) ? alternate : hex);
        }
    }
}
