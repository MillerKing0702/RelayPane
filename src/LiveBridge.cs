using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace CollaborationChatPreview
{
    internal sealed class LiveBridge : IDisposable
    {
        private readonly Dispatcher dispatcher;
        private readonly Action<IDictionary<string, object>> receive;
        private readonly string directory;
        private Process process;
        private StreamWriter input;
        public bool IsRunning { get { return process != null && !process.HasExited; } }

        public LiveBridge(Dispatcher dispatcher, Action<IDictionary<string, object>> receive)
        {
            this.dispatcher = dispatcher;
            this.receive = receive;
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CollaborationChatPreview", "bridge");
        }

        public void Send(object message)
        {
            Start();
            input.WriteLine(new JavaScriptSerializer().Serialize(message));
        }

        private void Start()
        {
            if (process != null && !process.HasExited) return;
            DisposeProcess();
            string node = FindNode();
            if (node == null) throw new InvalidOperationException("找不到本机 Node.js。Agent 连接需要 Node.js 20 或更新版本。");
            Directory.CreateDirectory(directory);
            string script = Path.Combine(directory, "agent-bridge.cjs");
            string endpoint = Path.Combine(directory, "endpoint-" + Guid.NewGuid().ToString("N") + ".json");
            using (Stream resource = typeof(LiveBridge).Assembly.GetManifestResourceStream("AgentBridge.cjs"))
            using (var content = new MemoryStream())
            {
                resource.CopyTo(content);
                byte[] bytes = content.ToArray();
                if (!File.Exists(script) || !File.ReadAllBytes(script).SequenceEqual(bytes)) File.WriteAllBytes(script, bytes);
            }
            var child = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = node, Arguments = "\"" + script + "\" \"" + endpoint + "\"", WorkingDirectory = directory,
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };
            child.OutputDataReceived += (sender, args) =>
            {
                if (args.Data == null) return;
                IDictionary<string, object> payload;
                try { payload = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(args.Data); }
                catch (ArgumentException) { return; }
                catch (InvalidOperationException) { return; }
                Dispatch(child, payload);
            };
            child.ErrorDataReceived += (sender, args) => { };
            child.Exited += (sender, args) => Dispatch(child, new Dictionary<string, object> { { "type", "offline" } });
            process = child;
            if (!child.Start()) throw new InvalidOperationException("无法启动 Agent 连接。");
            input = new StreamWriter(child.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
        }

        private void Dispatch(Process owner, IDictionary<string, object> payload)
        {
            if (!dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(new Action(() => { if (process == owner && payload != null) receive(payload); }));
        }

        private static string FindNode()
        {
            string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
            if (File.Exists(standard)) return standard;
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;
                    string candidate = Path.Combine(entry.Trim().Trim('"'), "node.exe");
                    if (Path.IsPathRooted(candidate) && File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        public void Dispose()
        {
            DisposeProcess();
        }

        private void DisposeProcess()
        {
            Process child = process;
            process = null;
            if (input != null)
            {
                try { input.Dispose(); } catch (IOException) { }
                input = null;
            }
            if (child != null) child.Dispose();
        }
    }
}
