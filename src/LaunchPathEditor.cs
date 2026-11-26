using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CollaborationChatPreview
{
    internal sealed class LaunchPathEditor
    {
        private readonly Window window;
        private readonly Func<AgentKind, AgentLaunchSettings> load;
        private readonly Func<AgentLaunchSettings, bool> save;
        private readonly Dictionary<AgentKind, AgentLaunchSettings> drafts = new Dictionary<AgentKind, AgentLaunchSettings>();
        private readonly Dictionary<AgentKind, Button> selectors = new Dictionary<AgentKind, Button>();
        private readonly TextBox path;
        private readonly TextBox distribution;
        private readonly StackPanel distributionGroup;
        private readonly TextBlock pathLabel;
        private readonly TextBlock status;
        private readonly Button detectButton;
        private readonly Button saveButton;
        private readonly Button defaultButton;
        private AgentKind selected;
        private bool checking;
        private bool closed;

        public LaunchPathEditor(Window owner, AgentKind[] agents, Func<AgentKind, AgentLaunchSettings> load,
            Func<AgentLaunchSettings, bool> save, AgentKind initial = AgentKind.Codex)
        {
            this.load = load;
            this.save = save;
            window = SurfaceDialog.NewWindow(owner, "Agent 启动路径", 514);
            window.Closed += (sender, args) => closed = true;
            Border frame = SurfaceDialog.Frame();
            var content = new DockPanel { LastChildFill = true };
            var title = SurfaceMenus.Text("Agent 启动路径", "TextBrush", 18, true);
            title.Margin = new Thickness(0, 0, 0, 6);
            DockPanel.SetDock(title, Dock.Top);
            content.Children.Add(title);
            var hint = SurfaceMenus.Text("这些路径用于打开本机窗口。聊天仍通过各 Agent 的官方接口连接。", "MutedBrush", 12);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(hint, Dock.Top);
            content.Children.Add(hint);

            var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
            defaultButton = Button("恢复默认");
            defaultButton.Click += (sender, args) => ResetDraft();
            DockPanel.SetDock(defaultButton, Dock.Left);
            buttons.Children.Add(defaultButton);
            saveButton = Button("保存", true);
            saveButton.Click += async (sender, args) => await Validate(true);
            DockPanel.SetDock(saveButton, Dock.Right);
            buttons.Children.Add(saveButton);
            detectButton = Button("检测路径");
            detectButton.Margin = new Thickness(0, 0, 4, 0);
            detectButton.Click += async (sender, args) => await Validate(false);
            DockPanel.SetDock(detectButton, Dock.Right);
            buttons.Children.Add(detectButton);
            var close = Button("关闭");
            close.IsCancel = true;
            DockPanel.SetDock(close, Dock.Right);
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Bottom);
            content.Children.Add(buttons);

            var fields = new StackPanel();
            var agentList = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 8) };
            foreach (AgentKind agent in agents)
            {
                var item = Button(null);
                item.Content = SurfaceMenus.AgentRow(agent);
                item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                item.Padding = new Thickness(6, 4, 6, 4);
                item.Margin = new Thickness(2, 2, 2, 2);
                AutomationProperties.SetName(item, PaneView.AgentName(agent));
                item.Click += (sender, args) => Select(agent);
                selectors.Add(agent, item);
                agentList.Children.Add(item);
            }
            fields.Children.Add(agentList);
            pathLabel = SurfaceMenus.Text("启动路径", "TextBrush", 12, true);
            pathLabel.Margin = new Thickness(0, 0, 0, 5);
            fields.Children.Add(pathLabel);
            path = PathBox("启动路径");
            fields.Children.Add(path);
            distributionGroup = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var distroLabel = SurfaceMenus.Text("WSL 发行版", "TextBrush", 12, true);
            distroLabel.Margin = new Thickness(0, 0, 0, 5);
            distributionGroup.Children.Add(distroLabel);
            distribution = PathBox("WSL 发行版");
            distributionGroup.Children.Add(distribution);
            fields.Children.Add(distributionGroup);
            status = SurfaceMenus.Text("尚未检测", "MutedBrush", 12);
            status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(0, 9, 0, 0);
            fields.Children.Add(status);
            path.TextChanged += (sender, args) => ResetStatus();
            distribution.TextChanged += (sender, args) => ResetStatus();
            content.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            frame.Child = content;
            window.Content = frame;
            Select(initial);
            window.Loaded += (sender, args) => path.Focus();
        }

        public void Show() { window.ShowDialog(); }

        private Button Button(string label, bool primary = false)
        {
            return new Button { Content = label, Style = (Style)window.FindResource(primary ? "AccentButton" : "QuietButton"), Padding = new Thickness(8, 5, 8, 5) };
        }

        private static TextBox PathBox(string name)
        {
            var input = new TextBox { FontSize = 13, Padding = new Thickness(8, 6, 8, 6), BorderThickness = new Thickness(1), MinHeight = 33 };
            input.SetResourceReference(FrameworkElement.StyleProperty, "EditorTextBox");
            input.SetResourceReference(Control.BackgroundProperty, "InputBrush");
            input.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            input.SetResourceReference(Control.BorderBrushProperty, "MenuLineBrush");
            AutomationProperties.SetName(input, name);
            SurfaceMenus.AddEditorMenu(input);
            return input;
        }

        private void Select(AgentKind agent)
        {
            if (selected != AgentKind.None) drafts[selected] = Draft();
            selected = agent;
            AgentLaunchSettings draft;
            if (!drafts.TryGetValue(agent, out draft)) draft = load(agent);
            SetFields(draft);
            foreach (var item in selectors) item.Value.SetResourceReference(Control.BackgroundProperty, item.Key == agent ? "MenuHoverBrush" : "MenuBrush");
        }

        private void ResetDraft()
        {
            AgentLaunchSettings draft = NativeAgentPrograms.Default(selected);
            drafts[selected] = draft;
            SetFields(draft);
        }

        private void SetFields(AgentLaunchSettings entry)
        {
            pathLabel.Text = entry.Agent == AgentKind.Hermes ? "Hermes 的 Linux 执行文件" : "程序或启动脚本路径";
            distributionGroup.Visibility = entry.Agent == AgentKind.Hermes ? Visibility.Visible : Visibility.Collapsed;
            path.Text = entry.Path ?? "";
            distribution.Text = entry.Distribution ?? "";
            ResetStatus();
        }

        private AgentLaunchSettings Draft()
        {
            string value = path.Text.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2);
            return new AgentLaunchSettings { Agent = selected, Path = value, Distribution = selected == AgentKind.Hermes ? distribution.Text.Trim() : null };
        }

        private void ResetStatus()
        {
            status.Text = "尚未检测";
            status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        }

        private void Busy(bool value)
        {
            checking = value;
            detectButton.IsEnabled = saveButton.IsEnabled = defaultButton.IsEnabled = !value;
            path.IsEnabled = distribution.IsEnabled = !value;
            foreach (Button item in selectors.Values) item.IsEnabled = !value;
        }

        private async Task Validate(bool commit)
        {
            if (checking) return;
            AgentLaunchSettings entry = Draft();
            Busy(true);
            status.Text = "正在检测路径…";
            status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            try
            {
                LaunchCheck report = await Task.Run(() => NativeAgentPrograms.Check(entry)).ConfigureAwait(false);
                await window.Dispatcher.InvokeAsync(() => ApplyCheck(entry, report, commit));
            }
            finally
            {
                if (!window.Dispatcher.HasShutdownStarted)
                    window.Dispatcher.Invoke(new Action(() => { if (!closed) Busy(false); }));
            }
        }

        private void ApplyCheck(AgentLaunchSettings entry, LaunchCheck report, bool commit)
        {
            if (closed) return;
            status.Text = report.Detail;
            status.SetResourceReference(TextBlock.ForegroundProperty, report.Verification == LaunchVerification.Verified ? "CompleteBrush" : "AttentionBrush");
            if (!commit || report.Verification == LaunchVerification.Invalid) return;
            if (!SurfaceDialog.Confirm(window, "保存 " + PaneView.AgentName(entry.Agent) + " 的启动路径？", report.Detail,
                entry.Agent == AgentKind.Hermes ? entry.Distribution + "\n" + entry.Path : entry.Path, "保存", "取消")) return;
            if (save(entry)) status.Text = "已保存 · " + report.Detail;
            else status.Text = "保存失败，请检查协作聊天的本地数据目录。";
        }
    }
}
