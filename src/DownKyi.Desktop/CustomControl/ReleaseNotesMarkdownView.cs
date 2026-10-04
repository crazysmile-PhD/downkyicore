using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DownKyi.CustomControl;

internal sealed class ReleaseNotesMarkdownView : StackPanel
{
    internal const string CodeBlockClass = "release-notes-code-block";
    internal const string HeadingClass = "release-notes-heading";
    internal const string ImageAltClass = "release-notes-image-alt";
    internal const string InlineCodeClass = "release-notes-inline-code";
    internal const string LinkClass = "release-notes-link";
    internal const string ListMarkerClass = "release-notes-list-marker";
    internal const string QuoteClass = "release-notes-quote";
    internal const string StrongClass = "release-notes-strong";

    private const string BodyClass = "release-notes-body";
    private const string CodeTextClass = "release-notes-code-text";
    private const string EmphasisClass = "release-notes-emphasis";
    private const string ListClass = "release-notes-list";
    private const string ListItemClass = "release-notes-list-item";
    private const string RuleClass = "release-notes-rule";

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UseCjkFriendlyEmphasis()
        .Build();

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<ReleaseNotesMarkdownView, string?>(nameof(Markdown));

    public ReleaseNotesMarkdownView()
    {
        Spacing = 8;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);

        if (change.Property == MarkdownProperty)
        {
            RenderMarkdown();
        }
    }

    private void RenderMarkdown()
    {
        Children.Clear();
        if (string.IsNullOrWhiteSpace(Markdown))
        {
            return;
        }

        var document = Markdig.Markdown.Parse(Markdown, MarkdownPipeline);
        AddBlocks(document, this);
    }

    private static void AddBlocks(ContainerBlock container, Panel target)
    {
        foreach (var block in container)
        {
            var control = CreateBlockControl(block);
            if (control is not null)
            {
                target.Children.Add(control);
            }
        }
    }

    private static Control? CreateBlockControl(Block block)
    {
        return block switch
        {
            HtmlBlock => null,
            HeadingBlock heading => CreateHeading(heading),
            ParagraphBlock paragraph => CreateTextBlock(paragraph, BodyClass),
            ListBlock list => CreateList(list),
            QuoteBlock quote => CreateQuote(quote),
            CodeBlock code => CreateCodeBlock(code),
            ThematicBreakBlock => CreateRule(),
            ContainerBlock container => CreateContainer(container),
            _ => null
        };
    }

    private static TextBlock CreateHeading(HeadingBlock heading)
    {
        var textBlock = CreateTextBlock(heading, HeadingClass);
        textBlock.Classes.Add($"release-notes-h{Math.Clamp(heading.Level, 1, 6)}");
        return textBlock;
    }

    private static TextBlock CreateTextBlock(LeafBlock block, string className)
    {
        var textBlock = new TextBlock
        {
            Inlines = new InlineCollection(),
            TextWrapping = TextWrapping.Wrap
        };
        textBlock.Classes.Add(className);
        AppendInlines(textBlock.Inlines, block.Inline);
        return textBlock;
    }

    private static StackPanel CreateList(ListBlock list)
    {
        var listPanel = new StackPanel
        {
            Spacing = 4
        };
        listPanel.Classes.Add(ListClass);

        var orderedItemIndex = 0;
        var orderedStart = 1;
        foreach (var block in list)
        {
            if (block is not ListItemBlock item)
            {
                continue;
            }

            if (list.IsOrdered && orderedItemIndex == 0)
            {
                orderedStart = item.Order;
            }

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 8
            };
            row.Classes.Add(ListItemClass);

            var marker = new TextBlock
            {
                Text = list.IsOrdered
                    ? string.Concat(
                        (orderedStart + orderedItemIndex).ToString(CultureInfo.InvariantCulture),
                        ".")
                    : "•",
                VerticalAlignment = VerticalAlignment.Top
            };
            marker.Classes.Add(ListMarkerClass);

            var content = new StackPanel
            {
                Spacing = 4
            };
            Grid.SetColumn(content, 1);
            AddBlocks(item, content);

            row.Children.Add(marker);
            row.Children.Add(content);
            listPanel.Children.Add(row);

            if (list.IsOrdered)
            {
                orderedItemIndex++;
            }
        }

        return listPanel;
    }

    private static Border CreateQuote(QuoteBlock quote)
    {
        var content = new StackPanel
        {
            Spacing = 4
        };
        AddBlocks(quote, content);

        var border = new Border
        {
            Child = content
        };
        border.Classes.Add(QuoteClass);
        return border;
    }

    private static Border CreateCodeBlock(CodeBlock code)
    {
        var text = code.Lines.ToString().TrimEnd('\r', '\n');
        var textBlock = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };
        textBlock.Classes.Add(CodeTextClass);

        var border = new Border
        {
            Child = textBlock
        };
        border.Classes.Add(CodeBlockClass);
        return border;
    }

    private static Separator CreateRule()
    {
        var separator = new Separator();
        separator.Classes.Add(RuleClass);
        return separator;
    }

    private static StackPanel CreateContainer(ContainerBlock container)
    {
        var content = new StackPanel
        {
            Spacing = 4
        };
        AddBlocks(container, content);
        return content;
    }

    private static void AppendInlines(InlineCollection target, ContainerInline? container)
    {
        for (var inline = container?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    var codeRun = new Run(code.Content);
                    codeRun.Classes.Add(InlineCodeClass);
                    target.Add(codeRun);
                    break;
                case LineBreakInline lineBreak when lineBreak.IsHard:
                    target.Add(new LineBreak());
                    break;
                case LineBreakInline:
                    target.Add(new Run(" "));
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case HtmlInline:
                    break;
                case AutolinkInline autolink:
                    target.Add(CreateAutolinkSpan(autolink));
                    break;
                case LinkInline link:
                    target.Add(CreateLinkSpan(link));
                    break;
                case EmphasisInline emphasis:
                    target.Add(CreateEmphasisSpan(emphasis));
                    break;
                case ContainerInline nested:
                    var span = CreateSpan();
                    AppendInlines(span.Inlines, nested);
                    target.Add(span);
                    break;
            }
        }
    }

    private static Span CreateAutolinkSpan(AutolinkInline autolink)
    {
        var span = CreateSpan();
        span.Classes.Add(LinkClass);
        span.Inlines.Add(new Run(autolink.Url));
        return span;
    }

    private static Span CreateLinkSpan(LinkInline link)
    {
        var span = CreateSpan();
        span.Classes.Add(link.IsImage ? ImageAltClass : LinkClass);
        AppendInlines(span.Inlines, link);
        return span;
    }

    private static Span CreateEmphasisSpan(EmphasisInline emphasis)
    {
        var span = CreateSpan();
        span.Classes.Add(emphasis.DelimiterCount >= 2 ? StrongClass : EmphasisClass);
        AppendInlines(span.Inlines, emphasis);
        return span;
    }

    private static Span CreateSpan()
    {
        return new Span
        {
            Inlines = new InlineCollection()
        };
    }
}
