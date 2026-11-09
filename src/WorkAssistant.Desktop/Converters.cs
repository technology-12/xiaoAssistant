using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using WorkAssistant.Desktop.Presentation;

namespace WorkAssistant.Desktop
{
    public sealed class InverseBoolConverter : IValueConverter
    {
        public static readonly InverseBoolConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is true);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is true);
    }

    public sealed class BoolToVisConverter : IValueConverter
    {
        public static readonly BoolToVisConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public sealed class InverseBoolToVisConverter : IValueConverter
    {
        public static readonly InverseBoolToVisConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public sealed class NonEmptyToVisConverter : IValueConverter
    {
        public static readonly NonEmptyToVisConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>步骤组折叠箭头旋转：展开 90°。</summary>
    public sealed class ExpandRotateConverter : IValueConverter
    {
        public static readonly ExpandRotateConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? 90d : 0d;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>图标键（Icon.Table 等）→ 资源几何。</summary>
    public sealed class IconKeyToGeometryConverter : IValueConverter
    {
        public static readonly IconKeyToGeometryConverter Instance = new();
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is string key && Application.Current?.TryFindResource(key) is StreamGeometry g ? g : null;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>按 ConversationItemKind 选择对话条目模板。</summary>
    public sealed class ConversationTemplateSelector : DataTemplateSelector
    {
        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is ConversationItem ci && container is FrameworkElement fe)
            {
                return ci.Kind switch
                {
                    ConversationItemKind.UserMessage => fe.TryFindResource("TplUser") as DataTemplate,
                    ConversationItemKind.AssistantMessage => fe.TryFindResource("TplAssistant") as DataTemplate,
                    ConversationItemKind.ExecutionGroup => fe.TryFindResource("TplExecution") as DataTemplate,
                    ConversationItemKind.ErrorMessage => fe.TryFindResource("TplError") as DataTemplate,
                    ConversationItemKind.UserQuestion => fe.TryFindResource("TplQuestion") as DataTemplate,
                    _ => fe.TryFindResource("TplNotice") as DataTemplate
                };
            }
            return base.SelectTemplate(item, container);
        }
    }
}
