using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AiFocus;

/// <summary>
/// Small Markdown renderer for chat bubbles: headings, emphasis, inline code, links, lists,
/// fenced code, tables, quotes and rules — the subset AI replies actually use. Unknown syntax
/// simply stays as text, so nothing is ever lost.
/// </summary>
public static partial class MarkdownView
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI");
    private static readonly Brush CodeBackground = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xEE, 0xE8)));
    private static readonly Brush RuleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE3, 0xE0, 0xD8)));
    private static readonly Brush QuoteBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD5, 0xD1, 0xC8)));
    private static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x87, 0x80)));
    private const double DefaultSize = 14;

    private static Brush Freeze(SolidColorBrush brush) { brush.Freeze(); return brush; }

    /// <param name="size">Body font size; headings, code and tables scale from it (cards use a smaller one).</param>
    public static FrameworkElement Render(string markdown, double size = DefaultSize)
    {
        var panel = new StackPanel();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            panel.Children.Add(Paragraph(string.Join("\n", paragraph), size));
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal); i++) code.Add(lines[i]);
                panel.Children.Add(CodeBlock(string.Join("\n", code), size));
                continue;
            }
            if (trimmed.Length == 0) { FlushParagraph(); continue; }
            if (trimmed.StartsWith('|'))
            {
                FlushParagraph();
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++) rows.Add(lines[i].Trim());
                i--;
                panel.Children.Add(Table(rows, size));
                continue;
            }
            if (HeadingRegex().Match(trimmed) is { Success: true } heading)
            {
                FlushParagraph();
                int level = heading.Groups[1].Length;
                var block = Inline(heading.Groups[2].Value, size + (level <= 2 ? 1.5 : 0.5));
                block.FontWeight = FontWeights.SemiBold;
                block.Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 8, 0, 3);
                panel.Children.Add(block);
                continue;
            }
            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                panel.Children.Add(new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 8, 0, 8) });
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = Inline(trimmed.TrimStart('>').Trim(), size);
                quote.Foreground = MutedBrush;
                panel.Children.Add(new Border
                {
                    BorderBrush = QuoteBrush, BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 1, 0, 1), Margin = new Thickness(0, 2, 0, 2), Child = quote
                });
                continue;
            }
            if (ListRegex().Match(line) is { Success: true } item)
            {
                FlushParagraph();
                int indent = item.Groups[1].Value.Replace("\t", "  ").Length / 2;
                var marker = item.Groups[2].Value;
                panel.Children.Add(ListItem(char.IsDigit(marker[0]) ? marker : "•", item.Groups[3].Value, indent, size));
                continue;
            }
            paragraph.Add(trimmed);
        }
        FlushParagraph();
        return panel;
    }

    private static TextBlock Paragraph(string text, double size)
    {
        var block = Inline(text, size);
        block.Margin = new Thickness(0, 2, 0, 4);
        return block;
    }

    private static FrameworkElement ListItem(string marker, string text, int indent, double size)
    {
        var grid = new Grid { Margin = new Thickness(indent * 16, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(marker == "•" ? 14 : 22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = marker, Foreground = MutedBrush, FontSize = size, LineHeight = size * 1.5 });
        var body = Inline(text, size);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private static FrameworkElement CodeBlock(string code, double size) => new Border
    {
        Background = CodeBackground, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 7),
        Margin = new Thickness(0, 4, 0, 6),
        Child = new TextBlock { Text = code, FontFamily = Mono, FontSize = size - 1.5, TextWrapping = TextWrapping.Wrap }
    };

    private static FrameworkElement Table(List<string> rows, double size)
    {
        static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        // The "|---|:---:|" line only separates the header.
        var data = rows.Where(r => !SeparatorRegex().IsMatch(r)).Select(Cells).ToList();
        int columns = data.Count == 0 ? 0 : data.Max(r => r.Length);
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 6) };
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < data.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < data[r].Length; c++)
            {
                var cell = Inline(data[r][c], size - 1);
                if (r == 0) cell.FontWeight = FontWeights.SemiBold;
                var border = new Border
                {
                    BorderBrush = RuleBrush, BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 4, 14, 4), Child = cell
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        return grid;
    }

    /// <summary>Text with inline code, bold, italic and links.</summary>
    private static TextBlock Inline(string text, double size)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, LineHeight = size * 1.5 };
        int last = 0;
        foreach (Match m in InlineRegex().Matches(text))
        {
            if (m.Index > last) block.Inlines.Add(new Run(text[last..m.Index]));
            if (m.Groups["code"].Success)
                block.Inlines.Add(new Run(" " + m.Groups["code"].Value + " ") { FontFamily = Mono, FontSize = size - 1, Background = CodeBackground });
            else if (m.Groups["bold"].Success)
                block.Inlines.Add(new Run(m.Groups["bold"].Value) { FontWeight = FontWeights.SemiBold });
            else if (m.Groups["italic"].Success)
                block.Inlines.Add(new Run(m.Groups["italic"].Value) { FontStyle = FontStyles.Italic });
            else if (m.Groups["link"].Success)
                block.Inlines.Add(new Run(m.Groups["link"].Value) { TextDecorations = TextDecorations.Underline });
            last = m.Index + m.Length;
        }
        if (last < text.Length) block.Inlines.Add(new Run(text[last..]));
        return block;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex HeadingRegex();
    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$")] private static partial Regex ListRegex();
    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")] private static partial Regex SeparatorRegex();
    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>.+?)\*\*|__(?<bold>.+?)__|(?<![A-Za-z0-9*])\*(?<italic>[^*\s][^*]*?)\*(?![A-Za-z0-9*])|\[(?<link>[^\]]+)\]\([^)]+\)")]
    private static partial Regex InlineRegex();
}

/// <summary>Binds a chat message's text to rendered Markdown in the conversation view.</summary>
public sealed class MarkdownConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        MarkdownView.Render(value as string ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
