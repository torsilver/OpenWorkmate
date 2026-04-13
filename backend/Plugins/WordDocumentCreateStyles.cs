using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace OfficeCopilot.Server.Plugins;

/// <summary>为 <see cref="WordPlugin.WordDocumentCreate"/> 应用文档样式与末节节属性（页边距等）。</summary>
public static class WordDocumentCreateStyles
{
    /// <summary>GB/T 9704 常用归纳：天头约 37mm、订口约 28mm；下白约 35mm、右白约 26mm（与版心 156×225mm 常见教程一致，非替代标准全文）。</summary>
    private static int MmToTwips(double mm) => (int)Math.Round(mm / 25.4 * 1440.0);

    public static void Apply(MainDocumentPart mainPart, WordDocumentCreatePreset preset)
    {
        if (preset == WordDocumentCreatePreset.CnGovGbt9704)
            ApplyCnGovGbt9704(mainPart);
        else
            ApplyDefault(mainPart);
    }

    public static SectionProperties CreateFinalSectionProperties(WordDocumentCreatePreset preset)
    {
        if (preset == WordDocumentCreatePreset.CnGovGbt9704)
        {
            var top = MmToTwips(37);
            var left = MmToTwips(28);
            var bottom = MmToTwips(35);
            var right = MmToTwips(26);
            return new SectionProperties(
                new PageSize { Width = 11906, Height = 16838, Orient = PageOrientationValues.Portrait },
                new PageMargin { Top = top, Right = (uint)right, Bottom = bottom, Left = (uint)left, Header = 720U, Footer = 720U });
        }

        return new SectionProperties(
            new PageSize { Width = 11906, Height = 16838, Orient = PageOrientationValues.Portrait },
            new PageMargin { Top = 1440, Right = 1800U, Bottom = 1440, Left = 1800U, Header = 720U, Footer = 720U });
    }

    /// <summary>历史默认：Calibri/雅黑 10.5pt、彩色标题、美式页边距。</summary>
    private static void ApplyDefault(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();

        var docDefaults = new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "微软雅黑", ComplexScript = "Calibri" },
                new FontSize { Val = "21" },
                new FontSizeComplexScript { Val = "21" },
                new Languages { Val = "en-US", EastAsia = "zh-CN" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                new SpacingBetweenLines { After = "160", Line = "360", LineRule = LineSpacingRuleValues.Auto })));
        styles.Append(docDefaults);

        styles.Append(new Style(
            new StyleName { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(
                new SpacingBetweenLines { After = "160", Line = "360", LineRule = LineSpacingRuleValues.Auto }))
        { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });

        styles.Append(new Style(
            new StyleName { Val = "heading 1" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", EastAsia = "微软雅黑" },
                new Bold(),
                new FontSize { Val = "44" },
                new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "1F3864" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "360", After = "120" },
                new KeepNext()))
        { Type = StyleValues.Paragraph, StyleId = "Heading1" });

        styles.Append(new Style(
            new StyleName { Val = "heading 2" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", EastAsia = "微软雅黑" },
                new Bold(),
                new FontSize { Val = "32" },
                new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "2E75B6" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "240", After = "80" },
                new KeepNext()))
        { Type = StyleValues.Paragraph, StyleId = "Heading2" });

        styles.Append(new Style(
            new StyleName { Val = "heading 3" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", EastAsia = "微软雅黑" },
                new Bold(),
                new FontSize { Val = "28" },
                new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "404040" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "200", After = "80" },
                new KeepNext()))
        { Type = StyleValues.Paragraph, StyleId = "Heading3" });

        styles.Append(new Style(
            new StyleName { Val = "List Paragraph" },
            new BasedOn { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(
                new Indentation { Left = "720" },
                new SpacingBetweenLines { After = "80" }))
        { Type = StyleValues.Paragraph, StyleId = "ListParagraph" });

        stylesPart.Styles = styles;
        stylesPart.Styles.Save();
    }

    /// <summary>公文常用：仿宋三号正文+固定 28 磅；标题 2 号宋体（小标宋常见名未装时由 Word 回退）；层次用黑体/楷体三号。</summary>
    private static void ApplyCnGovGbt9704(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();

        // 正文：3 号仿宋 ≈16pt；行距固定 28 磅 → w:line=560（1/20 pt），w:lineRule=exact
        var bodySpacingDoc = new SpacingBetweenLines { After = "0", Line = "560", LineRule = LineSpacingRuleValues.Exact };
        var bodySpacingNormal = new SpacingBetweenLines { After = "0", Line = "560", LineRule = LineSpacingRuleValues.Exact };

        var docDefaults = new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "FangSong", HighAnsi = "FangSong", EastAsia = "仿宋", ComplexScript = "FangSong" },
                new FontSize { Val = "32" },
                new FontSizeComplexScript { Val = "32" },
                new Languages { Val = "en-US", EastAsia = "zh-CN" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(bodySpacingDoc)));
        styles.Append(docDefaults);

        styles.Append(new Style(
            new StyleName { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(bodySpacingNormal))
        { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });

        // 文档主标题 / #：2 号 ≈22pt；居中；宋体作小标宋未安装时的稳妥回退（单位可改为方正小标宋简体）
        styles.Append(new Style(
            new StyleName { Val = "heading 1" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            new StyleRunProperties(
                new RunFonts { Ascii = "SimSun", HighAnsi = "SimSun", EastAsia = "宋体", ComplexScript = "SimSun" },
                new FontSize { Val = "44" },
                new FontSizeComplexScript { Val = "44" }),
            new StyleParagraphProperties(
                new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { Before = "240", After = "240", Line = "560", LineRule = LineSpacingRuleValues.Exact },
                new KeepNext()))
        { Type = StyleValues.Paragraph, StyleId = "Heading1" });

        // ##：黑体三号
        styles.Append(new Style(
            new StyleName { Val = "heading 2" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            new StyleRunProperties(
                new RunFonts { Ascii = "SimHei", HighAnsi = "SimHei", EastAsia = "黑体", ComplexScript = "SimHei" },
                new Bold(),
                new FontSize { Val = "32" },
                new FontSizeComplexScript { Val = "32" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "160", After = "80", Line = "560", LineRule = LineSpacingRuleValues.Exact },
                new KeepNext()))
        { Type = StyleValues.Paragraph, StyleId = "Heading2" });

        // ###：楷体三号
        styles.Append(new Style(
            new StyleName { Val = "heading 3" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            new StyleRunProperties(
                new RunFonts { Ascii = "KaiTi", HighAnsi = "KaiTi", EastAsia = "楷体", ComplexScript = "KaiTi" },
                new Bold(),
                new FontSize { Val = "32" },
                new FontSizeComplexScript { Val = "32" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "120", After = "80", Line = "560", LineRule = LineSpacingRuleValues.Exact },
                new KeepNext()))
        { Type = StyleValues.Paragraph, StyleId = "Heading3" });

        styles.Append(new Style(
            new StyleName { Val = "List Paragraph" },
            new BasedOn { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(
                new Indentation { Left = "720" },
                new SpacingBetweenLines { After = "0", Line = "560", LineRule = LineSpacingRuleValues.Exact }))
        { Type = StyleValues.Paragraph, StyleId = "ListParagraph" });

        stylesPart.Styles = styles;
        stylesPart.Styles.Save();
    }
}
