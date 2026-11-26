using System;
using System.Collections.Generic;
using System.Linq;

namespace CollaborationChatPreview
{
    internal static class AgentCatalog
    {
        internal sealed class Entry
        {
            public readonly AgentKind Kind;
            public readonly string Name;
            public readonly string FullName;
            public readonly string Badge;
            public readonly string InstallUrl;
            public readonly string[] Aliases;

            public Entry(AgentKind kind, string name, string fullName, string badge, string installUrl, params string[] aliases)
            {
                Kind = kind;
                Name = name;
                FullName = fullName;
                Badge = badge;
                InstallUrl = installUrl;
                Aliases = aliases;
            }
        }

        public static readonly Entry[] All = {
            new Entry(AgentKind.Codex, "Codex", "OpenAI Codex", "C", "https://developers.openai.com/codex/quickstart/", "GPT"),
            new Entry(AgentKind.Dsh, "DSH", "DeepSeek Harness", "D", "https://github.com/deepseek-ai/deepseek-harness", "DeepSeek", "DeepSeek Harness"),
            new Entry(AgentKind.Antigravity, "Antigravity", "Google Antigravity", "A", "https://antigravity.google/docs/ide/extensions/acp/", "Gemini"),
            new Entry(AgentKind.Hermes, "Hermes", "Hermes Agent", "H", "https://hermes-agent.nousresearch.com/docs/getting-started/quickstart"),
            new Entry(AgentKind.ClaudeCode, "Claude Code", "Claude Code · Claude Agent ACP", "Cl", "https://github.com/agentclientprotocol/claude-agent-acp", "Claude"),
            new Entry(AgentKind.GeminiCli, "Gemini CLI", "Google Gemini CLI", "G", "https://github.com/google-gemini/gemini-cli", "GeminiCLI"),
            new Entry(AgentKind.OpenCode, "OpenCode", "OpenCode", "O", "https://opencode.ai/docs/"),
            new Entry(AgentKind.QwenCode, "Qwen Code", "Qwen Code", "Q", "https://github.com/QwenLM/qwen-code", "Qwen")
        };

        private static readonly Dictionary<AgentKind, Entry> entries = All.ToDictionary(entry => entry.Kind);
        public static string TerminalName(AgentKind agent)
        {
            switch (agent)
            {
                case AgentKind.ClaudeCode: return "claude";
                case AgentKind.GeminiCli: return "gemini";
                case AgentKind.OpenCode: return "opencode";
                case AgentKind.QwenCode: return "qwen";
                default: return null;
            }
        }

        public static Entry Get(AgentKind agent) { return entries[agent]; }

        public static AgentKind MentionedAgent(string text, out int end)
        {
            AgentKind match = AgentKind.None;
            end = 0;
            foreach (Entry entry in All)
                foreach (string name in new[] { entry.Name }.Concat(entry.Aliases))
                {
                    int length = name.Length + 1;
                    if (length <= end || text.Length < length || !text.StartsWith("@" + name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (text.Length > length && !char.IsWhiteSpace(text[length])) continue;
                    end = length;
                    match = entry.Kind;
                }
            return match;
        }
    }
}
