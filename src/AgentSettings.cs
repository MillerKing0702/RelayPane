using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace CollaborationChatPreview
{
    internal sealed partial class ChatWindow
    {
        private readonly Dictionary<AgentKind, string> pendingAgentSettings = new Dictionary<AgentKind, string>();
        private readonly Queue<IDictionary<string, object>> approvalRequests = new Queue<IDictionary<string, object>>();
        private bool approvalOpen;

        private void ShowAgentSettings(PaneView pane)
        {
            AgentKind agent = pane.Data.Agent;
            if (agent == AgentKind.None || agent == AgentKind.Hermes) return;
            if (pendingAgentSettings.ContainsKey(agent)) return;
            var menu = CreateMenu(pane.SettingsButton);
            menu.Width = 286;
            menu.Placement = PlacementMode.Top;
            menu.VerticalOffset = -5;
            RequestAgentSettings(pane, menu, null, null);
            menu.IsOpen = true;
        }

        private void RequestAgentSettings(PaneView pane, ContextMenu menu, string key, string value)
        {
            AgentKind agent = pane.Data.Agent;
            if (pendingAgentSettings.ContainsKey(agent)) return;
            if (liveTurns.ContainsKey(agent) || pendingFollowups.Contains(agent) || connectionRun != null)
            {
                SetStatus("请在该 Agent 空闲时更改设置。");
                return;
            }
            menu.Tag = SurfaceMenus.Heading(pane.Name + " 设置", "当前会话");
            menu.Items.Clear();
            menu.Items.Add(new MenuItem { Header = key == null ? "正在读取原生选项…" : "正在应用选择…", IsEnabled = false });
            string id = Guid.NewGuid().ToString("N");
            pendingAgentSettings[agent] = id;
            UpdateConnectButton();
            try
            {
                bridge.Send(new { type = "agentSettings", id, workspace = BridgeSettings(), agent = agent.ToString(),
                    change = key == null ? null : new { id = key, value } });
            }
            catch (Exception error)
            {
                if (!IsBridgeError(error)) throw;
                pendingAgentSettings.Remove(agent);
                UpdateConnectButton();
                FillAgentSettingsMenu(pane, menu, new List<IDictionary<string, object>>(), error.Message);
                SetStatus(error.Message);
            }
        }

        private void ReceiveAgentSettings(PaneView pane, IDictionary<string, object> packet)
        {
            string request;
            if (!pendingAgentSettings.TryGetValue(pane.Data.Agent, out request) || request != Read(packet, "run")) return;
            pendingAgentSettings.Remove(pane.Data.Agent);
            UpdateConnectButton();
            var fields = Objects(packet, "fields").ToList();
            bool ok = ReadFlag(packet, "ok");
            string error = ok ? null : Read(packet, "text");
            if (ok) pane.SetSettingsAvailability(fields.Count > 0);
            string caption = SettingsSummary(fields);
            if (fields.Count > 0) pane.SetSettingsCaption(caption);
            object values;
            if (ok && packet.TryGetValue("preferences", out values) && values is IDictionary<string, object>)
            {
                pane.Data.Preferences = ((IDictionary<string, object>)values)
                    .Where(entry => entry.Value is string).ToDictionary(entry => entry.Key, entry => (string)entry.Value);
                pane.Data.SettingsCaption = pane.Data.Preferences.Count == 0 ? null : caption;
                if (Persist()) SetStatus(pane.Name + " 设置已保存");
            }
            if (!ok) SetStatus(error ?? "无法读取 Agent 设置。");
            var menu = pane.SettingsButton.ContextMenu;
            if (menu != null && menu.IsOpen)
            {
                if (ok && fields.Count == 0) menu.IsOpen = false;
                else FillAgentSettingsMenu(pane, menu, fields, error);
            }
        }

        private static IEnumerable<IDictionary<string, object>> Objects(IDictionary<string, object> bag, string key)
        {
            object value;
            return bag.TryGetValue(key, out value) && value is IEnumerable
                ? ((IEnumerable)value).OfType<IDictionary<string, object>>()
                : Enumerable.Empty<IDictionary<string, object>>();
        }

        private static string CurrentChoice(IDictionary<string, object> field)
        {
            string current = Read(field, "current");
            var selected = Objects(field, "choices").FirstOrDefault(choice => Read(choice, "value") == current);
            return selected == null ? null : Read(selected, "label");
        }

        private static string SettingsSummary(IEnumerable<IDictionary<string, object>> fields)
        {
            return string.Join(" · ", fields.Where(field => Read(field, "id") == "model" || Read(field, "id") == "effort" || Read(field, "id") == "access" ||
                (Read(field, "id") == "mode" && Read(field, "current") != "default"))
                .Select(CurrentChoice).Where(label => !string.IsNullOrEmpty(label)));
        }

        private void FillAgentSettingsMenu(PaneView pane, ContextMenu menu, List<IDictionary<string, object>> fields, string error = null)
        {
            menu.Tag = SurfaceMenus.Heading(pane.Name + " 设置", error ?? "当前会话");
            menu.Items.Clear();
            foreach (var field in fields)
            {
                var row = new MenuItem { Header = Read(field, "label"), InputGestureText = CurrentChoice(field) ?? "选择",
                    StaysOpenOnClick = true };
                row.Click += (sender, args) => ShowAgentChoices(pane, menu, fields, field);
                menu.Items.Add(row);
            }
            if (fields.Count == 0)
            {
                var retry = new MenuItem { Header = "重新读取", StaysOpenOnClick = true };
                retry.Click += (sender, args) => RequestAgentSettings(pane, menu, null, null);
                menu.Items.Add(retry);
            }
            if (pane.Data.Preferences != null && pane.Data.Preferences.Count > 0)
            {
                menu.Items.Add(new Separator());
                var clear = new MenuItem { Header = "清除本应用保存的选择", StaysOpenOnClick = true };
                clear.Click += (sender, args) => RequestAgentSettings(pane, menu, "clear", null);
                menu.Items.Add(clear);
            }
        }

        private void ShowAgentChoices(PaneView pane, ContextMenu menu, List<IDictionary<string, object>> fields, IDictionary<string, object> field)
        {
            string current = Read(field, "current");
            string key = Read(field, "id");
            menu.Tag = SurfaceMenus.Heading(Read(field, "label"), pane.Name);
            menu.Items.Clear();
            var back = new MenuItem { Header = "‹ 返回设置", StaysOpenOnClick = true };
            back.Click += (sender, args) => FillAgentSettingsMenu(pane, menu, fields);
            menu.Items.Add(back);
            menu.Items.Add(new Separator());
            foreach (var choice in Objects(field, "choices"))
            {
                string value = Read(choice, "value");
                string label = Read(choice, "label");
                var item = new MenuItem { Header = label, IsCheckable = value == current, IsChecked = value == current,
                    StaysOpenOnClick = true };
                item.Click += (sender, args) =>
                {
                    if (value == current) FillAgentSettingsMenu(pane, menu, fields);
                    else RequestAgentSettings(pane, menu, key, value);
                };
                menu.Items.Add(item);
            }
        }

        private void QueueAgentApproval(IDictionary<string, object> packet)
        {
            approvalRequests.Enqueue(packet);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)ShowNextApproval);
        }

        private void ShowNextApproval()
        {
            if (approvalOpen || approvalRequests.Count == 0) return;
            var packet = approvalRequests.Dequeue();
            AgentKind agent;
            string run;
            if (Read(packet, "workspace") == workspace.Id && Enum.TryParse(Read(packet, "agent"), out agent) &&
                liveTurns.TryGetValue(agent, out run) && run == Read(packet, "run"))
            {
                approvalOpen = true;
                try
                {
                    bool allow = SurfaceDialog.Confirm(this, PaneView.AgentName(agent) + " 请求确认",
                        Read(packet, "text"), Read(packet, "detail"), "允许一次", "拒绝");
                    BridgeNotify(new { type = "approvalDecision", id = Read(packet, "id"), allow });
                }
                finally { approvalOpen = false; }
            }
            else BridgeNotify(new { type = "approvalDecision", id = Read(packet, "id"), allow = false });
            if (approvalRequests.Count > 0)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)ShowNextApproval);
        }
    }
}
