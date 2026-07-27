using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MHC.Invoicing.App.Localization;

public sealed class Localization : DependencyObject
{
    public static readonly DependencyProperty TextKeyProperty = DependencyProperty.RegisterAttached(
        "TextKey",
        typeof(string),
        typeof(Localization),
        new PropertyMetadata(null, OnTextKeyChanged));

    public static readonly DependencyProperty ContentKeyProperty = DependencyProperty.RegisterAttached(
        "ContentKey",
        typeof(string),
        typeof(Localization),
        new PropertyMetadata(null, OnContentKeyChanged));

    public static readonly DependencyProperty PlaceholderKeyProperty = DependencyProperty.RegisterAttached(
        "PlaceholderKey",
        typeof(string),
        typeof(Localization),
        new PropertyMetadata(null, OnPlaceholderKeyChanged));

    public static readonly DependencyProperty HeaderKeyProperty = DependencyProperty.RegisterAttached(
        "HeaderKey",
        typeof(string),
        typeof(Localization),
        new PropertyMetadata(null, OnHeaderKeyChanged));

    public static readonly DependencyProperty AutomationNameKeyProperty = DependencyProperty.RegisterAttached(
        "AutomationNameKey",
        typeof(string),
        typeof(Localization),
        new PropertyMetadata(null, OnAutomationNameKeyChanged));

    public static string GetTextKey(DependencyObject target) => (string)target.GetValue(TextKeyProperty);

    public static void SetTextKey(DependencyObject target, string value) => target.SetValue(TextKeyProperty, value);

    public static string GetContentKey(DependencyObject target) => (string)target.GetValue(ContentKeyProperty);

    public static void SetContentKey(DependencyObject target, string value) => target.SetValue(ContentKeyProperty, value);

    public static string GetPlaceholderKey(DependencyObject target) => (string)target.GetValue(PlaceholderKeyProperty);

    public static void SetPlaceholderKey(DependencyObject target, string value) => target.SetValue(PlaceholderKeyProperty, value);

    public static string GetHeaderKey(DependencyObject target) => (string)target.GetValue(HeaderKeyProperty);

    public static void SetHeaderKey(DependencyObject target, string value) => target.SetValue(HeaderKeyProperty, value);

    public static string GetAutomationNameKey(DependencyObject target) =>
        (string)target.GetValue(AutomationNameKeyProperty);

    public static void SetAutomationNameKey(DependencyObject target, string value) =>
        target.SetValue(AutomationNameKeyProperty, value);

    private static void OnTextKeyChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is TextBlock textBlock && args.NewValue is string key)
        {
            textBlock.Text = LocalizationState.GetString(key);
        }
    }

    private static void OnContentKeyChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is ContentControl contentControl && args.NewValue is string key)
        {
            contentControl.Content = LocalizationState.GetString(key);
        }
    }

    private static void OnPlaceholderKeyChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not string key)
        {
            return;
        }

        string value = LocalizationState.GetString(key);
        switch (target)
        {
            case TextBox textBox:
                textBox.PlaceholderText = value;
                break;
            case AutoSuggestBox autoSuggestBox:
                autoSuggestBox.PlaceholderText = value;
                break;
        }
    }

    private static void OnHeaderKeyChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not string key)
        {
            return;
        }

        string value = LocalizationState.GetString(key);
        switch (target)
        {
            case TextBox textBox:
                textBox.Header = value;
                break;
            case ComboBox comboBox:
                comboBox.Header = value;
                break;
            case NumberBox numberBox:
                numberBox.Header = value;
                break;
            case DatePicker datePicker:
                datePicker.Header = value;
                break;
        }
    }

    private static void OnAutomationNameKeyChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is string key)
        {
            AutomationProperties.SetName(target, LocalizationState.GetString(key));
        }
    }
}
