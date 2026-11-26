using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;

namespace CollaborationChatPreview
{
    internal sealed class PaneView
    {
        public readonly ChatPane Data;
        public readonly Border Container;
        public readonly Border Grab;
        public readonly Button Options;
        public readonly Button SettingsButton;
        public readonly UniformGrid AgentChoices;
        public readonly TextBox Composer;

        private readonly ScrollViewer scroll;
        private readonly StackPanel messages;
        private readonly TextBlock placeholder;
        private readonly Button sendButton;
        private readonly Button stopButton;
        private readonly TextBlock settingsCaption;
        private string currentSettingsCaption;
        private bool settingsAvailable = true;
        private readonly Func<bool> onRead;
        private readonly RowDefinition headerRow;
        private readonly Border inputArea;
        private bool compact;
        private readonly TextBlock activity;
        private bool busy;
        private AgentKind shownIcon;
        private ChatMessage streaming;
        private MessageBubble streamingBubble;
        private readonly DispatcherTimer streamRefresh;

        public PaneView(ChatPane pane, Action<PaneView, string> onMessage, Action<PaneView> onStop, Func<bool> onRead)
        {
            Data = pane;
            this.onRead = onRead;
            using (Stream layout = typeof(PaneView).Assembly.GetManifestResourceStream("Pane.xaml"))
                Container = (Border)XamlReader.Load(layout);
            Grab = Element<Border>("Grab");
            Options = Element<Button>("PaneOptions");
            SettingsButton = Element<Button>("AgentSettingsButton");
            settingsCaption = Element<TextBlock>("AgentSettingsCaption");
            currentSettingsCaption = pane.SettingsCaption;
            AgentChoices = Element<UniformGrid>("AgentChoices");
            Composer = Element<TextBox>("Composer");
            sendButton = Element<Button>("SendButton");
            stopButton = Element<Button>("StopButton");
            scroll = Element<ScrollViewer>("MessageScroll");
            messages = Element<StackPanel>("Messages");
            placeholder = Element<TextBlock>("Placeholder");
            if (Data.IsMain) placeholder.Text = "写消息…  ·  @Agent 直接派发";
            headerRow = (RowDefinition)Container.FindName("HeaderRow");
            inputArea = Element<Border>("InputArea");
            activity = Element<TextBlock>("Activity");
            messages.PreviewMouseWheel += (sender, args) =>
            {
                if (scroll.ScrollableHeight <= 0 || args.Delta == 0) return;
                int lines = SystemParameters.WheelScrollLines;
                double distance = lines < 0 ? scroll.ViewportHeight : lines * 16.0;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset - distance * args.Delta / 120.0);
                args.Handled = true;
            };
            streamRefresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            streamRefresh.Tick += (sender, args) =>
            {
                streamRefresh.Stop();
                if (streamingBubble != null) RefreshStream();
            };
            Container.Unloaded += (sender, args) => streamRefresh.Stop();
            if (Data.IsMain)
            {
                Container.SetResourceReference(Border.BorderBrushProperty, "MainOutlineBrush");
                Element<Border>("PaneHeader").SetResourceReference(Border.BackgroundProperty, "MainHeaderBrush");
                Element<TextBlock>("GrabMark").Visibility = Visibility.Collapsed;
                Grab.Cursor = Cursors.Arrow;
                Grab.ToolTip = null;
            }

            Composer.TextChanged += (sender, args) =>
            {
                placeholder.Visibility = Composer.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                sendButton.IsEnabled = !string.IsNullOrWhiteSpace(Composer.Text);
            };
            DataObject.AddPastingHandler(Composer, ChatClipboard.PasteOriginal);
            SurfaceMenus.AddEditorMenu(Composer);
            Composer.PreviewKeyDown += (sender, args) =>
            {
                if (args.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
                args.Handled = true;
                Send(onMessage);
            };
            sendButton.Click += (sender, args) => Send(onMessage);
            stopButton.Click += (sender, args) => onStop(this);
            Container.SizeChanged += (sender, args) => ResizePane(args.NewSize.Height);

            foreach (ChatMessage message in Data.Messages)
                if (ShowsMessage(message)) messages.Children.Add(new MessageBubble(message, Data.IsMain, onRead));
            Refresh();
            ScrollToEnd();
        }

        public static string AgentName(AgentKind agent)
        {
            return agent == AgentKind.None ? "主对话" : AgentCatalog.Get(agent).Name;
        }

        public string Name
        {
            get { return Data.Agent == AgentKind.None ? "主对话" : AgentName(Data.Agent); }
        }

        private T Element<T>(string name) where T : FrameworkElement
        {
            return (T)Container.FindName(name);
        }

        public void Refresh()
        {
            bool selected = Data.Agent != AgentKind.None;
            if (shownIcon != Data.Agent)
            {
                var icon = Element<ContentControl>("ProviderIcon");
                icon.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                icon.Content = selected ? SurfaceMenus.AgentIcon(Data.Agent, 23) : null;
                shownIcon = Data.Agent;
                currentSettingsCaption = Data.SettingsCaption;
                settingsAvailable = true;
            }
            Element<TextBlock>("Heading").Text = Name;
            Element<Border>("MainTag").Visibility = Data.IsMain && selected ? Visibility.Visible : Visibility.Collapsed;
            Element<StackPanel>("ChooseAgent").Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
            Options.Visibility = selected && (!Data.IsMain || (string.IsNullOrEmpty(Data.SessionId) &&
                !Data.Messages.Exists(message => message.FromUser))) ? Visibility.Visible : Visibility.Collapsed;
            SettingsButton.Visibility = selected && Data.Agent != AgentKind.Hermes && settingsAvailable ? Visibility.Visible : Visibility.Collapsed;
            RefreshSettingsCaption();
            Element<TextBlock>("EmptyConversation").Visibility = selected && messages.Children.Count == 0 && streaming == null ? Visibility.Visible : Visibility.Collapsed;
            inputArea.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            scroll.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Send(Action<PaneView, string> onMessage)
        {
            string text = Composer.Text;
            if (Data.Agent == AgentKind.None || string.IsNullOrWhiteSpace(text)) return;
            onMessage(this, text);
        }

        public void AddMessage(ChatMessage message)
        {
            bool follow = message.FromUser || AtEnd();
            Data.Messages.Add(message);
            if (ShowsMessage(message)) messages.Children.Add(new MessageBubble(message, Data.IsMain, onRead));
            Refresh();
            ResizeMessages();
            if (follow) ScrollToEnd();
        }

        private bool ShowsMessage(ChatMessage message)
        {
            if (!Data.IsMain || message.FromUser || string.IsNullOrEmpty(message.Speaker)) return true;
            int separator = message.Speaker.IndexOf(" → ", StringComparison.Ordinal);
            if (separator < 1 || message.Speaker.Substring(separator + 3) != Name) return true;
            string sender = message.Speaker.Substring(0, separator);
            foreach (AgentKind agent in Enum.GetValues(typeof(AgentKind)))
                if (agent != AgentKind.None && agent != Data.Agent && AgentName(agent) == sender) return false;
            return true;
        }

        public void SetBusy(bool value)
        {
            busy = value;
            sendButton.Content = busy ? "追加" : "发送";
            sendButton.IsEnabled = !string.IsNullOrWhiteSpace(Composer.Text);
            sendButton.ToolTip = busy ? "追加消息 (Enter) · 换行 (Shift+Enter)" : "发送 (Enter) · 换行 (Shift+Enter)";
            stopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            SettingsButton.IsEnabled = !busy;
            RefreshSettingsCaption();
        }

        public void SetSettingsCaption(string value)
        {
            currentSettingsCaption = value;
            RefreshSettingsCaption();
        }

        public void SetSettingsAvailability(bool value)
        {
            settingsAvailable = value;
            SettingsButton.Visibility = value && Data.Agent != AgentKind.None && Data.Agent != AgentKind.Hermes
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshSettingsCaption()
        {
            double width = Container.ActualWidth;
            SettingsButton.MaxWidth = width > 0 ? Math.Max(52, width - 35) : 260;
            settingsCaption.Text = width > 0 && width < 185 ? "设置" :
                string.IsNullOrEmpty(currentSettingsCaption) ? "模型与模式" : currentSettingsCaption;
            SettingsButton.ToolTip = busy ? "本轮结束后可以调整设置" :
                string.IsNullOrEmpty(currentSettingsCaption) ? "选择模型、思考强度和模式" : currentSettingsCaption;
        }

        public void SetActivity(string text, string brush, string detail = null)
        {
            activity.Text = text ?? "";
            activity.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            activity.SetResourceReference(TextBlock.ForegroundProperty, brush);
            activity.ToolTip = detail;
        }

        public void SetStream(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (streaming == null)
            {
                bool follow = AtEnd();
                streaming = new ChatMessage { Speaker = Name + " · 进展", Text = text, CreatedAt = DateTime.UtcNow };
                streamingBubble = new MessageBubble(streaming, Data.IsMain, onRead);
                messages.Children.Add(streamingBubble);
                Refresh();
                ResizeMessages();
                if (follow) ScrollToEnd();
            }
            streaming.Text = text;
            if (!streamRefresh.IsEnabled) streamRefresh.Start();
        }

        public void FinishStream(string text, bool success, string progress = null)
        {
            DateTime started = streaming == null ? DateTime.UtcNow : streaming.CreatedAt;
            if (streamingBubble != null)
            {
                streamRefresh.Stop();
                if (success) messages.Children.Remove(streamingBubble);
                else
                {
                    RefreshStream();
                    Data.Messages.Add(streaming);
                }
                streaming = null;
                streamingBubble = null;
            }
            if (!success) return;
            if (!string.IsNullOrWhiteSpace(progress))
                AddMessage(new ChatMessage { Speaker = Name + " · 进展", Text = progress, CreatedAt = started });
            if (!string.IsNullOrEmpty(text))
                AddMessage(new ChatMessage { Speaker = Name, Text = text, CreatedAt = DateTime.UtcNow, IsFinalReply = Data.IsMain });
        }

        private void RefreshStream()
        {
            bool follow = AtEnd();
            streamingBubble.Render();
            if (follow) ScrollToEnd();
        }

        private bool AtEnd()
        {
            return scroll.ScrollableHeight - scroll.VerticalOffset <= 4;
        }

        internal void ScrollToEnd()
        {
            Container.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() => scroll.ScrollToEnd()));
        }

        private void ResizePane(double height)
        {
            bool useCompactLayout = height < 210;
            if (compact != useCompactLayout)
            {
                compact = useCompactLayout;
                headerRow.Height = new GridLength(compact ? 34 : 44);
                inputArea.Margin = compact ? new Thickness(6, 3, 6, 6) : new Thickness(11, 5, 11, 11);
                inputArea.Padding = new Thickness(compact ? 2 : 3);
                messages.Margin = compact ? new Thickness(8, 5, 8, 2) : new Thickness(15, 13, 15, 4);
                Composer.MinHeight = compact ? 27 : 37;
                Composer.Padding = compact ? new Thickness(5, 4, 3, 4) : new Thickness(8, 8, 3, 7);
                sendButton.MinHeight = compact ? 25 : 34;
                sendButton.Padding = compact ? new Thickness(7, 1, 7, 1) : new Thickness(12, 6, 12, 6);
                ScrollToEnd();
            }
            bool narrow = Container.ActualWidth > 0 && Container.ActualWidth < 220;
            stopButton.Content = narrow ? "■" : "停止";
            stopButton.MinWidth = narrow ? 25 : 43;
            stopButton.MinHeight = sendButton.MinHeight;
            stopButton.Padding = narrow ? new Thickness(3, 1, 3, 1)
                : compact ? new Thickness(5, 1, 5, 1) : new Thickness(8, 6, 8, 6);
            RefreshSettingsCaption();
            Composer.MaxHeight = Math.Max(Composer.MinHeight, Math.Min(compact ? 55 : 95, height * 0.33));
            ResizeMessages();
        }

        private void ResizeMessages()
        {
            double available = Math.Max(80, Container.ActualWidth - (compact ? 24 : 40));
            foreach (MessageBubble group in messages.Children) group.Configure(available, compact);
        }
    }
}
