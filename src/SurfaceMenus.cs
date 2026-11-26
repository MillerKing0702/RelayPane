using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CollaborationChatPreview
{
    internal static class SurfaceMenus
    {
        private static readonly string[] Palette = {
            "MenuBrush", "MenuLineBrush", "MenuHoverBrush", "TextBrush", "MutedBrush", "TransferBrush", "ScrollThumbBrush",
            "InputBrush"
        };
        private static readonly Dictionary<AgentKind, Task<ImageSource>> IconSources = new Dictionary<AgentKind, Task<ImageSource>>();

        public static void Apply(ContextMenu menu, FrameworkElement anchor)
        {
            var surface = anchor.TryFindResource("SurfaceMenu") as Style;
            if (surface == null) return;
            menu.Style = surface;
            menu.Resources[typeof(MenuItem)] = anchor.TryFindResource("SurfaceMenuItem");
            menu.Resources[typeof(Separator)] = anchor.TryFindResource("SurfaceSeparator");
            var scrollStyle = anchor.TryFindResource(typeof(ScrollBar)) as Style;
            if (scrollStyle != null) menu.Resources[typeof(ScrollBar)] = scrollStyle;
            foreach (string brush in Palette)
            {
                object value = anchor.TryFindResource(brush);
                if (value != null) menu.Resources[brush] = value;
            }
        }

        public static FrameworkElement Heading(string title, string detail = null)
        {
            var head = new StackPanel { Margin = new Thickness(8, 6, 8, 7) };
            head.Children.Add(Text(title, "TextBrush", 16, true));
            if (!string.IsNullOrWhiteSpace(detail))
            {
                TextBlock caption = Text(detail, "MutedBrush", 11);
                caption.Margin = new Thickness(0, 4, 0, 0);
                caption.TextTrimming = TextTrimming.CharacterEllipsis;
                caption.ToolTip = detail;
                head.Children.Add(caption);
            }
            return head;
        }

        public static void AddEditorMenu(TextBox editor, bool readOnly = false)
        {
            var menu = new ContextMenu();
            menu.Opened += (sender, args) => Apply(menu, editor);
            if (!readOnly) menu.Items.Add(EditorCommand(editor, "剪切", "Ctrl+X", ApplicationCommands.Cut));
            menu.Items.Add(EditorCommand(editor, "复制", "Ctrl+C", ApplicationCommands.Copy));
            if (!readOnly) menu.Items.Add(EditorCommand(editor, "粘贴", "Ctrl+V", ApplicationCommands.Paste));
            menu.Items.Add(new Separator());
            menu.Items.Add(EditorCommand(editor, "全选", "Ctrl+A", ApplicationCommands.SelectAll));
            editor.ContextMenu = menu;
        }

        private static MenuItem EditorCommand(TextBox editor, string text, string shortcut, ICommand command)
        {
            return new MenuItem { Header = text, InputGestureText = shortcut, Command = command, CommandTarget = editor };
        }

        public static TextBlock Text(string value, string brush, double size, bool prominent = false)
        {
            var label = new TextBlock { Text = value, FontSize = size };
            if (prominent) label.FontWeight = FontWeights.SemiBold;
            label.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return label;
        }

        public static FrameworkElement AgentIcon(AgentKind agent, double size = 31)
        {
            var icon = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size >= 30 ? 9 : 7), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            var fallback = Text(agent == AgentKind.None ? "·" : AgentCatalog.Get(agent).Badge, "TextBrush", size < 27 ? 11 : 13, true);
            fallback.HorizontalAlignment = HorizontalAlignment.Center;
            fallback.VerticalAlignment = VerticalAlignment.Center;
            icon.Child = fallback;
            if (agent == AgentKind.Codex || agent == AgentKind.Dsh || agent == AgentKind.Antigravity)
                SetNativeIcon(icon, agent, size);
            return icon;
        }

        private static async void SetNativeIcon(Border badge, AgentKind agent, double size)
        {
            Task<ImageSource> load;
            if (!IconSources.TryGetValue(agent, out load))
            {
                load = Task.Run<ImageSource>(() =>
                {
                    try
                    {
                        string path = NativeAgentPrograms.Default(agent).Path;
                        if (!File.Exists(path)) return null;
                        using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
                        {
                            if (icon == null) return null;
                            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                            source.Freeze();
                            return source;
                        }
                    }
                    catch (Exception error)
                    {
                        if (!(error is IOException || error is UnauthorizedAccessException || error is ArgumentException ||
                            error is System.ComponentModel.Win32Exception || error is System.Runtime.InteropServices.ExternalException ||
                            error is InvalidOperationException || error is NotSupportedException)) throw;
                        return null;
                    }
                });
                IconSources.Add(agent, load);
            }
            ImageSource image = await load.ConfigureAwait(false);
            if (image == null || badge.Dispatcher.HasShutdownStarted) return;
            await badge.Dispatcher.InvokeAsync(() =>
                badge.Child = new Image { Source = image, Width = size - 5, Height = size - 5, Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }

        public static FrameworkElement AgentRow(AgentKind agent, string trailing = null, bool available = true)
        {
            var row = new DockPanel { LastChildFill = true };
            if (!string.IsNullOrEmpty(trailing))
            {
                TextBlock action = Text(trailing, available ? "TransferBrush" : "MutedBrush", 11, available);
                action.Margin = new Thickness(6, 0, 0, 0);
                action.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(action, Dock.Right);
                row.Children.Add(action);
            }
            FrameworkElement icon = AgentIcon(agent);
            icon.Margin = new Thickness(0, 0, 9, 0);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var name = Text(PaneView.AgentName(agent), "TextBrush", 13, true);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            return row;
        }
    }
}
