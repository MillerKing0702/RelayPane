using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace CollaborationChatPreview
{
    internal static class SurfaceDialog
    {
        public static bool Confirm(Window owner, string title, string body, string detail, string confirm, string cancel)
        {
            return Create(owner, title, body, detail, confirm, cancel).ShowDialog() == true;
        }

        internal static Window NewWindow(Window owner, string title, double desiredWidth)
        {
            FrameworkElement styles = owner.Content as FrameworkElement ?? owner;
            var dialog = new Window
            {
                Owner = owner, Title = title, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent,
                Width = Math.Min(desiredWidth, Math.Max(346, owner.ActualWidth - 16)), SizeToContent = SizeToContent.Height,
                MaxHeight = Math.Min(540, SystemParameters.WorkArea.Height - 26),
                FontFamily = owner.FontFamily, UseLayoutRounding = true
            };
            foreach (string name in new[] { "MenuBrush", "MenuLineBrush", "TextBrush", "MutedBrush", "InputBrush", "AttentionBrush",
                "HoverBrush", "AccentBrush", "AccentHoverBrush", "ScrollThumbBrush", "MenuHoverBrush", "TransferBrush", "CompleteBrush", "MainOutlineBrush", "SelectionBrush" })
                dialog.Resources[name] = owner.FindResource(name);
            foreach (string name in new[] { "SurfaceMenu", "SurfaceMenuItem", "SurfaceSeparator", "QuietButton", "AccentButton", "EditorTextBox" })
                dialog.Resources[name] = styles.FindResource(name);
            object scrollStyle = styles.TryFindResource(typeof(ScrollBar));
            if (scrollStyle != null) dialog.Resources[typeof(ScrollBar)] = scrollStyle;
            var backdrop = styles.FindName("DialogBackdrop") as Border;
            if (backdrop != null)
            {
                bool dimmed = false;
                dialog.Loaded += (sender, args) =>
                {
                    if (dimmed) return;
                    dimmed = true;
                    backdrop.Tag = (backdrop.Tag as int? ?? 0) + 1;
                    backdrop.Visibility = Visibility.Visible;
                };
                dialog.Closed += (sender, args) =>
                {
                    if (!dimmed) return;
                    int remaining = Math.Max(0, (backdrop.Tag as int? ?? 1) - 1);
                    backdrop.Tag = remaining;
                    if (remaining == 0) backdrop.Visibility = Visibility.Collapsed;
                };
            }
            return dialog;
        }

        internal static Border Frame()
        {
            var panel = new Border
            {
                Margin = new Thickness(10), Padding = new Thickness(17), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 17, ShadowDepth = 3, Opacity = 0.29, Color = Colors.Black }
            };
            panel.SetResourceReference(Border.BackgroundProperty, "MenuBrush");
            panel.SetResourceReference(Border.BorderBrushProperty, "MenuLineBrush");
            return panel;
        }

        internal static Window Create(Window owner, string title, string body, string detail, string confirm, string cancel)
        {
            Window dialog = NewWindow(owner, title, 470);
            var panel = Frame();
            var layout = new StackPanel();
            var header = new DockPanel();
            var attention = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 9, 0) };
            attention.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            TextBlock glyph = SurfaceMenus.Text("!", "AttentionBrush", 16, true);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            attention.Child = glyph;
            DockPanel.SetDock(attention, Dock.Left);
            header.Children.Add(attention);
            TextBlock caption = SurfaceMenus.Text(title, "TextBrush", 16, true);
            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.TextWrapping = TextWrapping.Wrap;
            header.Children.Add(caption);
            layout.Children.Add(header);

            var description = SurfaceMenus.Text(body, "MutedBrush", 13);
            description.Margin = new Thickness(0, 12, 0, 0);
            description.TextWrapping = TextWrapping.Wrap;
            layout.Children.Add(description);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                var excerpt = new TextBox
                {
                    Text = detail, IsReadOnly = true, IsReadOnlyCaretVisible = false, TextWrapping = TextWrapping.Wrap,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 166, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 12
                };
                excerpt.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                excerpt.SetResourceReference(FrameworkElement.StyleProperty, "EditorTextBox");
                SurfaceMenus.AddEditorMenu(excerpt, true);
                var excerptPanel = new Border
                {
                    Child = excerpt, CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 9, 0, 0)
                };
                excerptPanel.SetResourceReference(Border.BackgroundProperty, "InputBrush");
                layout.Children.Add(excerptPanel);
            }

            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
            var cancelButton = new Button { Content = cancel, IsCancel = true, Style = (Style)dialog.FindResource("QuietButton"), MinWidth = 62 };
            var confirmButton = new Button { Content = confirm, Style = (Style)dialog.FindResource("AccentButton"), MinWidth = 82, Margin = new Thickness(7, 0, 0, 0) };
            confirmButton.Click += (sender, args) => dialog.DialogResult = true;
            controls.Children.Add(cancelButton);
            controls.Children.Add(confirmButton);
            layout.Children.Add(controls);
            panel.Child = layout;
            dialog.Content = panel;
            dialog.Loaded += (sender, args) => cancelButton.Focus();
            return dialog;
        }
    }
}
