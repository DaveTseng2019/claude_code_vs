using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVs.Ui;

/// <summary>
/// Ties this extension's hand-built WPF panels to VS's own Environment Font size (Tools > Options >
/// Environment > Fonts and Colors), instead of the hardcoded point values every element used before.
/// <see cref="BindRoot"/> makes a panel's own FontSize track <c>VsFonts.EnvironmentFontSizeKey</c>;
/// <see cref="Bind{T}"/> scales a descendant relative to that root so the panel's size hierarchy
/// (title vs. body vs. caption) survives, and both update live if the user changes the VS setting.
/// </summary>
internal static class FontScale
{
    private static readonly IValueConverter Ratio = new RatioConverter();

    public static void BindRoot(FrameworkElement root)
        => root.SetResourceReference(TextElement.FontSizeProperty, VsFonts.EnvironmentFontSizeKey);

    public static T Bind<T>(T el, Type rootType, double ratio = 1.0) where T : FrameworkElement
    {
        el.SetBinding(TextElement.FontSizeProperty, new Binding
        {
            Path = new PropertyPath(TextElement.FontSizeProperty),
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, rootType, 1),
            Converter = Ratio,
            ConverterParameter = ratio,
        });
        return el;
    }

    private sealed class RatioConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double d && parameter is double r ? d * r : value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
