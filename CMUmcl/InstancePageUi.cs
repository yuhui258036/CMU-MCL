// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CMUmcl;

// Shared presentation for the embedded settings and instance-content pages.
internal static class InstancePageUi
{
    internal static void Initialize(UserControl view)
    {
        var main = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        if (main != null) view.Resources.MergedDictionaries.Add(main.Resources);
        view.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/CMUmcl;component/InstancePageResources.xaml", UriKind.Relative)
        });
        view.Resources[typeof(CheckBox)] = view.FindResource("InstanceCheckBox");
        view.FontFamily = new FontFamily("Segoe UI Variable, Segoe UI, Microsoft YaHei UI");
        view.FontSize = 14;
        view.UseLayoutRounding = view.SnapsToDevicePixels = true;
        view.SetResourceReference(Control.BackgroundProperty, "BgBrush");
        view.SetResourceReference(Control.ForegroundProperty, "TextBrush");
    }

    internal static Grid Shell(UserControl view, string title, Action back, ListBox navigation,
        UIElement content, TextBlock status, UIElement? actions = null)
    {
        Initialize(view);

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        var backButton = Button("← 返回", back);
        backButton.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(backButton, Dock.Left);
        heading.Children.Add(backButton);
        // The main window header already shows the page and instance name; only navigation goes here.
        heading.LastChildFill = false;
        root.Children.Add(heading);

        var body = new Grid();
        body.ColumnDefinitions.Add(new() { Width = new GridLength(146) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        navigation.BorderThickness = new Thickness(0);
        navigation.Padding = new Thickness(0);
        navigation.Background = Brushes.Transparent;
        navigation.Margin = new Thickness(0, 0, 16, 0);
        navigation.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        navigation.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        navigation.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "InstanceNavigationItem");
        ScrollViewer.SetHorizontalScrollBarVisibility(navigation, ScrollBarVisibility.Disabled);
        body.Children.Add(navigation);
        if(navigation.Items.Count==1){navigation.Visibility=Visibility.Collapsed;body.ColumnDefinitions[0].Width=new GridLength(0);}
        Grid.SetColumn(content, 1);
        body.Children.Add(content);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        status.FontSize = 12;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Thickness(0, 0, 16, 0);
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        footer.Children.Add(status);
        if (actions != null) { Grid.SetColumn(actions, 1); footer.Children.Add(actions); }
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        // Preserve a usable list viewport on short windows and scaled desktops.
        view.SizeChanged += (_, _) =>
        {
            bool compact = view.ActualHeight < 400;
            root.Margin = new Thickness(compact ? 12 : 20);
            heading.Margin = new Thickness(0, 0, 0, compact ? 10 : 20);
            footer.Margin = new Thickness(0, compact ? 6 : 12, 0, 0);
        };
        view.Content = root;
        return root;
    }

    internal static TextBlock Text(string text, double size = 14, bool muted = false)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedTextBrush" : "TextBrush");
        return label;
    }

    internal static Border Card(UIElement child, Thickness? padding = null)
    {
        var border = new Border { Child = child, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(20, 18, 20, 18), Margin = new Thickness(0, 0, 0, 14) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
        if (padding == null)
            border.SizeChanged += (_, _) => border.Padding = border.ActualWidth < 520
                ? new Thickness(14, 12, 14, 12) : new Thickness(20, 18, 20, 18);
        return border;
    }

    internal static StackPanel Section(Panel page, string title)
    {
        var content = new StackPanel();
        var heading = Text(title, 15);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 0, 0, 16);
        content.Children.Add(heading);
        page.Children.Add(Card(content));
        return content;
    }

    internal static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(14, 0, 14, 0), MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "PrimaryButton" : "SecondaryButton");
        button.Click += (_, _) => action();
        return button;
    }

    internal static void Hint(Panel panel, string text)
    {
        var label = Text(text, 12, true);
        label.Margin = new Thickness(0, 0, 0, 14);
        panel.Children.Add(label);
    }

    internal static void Field(Panel panel, string label, Control field)
    {
        var caption = Text(label);
        caption.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(caption);
        field.MinHeight = 40;
        field.Margin = new Thickness(0, 0, 0, 16);
        panel.Children.Add(field);
    }
}
