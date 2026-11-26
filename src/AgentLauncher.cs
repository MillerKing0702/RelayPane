using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CollaborationChatPreview
{
    internal sealed partial class ChatWindow
    {
        private Button launchAgentButton;

        private void InitializeLaunchers()
        {
            if (state.Launchers == null) state.Launchers = new List<AgentLaunchSettings>();
            launchAgentButton = Element<Button>("LaunchAgentButton");
            launchAgentButton.Click += (sender, args) => ShowLaunchMenu();
        }

        private void ShowLaunchMenu()
        {
            var menu = CreateMenu(launchAgentButton);
            menu.Width = 276;
            menu.Tag = SurfaceMenus.Heading("一键打开", "启动本机 Agent 窗口");
            menu.Placement = PlacementMode.Custom;
            menu.CustomPopupPlacementCallback = (popup, anchor, offset) => new[] {
                new CustomPopupPlacement(new Point(anchor.Width - popup.Width, -popup.Height - 5), PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(anchor.Width - popup.Width, anchor.Height + 5), PopupPrimaryAxis.Horizontal)
            };
            foreach (AgentKind agent in Agents)
            {
                var item = new MenuItem { Header = SurfaceMenus.AgentRow(agent, "打开"), Padding = new Thickness(8, 5, 8, 5) };
                AutomationProperties.SetName(item, "打开 " + PaneView.AgentName(agent));
                item.Click += (sender, args) => OpenNativeAgent(agent);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "检测和修改启动路径…", () => new LaunchPathEditor(this, Agents, LaunchSettings, SaveLaunchSettings).Show());
            menu.IsOpen = true;
        }

        private AgentLaunchSettings LaunchSettings(AgentKind agent)
        {
            AgentLaunchSettings saved = state.Launchers.LastOrDefault(item => item.Agent == agent);
            return saved == null ? NativeAgentPrograms.Default(agent) : new AgentLaunchSettings { Agent = agent, Path = saved.Path, Distribution = saved.Distribution };
        }

        private bool SaveLaunchSettings(AgentLaunchSettings replacement)
        {
            List<AgentLaunchSettings> previous = state.Launchers.ToList();
            state.Launchers.RemoveAll(item => item.Agent == replacement.Agent);
            state.Launchers.Add(replacement);
            if (!Persist())
            {
                state.Launchers = previous;
                return false;
            }
            SetStatus(PaneView.AgentName(replacement.Agent) + " 启动路径已保存");
            return true;
        }

        private async void OpenNativeAgent(AgentKind agent)
        {
            if (!launchAgentButton.IsEnabled) return;
            launchAgentButton.IsEnabled = false;
            string name = PaneView.AgentName(agent);
            SetStatus("正在核对 " + name + " 的启动路径");
            try
            {
                AgentLaunchSettings entry = LaunchSettings(agent);
                LaunchCheck result = await Task.Run(() => NativeAgentPrograms.Check(entry));
                if (!IsLoaded) return;
                if (result.Verification == LaunchVerification.Invalid)
                {
                    SetStatus(name + " · " + result.Detail);
                    if (SurfaceDialog.Confirm(this, name + " 启动路径不可用", result.Detail, entry.Path, "修改路径", "关闭"))
                        new LaunchPathEditor(this, Agents, LaunchSettings, SaveLaunchSettings, agent).Show();
                    return;
                }
                if (result.Verification == LaunchVerification.Unverified &&
                    !SurfaceDialog.Confirm(this, "打开自定义启动入口？", result.Detail, entry.Path, "打开", "取消"))
                {
                    SetStatus("已取消打开 " + name);
                    return;
                }
                using (Process launched = Process.Start(NativeAgentPrograms.CreateStartInfo(entry))) { }
                SetStatus("已发送打开 " + name + " 的请求");
            }
            catch (Exception error)
            {
                if (!(IsBridgeError(error) || error is ArgumentException || error is NotSupportedException)) throw;
                SetStatus(name + " 启动失败 · " + error.Message);
            }
            finally { launchAgentButton.IsEnabled = true; }
        }
    }
}
