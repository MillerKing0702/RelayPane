using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace CollaborationChatPreview
{
    internal static class ChatClipboard
    {
        internal const string OriginalFormat = "CollaborationChat.MarkdownSource";

        public static void CopyMessage(string source, TextBoxBase reader)
        {
            if (string.IsNullOrEmpty(source)) return;
            try { Clipboard.SetDataObject(MessageData(source, reader), true); }
            catch (ExternalException) { }
        }

        internal static DataObject MessageData(string source, TextBoxBase reader)
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, source);
            data.SetData(OriginalFormat, source, false);
            var rich = reader as RichTextBox;
            if (rich != null)
            {
                var range = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd);
                using (var buffer = new MemoryStream())
                {
                    try
                    {
                        range.Save(buffer, DataFormats.Rtf);
                        data.SetData(DataFormats.Rtf, Encoding.ASCII.GetString(buffer.ToArray()));
                    }
                    catch (IOException) { }
                }
            }
            return data;
        }

        public static bool WholeMessageSelected(TextBoxBase reader)
        {
            var plain = reader as TextBox;
            if (plain != null) return plain.SelectionLength > 0 && plain.SelectionLength == plain.Text.Length;
            var rich = reader as RichTextBox;
            if (rich == null || rich.Selection.IsEmpty) return false;
            var whole = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd);
            return whole.Text.TrimEnd('\r', '\n') == rich.Selection.Text.TrimEnd('\r', '\n');
        }

        public static void PasteOriginal(object sender, DataObjectPastingEventArgs args)
        {
            if (!args.DataObject.GetDataPresent(OriginalFormat, false)) return;
            var composer = sender as TextBox;
            string original = args.DataObject.GetData(OriginalFormat, false) as string;
            if (composer == null || original == null) return;
            args.CancelCommand();
            composer.SelectedText = original;
            composer.CaretIndex = composer.SelectionStart + composer.SelectionLength;
            composer.SelectionLength = 0;
        }
    }
}
