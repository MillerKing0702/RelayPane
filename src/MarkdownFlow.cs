using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Md = Markdig.Extensions.Tables;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;

namespace CollaborationChatPreview
{
    internal sealed class MarkdownFlow
    {
        private static readonly MarkdownPipeline parser = new MarkdownPipelineBuilder()
            .DisableHtml().UsePipeTables().UseAutoLinks().UseEmphasisExtras().Build();
        private static readonly FontFamily mono = new FontFamily("Cascadia Code, Consolas, Microsoft YaHei UI");
        private readonly bool compact;
        private readonly bool narrow;

        public MarkdownFlow(bool compact, bool narrow)
        {
            this.compact = compact;
            this.narrow = narrow;
        }

        public FlowDocument Render(string source)
        {
            MarkdownDocument parsed = Markdown.Parse(source, parser);
            if (parsed.Count == 0 || IsPlainLine(parsed, source)) return null;
            var flow = new FlowDocument
            {
                PagePadding = new Thickness(0),
                ColumnWidth = double.PositiveInfinity,
                FontSize = compact ? 12 : 14,
                IsHyphenationEnabled = false
            };
            flow.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            AddBlocks(parsed, flow.Blocks);
            if (flow.Blocks.Count == 0) return null;
            flow.Blocks.LastBlock.Margin = new Thickness(flow.Blocks.LastBlock.Margin.Left, flow.Blocks.LastBlock.Margin.Top, flow.Blocks.LastBlock.Margin.Right, 0);
            return flow;
        }

        private static bool IsPlainLine(MarkdownDocument parsed, string source)
        {
            var paragraph = parsed.Count == 1 ? parsed[0] as ParagraphBlock : null;
            var literal = paragraph == null || paragraph.Inline == null ? null : paragraph.Inline.FirstChild as LiteralInline;
            return literal != null && literal.NextSibling == null && literal.Content.ToString() == source;
        }

        private void AddBlocks(ContainerBlock source, BlockCollection target)
        {
            foreach (Markdig.Syntax.Block item in source)
            {
                WpfBlock rendered = RenderBlock(item);
                if (rendered != null) target.Add(rendered);
            }
        }

        private WpfBlock RenderBlock(Markdig.Syntax.Block source)
        {
            var heading = source as HeadingBlock;
            if (heading != null)
            {
                Paragraph title = MakeParagraph(heading.Inline);
                title.FontSize = (compact ? 13 : 15) + Math.Max(0, 3 - heading.Level) * (compact ? 1 : 2);
                title.FontWeight = FontWeights.SemiBold;
                title.Margin = new Thickness(0, 2, 0, compact ? 4 : 7);
                return title;
            }

            var body = source as ParagraphBlock;
            if (body != null) return MakeParagraph(body.Inline);
            var code = source as CodeBlock;
            if (code != null) return MakeCodeBlock(code.Lines.ToString());
            var list = source as ListBlock;
            if (list != null) return MakeList(list);
            var table = source as Md.Table;
            if (table != null) return narrow ? RenderNarrowTable(table) : RenderTable(table);
            if (source is ThematicBreakBlock)
            {
                var line = new Paragraph { FontSize = 1, Margin = new Thickness(0, 4, 0, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
                line.SetResourceReference(WpfBlock.BorderBrushProperty, "MarkdownRuleBrush");
                return line;
            }
            var container = source as ContainerBlock;
            if (container != null)
            {
                var section = new Section();
                AddBlocks(container, section.Blocks);
                if (container is QuoteBlock)
                {
                    section.BorderThickness = new Thickness(2, 0, 0, 0);
                    section.SetResourceReference(WpfBlock.BorderBrushProperty, "MarkdownRuleBrush");
                    section.Padding = new Thickness(8, 1, 0, 1);
                    section.Margin = new Thickness(0, 0, 0, 7);
                }
                return section;
            }
            var leaf = source as LeafBlock;
            return leaf == null ? null : new Paragraph(new Run(leaf.Lines.ToString()));
        }

        private Paragraph MakeParagraph(ContainerInline source)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, compact ? 5 : 9),
                LineHeight = compact ? 17 : 21
            };
            AddInlines(source, paragraph.Inlines);
            return paragraph;
        }

        private Paragraph MakeCodeBlock(string source)
        {
            var paragraph = new Paragraph
            {
                FontFamily = mono, FontSize = compact ? 11 : 13,
                LineHeight = compact ? 16 : 19,
                Padding = compact ? new Thickness(6, 4, 6, 4) : new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 0, compact ? 5 : 9)
            };
            paragraph.SetResourceReference(TextElement.BackgroundProperty, "MarkdownCodeBrush");
            string[] lines = source.Replace("\r\n", "\n").TrimEnd('\r', '\n').Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                if (index != 0) paragraph.Inlines.Add(new LineBreak());
                paragraph.Inlines.Add(new Run(lines[index]));
            }
            return paragraph;
        }

        private System.Windows.Documents.List MakeList(ListBlock source)
        {
            int start;
            var list = new System.Windows.Documents.List
            {
                MarkerStyle = source.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                StartIndex = int.TryParse(source.OrderedStart, out start) ? Math.Max(1, start) : 1,
                MarkerOffset = compact ? 4 : 6,
                Padding = new Thickness(compact ? 17 : 23, 0, 0, 0),
                Margin = new Thickness(0, 0, 0, compact ? 5 : 8)
            };
            foreach (ListItemBlock entry in source.OfType<ListItemBlock>())
            {
                var item = new ListItem();
                AddBlocks(entry, item.Blocks);
                if (item.Blocks.Count > 0) item.Blocks.LastBlock.Margin = new Thickness(0, 0, 0, compact ? 2 : 4);
                list.ListItems.Add(item);
            }
            return list;
        }

        private WpfBlock RenderTable(Md.Table source)
        {
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 1, 0, 9) };
            int width = Math.Max(source.ColumnDefinitions.Count, source.OfType<Md.TableRow>().Select(row => row.Count).DefaultIfEmpty(0).Max());
            for (int index = 0; index < width; index++) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            foreach (Md.TableRow entry in source.OfType<Md.TableRow>())
            {
                var row = new TableRow();
                foreach (Md.TableCell node in entry.OfType<Md.TableCell>())
                {
                    var cell = new TableCell
                    {
                        Padding = new Thickness(7, 4, 7, 4),
                        BorderThickness = new Thickness(1, 0, 0, 1),
                        FontWeight = entry.IsHeader ? FontWeights.SemiBold : FontWeights.Normal
                    };
                    cell.SetResourceReference(TableCell.BorderBrushProperty, "MarkdownRuleBrush");
                    if (entry.IsHeader) cell.SetResourceReference(TextElement.BackgroundProperty, "MarkdownHeaderBrush");
                    AddBlocks(node, cell.Blocks);
                    if (cell.Blocks.Count > 0) cell.Blocks.LastBlock.Margin = new Thickness(0);
                    row.Cells.Add(cell);
                }
                if (row.Cells.Count > 0) row.Cells[row.Cells.Count - 1].BorderThickness = new Thickness(1, 0, 1, 1);
                group.Rows.Add(row);
            }
            return table;
        }

        private WpfBlock RenderNarrowTable(Md.Table source)
        {
            var section = new Section { Margin = new Thickness(0, 1, 0, 6) };
            if (!source.OfType<Md.TableRow>().Any(row => !row.IsHeader)) return RenderTable(source);
            var labels = new List<string>();
            Md.TableRow header = source.OfType<Md.TableRow>().FirstOrDefault(row => row.IsHeader);
            if (header != null)
                foreach (Md.TableCell cell in header.OfType<Md.TableCell>())
                {
                    var paragraph = cell.OfType<ParagraphBlock>().FirstOrDefault();
                    Paragraph rendered = MakeParagraph(paragraph == null ? null : paragraph.Inline);
                    labels.Add(new TextRange(rendered.ContentStart, rendered.ContentEnd).Text.Trim());
                }
            foreach (Md.TableRow row in source.OfType<Md.TableRow>().Where(item => !item.IsHeader))
            {
                var record = new Section { Padding = new Thickness(7, 4, 5, 3), BorderThickness = new Thickness(2, 0, 0, 0), Margin = new Thickness(0, 0, 0, 6) };
                record.SetResourceReference(WpfBlock.BorderBrushProperty, "MarkdownRuleBrush");
                record.SetResourceReference(TextElement.BackgroundProperty, "MarkdownHeaderBrush");
                int position = 0;
                foreach (Md.TableCell cell in row.OfType<Md.TableCell>())
                {
                    var label = position < labels.Count ? labels[position] : "";
                    var paragraph = cell.OfType<ParagraphBlock>().FirstOrDefault();
                    Paragraph line = MakeParagraph(paragraph == null ? null : paragraph.Inline);
                    if (label.Length != 0)
                    {
                        var prefix = new Run(label + "  ·  ") { FontWeight = FontWeights.SemiBold };
                        if (line.Inlines.FirstInline == null) line.Inlines.Add(prefix);
                        else line.Inlines.InsertBefore(line.Inlines.FirstInline, prefix);
                    }
                    line.Margin = new Thickness(0, 0, 0, 3);
                    record.Blocks.Add(line);
                    position++;
                }
                section.Blocks.Add(record);
            }
            return section;
        }

        private void AddInlines(ContainerInline source, InlineCollection target)
        {
            if (source == null) return;
            foreach (Markdig.Syntax.Inlines.Inline item in source)
            {
                WpfInline rendered = RenderInline(item);
                if (rendered != null) target.Add(rendered);
            }
        }

        private WpfInline RenderInline(Markdig.Syntax.Inlines.Inline source)
        {
            var literal = source as LiteralInline;
            if (literal != null) return new Run(literal.Content.ToString());
            var entity = source as HtmlEntityInline;
            if (entity != null) return new Run(entity.Transcoded.ToString());
            var line = source as LineBreakInline;
            if (line != null) return line.IsHard ? (WpfInline)new LineBreak() : new Run(" ");
            var code = source as CodeInline;
            if (code != null)
            {
                var label = new Run(code.Content) { FontFamily = mono };
                label.SetResourceReference(TextElement.BackgroundProperty, "MarkdownCodeBrush");
                return label;
            }
            var emphasis = source as EmphasisInline;
            if (emphasis != null)
            {
                var span = new Span();
                if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.SemiBold;
                else span.FontStyle = FontStyles.Italic;
                AddInlines(emphasis, span.Inlines);
                return span;
            }
            var link = source as LinkInline;
            if (link != null)
            {
                string url = link.GetDynamicUrl == null ? link.Url : link.GetDynamicUrl();
                Span span = link.IsImage ? new Span() : MakeLink(url);
                AddInlines(link, span.Inlines);
                return span;
            }
            var auto = source as AutolinkInline;
            if (auto != null)
            {
                Span span = MakeLink(auto.Url);
                span.Inlines.Add(new Run(auto.Url));
                return span;
            }
            var container = source as ContainerInline;
            if (container == null) return null;
            var fallback = new Span();
            AddInlines(container, fallback.Inlines);
            return fallback;
        }

        private static Span MakeLink(string target)
        {
            Uri uri;
            if (!Uri.TryCreate(target, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return new Span();
            var link = new Hyperlink { NavigateUri = uri };
            link.SetResourceReference(TextElement.ForegroundProperty, "MarkdownLinkBrush");
            link.RequestNavigate += OpenLink;
            return link;
        }

        private static void OpenLink(object sender, RequestNavigateEventArgs args)
        {
            args.Handled = true;
            if (args.Uri.Scheme != Uri.UriSchemeHttp && args.Uri.Scheme != Uri.UriSchemeHttps) return;
            try { Process.Start(new ProcessStartInfo { FileName = args.Uri.AbsoluteUri, UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
