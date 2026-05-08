using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace OpenWorkmate.Server.Plugins;

/// <summary>基于 Microsoft Learn 示例生成最小合法 .pptx（含母版、版式、主题、首张幻灯片）。</summary>
internal static class PptPresentationCreator
{
    /// <summary>创建空白演示文稿；若文件已存在则覆盖。</summary>
    public static void CreateBlankPresentation(string filePath, bool macroEnabled)
    {
        var docType = macroEnabled
            ? PresentationDocumentType.MacroEnabledPresentation
            : PresentationDocumentType.Presentation;

        using var presentationDoc = PresentationDocument.Create(filePath, docType);
        var presentationPart = presentationDoc.AddPresentationPart();
        presentationPart.Presentation = new Presentation();
        CreatePresentationParts(presentationPart);
        presentationPart.Presentation.Save();
    }

    private static void CreatePresentationParts(PresentationPart presentationPart)
    {
        var slideMasterIdList1 = new SlideMasterIdList(new SlideMasterId { Id = 2147483648U, RelationshipId = "rId1" });
        var slideIdList1 = new SlideIdList(new SlideId { Id = 256U, RelationshipId = "rId2" });
        var slideSize1 = new SlideSize { Cx = 9144000, Cy = 6858000, Type = SlideSizeValues.Screen4x3 };
        var notesSize1 = new NotesSize { Cx = 6858000, Cy = 9144000 };
        var defaultTextStyle1 = new DefaultTextStyle();

        if (presentationPart.Presentation is not { } presentation)
            throw new InvalidOperationException("Presentation 根未初始化。");
        presentation.Append(slideMasterIdList1, slideIdList1, slideSize1, notesSize1, defaultTextStyle1);

        var slidePart1 = CreateSlidePart(presentationPart);
        var slideLayoutPart1 = CreateSlideLayoutPart(slidePart1);
        _ = slidePart1.GetIdOfPart(slideLayoutPart1);
        var slideMasterPart1 = CreateSlideMasterPart(slideLayoutPart1);
        var themePart1 = CreateTheme(slideMasterPart1);

        slideMasterPart1.AddPart(slideLayoutPart1, "rId1");
        presentationPart.AddPart(slideMasterPart1, "rId1");
        presentationPart.AddPart(themePart1, "rId5");
    }

    private static SlidePart CreateSlidePart(PresentationPart presentationPart)
    {
        var slidePart1 = presentationPart.AddNewPart<SlidePart>("rId2");
        var shapeTree = new ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()),
            new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 2U, Name = "Title 1" },
                    new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties(new PlaceholderShape { Type = PlaceholderValues.Title })),
                new P.ShapeProperties(),
                new P.TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.EndParagraphRunProperties { Language = "en-US" }))));
        slidePart1.Slide = new Slide(
            new CommonSlideData(shapeTree),
            new ColorMapOverride(new A.MasterColorMapping()));
        return slidePart1;
    }

    private static SlideLayoutPart CreateSlideLayoutPart(SlidePart slidePart1)
    {
        var slideLayoutPart1 = slidePart1.AddNewPart<SlideLayoutPart>();
        var layoutShapeTree = new ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()),
            new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 2U, Name = "" },
                    new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties(new PlaceholderShape())),
                new P.ShapeProperties(),
                new P.TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.EndParagraphRunProperties()))));
        var slideLayout = new SlideLayout(
            new CommonSlideData(layoutShapeTree),
            new ColorMapOverride(new A.MasterColorMapping()));
        slideLayoutPart1.SlideLayout = slideLayout;
        return slideLayoutPart1;
    }

    private static SlideMasterPart CreateSlideMasterPart(SlideLayoutPart slideLayoutPart1)
    {
        var slideMasterPart1 = slideLayoutPart1.AddNewPart<SlideMasterPart>("rId1");
        var masterShapeTree = new ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()),
            new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 2U, Name = "Title Placeholder 1" },
                    new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties(new PlaceholderShape { Type = PlaceholderValues.Title })),
                new P.ShapeProperties(),
                new P.TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph())));
        var slideMaster = new SlideMaster(
            new CommonSlideData(masterShapeTree),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
            },
            new SlideLayoutIdList(new SlideLayoutId { Id = 2147483649U, RelationshipId = "rId1" }),
            new TextStyles(new TitleStyle(), new BodyStyle(), new OtherStyle()));
        slideMasterPart1.SlideMaster = slideMaster;
        return slideMasterPart1;
    }

    private static ThemePart CreateTheme(SlideMasterPart slideMasterPart1)
    {
        var themePart1 = slideMasterPart1.AddNewPart<ThemePart>("rId5");
        var theme1 = new A.Theme { Name = "Office Theme" };

        var themeElements1 = new A.ThemeElements(
            new A.ColorScheme(
                new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
                new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
                new A.Dark2Color(new A.RgbColorModelHex { Val = "1F497D" }),
                new A.Light2Color(new A.RgbColorModelHex { Val = "EEECE1" }),
                new A.Accent1Color(new A.RgbColorModelHex { Val = "4F81BD" }),
                new A.Accent2Color(new A.RgbColorModelHex { Val = "C0504D" }),
                new A.Accent3Color(new A.RgbColorModelHex { Val = "9BBB59" }),
                new A.Accent4Color(new A.RgbColorModelHex { Val = "8064A2" }),
                new A.Accent5Color(new A.RgbColorModelHex { Val = "4BACC6" }),
                new A.Accent6Color(new A.RgbColorModelHex { Val = "F79646" }),
                new A.Hyperlink(new A.RgbColorModelHex { Val = "0000FF" }),
                new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "800080" }))
            { Name = "Office" },
            new A.FontScheme(
                new A.MajorFont(
                    new A.LatinFont { Typeface = "Calibri" },
                    new A.EastAsianFont { Typeface = "" },
                    new A.ComplexScriptFont { Typeface = "" }),
                new A.MinorFont(
                    new A.LatinFont { Typeface = "Calibri" },
                    new A.EastAsianFont { Typeface = "" },
                    new A.ComplexScriptFont { Typeface = "" }))
            { Name = "Office" },
            new A.FormatScheme(
                new A.FillStyleList(
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                    new A.GradientFill(
                        new A.GradientStopList(
                            new A.GradientStop(
                                new A.SchemeColor(
                                    new A.Tint { Val = 50000 },
                                    new A.SaturationModulation { Val = 300000 })
                                { Val = A.SchemeColorValues.PhColor })
                            { Position = 0 },
                            new A.GradientStop(
                                new A.SchemeColor(
                                    new A.Tint { Val = 37000 },
                                    new A.SaturationModulation { Val = 300000 })
                                { Val = A.SchemeColorValues.PhColor })
                            { Position = 35000 },
                            new A.GradientStop(
                                new A.SchemeColor(
                                    new A.Tint { Val = 15000 },
                                    new A.SaturationModulation { Val = 350000 })
                                { Val = A.SchemeColorValues.PhColor })
                            { Position = 100000 }),
                        new A.LinearGradientFill { Angle = 16200000, Scaled = true }),
                    new A.NoFill(),
                    new A.PatternFill(),
                    new A.GroupFill()),
                new A.LineStyleList(
                    new A.Outline(
                        new A.SolidFill(
                            new A.SchemeColor(
                                new A.Shade { Val = 95000 },
                                new A.SaturationModulation { Val = 105000 })
                            { Val = A.SchemeColorValues.PhColor }),
                        new A.PresetDash { Val = A.PresetLineDashValues.Solid })
                    {
                        Width = 9525,
                        CapType = A.LineCapValues.Flat,
                        CompoundLineType = A.CompoundLineValues.Single,
                        Alignment = A.PenAlignmentValues.Center
                    },
                    new A.Outline(
                        new A.SolidFill(
                            new A.SchemeColor(
                                new A.Shade { Val = 95000 },
                                new A.SaturationModulation { Val = 105000 })
                            { Val = A.SchemeColorValues.PhColor }),
                        new A.PresetDash { Val = A.PresetLineDashValues.Solid })
                    {
                        Width = 9525,
                        CapType = A.LineCapValues.Flat,
                        CompoundLineType = A.CompoundLineValues.Single,
                        Alignment = A.PenAlignmentValues.Center
                    },
                    new A.Outline(
                        new A.SolidFill(
                            new A.SchemeColor(
                                new A.Shade { Val = 95000 },
                                new A.SaturationModulation { Val = 105000 })
                            { Val = A.SchemeColorValues.PhColor }),
                        new A.PresetDash { Val = A.PresetLineDashValues.Solid })
                    {
                        Width = 9525,
                        CapType = A.LineCapValues.Flat,
                        CompoundLineType = A.CompoundLineValues.Single,
                        Alignment = A.PenAlignmentValues.Center
                    }),
                new A.EffectStyleList(
                    new A.EffectStyle(
                        new A.EffectList(
                            new A.OuterShadow(
                                new A.RgbColorModelHex(new A.Alpha { Val = 38000 }) { Val = "000000" })
                            { BlurRadius = 40000L, Distance = 20000L, Direction = 5400000, RotateWithShape = false })),
                    new A.EffectStyle(
                        new A.EffectList(
                            new A.OuterShadow(
                                new A.RgbColorModelHex(new A.Alpha { Val = 38000 }) { Val = "000000" })
                            { BlurRadius = 40000L, Distance = 20000L, Direction = 5400000, RotateWithShape = false })),
                    new A.EffectStyle(
                        new A.EffectList(
                            new A.OuterShadow(
                                new A.RgbColorModelHex(new A.Alpha { Val = 38000 }) { Val = "000000" })
                            { BlurRadius = 40000L, Distance = 20000L, Direction = 5400000, RotateWithShape = false }))),
                new A.BackgroundFillStyleList(
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                    new A.GradientFill(
                        new A.GradientStopList(
                            new A.GradientStop(
                                new A.SchemeColor(
                                    new A.Tint { Val = 50000 },
                                    new A.SaturationModulation { Val = 300000 })
                                { Val = A.SchemeColorValues.PhColor })
                            { Position = 0 },
                            new A.GradientStop(
                                new A.SchemeColor(
                                    new A.Tint { Val = 50000 },
                                    new A.SaturationModulation { Val = 300000 })
                                { Val = A.SchemeColorValues.PhColor })
                            { Position = 0 },
                            new A.GradientStop(
                                new A.SchemeColor(
                                    new A.Tint { Val = 50000 },
                                    new A.SaturationModulation { Val = 300000 })
                                { Val = A.SchemeColorValues.PhColor })
                            { Position = 0 }),
                        new A.LinearGradientFill { Angle = 16200000, Scaled = true }),
                    new A.GradientFill(
                        new A.GradientStopList(
                            new A.GradientStop(
                                new A.SchemeColor(
                                    new A.Tint { Val = 50000 },
                                    new A.SaturationModulation { Val = 300000 })
                                { Val = A.SchemeColorValues.PhColor })
                            { Position = 0 },
                            new A.GradientStop(
                                new A.SchemeColor(
                                    new A.Tint { Val = 50000 },
                                    new A.SaturationModulation { Val = 300000 })
                                { Val = A.SchemeColorValues.PhColor })
                            { Position = 0 }),
                        new A.LinearGradientFill { Angle = 16200000, Scaled = true })))
            { Name = "Office" });

        theme1.Append(themeElements1);
        theme1.Append(new A.ObjectDefaults());
        theme1.Append(new A.ExtraColorSchemeList());

        themePart1.Theme = theme1;
        return themePart1;
    }
}
