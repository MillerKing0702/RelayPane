using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace CollaborationChatPreview
{
    internal sealed partial class ChatWindow
    {
        private readonly Dictionary<AgentKind, string> liveTurns = new Dictionary<AgentKind, string>();
        private readonly HashSet<AgentKind> pendingFollowups = new HashSet<AgentKind>();
        private readonly HashSet<AgentKind> pendingConnections = new HashSet<AgentKind>();
        private readonly Dictionary<AgentKind, ConnectionResult> connectionResults = new Dictionary<AgentKind, ConnectionResult>();
        private LiveBridge bridge;
        private Connections connections;
        private Button connectButton;
        private string connectionRun;

        private sealed class ConnectionResult
        {
            public bool Success;
            public bool Missing;
            public string Details;
        }

        private void InitializeLive()
        {
            bridge = new LiveBridge(Dispatcher, BridgeEvent);
            connections = new Connections();
            Panel.SetZIndex(connections, -1);
            stage.Children.Add(connections);
            connections.Completed = agent =>
            {
                CollaborationExchange exchange = LastExchange(agent);
                if (exchange == null || exchange.Status != "returning") return;
                exchange.Status = "completed";
                UpdateActivity(agent);
                Persist();
            };
            Element<Button>("ProjectButton").Click += (sender, args) => ShowProjectMenu();
            connectButton = Element<Button>("ConnectButton");
            connectButton.Click += (sender, args) => ConnectAgents();
            Element<Button>("AgentInventoryButton").Click += (sender, args) => ShowAgentInventory();
            Closing += ConfirmClose;
            Closed += (sender, args) => { bridge.Dispose(); connections.Dispose(); };
        }

        private void ConfirmClose(object sender, CancelEventArgs args)
        {
            if ((liveTurns.Count != 0 || pendingFollowups.Count != 0) && !SurfaceDialog.Confirm(this, "退出协作聊天？", "退出会停止正在运行的请求。", null,
                "停止并退出", "继续使用")) args.Cancel = true;
        }

        private void SetStatus(string text)
        {
            status.Text = text;
            status.ToolTip = text;
        }

        private void RestoreConnections()
        {
            connectionResults.Clear();
            connections.Reset();
            if (workspace.Exchanges == null) workspace.Exchanges = new List<CollaborationExchange>();
            foreach (CollaborationExchange item in workspace.Exchanges)
            {
                if (item.Status == "returning") item.Status = "completed";
                if (item.Status == "sending" || item.Status == "running") item.Status = "cancelled";
                connections.Set(item.Target, item.Status);
            }
            SetStatus(workspace.ProjectDirectory == null ? "本机连接" : "项目 · " + workspace.ProjectDirectory);
        }

        private CollaborationExchange LastExchange(AgentKind agent)
        {
            return workspace.Exchanges.LastOrDefault(item => item.Target == agent);
        }

        private void UpdateActivity(AgentKind agent)
        {
            PaneView pane = FindPane(agent);
            if (pane == null) return;
            CollaborationExchange recent = LastExchange(agent);
            string state = recent == null ? null : recent.Status;
            if (liveTurns.ContainsKey(agent) || state == "sending" || state == "running") pane.SetActivity("处理中", "TransferBrush");
            else if (pendingConnections.Contains(agent)) pane.SetActivity("检测中", "TransferBrush");
            else if (connectionResults.ContainsKey(agent))
            {
                ConnectionResult result = connectionResults[agent];
                pane.SetActivity(result.Success ? "已连接" : result.Missing ? "未安装" : "检测失败",
                    result.Success ? "CompleteBrush" : "AttentionBrush", result.Details);
            }
            else if (state == "completed") pane.SetActivity("已完成", "CompleteBrush");
            else if (state == "failed" || state == "cancelled") pane.SetActivity(state == "failed" ? "失败" : "已停止", "AttentionBrush");
            else if (state == "returning") pane.SetActivity("回信中", "TransferBrush");
            else pane.SetActivity(null, "MutedBrush");
        }

        private PaneView FindPane(AgentKind agent)
        {
            ChatPane data = workspace.Panes.FirstOrDefault(pane => pane.Agent == agent);
            PaneView view;
            return data != null && views.TryGetValue(data, out view) ? view : null;
        }

        private void UpdateConnectButton()
        {
            connectButton.IsEnabled = MainPane.Agent != AgentKind.None && connectionRun == null && liveTurns.Count == 0
                && pendingFollowups.Count == 0 && pendingAgentSettings.Count == 0;
            connectButton.Content = connectionRun != null ? "检测中…" : connectionResults.Count == 0 ? "一键连接" : "检测连接";
            connectButton.ToolTip = MainPane.Agent == AgentKind.None ? "先选择主 Agent" :
                connectionResults.Count == 0 ? "连接已打开的 Agent，不发送消息" : ConnectionSummary();
        }

        private string ConnectionSummary()
        {
            return string.Join("\n", connectionResults.OrderBy(item => item.Key).Select(item =>
                PaneView.AgentName(item.Key) + " · " + item.Value.Details));
        }

        private void ConnectAgents()
        {
            if (!connectButton.IsEnabled) return;
            connectionRun = Guid.NewGuid().ToString("N");
            connectionResults.Clear();
            foreach (ChatPane pane in visiblePanes) pendingConnections.Add(pane.Agent);
            foreach (AgentKind agent in pendingConnections) UpdateActivity(agent);
            addButton.IsEnabled = false;
            UpdateConnectButton();
            SetStatus("正在检测已打开的 Agent · 不发送消息");
            try { bridge.Send(new { type = "connect", id = connectionRun, workspace = BridgeSettings() }); }
            catch (Exception error)
            {
                if (!IsBridgeError(error)) throw;
                FailPendingConnectionChecks(error.Message);
            }
        }

        private void SaveConnectionResult(AgentKind agent, bool success, string details, bool missing = false)
        {
            if (!pendingConnections.Remove(agent)) return;
            connectionResults[agent] = new ConnectionResult { Success = success, Details = details, Missing = missing };
            UpdateActivity(agent);
            if (pendingConnections.Count != 0) return;

            connectionRun = null;
            addButton.IsEnabled = MainPane.Agent != AgentKind.None && visiblePanes.Count < PaneLimit;
            UpdateConnectButton();
            SetStatus(string.Format("连接检测完成 · {0}/{1} 已连接", connectionResults.Values.Count(result => result.Success), connectionResults.Count));
            status.ToolTip = ConnectionSummary();
        }

        private void FailPendingConnectionChecks(string error)
        {
            foreach (AgentKind agent in pendingConnections.ToArray()) SaveConnectionResult(agent, false, error);
        }

        private void ShowProjectMenu()
        {
            var anchor = Element<Button>("ProjectButton");
            var menu = CreateMenu(anchor);
            menu.Tag = SurfaceMenus.Heading("项目目录", workspace.ProjectDirectory ?? "默认使用独立工作目录");
            bool changeable = liveTurns.Count == 0 && pendingAgentSettings.Count == 0 && connectionRun == null && workspace.Panes.All(pane => string.IsNullOrEmpty(pane.SessionId));
            if (changeable)
            {
                AddMenuItem(menu, "选择项目目录…", ChooseProjectDirectory);
                if (workspace.ProjectDirectory != null) AddMenuItem(menu, "恢复默认目录", () => SetProjectDirectory(null));
            }
            else menu.Items.Add(new MenuItem { Header = "新建对话后可更改项目目录", IsEnabled = false });
            menu.IsOpen = true;
        }

        private void ChooseProjectDirectory()
        {
            using (var picker = new Forms.FolderBrowserDialog { Description = "选择这次对话使用的项目目录", ShowNewFolderButton = false })
            {
                if (workspace.ProjectDirectory != null) picker.SelectedPath = workspace.ProjectDirectory;
                if (picker.ShowDialog() == Forms.DialogResult.OK) SetProjectDirectory(picker.SelectedPath);
            }
        }

        private void SetProjectDirectory(string directory)
        {
            workspace.ProjectDirectory = directory;
            connectionResults.Clear();
            foreach (ChatPane pane in visiblePanes) UpdateActivity(pane.Agent);
            UpdateConnectButton();
            SetStatus(directory == null ? "本机连接" : "项目 · " + directory);
            RememberWorkspace();
        }

        private string DirectoryForTurn()
        {
            if (workspace.ProjectDirectory != null) return workspace.ProjectDirectory;
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CollaborationChatPreview", "workspaces", workspace.Id);
            Directory.CreateDirectory(directory);
            return directory;
        }

        private void SendLive(PaneView pane, string original)
        {
            if (connectionRun != null) { SetStatus("连接检测正在进行，请稍等。"); return; }
            AgentKind agent = pane.Data.Agent;
            AgentKind target = AgentKind.None;
            string text = original;
            if (pane.Data.IsMain && original[0] == '@')
            {
                int end;
                target = AgentCatalog.MentionedAgent(original, out end);
                if (target == agent) target = AgentKind.None;
                if (target != AgentKind.None)
                {
                    int payload = end + 1;
                    if (end < original.Length && original[end] == '\r' && payload < original.Length && original[payload] == '\n') payload++;
                    text = end >= original.Length ? "" : original.Substring(payload);
                    if (!visiblePanes.Any(item => item.Agent == target) || string.IsNullOrWhiteSpace(text))
                    { SetStatus("请打开目标聊天框，并在 Agent 名称后输入要发的内容。"); return; }
                }
            }
            AgentKind receiver = target == AgentKind.None ? agent : target;
            if (pendingAgentSettings.ContainsKey(receiver)) { SetStatus("正在切换这个 Agent 的设置，请稍等。"); return; }
            bool followingUp = liveTurns.ContainsKey(receiver) || pendingFollowups.Contains(receiver);
            if (followingUp && target != AgentKind.None) { SetStatus(PaneView.AgentName(receiver) + " 正在回复。可在它的聊天框接着发。"); return; }
            if (target != AgentKind.None && text.Length > 10000) { SetStatus("直接派发的内容最多 10000 个字符。"); return; }
            if (target == AgentKind.None && text.Length > 12000) { SetStatus("每次发送最多 12000 个字符，长文还在输入框里。"); return; }

            string id = Guid.NewGuid().ToString("N");
            PaneView receiverView = FindPane(receiver);
            if (!followingUp)
            {
                liveTurns[receiver] = id;
                connectionResults.Remove(receiver);
                UpdateConnectButton();
                receiverView.SetBusy(true);
                receiverView.SetActivity("连接中", "TransferBrush");
            }
            try
            {
                bridge.Send(new { type = "send", id, workspace = BridgeSettings(), agent = agent.ToString(),
                    target = target == AgentKind.None ? null : target.ToString(), text });
                pane.Composer.Clear();
                SetStatus((followingUp ? "正在提交插话给 " : "正在连接 ") + PaneView.AgentName(receiver));
            }
            catch (Exception error)
            {
                if (!IsBridgeError(error)) throw;
                if (!followingUp)
                {
                    liveTurns.Remove(receiver);
                    UpdateConnectButton();
                    receiverView.SetBusy(false);
                    receiverView.SetActivity("连接失败", "AttentionBrush");
                }
                SetStatus(error.Message);
            }
        }

        private object BridgeSettings()
        {
            return new
            {
                id = workspace.Id, main = MainPane.Agent.ToString(), directory = DirectoryForTurn(),
                agents = visiblePanes.Select(item => item.Agent.ToString()).ToArray(),
                rosterNotice = workspace.RosterNotice,
                confirmBinding = true,
                missingBindings = workspace.Panes.Where(pane => pane.Agent != AgentKind.None && string.IsNullOrEmpty(pane.SessionId) &&
                    pane.Messages.Any(message => !message.FromUser && (message.Speaker == PaneView.AgentName(pane.Agent) ||
                        message.Speaker == PaneView.AgentName(pane.Agent) + " · 进展")))
                    .Select(pane => pane.Agent.ToString()).ToArray(),
                preferences = workspace.Panes.Where(item => item.Agent != AgentKind.None && item.Preferences != null && item.Preferences.Count != 0)
                    .ToDictionary(item => item.Agent.ToString(), item => item.Preferences),
                sessionIds = workspace.Panes.Where(item => item.Agent != AgentKind.None && item.SessionId != null)
                    .ToDictionary(item => item.Agent.ToString(), item => item.SessionId)
            };
        }

        private static string RosterAgentName(AgentKind agent)
        {
            switch (agent)
            {
                case AgentKind.Codex: return "Codex（GPT）";
                case AgentKind.Dsh: return "DSH（DeepSeek）";
                case AgentKind.Antigravity: return "Antigravity（Gemini）";
                default: return PaneView.AgentName(agent);
            }
        }

        private void RecordRosterChange(AgentKind agent, bool added)
        {
            if (liveTurns.Count == 0 && !HasConversationMessages())
            {
                RememberWorkspace();
                return;
            }
            DateTimeOffset time = DateTimeOffset.Now;
            string[] names = workspace.Panes.Where(pane => !pane.IsMain && !pane.IsClosed)
                .Select(pane => RosterAgentName(pane.Agent)).ToArray();
            string update = "用户已" + (added ? "添加 " : "移除 ") + RosterAgentName(agent)
                + "。当前辅助 Agent：" + (names.Length == 0 ? "无" : string.Join("、", names)) + "。";
            workspace.RosterNotice = time.ToString("yyyy-MM-dd HH:mm:ss zzz") + "　" + update
                + "\n\n此消息由协作工具根据用户操作自动生成，无需向用户回复。请以最新名单为准。";
            Append(FindPane(MainPane.Agent), update, "协作工具", false);
        }

        private void SyncVisibleAgents()
        {
            if (!bridge.IsRunning || MainPane.Agent == AgentKind.None) return;
            try { BridgeNotify(new { type = "sync", workspace = BridgeSettings() }); }
            catch (Exception error) { if (!IsBridgeError(error)) throw; SetStatus(error.Message); }
        }

        private static bool IsBridgeError(Exception error)
        {
            return error is IOException || error is InvalidOperationException || error is System.ComponentModel.Win32Exception || error is UnauthorizedAccessException;
        }

        private void StopLive(PaneView pane)
        {
            if (!liveTurns.ContainsKey(pane.Data.Agent) && !pendingFollowups.Contains(pane.Data.Agent)) return;
            pane.SetActivity("正在停止", "AttentionBrush");
            BridgeNotify(new { type = "stop", workspace = workspace.Id, agent = pane.Data.Agent.ToString() });
        }

        private void BridgeNotify(object packet)
        {
            try { if (bridge.IsRunning) bridge.Send(packet); }
            catch (Exception error)
            {
                if (!IsBridgeError(error)) throw;
                BridgeOffline();
            }
        }

        private static string Read(IDictionary<string, object> packet, string field)
        {
            object value;
            return packet.TryGetValue(field, out value) ? value as string : null;
        }

        private static bool ReadFlag(IDictionary<string, object> packet, string field)
        {
            object value;
            return packet.TryGetValue(field, out value) && Equals(value, true);
        }

        private void BridgeEvent(IDictionary<string, object> packet)
        {
            string type = Read(packet, "type");
            if (type == "offline") { BridgeOffline(); return; }
            if (type == "installationResult") { ReceiveInstallations(packet); return; }
            AgentKind agent;
            if (Read(packet, "workspace") != workspace.Id || !Enum.TryParse(Read(packet, "agent"), out agent)) return;
            PaneView pane = FindPane(agent);
            if (pane == null) return;
            string run = Read(packet, "run");
            string text = Read(packet, "text");
            string source = Read(packet, "source");
            if (type == "agentSettings") { ReceiveAgentSettings(pane, packet); return; }
            if (type == "approval") { QueueAgentApproval(packet); return; }
            if (type == "connectionResult")
            {
                if (connectionRun != null && connectionRun == run) SaveConnectionResult(agent, ReadFlag(packet, "ok"), text, ReadFlag(packet, "notInstalled"));
                return;
            }
            if (type == "link") { ReceiveLink(agent, run, Read(packet, "phase"), text); return; }
            object remaining;
            if (packet.TryGetValue("remaining", out remaining) && remaining is int)
            {
                if ((int)remaining > 0) pendingFollowups.Add(agent);
                else pendingFollowups.Remove(agent);
                if (!liveTurns.ContainsKey(agent)) pane.SetBusy(pendingFollowups.Contains(agent));
                UpdateConnectButton();
            }
            if (type == "queued")
            {
                Append(pane, text, null, true);
                SetStatus(pane.Data.Agent == AgentKind.Codex ? "追加消息已提交，等待插话或自动续发。" : "追加消息已排队，本轮结束后自动发送。");
                return;
            }
            if (type == "steered")
            {
                if (!liveTurns.ContainsKey(agent) && !pendingFollowups.Contains(agent)) pane.SetActivity("已回复", "CompleteBrush");
                SetStatus("插话已进入 " + pane.Name + " 的当前轮。");
                return;
            }
            if (type == "queueCleared" || type == "submissionError")
            {
                string notice = type == "queueCleared" ? "已取消等待发送的追加消息。" : text;
                if (type == "queueCleared" && !liveTurns.ContainsKey(agent)) pane.SetActivity("已停止", "AttentionBrush");
                string draft = Read(packet, "draft");
                if (!string.IsNullOrEmpty(draft))
                {
                    if (string.IsNullOrWhiteSpace(pane.Composer.Text)) pane.Composer.Text = draft;
                    else Append(pane, draft, "你 · 未发送", true);
                }
                Append(pane, notice, "连接状态", false);
                SetStatus(notice);
                return;
            }
            if (type == "start")
            {
                liveTurns[agent] = run;
                connectionResults.Remove(agent);
                UpdateConnectButton();
                pane.SetBusy(true);
                pane.SetActivity("连接中", "TransferBrush");
                if (!ReadFlag(packet, "queued"))
                    Append(pane, text, source == null ? null : PaneView.AgentName(MainPane.Agent) + " → " + pane.Name, true);
                return;
            }
            string active;
            if (!liveTurns.TryGetValue(agent, out active) || active != run) return;
            if (type == "settingsPreview")
            {
                var fields = Objects(packet, "fields").ToList();
                pane.SetSettingsAvailability(fields.Count != 0);
                pane.SetSettingsCaption(SettingsSummary(fields));
            }
            if (type == "session")
            {
                string sessionId = Read(packet, "sessionId");
                bool ok = false;
                if (!string.IsNullOrEmpty(sessionId) && (string.IsNullOrEmpty(pane.Data.SessionId) || pane.Data.SessionId == sessionId))
                {
                    string previous = pane.Data.SessionId;
                    pane.Data.SessionId = sessionId;
                    ok = Persist();
                    if (!ok) pane.Data.SessionId = previous;
                }
                if (ReadFlag(packet, "confirm")) BridgeNotify(new { type = "sessionBound", workspace = workspace.Id,
                    agent = agent.ToString(), run, sessionId, ok });
            }
            if (type == "running")
            {
                pane.SetActivity("处理中", "TransferBrush");
                if (source != null)
                {
                    CollaborationExchange item = LastExchange(agent);
                    if (item != null && item.Id == run) item.Status = "running";
                    connections.Set(agent, "running");
                }
                SetStatus(PaneView.AgentName(agent) + " 正在回复");
            }
            if (type == "reply") pane.SetStream(text);
            if (type == "finish" || type == "error")
            {
                bool success = ReadFlag(packet, "ok");
                liveTurns.Remove(agent);
                UpdateConnectButton();
                pane.FinishStream(text, success, Read(packet, "progress"));
                pane.SetBusy(pendingFollowups.Contains(agent));
                if (success) pane.SetActivity(pendingFollowups.Contains(agent) ? "等待续发" : "已回复",
                    pendingFollowups.Contains(agent) ? "TransferBrush" : "CompleteBrush");
                else
                {
                    pane.SetActivity(ReadFlag(packet, "cancelled") ? "已停止" : "失败", "AttentionBrush");
                    Append(pane, text, "连接状态", false);
                }
                SetStatus(liveTurns.Count != 0 ? "Agent 正在处理请求" :
                    pendingFollowups.Count != 0 ? "追加消息等待续发" : success ? "回复已完成" : text);
                Persist();
            }
        }

        private void ReceiveLink(AgentKind target, string id, string phase, string text)
        {
            PaneView main = FindPane(MainPane.Agent);
            if (phase == "sending")
            {
                workspace.Exchanges.RemoveAll(old => old.Target == target);
                workspace.Exchanges.Add(new CollaborationExchange { Id = id, Target = target, Status = phase });
                Append(main, text, main.Name + " → " + PaneView.AgentName(target), true);
            }
            else
            {
                CollaborationExchange item = LastExchange(target);
                if (item == null || item.Id != id) return;
                item.Status = phase;
            }
            connections.Set(target, phase);
            UpdateActivity(target);
            Persist();
        }

        private void Append(PaneView pane, string text, string speaker, bool outgoing)
        {
            if (pane == null || string.IsNullOrEmpty(text)) return;
            pane.AddMessage(new ChatMessage { Text = text, Speaker = speaker, FromUser = outgoing, CreatedAt = DateTime.UtcNow });
            RememberMessage(pane);
        }

        private void BridgeOffline()
        {
            if (inventory != null && installationRun != null)
            {
                installationRun = null;
                inventory.Complete(Enumerable.Empty<IDictionary<string, object>>(), "本机连接中断，请重新检测。");
            }
            if (pendingAgentSettings.Count != 0)
            {
                foreach (AgentKind agent in pendingAgentSettings.Keys)
                {
                    var pane = FindPane(agent);
                    var menu = pane == null ? null : pane.SettingsButton.ContextMenu;
                    if (menu != null) menu.IsOpen = false;
                }
                pendingAgentSettings.Clear();
                UpdateConnectButton();
                SetStatus("本机连接中断，请重新打开 Agent 设置。");
            }
            approvalRequests.Clear();
            if (connectionRun != null) FailPendingConnectionChecks("本机连接进程已中断。可以重新检测。");
            if (liveTurns.Count == 0 && pendingFollowups.Count == 0) return;
            foreach (AgentKind agent in liveTurns.Keys.Concat(pendingFollowups).Distinct().ToArray())
            {
                PaneView pane = FindPane(agent);
                if (pane != null)
                {
                    pane.FinishStream(null, false);
                    pane.SetBusy(false);
                    pane.SetActivity("连接中断", "AttentionBrush");
                    Append(pane, "本机连接已中断。下一次发送会重新连接。", "连接状态", false);
                }
                CollaborationExchange item = LastExchange(agent);
                if (item != null && (item.Status == "running" || item.Status == "sending"))
                { item.Status = "failed"; connections.Set(agent, "failed"); }
            }
            liveTurns.Clear();
            pendingFollowups.Clear();
            UpdateConnectButton();
            SetStatus("本机 Agent 连接已中断");
            Persist();
        }
    }
}
