using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace CollaborationChatPreview
{
    internal sealed class AgentInventory
    {
        private readonly Window window;
        private readonly Button detect;
        private readonly TextBlock summary;
        private readonly Dictionary<AgentKind, TextBlock> rows = new Dictionary<AgentKind, TextBlock>();
        private readonly Dictionary<AgentKind, Button> guides = new Dictionary<AgentKind, Button>();

        public AgentInventory(Window owner, Action run, Action<string> status)
        {
            window = SurfaceDialog.NewWindow(owner, "Agent 安装检测", 620);
            window.MaxHeight = Math.Min(610, SystemParameters.WorkArea.Height - 26);
            var frame = SurfaceDialog.Frame();
            var layout = new DockPanel();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            detect = Button("一键检测", true);
            detect.Click += (sender, args) => run();
            DockPanel.SetDock(detect, Dock.Right);
            header.Children.Add(detect);
            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(SurfaceMenus.Text("Agent 安装检测", "TextBrush", 19, true));
            summary = SurfaceMenus.Text("检查本机安装和 ACP 入口", "MutedBrush", 11);
            summary.Margin = new Thickness(0, 3, 7, 0);
            summary.TextTrimming = TextTrimming.CharacterEllipsis;
            heading.Children.Add(summary);
            header.Children.Add(heading);
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            var close = Button("关闭");
            close.IsCancel = true;
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, 12, 0, 0);
            DockPanel.SetDock(close, Dock.Bottom);
            layout.Children.Add(close);
            var list = new StackPanel();
            foreach (AgentCatalog.Entry entry in AgentCatalog.All)
            {
                var row = new DockPanel { LastChildFill = true };
                var guide = Button("安装教程");
                guide.ToolTip = "在浏览器打开官方安装教程";
                guide.Margin = new Thickness(7, 0, 0, 0);
                guide.VerticalAlignment = VerticalAlignment.Center;
                AutomationProperties.SetName(guide, "打开 " + entry.FullName + " 官方安装教程");
                guides.Add(entry.Kind, guide);
                guide.Click += (sender, args) =>
                {
                    try
                    {
                        using (Process opened = Process.Start(new ProcessStartInfo(entry.InstallUrl) { UseShellExecute = true })) { }
                    }
                    catch (System.ComponentModel.Win32Exception) { status("无法打开默认浏览器。"); }
                };
                DockPanel.SetDock(guide, Dock.Right);
                row.Children.Add(guide);
                var icon = SurfaceMenus.AgentIcon(entry.Kind, 26);
                icon.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = SurfaceMenus.Text(entry.FullName, "TextBrush", 13, true);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                info.Children.Add(name);
                var indicator = SurfaceMenus.Text("等待检测", "MutedBrush", 12, true);
                indicator.Margin = new Thickness(0, 2, 0, 0);
                info.Children.Add(indicator);
                rows.Add(entry.Kind, indicator);
                row.Children.Add(info);
                var card = new Border { Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 6, 5),
                    Margin = new Thickness(0, 0, 0, 5), BorderThickness = new Thickness(1) };
                card.SetResourceReference(Border.BackgroundProperty, "InputBrush");
                card.SetResourceReference(Border.BorderBrushProperty, "MenuLineBrush");
                list.Children.Add(card);
            }
            layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 464 });
            frame.Child = layout;
            window.Content = frame;
        }

        public void Show() { window.ShowDialog(); }

        private Button Button(string label, bool primary = false)
        {
            return new Button { Content = label, Style = (Style)window.FindResource(primary ? "AccentButton" : "QuietButton"),
                MinHeight = 28, Padding = new Thickness(8, 4, 8, 4), FontSize = 12 };
        }

        public void Begin()
        {
            detect.IsEnabled = false;
            summary.Text = "正在检查本机安装…";
        }

        public void Complete(IEnumerable<IDictionary<string, object>> results, string error)
        {
            detect.IsEnabled = true;
            detect.Content = "重新检测";
            int installed = 0, missing = 0, other = 0;
            foreach (var result in results)
            {
                object value;
                AgentKind agent;
                if (!result.TryGetValue("agent", out value) || !Enum.TryParse(value as string, out agent) || !rows.ContainsKey(agent)) continue;
                string detail = result.TryGetValue("detail", out value) ? value as string : null;
                string state = result.TryGetValue("status", out value) ? value as string : null;
                var row = rows[agent];
                row.Text = state == "installed" ? "已安装" : state == "missing" ? "未安装" : state == "adapter" ? "缺少 ACP 适配器" : "无法确认";
                row.ToolTip = detail;
                row.SetResourceReference(TextBlock.ForegroundProperty, state == "installed" ? "CompleteBrush" :
                    state == "missing" ? "TextBrush" : "AttentionBrush");
                var guide = guides[agent];
                guide.Content = state == "adapter" ? "适配器教程 ↗" : state == "missing" ? "安装教程 ↗" : "安装教程";
                guide.SetResourceReference(Control.BackgroundProperty, state == "installed" ? "InputBrush" : "MenuHoverBrush");
                if (state == "installed") installed++;
                else if (state == "missing") missing++;
                else other++;
            }
            summary.Text = !string.IsNullOrWhiteSpace(error) ? error :
                installed + " 已安装  ·  " + missing + " 未安装" + (other > 0 ? "  ·  " + other + " 待处理" : "");
            summary.ToolTip = summary.Text;
        }
    }

    internal sealed partial class ChatWindow
    {
        private AgentInventory inventory;
        private string installationRun;

        private void ShowAgentInventory()
        {
            inventory = new AgentInventory(this, DetectInstallations, SetStatus);
            DetectInstallations();
            try { inventory.Show(); }
            finally { inventory = null; installationRun = null; }
        }

        private void DetectInstallations()
        {
            if (inventory == null || installationRun != null) return;
            inventory.Begin();
            installationRun = Guid.NewGuid().ToString("N");
            try { bridge.Send(new { type = "detectAgents", id = installationRun }); }
            catch (Exception error)
            {
                if (!IsBridgeError(error)) throw;
                installationRun = null;
                inventory.Complete(Enumerable.Empty<IDictionary<string, object>>(), error.Message);
            }
        }

        private void ReceiveInstallations(IDictionary<string, object> packet)
        {
            if (inventory == null || installationRun != Read(packet, "run")) return;
            installationRun = null;
            inventory.Complete(Objects(packet, "results"), Read(packet, "text"));
        }
    }
}
