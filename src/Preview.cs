using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CollaborationChatPreview
{
    internal static class Preview
    {
        [STAThread]
        private static void Main()
        {
            bool firstInstance;
            using (var singleInstance = new Mutex(true, @"Local\CollaborationChatPreview", out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("RelayPane 已经打开。", "RelayPane", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
                string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var store = new StateStore(Path.Combine(localData, "CollaborationChatPreview"),
                    Path.Combine(localData, "CollaborationChatPreviewV2"));
                new Application().Run(new ChatWindow(store));
            }
        }
    }

    internal sealed partial class ChatWindow : Window
    {
        private static readonly AgentKind[] Agents = AgentCatalog.All.Select(entry => entry.Kind).ToArray();
        private const int PaneLimit = 4;

        private readonly StateStore store;
        private readonly ChatState state;
        private readonly Grid view;
        private readonly Canvas stage;
        private readonly Border dropHint;
        private readonly TextBlock status;
        private readonly TextBlock paneCount;
        private readonly Button themeButton;
        private readonly Button addButton;
        private readonly StackPanel historyList;
        private readonly Grid historyLayer;
        private bool showArchivedHistory;
        private readonly Dictionary<ChatPane, PaneView> views = new Dictionary<ChatPane, PaneView>();
        private WorkspaceLayout placement = new WorkspaceLayout();
        private List<ChatPane> visiblePanes = new List<ChatPane>();
        private Conversation workspace;
        private ChatPane activePane;
        private ChatPane movingPane;
        private Point dragStart;
        private bool dragging;
        private bool darkTheme;

        public ChatWindow(StateStore store)
        {
            EmbeddedLibraries.Initialize();
            this.store = store;
            state = store.Load();
            Title = "RelayPane";
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            FontSize = 13;
            MinWidth = 440;
            MinHeight = 420;
            Width = Math.Max(MinWidth, Math.Min(1120, SystemParameters.WorkArea.Width - 42));
            Height = Math.Max(MinHeight, Math.Min(780, SystemParameters.WorkArea.Height - 42));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SetResourceReference(BackgroundProperty, "StageBrush");

            var assembly = typeof(ChatWindow).Assembly;
            using (Stream iconStream = assembly.GetManifestResourceStream("RelayPane.ico"))
                Icon = BitmapFrame.Create(iconStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            using (Stream layout = assembly.GetManifestResourceStream("Preview.xaml"))
                view = (Grid)XamlReader.Load(layout);
            Content = view;

            stage = Element<Canvas>("Stage");
            dropHint = Element<Border>("DropHint");
            status = Element<TextBlock>("Status");
            paneCount = Element<TextBlock>("PaneCount");
            themeButton = Element<Button>("ThemeButton");
            addButton = Element<Button>("AddAgentButton");
            historyList = Element<StackPanel>("HistoryList");
            historyLayer = Element<Grid>("HistoryLayer");

            Element<Button>("HistoryButton").Click += (sender, args) => ShowHistory();
            Element<Button>("NewButton").Click += (sender, args) => OpenWorkspace(null);
            Element<Button>("CloseHistoryButton").Click += (sender, args) => CloseHistory();
            Element<Button>("RecentHistoryButton").Click += (sender, args) => { showArchivedHistory = false; RenderHistory(); };
            Element<Button>("ArchivedHistoryButton").Click += (sender, args) => { showArchivedHistory = true; RenderHistory(); };
            Element<Border>("HistoryBackdrop").MouseLeftButtonDown += (sender, args) => CloseHistory();
            themeButton.Click += (sender, args) => ShowThemeMenu();
            addButton.Click += (sender, args) => ShowAddMenu();
            stage.SizeChanged += (sender, args) => LayoutPanes();
            PreviewKeyDown += HandleWindowKey;
            SourceInitialized += (sender, args) => SetCaptionTheme();
            SystemEvents.UserPreferenceChanged += FollowSystemTheme;
            Closed += (sender, args) => SystemEvents.UserPreferenceChanged -= FollowSystemTheme;
            Loaded += (sender, args) => { LayoutPanes(); FocusActivePane(); };

            InitializeLive();
            InitializeLaunchers();
            ApplyTheme();
            var initial = state.ActiveConversationId == null
                ? state.Conversations.Where(item => !item.ArchivedAt.HasValue).OrderByDescending(item => item.UpdatedAt).FirstOrDefault()
                : state.Conversations.FirstOrDefault(item => item.Id == state.ActiveConversationId);
            OpenWorkspace(initial);
        }

        private T Element<T>(string name) where T : FrameworkElement
        {
            return (T)view.FindName(name);
        }

        private ChatPane MainPane
        {
            get { return workspace.Panes.First(pane => pane.IsMain); }
        }

        private bool HasConversationMessages()
        {
            return workspace.Panes.Any(pane => pane.Messages.Any(message => message.FromUser));
        }

        private void OpenWorkspace(Conversation saved)
        {
            if (liveTurns.Count != 0 || pendingFollowups.Count != 0) { SetStatus("请求正在进行，可先在聊天框里停止。"); return; }
            if (connectionRun != null) { SetStatus("连接检测正在进行，请稍等。"); return; }
            if (pendingAgentSettings.Count != 0) { SetStatus("正在读取或更改 Agent 设置，请稍等。"); return; }
            if (workspace != null && bridge.IsRunning) BridgeNotify(new { type = "closeWorkspace", workspace = workspace.Id });
            if (movingPane != null) CancelDrag();
            foreach (PaneView pane in views.Values) stage.Children.Remove(pane.Container);
            views.Clear();
            workspace = saved ?? new Conversation { Id = Guid.NewGuid().ToString("N"), Title = "新对话", UpdatedAt = DateTime.UtcNow };
            if (workspace.Panes == null || workspace.Panes.Count == 0)
            {
                var main = new ChatPane { IsMain = true, Messages = workspace.Messages ?? new List<ChatMessage>() };
                workspace.Panes = new List<ChatPane> { main };
                workspace.Messages = null;
            }
            if (!HasConversationMessages())
            {
                workspace.RosterNotice = null;
                MainPane.Messages.RemoveAll(message => message.Speaker == "协作工具" && message.Text != null &&
                    (message.Text.StartsWith("用户已添加 ", StringComparison.Ordinal) ||
                     message.Text.StartsWith("用户已移除 ", StringComparison.Ordinal)));
            }
            activePane = MainPane;
            RestoreConnections();
            UpdatePanes();
            CloseHistory();
            string activeId = saved == null ? string.Empty : saved.Id;
            if (state.ActiveConversationId != activeId)
            {
                state.ActiveConversationId = activeId;
                Persist();
            }
        }

        private void UpdatePanes()
        {
            visiblePanes = workspace.Panes.Where(pane => !pane.IsClosed).ToList();
            foreach (ChatPane data in visiblePanes)
            {
                PaneView pane;
                if (!views.TryGetValue(data, out pane))
                {
                    pane = CreatePane(data);
                    views.Add(data, pane);
                    stage.Children.Add(pane.Container);
                }
                pane.Refresh();
                if (data.IsMain && !CanChangeMainAgent()) pane.Options.Visibility = Visibility.Collapsed;
            }
            foreach (var entry in views)
                entry.Value.Container.Visibility = entry.Key.IsClosed ? Visibility.Collapsed : Visibility.Visible;
            bool mainSelected = MainPane.Agent != AgentKind.None;
            addButton.Visibility = mainSelected ? Visibility.Visible : Visibility.Collapsed;
            addButton.IsEnabled = mainSelected && visiblePanes.Count < PaneLimit && connectionRun == null;
            addButton.ToolTip = visiblePanes.Count >= PaneLimit ? "当前页面最多打开 4 个 Agent" : "添加一个 Agent 聊天框";
            paneCount.Visibility = mainSelected ? Visibility.Visible : Visibility.Collapsed;
            paneCount.Text = visiblePanes.Count(pane => pane.Agent != AgentKind.None) + " / " + PaneLimit + " 个 Agent";
            foreach (ChatPane pane in visiblePanes)
                if (pane.Agent != AgentKind.None) UpdateActivity(pane.Agent);
            LayoutPanes();
            SyncVisibleAgents();
            UpdateConnectButton();
        }

        private PaneView CreatePane(ChatPane data)
        {
            var pane = new PaneView(data, SendLive, StopLive, Persist);
            if (data.IsMain)
                foreach (AgentKind agent in Agents)
                {
                    var choice = new Button
                    {
                        Content = SurfaceMenus.AgentRow(agent),
                        Style = (Style)view.Resources["QuietButton"],
                        MinHeight = 46,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(8, 5, 8, 5),
                        Margin = new Thickness(4)
                    };
                    AutomationProperties.SetName(choice, PaneView.AgentName(agent));
                    choice.SetResourceReference(Button.BackgroundProperty, "InputBrush");
                    choice.SetResourceReference(Button.BorderBrushProperty, "LineBrush");
                    choice.Click += (sender, args) => SelectMainAgent(agent);
                    pane.AgentChoices.Children.Add(choice);
                }
            pane.Options.Click += (sender, args) => ShowPaneMenu(pane);
            pane.SettingsButton.Click += (sender, args) => ShowAgentSettings(pane);
            pane.Composer.GotKeyboardFocus += (sender, args) => activePane = data;
            pane.Grab.MouseLeftButtonDown += (sender, args) => StartDrag(pane, args);
            pane.Grab.MouseMove += (sender, args) => MoveDrag(args);
            pane.Grab.MouseLeftButtonUp += (sender, args) => EndDrag(args);
            pane.Grab.LostMouseCapture += (sender, args) => CancelDrag();
            return pane;
        }

        private void ShowMainAgentMenu(Button target)
        {
            var menu = CreateMenu(target);
            foreach (AgentKind agent in Agents.Where(agent => !workspace.Panes.Any(pane => !pane.IsMain && pane.Agent == agent)))
                AddMenuItem(menu, PaneView.AgentName(agent), () => SelectMainAgent(agent), MainPane.Agent == agent);
            menu.IsOpen = true;
        }

        private void SelectMainAgent(AgentKind agent)
        {
            ChatPane main = MainPane;
            if (liveTurns.Count != 0 || pendingAgentSettings.Count != 0 || connectionRun != null || !Agents.Contains(agent) ||
                main.Agent == agent || workspace.Panes.Any(pane => !string.IsNullOrEmpty(pane.SessionId) || !pane.IsMain && pane.Agent == agent)) return;
            main.Agent = agent;
            main.Preferences = null;
            main.SettingsCaption = null;
            if (!main.Messages.Any(message => message.FromUser)) workspace.Title = PaneView.AgentName(agent) + " 对话";
            RememberWorkspace();
            UpdatePanes();
            SelectPane(main);
        }

        private void ShowAddMenu()
        {
            if (!addButton.IsEnabled) return;
            var menu = CreateMenu(addButton);
            menu.Width = 272;
            menu.Tag = SurfaceMenus.Heading("添加 Agent", string.Format("已打开 {0} / {1}", visiblePanes.Count, PaneLimit));
            foreach (AgentKind agent in Agents)
            {
                ChatPane existing = workspace.Panes.FirstOrDefault(pane => pane.Agent == agent);
                bool available = existing == null || existing.IsClosed;
                string state = existing == null ? "＋ 添加" : existing.IsMain ? "主 Agent" : existing.IsClosed ? "恢复" : "已添加";
                var item = new MenuItem { Header = SurfaceMenus.AgentRow(agent, state, available), IsEnabled = available, Padding = new Thickness(8, 5, 8, 5) };
                AutomationProperties.SetName(item, PaneView.AgentName(agent) + " · " + state);
                if (available) item.Click += (sender, args) => AddAgent(agent);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private void AddAgent(AgentKind agent)
        {
            if (MainPane.Agent == AgentKind.None || visiblePanes.Count >= PaneLimit || connectionRun != null || !Agents.Contains(agent)) return;
            ChatPane pane = workspace.Panes.FirstOrDefault(item => item.Agent == agent);
            if (pane == null)
            {
                pane = new ChatPane { Agent = agent };
                workspace.Panes.Add(pane);
            }
            else if (pane.IsClosed) pane.IsClosed = false;
            else return;
            RecordRosterChange(agent, true);
            UpdatePanes();
            SelectPane(pane);
        }

        private void ClosePane(ChatPane pane)
        {
            if (pane.IsMain) return;
            if (connectionRun != null) { SetStatus("连接检测正在进行，请稍等。"); return; }
            if (pendingAgentSettings.ContainsKey(pane.Agent)) { SetStatus("正在更改这个 Agent 的设置，请稍等。"); return; }
            if (liveTurns.ContainsKey(pane.Agent) || pendingFollowups.Contains(pane.Agent)) StopLive(FindPane(pane.Agent));
            pane.IsClosed = true;
            RecordRosterChange(pane.Agent, false);
            UpdatePanes();
            SelectPane(activePane == pane ? MainPane : activePane);
        }

        private void SelectPane(ChatPane pane)
        {
            if (!visiblePanes.Contains(pane)) return;
            activePane = pane;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
            {
                if (activePane == pane) FocusActivePane();
            }));
        }

        private void FocusActivePane()
        {
            PaneView pane;
            if (activePane == null || !views.TryGetValue(activePane, out pane)) return;
            if (pane.Data.Agent == AgentKind.None) ((Button)pane.AgentChoices.Children[0]).Focus();
            else pane.Composer.Focus();
        }

        private void HandleWindowKey(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.Escape)
            {
                if (movingPane != null) { CancelDrag(); args.Handled = true; }
                else if (historyLayer.IsVisible) { CloseHistory(); args.Handled = true; }
                return;
            }

            ModifierKeys modifiers = Keyboard.Modifiers;
            if (args.Key == Key.Tab && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) && visiblePanes.Count > 1)
            {
                int step = modifiers == ModifierKeys.Control ? 1 : -1;
                int index = visiblePanes.IndexOf(activePane);
                SelectPane(visiblePanes[(index + step + visiblePanes.Count) % visiblePanes.Count]);
                args.Handled = true;
            }
            else if (modifiers == ModifierKeys.Control && args.Key == Key.N)
            {
                OpenWorkspace(null);
                args.Handled = true;
            }
            else if (modifiers == ModifierKeys.Control && args.Key == Key.H)
            {
                if (historyLayer.IsVisible) CloseHistory();
                else ShowHistory();
                args.Handled = true;
            }
        }

        private void RememberMessage(PaneView pane)
        {
            if (pane.Data.IsMain && pane.Data.Messages.Count(message => message.FromUser) == 1)
            {
                string text = pane.Data.Messages.First(message => message.FromUser).Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                var title = new StringInfo(text.Trim());
                workspace.Title = title.LengthInTextElements > 28 ? title.SubstringByTextElements(0, 28) + "…" : title.String;
            }
            RememberWorkspace();
        }

        private void RememberWorkspace()
        {
            if (MainPane.Agent != AgentKind.None)
            {
                state.ActiveConversationId = workspace.Id;
                workspace.UpdatedAt = DateTime.UtcNow;
                if (!state.Conversations.Contains(workspace)) state.Conversations.Add(workspace);
            }
            Persist();
        }

        private void ShowHistory()
        {
            RenderHistory();
            historyLayer.Visibility = Visibility.Visible;
            Element<Button>("CloseHistoryButton").Focus();
        }

        private void RenderHistory()
        {
            Element<Button>("RecentHistoryButton").SetResourceReference(Button.BackgroundProperty, showArchivedHistory ? "PanelBrush" : "MenuHoverBrush");
            Element<Button>("ArchivedHistoryButton").SetResourceReference(Button.BackgroundProperty, showArchivedHistory ? "MenuHoverBrush" : "PanelBrush");
            historyList.Children.Clear();
            var entries = state.Conversations.Where(item => item.ArchivedAt.HasValue == showArchivedHistory).OrderByDescending(item => item.UpdatedAt).ToList();
            if (entries.Count == 0)
            {
                var empty = new TextBlock { Text = showArchivedHistory ? "还没有归档的对话。" : "还没有保存的对话。", Margin = new Thickness(7, 12, 0, 0) };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                historyList.Children.Add(empty);
            }
            foreach (Conversation item in entries)
            {
                ChatPane main = item.Panes == null ? null : item.Panes.FirstOrDefault(pane => pane.IsMain);
                AgentKind agent = main == null ? AgentKind.None : main.Agent;
                var title = SurfaceMenus.Text(item.Title, "TextBrush", 13, true);
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                string metadata = (agent == AgentKind.None ? "" : PaneView.AgentName(agent) + "  ·  ") + item.UpdatedAt.ToLocalTime().ToString("M月d日 HH:mm");
                var date = SurfaceMenus.Text(metadata, "MutedBrush", 11);
                date.Margin = new Thickness(0, 4, 0, 0);
                var label = new StackPanel();
                label.Children.Add(title);
                label.Children.Add(date);
                var row = new DockPanel();
                FrameworkElement icon = SurfaceMenus.AgentIcon(agent);
                icon.Margin = new Thickness(0, 0, 9, 0);
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
                row.Children.Add(label);
                var button = new Button { Content = row, Margin = new Thickness(0, 0, 0, 4), Style = (Style)view.Resources["QuietButton"], Padding = new Thickness(8, 7, 8, 7), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(button, item.Title);
                if (item == workspace) button.SetResourceReference(Button.BackgroundProperty, "MenuHoverBrush");
                button.Click += (sender, args) => { if (item == workspace) CloseHistory(); else OpenWorkspace(item); };
                var actions = new Button { Content = "⋯", Style = (Style)view.Resources["QuietButton"], Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 4) };
                AutomationProperties.SetName(actions, item.Title + " 的历史操作");
                actions.Click += (sender, args) => ShowHistoryActions(actions, item);
                var entry = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(actions, Dock.Right);
                entry.Children.Add(actions);
                entry.Children.Add(button);
                historyList.Children.Add(entry);
            }
        }

        private void ShowHistoryActions(Button anchor, Conversation item)
        {
            var menu = CreateMenu(anchor);
            AddMenuItem(menu, item.ArchivedAt.HasValue ? "取消归档" : "归档", () => ToggleArchive(item));
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "删除记录…", () => AskDeleteHistory(item));
            menu.IsOpen = true;
        }

        private void ToggleArchive(Conversation item)
        {
            DateTime? previous = item.ArchivedAt;
            item.ArchivedAt = item.ArchivedAt.HasValue ? (DateTime?)null : DateTime.UtcNow;
            if (!Persist()) item.ArchivedAt = previous;
            else SetStatus(item.ArchivedAt.HasValue ? "对话已归档" : "对话已恢复到最近记录");
            RenderHistory();
        }

        private void AskDeleteHistory(Conversation item)
        {
            if (item == workspace && (liveTurns.Count > 0 || connectionRun != null))
            {
                SetStatus("请等当前请求或连接检测结束，再删除对话。");
                return;
            }
            if (SurfaceDialog.Confirm(this, "删除这条记录？", "只删除协作聊天中的历史记录。各 Agent 自己的原生会话会保留。", item.Title,
                "删除记录", "取消")) DeleteHistory(item);
        }

        private void DeleteHistory(Conversation item)
        {
            int position = state.Conversations.IndexOf(item);
            if (position < 0 || (item == workspace && (liveTurns.Count > 0 || connectionRun != null))) return;
            state.Conversations.RemoveAt(position);
            if (!Persist()) state.Conversations.Insert(position, item);
            else
            {
                if (item == workspace) OpenWorkspace(null);
                SetStatus("已删除这条本地历史记录");
            }
            RenderHistory();
        }

        private void CloseHistory()
        {
            historyLayer.Visibility = Visibility.Collapsed;
            if (IsLoaded) FocusActivePane();
        }

        private bool Persist()
        {
            try
            {
                store.Save(state);
                return true;
            }
            catch (IOException) { status.Text = "历史未保存"; status.ToolTip = "下次更改时会重试。"; }
            catch (UnauthorizedAccessException) { status.Text = "历史未保存"; status.ToolTip = "没有权限写入本地历史。"; }
            return false;
        }

        private void ShowThemeMenu()
        {
            var menu = CreateMenu(themeButton);
            menu.Tag = SurfaceMenus.Heading("外观");
            AddMenuItem(menu, "跟随系统", () => SetTheme(ThemeChoice.System), state.Theme == ThemeChoice.System);
            AddMenuItem(menu, "浅色", () => SetTheme(ThemeChoice.Light), state.Theme == ThemeChoice.Light);
            AddMenuItem(menu, "深色", () => SetTheme(ThemeChoice.Dark), state.Theme == ThemeChoice.Dark);
            menu.IsOpen = true;
        }

        private ContextMenu CreateMenu(Button target)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom, FontSize = 13, FontFamily = FontFamily };
            menu.Background = (Brush)Resources["PanelBrush"];
            menu.Foreground = (Brush)Resources["TextBrush"];
            menu.BorderBrush = (Brush)Resources["LineBrush"];
            if (target == themeButton || target == addButton)
            {
                menu.Placement = PlacementMode.Custom;
                menu.CustomPopupPlacementCallback = (popup, anchor, offset) => new[] {
                    new CustomPopupPlacement(new Point(anchor.Width - popup.Width, anchor.Height + 5), PopupPrimaryAxis.Horizontal),
                    new CustomPopupPlacement(new Point(anchor.Width - popup.Width, -popup.Height - 5), PopupPrimaryAxis.Horizontal)
                };
            }
            else menu.VerticalOffset = 5;
            target.ContextMenu = menu;
            SurfaceMenus.Apply(menu, target);
            return menu;
        }

        private static void AddMenuItem(ContextMenu menu, string text, Action action, bool selected = false)
        {
            var item = new MenuItem { Header = text, IsCheckable = selected, IsChecked = selected, Foreground = menu.Foreground };
            item.Click += (sender, args) => action();
            menu.Items.Add(item);
        }

        private void SetTheme(ThemeChoice theme)
        {
            if (state.Theme == theme) return;
            state.Theme = theme;
            ApplyTheme();
            Persist();
        }

        private void FollowSystemTheme(object sender, UserPreferenceChangedEventArgs args)
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke((Action)(() => { if (state.Theme == ThemeChoice.System) ApplyTheme(); }));
        }


        private void ApplyTheme()
        {
            object windowsLightMode = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            darkTheme = state.Theme == ThemeChoice.Dark || (state.Theme == ThemeChoice.System && 0.Equals(windowsLightMode));
            SetBrush("StageBrush", "#F1F4F3", "#111313");
            SetBrush("PanelBrush", "#FFFFFF", "#1B1B1B");
            SetBrush("LineBrush", "#D9E2DF", "#343937");
            SetBrush("MainOutlineBrush", "#86AFA0", "#557166");
            SetBrush("MainHeaderBrush", "#F2F8F6", "#222725");
            SetBrush("TextBrush", "#1E2B29", "#F0F2EF");
            SetBrush("MutedBrush", "#51645F", "#B5C2BC");
            SetBrush("HoverBrush", "#E6EFEB", "#303A36");
            SetBrush("InputBrush", "#F5F8F7", "#232725");
            SetBrush("BubbleBrush", "#DFEFE8", "#2E3B34");
            SetBrush("FinalReplyBrush", "#EBF7F0", "#192A22");
            SetBrush("FinalReplyBorderBrush", "#68AA8A", "#75B79A");
            SetBrush("FinalReplyTextBrush", "#1D342A", "#F4FFF8");
            SetBrush("FinalReplyButtonBrush", "#D7ECDF", "#2C4738");
            SetBrush("AccentBrush", "#216A58", "#3D6556");
            SetBrush("AccentHoverBrush", "#195546", "#477762");
            SetBrush("HintBrush", "#14216A58", "#3066A289");
            SetBrush("ScrimBrush", "#3919263C", "#850C0C0C");
            SetBrush("CompleteBrush", "#1B6F50", "#9DD9B7");
            SetBrush("TransferBrush", "#246B5B", "#9BD0BC");
            SetBrush("AttentionBrush", "#935724", "#FFD3AE");
            SetBrush("ScrollThumbBrush", "#BCC9DD", "#56514C");
            SetBrush("MarkdownLinkBrush", "#2956BD", "#E0C599");
            SetBrush("MarkdownCodeBrush", "#E8EEF8", "#171717");
            SetBrush("MarkdownHeaderBrush", "#EAF0F8", "#2D2A27");
            SetBrush("MarkdownRuleBrush", "#C9D8EC", "#494440");
            SetBrush("SelectionBrush", "#A8BCE9", "#A58F72");
            SetBrush("MenuBrush", "#FFFFFF", "#212624");
            SetBrush("MenuLineBrush", "#DBE4E0", "#3C4843");
            SetBrush("MenuHoverBrush", "#EAF3EF", "#2F3D37");
            connections.Theme(darkTheme);
            themeButton.Content = (state.Theme == ThemeChoice.System ? "跟随系统" : state.Theme == ThemeChoice.Light ? "浅色" : "深色") + " ▾";
            SetCaptionTheme();
        }

        private void SetBrush(string name, string light, string dark)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(darkTheme ? dark : light));
            brush.Freeze();
            Resources[name] = brush;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        private void SetCaptionTheme()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            int enabled = darkTheme ? 1 : 0;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }


        private Rect FrameBounds()
        {
            double margin = stage.ActualWidth < 650 ? 9 : 18;
            double width = Math.Min(1510, Math.Max(0, stage.ActualWidth - 2 * margin));
            double height = Math.Max(0, stage.ActualHeight - 2 * margin);
            return new Rect((stage.ActualWidth - width) / 2, margin, width, height);
        }

        private void LayoutPanes()
        {
            if (visiblePanes.Count == 0 || dragging) return;
            Rect frame = FrameBounds();
            if (frame.Width <= 0 || frame.Height <= 0) return;

            bool usesBands = visiblePanes.Count > 1 && (frame.Width < WorkspaceLayout.WideThreshold ||
                visiblePanes.Any(pane => !pane.IsMain && (pane.Placement == PanePlacement.Top || pane.Placement == PanePlacement.Bottom)));
            MinHeight = usesBands ? Math.Min(505, Math.Max(420, SystemParameters.WorkArea.Height - 32)) : 420;
            placement = WorkspaceLayout.Arrange(frame, visiblePanes, MainPane);
            foreach (var item in placement.Bounds) Place(views[item.Key].Container, item.Value);
            Place(connections, new Rect(0, 0, stage.ActualWidth, stage.ActualHeight));
            connections.Reposition(placement.Bounds[MainPane], placement.Bounds.Where(item => !item.Key.IsMain)
                .ToDictionary(item => item.Key.Agent, item => item.Value));
        }

        private static void Place(FrameworkElement element, Rect bounds)
        {
            Canvas.SetLeft(element, bounds.X);
            Canvas.SetTop(element, bounds.Y);
            element.Width = bounds.Width;
            element.Height = bounds.Height;
        }

        private bool CanChangeMainAgent()
        {
            return MainPane.Agent != AgentKind.None && !MainPane.Messages.Any(message => message.FromUser)
                && workspace.Panes.All(pane => string.IsNullOrEmpty(pane.SessionId)) && workspace.Panes.Count < Agents.Length;
        }

        private void ShowPaneMenu(PaneView pane)
        {
            var menu = CreateMenu(pane.Options);
            menu.Tag = SurfaceMenus.Heading(pane.Name);
            if (pane.Data.IsMain)
            {
                if (CanChangeMainAgent())
                    AddMenuItem(menu, "更换主 Agent", () => Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() => ShowMainAgentMenu(pane.Options))));
            }
            else
            {
                var destinations = new Dictionary<PanePlacement, string>();
                if (FrameBounds().Width >= WorkspaceLayout.WideThreshold)
                {
                    destinations.Add(PanePlacement.Left, "放到左侧");
                    destinations.Add(PanePlacement.Right, "放到右侧");
                }
                destinations.Add(PanePlacement.Top, "放到上方");
                destinations.Add(PanePlacement.Bottom, "放到下方");
                foreach (var destination in destinations)
                    AddMenuItem(menu, destination.Value, () => MovePane(pane.Data, destination.Key), pane.Data.Placement == destination.Key);
                AddMenuItem(menu, "自动排列", () => MovePane(pane.Data, PanePlacement.Auto), pane.Data.Placement == PanePlacement.Auto);
                menu.Items.Add(new Separator());
                AddMenuItem(menu, "关闭聊天框", () => ClosePane(pane.Data));
            }
            if (menu.Items.Count > 0) menu.IsOpen = true;
        }

        private void MovePane(ChatPane pane, PanePlacement destination)
        {
            if (pane.IsMain || pane.Placement == destination) return;
            pane.Placement = destination;
            RememberWorkspace();
            LayoutPanes();
        }

        private void SwapPanes(ChatPane first, ChatPane second)
        {
            if (first.IsMain || second.IsMain || first == second) return;
            PanePlacement source = placement.Placements[first];
            first.Placement = placement.Placements[second];
            second.Placement = source;
            int firstIndex = workspace.Panes.IndexOf(first);
            int secondIndex = workspace.Panes.IndexOf(second);
            workspace.Panes[firstIndex] = second;
            workspace.Panes[secondIndex] = first;
            RememberWorkspace();
            UpdatePanes();
        }

        private void StartDrag(PaneView pane, MouseButtonEventArgs args)
        {
            if (pane.Data.IsMain || !placement.Bounds.ContainsKey(pane.Data)) return;
            dragStart = args.GetPosition(stage);
            if (pane.Grab.CaptureMouse())
            {
                movingPane = pane.Data;
                args.Handled = true;
            }
        }

        private void MoveDrag(MouseEventArgs args)
        {
            if (movingPane == null || args.LeftButton != MouseButtonState.Pressed) return;
            Point pointer = args.GetPosition(stage);
            if (!dragging && Math.Abs(pointer.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pointer.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            dragging = true;
            views[movingPane].Container.Opacity = 0.56;
            ShowDropHint(pointer);
        }

        private void ShowDropHint(Point pointer)
        {
            ChatPane target = DropTarget(pointer);
            if (target != null)
            {
                Place(dropHint, placement.Bounds[target]);
                dropHint.Visibility = Visibility.Visible;
                return;
            }

            Rect frame = FrameBounds();
            PanePlacement destination = WorkspaceLayout.EdgeAt(frame, pointer);
            if (destination == PanePlacement.Auto)
            {
                dropHint.Visibility = Visibility.Collapsed;
                return;
            }
            WorkspaceLayout preview = WorkspaceLayout.Arrange(frame, visiblePanes, MainPane, movingPane, destination);
            Place(dropHint, preview.Bounds[movingPane]);
            dropHint.Visibility = Visibility.Visible;
        }

        private ChatPane DropTarget(Point pointer)
        {
            foreach (var item in placement.Bounds)
                if (item.Key != movingPane && !item.Key.IsMain && item.Value.Contains(pointer)) return item.Key;
            return null;
        }

        private void EndDrag(MouseButtonEventArgs args)
        {
            if (movingPane == null) return;
            ChatPane pane = movingPane;
            Point pointer = args.GetPosition(stage);
            ChatPane target = dragging ? DropTarget(pointer) : null;
            PanePlacement destination = dragging ? WorkspaceLayout.EdgeAt(FrameBounds(), pointer) : PanePlacement.Auto;
            CancelDrag();
            if (target != null) SwapPanes(pane, target);
            else if (destination != PanePlacement.Auto) MovePane(pane, destination);
            args.Handled = true;
        }

        private void CancelDrag()
        {
            if (movingPane == null) return;
            PaneView pane = views[movingPane];
            movingPane = null;
            dragging = false;
            pane.Container.Opacity = 1;
            if (pane.Grab.IsMouseCaptured) pane.Grab.ReleaseMouseCapture();
            dropHint.Visibility = Visibility.Collapsed;
            LayoutPanes();
        }
    }
}

