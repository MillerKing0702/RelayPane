using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace CollaborationChatPreview
{
    internal sealed class MessageBubble : StackPanel
    {
        private readonly ChatMessage message;
        private readonly bool mainPane;
        private readonly TextBlock speaker;
        private readonly Border bubble;
        private TextBoxBase presentation;
        private string shownText;
        private bool compact;
        private bool narrow;
        private bool configured;

        public MessageBubble(ChatMessage message, bool mainPane, Func<bool> onRead)
        {
            this.message = message;
            this.mainPane = mainPane;
            HorizontalAlignment = message.FromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            speaker = new TextBlock
            {
                Text = (message.Speaker ?? (message.FromUser ? "你" : "演示回复")) +
                    (message.IsFinalReply ? " · 最终回复" : "") + " · " + message.CreatedAt.ToLocalTime().ToString("HH:mm"),
                FontSize = message.IsFinalReply ? 11 : 10,
                FontWeight = message.IsFinalReply ? FontWeights.SemiBold : FontWeights.Normal,
                TextAlignment = message.FromUser ? TextAlignment.Right : TextAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            bubble = new Border { CornerRadius = new CornerRadius(9), ClipToBounds = true };
            if (message.IsFinalReply && !message.Acknowledged)
            {
                var header = new DockPanel();
                var markRead = new Button { Content = "✓", FontSize = 13, FontWeight = FontWeights.SemiBold,
                    MinWidth = 26, MinHeight = 23, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(5, 0, 0, 4),
                    ToolTip = "标为已读并取消高亮", Cursor = Cursors.Hand };
                markRead.SetResourceReference(Button.StyleProperty, "AccentButton");
                markRead.SetResourceReference(Button.BackgroundProperty, "FinalReplyButtonBrush");
                markRead.SetResourceReference(Button.ForegroundProperty, "CompleteBrush");
                System.Windows.Automation.AutomationProperties.SetName(markRead, "将最终回复标为已读");
                DockPanel.SetDock(markRead, Dock.Right);
                markRead.Click += (sender, args) =>
                {
                    message.Acknowledged = true;
                    if (!onRead())
                    {
                        message.Acknowledged = false;
                        return;
                    }
                    markRead.Visibility = Visibility.Collapsed;
                    UpdateHighlight();
                    FitDocument();
                };
                header.Children.Add(markRead);
                header.Children.Add(speaker);
                Children.Add(header);
            }
            else Children.Add(speaker);
            Children.Add(bubble);
            UpdateHighlight();
        }

        private void UpdateHighlight()
        {
            bool highlighted = message.IsFinalReply && !message.Acknowledged;
            speaker.SetResourceReference(TextBlock.ForegroundProperty, highlighted ? "CompleteBrush" : "MutedBrush");
            bubble.SetResourceReference(Border.BackgroundProperty, highlighted ? "FinalReplyBrush" : message.FromUser ? "BubbleBrush" : "InputBrush");
            bubble.BorderThickness = new Thickness(highlighted ? 1 : 0);
            if (highlighted) bubble.SetResourceReference(Border.BorderBrushProperty, "FinalReplyBorderBrush");
            else bubble.ClearValue(Border.BorderBrushProperty);
        }

        public void Configure(double availableWidth, bool compactMode)
        {
            double ratio = message.FromUser ? 0.90 : 0.96;
            double width = Math.Min(mainPane ? 780 : 500, Math.Max(60, availableWidth * ratio));
            bool useNarrowTables = width < 350;
            bool reformat = !configured || compact != compactMode || narrow != useNarrowTables;
            compact = compactMode;
            narrow = useNarrowTables;
            configured = true;
            MaxWidth = width;
            Margin = new Thickness(0, 0, 0, compact ? 6 : 14);
            speaker.Margin = new Thickness(2, 0, 2, compact ? 3 : 5);
            bubble.Padding = compact ? new Thickness(7, 5, 7, 5) : new Thickness(11, 8, 11, 8);
            if (reformat) Render(true);
            FitDocument();
        }

        public void Render(bool force = false)
        {
            string source = message.Text ?? "";
            if (!force && shownText == source) return;
            shownText = source;
            FlowDocument document = new MarkdownFlow(compact, narrow).Render(source);
            if (document == null)
            {
                var plain = presentation as TextBox;
                if (plain == null)
                {
                    plain = new TextBox { TextWrapping = TextWrapping.Wrap };
                    ApplyReaderStyle(plain);
                }
                plain.FontSize = compact ? 12 : 14;
                plain.Text = source;
            }
            else
            {
                if (message.IsFinalReply) document.SetResourceReference(TextElement.ForegroundProperty, "FinalReplyTextBrush");
                var rich = presentation as RichTextBox;
                if (rich == null)
                {
                    rich = new RichTextBox { IsDocumentEnabled = true };
                    ApplyReaderStyle(rich);
                }
                rich.Document = document;
                FitDocument();
            }
        }

        private void FitDocument()
        {
            var rich = presentation as RichTextBox;
            if (rich != null && configured)
                rich.Width = Math.Max(1, MaxWidth - bubble.Padding.Left - bubble.Padding.Right - bubble.BorderThickness.Left - bubble.BorderThickness.Right);
        }

        private void ApplyReaderStyle(TextBoxBase box)
        {
            box.IsReadOnly = true;
            box.IsReadOnlyCaretVisible = false;
            box.BorderThickness = new Thickness(0);
            box.Padding = new Thickness(0);
            box.Background = Brushes.Transparent;
            box.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            box.SetResourceReference(Control.ForegroundProperty, message.IsFinalReply ? "FinalReplyTextBrush" : "TextBrush");
            box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "SelectionBrush");

            var menu = new ContextMenu();
            menu.Opened += (sender, args) => SurfaceMenus.Apply(menu, box);
            menu.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
            menu.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            var copySelection = new MenuItem { Header = "复制选中内容", InputGestureText = "Ctrl+C", Command = ApplicationCommands.Copy, CommandTarget = box };
            copySelection.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            var copySource = new MenuItem { Header = "复制整条消息" };
            copySource.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            copySource.Click += (sender, args) => ChatClipboard.CopyMessage(message.Text, box);
            CommandManager.AddPreviewExecutedHandler(box, (sender, args) =>
            {
                if (args.Command != ApplicationCommands.Copy || !ChatClipboard.WholeMessageSelected(box)) return;
                args.Handled = true;
                ChatClipboard.CopyMessage(message.Text, box);
            });
            menu.Items.Add(copySelection);
            menu.Items.Add(copySource);
            box.ContextMenu = menu;
            presentation = box;
            bubble.Child = box;
        }
    }
}
