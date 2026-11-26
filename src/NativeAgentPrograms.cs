using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CollaborationChatPreview
{
    internal enum LaunchVerification { Verified, Unverified, Invalid }

    internal sealed class LaunchCheck
    {
        public readonly LaunchVerification Verification;
        public readonly string Detail;

        public LaunchCheck(LaunchVerification verification, string detail)
        {
            Verification = verification;
            Detail = detail;
        }
    }

    internal static class NativeAgentPrograms
    {
        private static readonly string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        private static readonly string powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        private static readonly string wsl = Path.Combine(system, "wsl.exe");
        private static readonly Lazy<CodexInstallation> codex = new Lazy<CodexInstallation>(FindCodex);

        private sealed class CodexInstallation
        {
            public string AppId;
            public string File;
        }

        public static AgentLaunchSettings Default(AgentKind agent)
        {
            var value = new AgentLaunchSettings { Agent = agent };
            switch (agent)
            {
                case AgentKind.Codex:
                    value.Path = codex.Value == null ? "" : codex.Value.File;
                    break;
                case AgentKind.Dsh:
                    value.Path = Path.Combine(local, "Programs", "DeepSeek Harness", "DeepSeek Harness.exe");
                    break;
                case AgentKind.Antigravity:
                    value.Path = Path.Combine(local, "Programs", "antigravity", "Antigravity.exe");
                    break;
                case AgentKind.Hermes:
                    value.Distribution = "";
                    value.Path = "hermes";
                    break;
                default:
                    value.Path = FindTerminal(AgentCatalog.TerminalName(agent)) ?? "";
                    break;
            }
            return value;
        }

        private static string FindTerminal(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            var entries = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
                .Select(value => value.Trim().Trim('"'))
                .Concat(new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm") });
            foreach (string root in entries)
            {
                if (!Regex.IsMatch(root, @"^[A-Za-z]:[\\/]")) continue;
                foreach (string suffix in new[] { ".exe", ".cmd" })
                {
                    string file;
                    try { file = Path.Combine(root, command + suffix); }
                    catch (ArgumentException) { continue; }
                    if (File.Exists(file)) return file;
                }
            }
            return null;
        }

        private static CodexInstallation FindCodex()
        {
            string query = "$p=Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | Select-Object -First 1; " +
                "if($p){$a=(Get-AppxPackageManifest $p).Package.Applications.Application | Where-Object Id -eq 'App' | Select-Object -First 1; " +
                "if($a){[Console]::WriteLine($p.PackageFamilyName+'!'+$a.Id);[Console]::WriteLine((Join-Path $p.InstallLocation $a.Executable))}}";
            if (!File.Exists(powershell)) return null;
            string output;
            try
            {
                if (Probe(powershell, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Encode(query), out output) != 0) return null;
            }
            catch (System.ComponentModel.Win32Exception) { return null; }
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != 2 || !lines[0].StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) || !File.Exists(lines[1])) return null;
            return new CodexInstallation { AppId = lines[0], File = lines[1] };
        }

        public static LaunchCheck Check(AgentLaunchSettings entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Path)) return Invalid("请填写启动路径。");
            try
            {
                if (entry.Agent == AgentKind.Hermes) return CheckHermes(entry);
                if (!Regex.IsMatch(entry.Path, @"^[A-Za-z]:[\\/]") || entry.Path.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    return Invalid("请选择本机磁盘上的完整文件路径。");
                string file = Path.GetFullPath(entry.Path);
                if (!File.Exists(file)) return Invalid("找不到这个文件。");
                string extension = Path.GetExtension(file);
                string terminal = AgentCatalog.TerminalName(entry.Agent);
                if (terminal != null && string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(file, FindTerminal(terminal), StringComparison.OrdinalIgnoreCase))
                    return new LaunchCheck(LaunchVerification.Unverified, "这是本机安装提供的终端脚本，打开前请核对路径。");
                if (entry.Agent == AgentKind.Antigravity && string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)) return CheckBatch(file);
                if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)) return Invalid("请选择这个 Agent 的 .exe 启动文件。");
                return CheckWindowsProgram(entry.Agent, file);
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException || error is ArgumentException ||
                    error is System.ComponentModel.Win32Exception || error is NotSupportedException)) throw;
                return Invalid("无法检测这个路径。请检查文件是否可读。");
            }
        }

        private static LaunchCheck CheckWindowsProgram(AgentKind agent, string file)
        {
            string baseName = Path.GetFileNameWithoutExtension(file);
            var version = FileVersionInfo.GetVersionInfo(file);
            string product = (version.ProductName ?? "").Trim();
            string description = (version.FileDescription ?? "").Trim();
            string metadata = product + " " + description;
            if (Regex.IsMatch(baseName, @"^(?:uninstall|setup|installer|squirrel[.]exe)|webview", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(metadata, @"\buninstaller\b", RegexOptions.IgnoreCase)) return Invalid("这是安装或卸载程序，请选择主程序。");

            AgentKind detected = DetectProduct(product, description);
            if (detected != AgentKind.None && detected != agent)
                return Invalid("这个路径属于 " + PaneView.AgentName(detected) + "。请选择 " + PaneView.AgentName(agent) + " 的路径。");
            if (detected == agent) return new LaunchCheck(LaunchVerification.Verified, "已识别为 " + PaneView.AgentName(agent) + " 主程序。");
            if (Regex.IsMatch(baseName, @"^(chrome|msedge|firefox|brave|vivaldi|opera|iexplore|chromium|browser)$", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(metadata, @"Google Chrome|Microsoft Edge|Mozilla Firefox|Brave Browser|Vivaldi|Opera Browser|\bChromium\b", RegexOptions.IgnoreCase))
                return Invalid("这个路径指向浏览器，请选择对应的 Agent。");
            return new LaunchCheck(LaunchVerification.Unverified, "文件存在，产品身份无法确认。可以保存为自定义路径，打开前会再次确认。");
        }

        private static AgentKind DetectProduct(string product, string description)
        {
            foreach (string value in new[] { product, description })
            {
                if (string.Equals(value, "Codex", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "OpenAI Codex", StringComparison.OrdinalIgnoreCase)) return AgentKind.Codex;
                if (string.Equals(value, "DeepSeek Harness", StringComparison.OrdinalIgnoreCase)) return AgentKind.Dsh;
                if (string.Equals(value, "Antigravity", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "Google Antigravity", StringComparison.OrdinalIgnoreCase)) return AgentKind.Antigravity;
                if (string.Equals(value, "Hermes Agent", StringComparison.OrdinalIgnoreCase)) return AgentKind.Hermes;
                if (string.Equals(value, "Claude Code", StringComparison.OrdinalIgnoreCase)) return AgentKind.ClaudeCode;
                if (string.Equals(value, "Gemini CLI", StringComparison.OrdinalIgnoreCase)) return AgentKind.GeminiCli;
                if (string.Equals(value, "OpenCode", StringComparison.OrdinalIgnoreCase)) return AgentKind.OpenCode;
                if (string.Equals(value, "Qwen Code", StringComparison.OrdinalIgnoreCase)) return AgentKind.QwenCode;
            }
            return AgentKind.None;
        }

        private static LaunchCheck CheckBatch(string file)
        {
            if (new FileInfo(file).Length > 65536) return new LaunchCheck(LaunchVerification.Unverified, "批处理较长，打开前会要求确认内容。");
            string commands = string.Join("\n", File.ReadLines(file).Where(line => !Regex.IsMatch(line, @"^\s*(?:::|@?rem(?:\s|$))", RegexOptions.IgnoreCase)));
            if (Regex.IsMatch(commands, @"\b(?:chrome|msedge|firefox|brave|opera|vivaldi|codex|chatgpt|DeepSeek Harness)[.]exe\b", RegexOptions.IgnoreCase))
                return Invalid("这个批处理包含浏览器或其他 Agent 的启动目标。");
            return new LaunchCheck(LaunchVerification.Unverified, "这是自定义批处理，运行前会显示路径并确认。");
        }

        private static LaunchCheck CheckHermes(AgentLaunchSettings entry)
        {
            if (!File.Exists(wsl)) return Invalid("找不到 WSL。");
            if (entry.Path == "hermes" && string.IsNullOrWhiteSpace(entry.Distribution))
            {
                string installedDistros;
                if (Probe(wsl, "--list --quiet", out installedDistros) != 0) return Invalid("未检测到可用的 WSL 发行版。");
                foreach (string distro in installedDistros.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item.Trim('\ufeff', ' ')).Where(item => item.Length != 0 && !item.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)))
                {
                    string executable;
                    string prefix = "-d " + Quote(distro) + " --exec ";
                    if (Probe(wsl, prefix + "/usr/bin/which hermes", out executable) != 0 || !ValidLinuxPath(executable))
                    {
                        string home;
                        if (Probe(wsl, prefix + "/usr/bin/printenv HOME", out home) != 0 || !ValidLinuxPath(home)) continue;
                        executable = home.TrimEnd('/') + "/.local/bin/hermes";
                        string ignored;
                        if (Probe(wsl, prefix + "/usr/bin/test -x " + Quote(executable), out ignored) != 0) continue;
                    }
                    entry.Distribution = distro;
                    entry.Path = executable;
                    return new LaunchCheck(LaunchVerification.Verified, "已在 WSL 中检测到 Hermes。");
                }
                return Invalid("WSL 中未检测到 Hermes。");
            }
            if (string.IsNullOrWhiteSpace(entry.Distribution) || entry.Distribution[0] == '-' || entry.Distribution.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                return Invalid("请填写有效的 WSL 发行版名称。");
            if (!entry.Path.StartsWith("/", StringComparison.Ordinal) || entry.Path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                return Invalid("请填写 WSL 中的完整 Linux 路径。");
            string name = entry.Path.Split('/').Last();
            if (name == "bash" || name == "sh" || name == "node" || name == "python" || name == "python3" || name == "codex" || name == "agy" || name == "dsh")
                return Invalid("该路径是解释器或其他 Agent，请填写 Hermes 启动文件。");
            string output;
            string file = Quote(entry.Path);
            string args = "-d " + Quote(entry.Distribution) + " --exec /usr/bin/test -f " + file + " -a -x " + file;
            if (Probe(wsl, args, out output) != 0) return Invalid("找不到该 WSL 发行版，或其中的启动文件不可执行。");
            return new LaunchCheck(LaunchVerification.Unverified, "WSL 文件可执行。自定义启动入口将在打开前确认。");
        }

        private static bool ValidLinuxPath(string value)
        {
            return value.StartsWith("/", StringComparison.Ordinal) && value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;
        }

        public static ProcessStartInfo CreateStartInfo(AgentLaunchSettings entry)
        {
            if (entry.Agent == AgentKind.Hermes)
            {
                string script = "& " + ShellWord(wsl) + " -d " + ShellWord(entry.Distribution) + " --exec " + ShellWord(entry.Path);
                return new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NoExit -EncodedCommand " + Encode(script)) { UseShellExecute = true };
            }
            if (AgentCatalog.TerminalName(entry.Agent) != null)
            {
                string script = "& " + ShellWord(entry.Path);
                return new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NoExit -EncodedCommand " + Encode(script)) { UseShellExecute = true };
            }
            if (entry.Agent == AgentKind.Codex && codex.Value != null &&
                string.Equals(Path.GetFullPath(entry.Path), Path.GetFullPath(codex.Value.File), StringComparison.OrdinalIgnoreCase))
            {
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                return new ProcessStartInfo(explorer, Quote("shell:AppsFolder\\" + codex.Value.AppId)) { UseShellExecute = true };
            }
            return new ProcessStartInfo(entry.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(entry.Path) };
        }

        private static string ShellWord(string text) { return "'" + text.Replace("'", "''") + "'"; }
        private static string Encode(string script) { return Convert.ToBase64String(Encoding.Unicode.GetBytes(script)); }

        private static string Quote(string text)
        {
            if (text.Length != 0 && !text.Any(character => char.IsWhiteSpace(character) || character == '"')) return text;
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in text)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static int Probe(string executable, string args, out string output)
        {
            var start = new ProcessStartInfo(executable, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (Process process = Process.Start(start))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(9500))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    output = "";
                    return -1;
                }
                output = stdout.GetAwaiter().GetResult().Replace("\0", "").Trim();
                stderr.GetAwaiter().GetResult();
                return process.ExitCode;
            }
        }

        private static LaunchCheck Invalid(string text) { return new LaunchCheck(LaunchVerification.Invalid, text); }
    }
}
