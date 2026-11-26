using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace CollaborationChatPreview
{
    internal enum ThemeChoice { System, Light, Dark }
    internal enum AgentKind { None, Codex, Dsh, Antigravity, Hermes, ClaudeCode, GeminiCli, OpenCode, QwenCode }
    internal enum PanePlacement { Auto, Left, Right, Top, Bottom }

    [DataContract]
    internal sealed class ChatState
    {
        [DataMember] public ThemeChoice Theme;
        [DataMember] public List<Conversation> Conversations = new List<Conversation>();
        [DataMember(EmitDefaultValue = false)] public string ActiveConversationId;
        [DataMember(EmitDefaultValue = false)] public bool ImportedPreviewHistory;
        [DataMember(EmitDefaultValue = false)] public List<AgentLaunchSettings> Launchers;
    }

    [DataContract]
    internal sealed class Conversation
    {
        [DataMember] public string Id;
        [DataMember] public string Title;
        [DataMember] public DateTime UpdatedAt;
        [DataMember(EmitDefaultValue = false)] public List<ChatMessage> Messages;
        [DataMember(EmitDefaultValue = false)] public List<ChatPane> Panes;
        [DataMember(EmitDefaultValue = false)] public string ProjectDirectory;
        [DataMember(EmitDefaultValue = false)] public string RosterNotice;
        [DataMember(EmitDefaultValue = false)] public DateTime? ArchivedAt;
        [DataMember(EmitDefaultValue = false)] public List<CollaborationExchange> Exchanges;
    }

    [DataContract]
    internal sealed class AgentLaunchSettings
    {
        [DataMember] public AgentKind Agent;
        [DataMember] public string Path;
        [DataMember(EmitDefaultValue = false)] public string Distribution;
    }

    [DataContract]
    internal sealed class ChatPane
    {
        [DataMember] public AgentKind Agent;
        [DataMember] public bool IsMain;
        [DataMember] public bool IsClosed;
        [DataMember(EmitDefaultValue = false)] public PanePlacement Placement;
        [DataMember(EmitDefaultValue = false)] public string SessionId;
        [DataMember(EmitDefaultValue = false)] public Dictionary<string, string> Preferences;
        [DataMember(EmitDefaultValue = false)] public string SettingsCaption;
        [DataMember] public List<ChatMessage> Messages = new List<ChatMessage>();
    }

    [DataContract]
    internal sealed class ChatMessage
    {
        [DataMember] public bool FromUser;
        [DataMember] public string Text;
        [DataMember] public DateTime CreatedAt;
        [DataMember(EmitDefaultValue = false)] public string Speaker;
        [DataMember(EmitDefaultValue = false)] public bool IsFinalReply;
        [DataMember(EmitDefaultValue = false)] public bool Acknowledged;
    }

    [DataContract]
    internal sealed class CollaborationExchange
    {
        [DataMember] public string Id;
        [DataMember] public AgentKind Target;
        [DataMember] public string Status;
    }

    internal sealed class StateStore
    {
        private readonly string directory;
        private readonly string statePath;
        private readonly string previousPath;

        public StateStore(string directory) : this(directory, null) { }

        public StateStore(string directory, string previousDirectory)
        {
            this.directory = directory;
            statePath = Path.Combine(directory, "history.json");
            if (previousDirectory != null) previousPath = Path.Combine(previousDirectory, "history.json");
        }

        public ChatState Load()
        {
            ChatState original = Read(statePath) ?? Read(statePath + ".bak");
            ChatState current = original ?? new ChatState();
            if (previousPath != null && !current.ImportedPreviewHistory)
            {
                ChatState previous = Read(previousPath) ?? Read(previousPath + ".bak");
                if (previous != null)
                {
                    foreach (Conversation item in previous.Conversations)
                    {
                        int index = current.Conversations.FindIndex(saved => saved.Id == item.Id);
                        if (index < 0) current.Conversations.Add(item);
                        else if (item.UpdatedAt >= current.Conversations[index].UpdatedAt) current.Conversations[index] = item;
                    }
                    if (original == null) current.Theme = previous.Theme;
                    current.ImportedPreviewHistory = true;
                }
            }
            foreach (Conversation conversation in current.Conversations) MergeDuplicatePanes(conversation);
            return current;
        }

        private static void MergeDuplicatePanes(Conversation conversation)
        {
            if (conversation.Panes == null) return;
            foreach (ChatPane pane in conversation.Panes)
                if (pane.Messages == null) pane.Messages = new List<ChatMessage>();
            foreach (var group in conversation.Panes.GroupBy(pane => pane.Agent).ToList())
            {
                if (group.Count() == 1) continue;
                ChatPane retained = group.FirstOrDefault(pane => pane.IsMain) ??
                    group.FirstOrDefault(pane => !pane.IsClosed) ?? group.First();
                var bindings = group.Select(pane => pane.SessionId).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
                if (string.IsNullOrEmpty(retained.SessionId) && bindings.Count == 1) retained.SessionId = bindings[0];
                retained.Messages = group.SelectMany(pane => pane.Messages).OrderBy(message => message.CreatedAt).ToList();
                retained.IsClosed = !retained.IsMain && group.All(pane => pane.IsClosed);
                foreach (ChatPane duplicate in group.Where(pane => pane != retained)) conversation.Panes.Remove(duplicate);
            }
        }

        private static ChatState Read(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    var state = (ChatState)new DataContractJsonSerializer(typeof(ChatState)).ReadObject(stream);
                    return state != null && state.Conversations != null ? state : null;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (SerializationException) { return null; }
        }

        public void Save(ChatState state)
        {
            Directory.CreateDirectory(directory);
            string temporaryPath = statePath + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(ChatState)).WriteObject(stream, state);
                stream.Flush(true);
            }

            if (File.Exists(statePath))
                File.Replace(temporaryPath, statePath, statePath + ".bak", true);
            else
                File.Move(temporaryPath, statePath);
        }
    }
}
