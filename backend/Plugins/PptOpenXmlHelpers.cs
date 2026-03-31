using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
namespace OfficeCopilot.Server.Plugins;

/// <summary>PPT OpenXml 共用：形状枚举、文本写入、图片、备注、重排、表格、超链接、幻灯片复制。</summary>
internal static class PptOpenXmlHelpers
{
    /// <summary>
    /// 将 .pptx 读入 MemoryStream 后编辑再整文件写回，避免就地打开时 part 变短未截断导致包尾脏字节。
    /// mutate 前后会强制加载所有 SlidePart，降低「只操作一页却清空其他页」的风险。
    /// </summary>
    /// <returns>失败时返回错误文案；成功返回 null。</returns>
    internal static string? EditPresentationInMemory(string filePath, Func<PresentationDocument, string?> mutate)
    {
        var bytes = File.ReadAllBytes(filePath);
        using var ms = new MemoryStream();
        ms.Write(bytes, 0, bytes.Length);
        ms.Position = 0;
        string? err;
        using (var doc = PresentationDocument.Open(ms, true))
        {
            var pp0 = doc.PresentationPart;
            if (pp0 != null)
            {
                foreach (var sp in pp0.GetPartsOfType<SlidePart>())
                    _ = sp.Slide?.InnerXml;
            }

            err = mutate(doc);
            if (err == null)
            {
                var pp = doc.PresentationPart;
                if (pp != null)
                {
                    foreach (var sp in pp.GetPartsOfType<SlidePart>())
                        _ = sp.Slide?.InnerXml;
                }

                pp?.Presentation?.Save();
            }
        }

        if (err == null)
            File.WriteAllBytes(filePath, ms.ToArray());
        return err;
    }

    /// <summary>SlideIdList 下除 p:sldId 外还可能出现扩展节点；必须用 Elements&lt;SlideId&gt; 才能保证与播放顺序一致。</summary>
    internal static IReadOnlyList<SlideId> GetOrderedSlideIds(SlideIdList list) =>
        list.Elements<SlideId>().ToList();

    internal static SlidePart? GetSlidePartByIndex(PresentationPart part, int slideIndex, out string? error)
    {
        error = null;
        if (part.Presentation?.SlideIdList == null)
        {
            error = "[错误] 演示文稿中无幻灯片。";
            return null;
        }

        var slideIds = GetOrderedSlideIds(part.Presentation.SlideIdList);
        if (slideIndex < 1 || slideIndex > slideIds.Count)
        {
            error = $"[错误] 幻灯片序号 {slideIndex} 超出范围，当前共 {slideIds.Count} 张。";
            return null;
        }

        var slideId = slideIds[slideIndex - 1];
        if (slideId?.RelationshipId?.Value == null)
        {
            error = "[错误] 无法获取该幻灯片。";
            return null;
        }

        return part.GetPartById(slideId.RelationshipId.Value) as SlidePart;
    }

    internal static IEnumerable<Shape> EnumerateTextShapes(ShapeTree tree)
    {
        foreach (var shape in EnumerateShapes(tree))
        {
            if (shape.TextBody != null)
                yield return shape;
        }
    }

    private static IEnumerable<Shape> EnumerateShapes(OpenXmlCompositeElement container)
    {
        foreach (var el in container.ChildElements)
        {
            switch (el)
            {
                case Shape sh:
                    yield return sh;
                    break;
                case GroupShape gs:
                    foreach (var inner in EnumerateShapes(gs))
                        yield return inner;
                    break;
            }
        }
    }

    internal static string GetShapeName(Shape shape)
    {
        var nv = shape.NonVisualShapeProperties?.NonVisualDrawingProperties;
        return nv?.Name?.Value ?? "";
    }

    internal static string? GetPlaceholderTypeLabel(Shape shape)
    {
        var ph = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<PlaceholderShape>();
        if (ph?.Type?.Value == null) return null;
        return ph.Type.Value.ToString();
    }

    internal static string GetSlidePlainText(Slide? slide, int maxChars)
    {
        if (slide == null) return "(无文本)";
        var texts = slide.Descendants<A.Text>().Select(t => t.Text ?? "").ToList();
        var combined = string.Join(" ", texts).Trim();
        if (string.IsNullOrEmpty(combined)) return "(无文本)";
        if (combined.Length > maxChars) return combined[..maxChars] + "...";
        return combined;
    }

    internal static string FormatShapeListForSlide(Slide? slide)
    {
        if (slide?.CommonSlideData?.ShapeTree == null)
            return "(无形状树)";
        var sb = new StringBuilder();
        var list = EnumerateTextShapes(slide.CommonSlideData.ShapeTree).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var sh = list[i];
            var name = GetShapeName(sh);
            var ph = GetPlaceholderTypeLabel(sh);
            var phNote = string.IsNullOrEmpty(ph) ? "无占位符" : $"占位符={ph}";
            var texts = sh.Descendants<A.Text>().Select(t => t.Text ?? "").Where(s => s.Length > 0);
            var preview = string.Join(" ", texts).Trim();
            if (preview.Length > 120) preview = preview[..120] + "...";
            if (string.IsNullOrEmpty(preview)) preview = "(空)";
            sb.AppendLine($"  [{i + 1}] Name=\"{name}\" {phNote} 预览: {preview}");
        }

        if (list.Count == 0)
            return "（本页无带文本的形状）";
        return sb.ToString().TrimEnd();
    }

    internal static bool TryParsePlaceholderType(string? placeholderType, out PlaceholderValues target)
    {
        target = PlaceholderValues.Title;
        var s = (placeholderType ?? "title").Trim();
        if (s.Equals("title", StringComparison.OrdinalIgnoreCase)) { target = PlaceholderValues.Title; return true; }
        if (s.Equals("body", StringComparison.OrdinalIgnoreCase)) { target = PlaceholderValues.Body; return true; }
        if (s.Equals("subtitle", StringComparison.OrdinalIgnoreCase) || s.Equals("subTitle", StringComparison.OrdinalIgnoreCase))
        {
            target = PlaceholderValues.SubTitle;
            return true;
        }

        if (s.Equals("ctrTitle", StringComparison.OrdinalIgnoreCase) || s.Equals("centeredtitle", StringComparison.OrdinalIgnoreCase)
                                                                     || s.Equals("center", StringComparison.OrdinalIgnoreCase))
        {
            target = PlaceholderValues.CenteredTitle;
            return true;
        }

        return false;
    }

    internal const string InsertedSlideBodyShapeName = "Content Placeholder 1";

    /// <summary>ppt_slide_insert 生成的标题框 Name，与占位符无关时供 ppt_slide_write(..., "title", ...) 回退匹配。</summary>
    internal const string InsertedSlideTitleShapeName = "Title 1";

    internal static Shape? FindShapeByPlaceholder(ShapeTree tree, PlaceholderValues phType)
    {
        foreach (var sp in EnumerateShapes(tree).OfType<Shape>())
        {
            var ph = sp.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<PlaceholderShape>();
            if (ph?.Type?.Value == null) continue;
            if (phType == PlaceholderValues.Body)
            {
                if (ph.Type.Value == PlaceholderValues.Body || ph.Index?.Value == 1)
                    return sp;
            }
            else if (ph.Type.Value == phType)
            {
                return sp;
            }
        }

        // ppt_slide_insert 的正文框：Body 占位符或按固定 Name 匹配以便 ppt_slide_write(..., "body", ...)
        if (phType == PlaceholderValues.Body)
        {
            foreach (var sp in EnumerateTextShapes(tree))
            {
                if (string.Equals(GetShapeName(sp), InsertedSlideBodyShapeName, StringComparison.OrdinalIgnoreCase))
                    return sp;
            }
        }

        // 插入页使用「无 p:ph」的纯文本框时，仍可按 Name 写入标题（避免与共享版式上的占位符合并后清空 a:t）
        if (phType == PlaceholderValues.Title || phType == PlaceholderValues.CenteredTitle)
        {
            foreach (var sp in EnumerateTextShapes(tree))
            {
                if (string.Equals(GetShapeName(sp), InsertedSlideTitleShapeName, StringComparison.OrdinalIgnoreCase))
                    return sp;
            }
        }

        return null;
    }

    internal static Shape? FindShapeByIndex(ShapeTree tree, int shapeIndex1)
    {
        if (shapeIndex1 < 1) return null;
        var list = EnumerateTextShapes(tree).ToList();
        return shapeIndex1 <= list.Count ? list[shapeIndex1 - 1] : null;
    }

    internal static Shape? FindShapeByName(ShapeTree tree, string shapeName)
    {
        if (string.IsNullOrWhiteSpace(shapeName)) return null;
        foreach (var sp in EnumerateTextShapes(tree))
        {
            if (string.Equals(GetShapeName(sp), shapeName.Trim(), StringComparison.OrdinalIgnoreCase))
                return sp;
        }

        return null;
    }

    internal static void SetShapeText(Shape shape, string text, int defaultFontSizeHundredths = 1800)
    {
        var tb = shape.TextBody;
        if (tb == null) return;
        tb.RemoveAllChildren<A.Paragraph>();
        var normalized = ToolMultilineTextNormalizer.NormalizeToNewlineSeparatedLines(text);
        var lines = normalized.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (string.IsNullOrEmpty(trimmed))
            {
                tb.AppendChild(new A.Paragraph());
                continue;
            }

            var isBullet = trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal);
            var content = isBullet ? trimmed[2..].TrimStart() : trimmed;

            var runProps = new A.RunProperties { Language = "zh-CN", FontSize = defaultFontSizeHundredths };
            runProps.Append(new A.LatinFont { Typeface = "Calibri" });
            runProps.Append(new A.EastAsianFont { Typeface = "微软雅黑" });

            var para = new A.Paragraph(new A.Run(runProps, new A.Text { Text = content }));
            if (isBullet)
            {
                var pPr = new A.ParagraphProperties { Level = 0 };
                pPr.Append(new A.BulletFont { Typeface = "Arial" });
                pPr.Append(new A.CharacterBullet { Char = "•" });
                para.InsertAt(pPr, 0);
            }

            tb.AppendChild(para);
        }

        if (!tb.Elements<A.Paragraph>().Any())
            tb.AppendChild(new A.Paragraph());
    }

    internal static uint NextSlideNumericId(PresentationPart part)
    {
        uint maxId = 0;
        var slideIdList = part.Presentation?.SlideIdList;
        if (slideIdList == null) return 256;
        foreach (var sid in GetOrderedSlideIds(slideIdList))
        {
            if (sid.Id?.Value != null && sid.Id.Value > maxId)
                maxId = sid.Id.Value;
        }

        return maxId + 1;
    }

    internal static string? AddImageToSlide(SlidePart slidePart, string imagePath, long offsetEmuX, long offsetEmuY, long widthEmu, long heightEmu)
    {
        var slide = slidePart.Slide;
        if (slide?.CommonSlideData?.ShapeTree == null)
            return "[错误] 幻灯片无形状树。";
        var tree = slide.CommonSlideData.ShapeTree;

        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        ImagePart imagePart = ext is ".jpg" or ".jpeg"
            ? slidePart.AddImagePart(ImagePartType.Jpeg)
            : ext == ".gif"
                ? slidePart.AddImagePart(ImagePartType.Gif)
                : ext == ".bmp"
                    ? slidePart.AddImagePart(ImagePartType.Bmp)
                    : slidePart.AddImagePart(ImagePartType.Png);
        using (var stream = File.OpenRead(imagePath))
            imagePart.FeedData(stream);
        var embedId = slidePart.GetIdOfPart(imagePart);

        var maxShapeId = 1U;
        foreach (var sh in tree.Descendants<NonVisualDrawingProperties>())
        {
            if (sh.Id?.Value != null && sh.Id.Value > maxShapeId)
                maxShapeId = sh.Id.Value;
        }

        var picId = maxShapeId + 1;
        var pic = new Picture(
            new NonVisualPictureProperties(
                new NonVisualDrawingProperties { Id = picId, Name = "Picture " + picId },
                new NonVisualPictureDrawingProperties()),
            new BlipFill(
                new A.Blip { Embed = embedId, CompressionState = A.BlipCompressionValues.Print },
                new A.Stretch(new A.FillRectangle())),
            new ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = offsetEmuX, Y = offsetEmuY },
                    new A.Extents { Cx = widthEmu, Cy = heightEmu }),
                new A.PresetGeometry { Preset = A.ShapeTypeValues.Rectangle }));

        tree.AppendChild(pic);
        return null;
    }

    internal static string? ReadNotesText(NotesSlidePart? notesPart)
    {
        if (notesPart?.NotesSlide == null) return "";
        return string.Join("\n", notesPart.NotesSlide.Descendants<A.Text>().Select(t => t.Text ?? "").Where(x => x.Length > 0));
    }

    internal static void WriteNotesPlainText(SlidePart slidePart, string text)
    {
        var notesPart = slidePart.GetPartsOfType<NotesSlidePart>().FirstOrDefault();
        if (notesPart == null)
        {
            notesPart = slidePart.AddNewPart<NotesSlidePart>();
            notesPart.NotesSlide = new NotesSlide(
                new CommonSlideData(
                    new ShapeTree(
                        new P.NonVisualGroupShapeProperties(
                            new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                            new P.NonVisualGroupShapeDrawingProperties(),
                            new ApplicationNonVisualDrawingProperties()),
                        new GroupShapeProperties(new A.TransformGroup()),
                        new Shape(
                            new P.NonVisualShapeProperties(
                                new P.NonVisualDrawingProperties { Id = 2U, Name = "Notes Placeholder" },
                                new P.NonVisualShapeDrawingProperties(),
                                new ApplicationNonVisualDrawingProperties(new PlaceholderShape { Type = PlaceholderValues.Body, Index = 1 })),
                            new P.ShapeProperties(),
                            new P.TextBody(
                                new A.BodyProperties(),
                                new A.ListStyle(),
                                new A.Paragraph())))),
                new ColorMapOverride(new A.MasterColorMapping()));
        }

        var notesSlide = notesPart.NotesSlide;
        if (notesSlide == null)
            return;
        var bodyShape = notesSlide.CommonSlideData?.ShapeTree?.Elements<Shape>().FirstOrDefault(s => s.TextBody != null);
        if (bodyShape?.TextBody == null)
            return;
        SetShapeText(bodyShape, text ?? "");
    }

    internal static bool ReorderSlides(PresentationPart part, IReadOnlyList<int> newOrder1Based)
    {
        var slideIdList = part.Presentation?.SlideIdList;
        if (slideIdList == null) return false;
        var slideIds = GetOrderedSlideIds(slideIdList).ToList();
        var n = slideIds.Count;
        if (newOrder1Based.Count != n) return false;
        var set = newOrder1Based.ToHashSet();
        if (set.Count != n || set.Any(x => x < 1 || x > n)) return false;

        var reordered = newOrder1Based.Select(i => slideIds[i - 1]).ToList();
        slideIdList.RemoveAllChildren();
        foreach (var sid in reordered)
            slideIdList.AppendChild(sid);
        return true;
    }

    /// <summary>在形状第一个 Run 上设置点击超链接（外部 URL）。</summary>
    internal static string? SetFirstRunHyperlink(SlidePart slidePart, Shape shape, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "[错误] URL 为空。";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return "[错误] URL 必须是绝对地址（如 https://...）。";
        var tb = shape.TextBody;
        if (tb == null) return "[错误] 该形状无文本。";
        var firstRun = tb.Descendants<A.Run>().FirstOrDefault();
        if (firstRun == null)
        {
            var t = tb.Descendants<A.Text>().FirstOrDefault();
            if (t?.Parent is A.Run r)
                firstRun = r;
        }

        if (firstRun == null) return "[错误] 未找到可附加链接的文本 Run。";

        var rel = slidePart.AddHyperlinkRelationship(uri, true);
        var rid = rel.Id;
        if (string.IsNullOrEmpty(rid)) return "[错误] 无法创建超链接关系。";
        var h = firstRun.GetFirstChild<A.HyperlinkOnClick>();
        if (h != null) h.Remove();
        firstRun.InsertAt(new A.HyperlinkOnClick { Id = rid }, 0);
        return null;
    }

    internal static string? AddSimpleTable(ShapeTree tree, int rows, int cols, long offsetX, long offsetY, long widthEmu, long heightEmu)
    {
        if (rows < 1 || cols < 1 || rows > 20 || cols > 10)
            return "[错误] 表格行列无效（行 1–20，列 1–10）。";
        var maxId = 1U;
        foreach (var nv in tree.Descendants<NonVisualDrawingProperties>())
            if (nv.Id?.Value > maxId) maxId = nv.Id!.Value;
        var graphicFrameId = maxId + 1;
        var cellW = widthEmu / cols;
        var cellH = heightEmu / rows;

        var tbl = new A.Table(
            new A.TableProperties { FirstRow = true, BandRow = true },
            new A.TableGrid(Enumerable.Range(0, cols).Select(_ => new A.GridColumn { Width = cellW }).ToArray()));

        for (var r = 0; r < rows; r++)
        {
            var tr = new A.TableRow { Height = cellH };
            for (var c = 0; c < cols; c++)
            {
                var tc = new A.TableCell(
                    new A.TextBody(
                        new A.BodyProperties(),
                        new A.ListStyle(),
                        new A.Paragraph(new A.Run(new A.Text { Text = "" }))),
                    new A.TableCellProperties());
                tr.Append(tc);
            }

            tbl.Append(tr);
        }

        var graphicFrame = new GraphicFrame(
            new NonVisualGraphicFrameProperties(
                new NonVisualDrawingProperties { Id = graphicFrameId, Name = "Table " + graphicFrameId },
                new NonVisualGraphicFrameDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new Transform(
                new A.Offset { X = offsetX, Y = offsetY },
                new A.Extents { Cx = widthEmu, Cy = heightEmu }),
            new A.Graphic(
                new A.GraphicData(tbl) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));

        tree.AppendChild(graphicFrame);
        return null;
    }

    internal static bool SlidePartHasImages(SlidePart part) => part.ImageParts.Any();

    /// <summary>rows 之间用 |，单元格之间用英文逗号（单元格内容勿含未转义逗号）。</summary>
    internal static string? WriteFirstTableCells(Slide? slide, string rowsCsv)
    {
        var tbl = slide?.CommonSlideData?.ShapeTree?.Descendants<A.Table>().FirstOrDefault();
        if (tbl == null) return "[错误] 该幻灯片上未找到表格。";
        var rowEls = tbl.Elements<A.TableRow>().ToList();
        if (rowEls.Count == 0) return "[错误] 表格无行。";
        var inputRows = rowsCsv.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var r = 0; r < Math.Min(inputRows.Length, rowEls.Count); r++)
        {
            var cells = inputRows[r].Split(',', StringSplitOptions.TrimEntries);
            var tcs = rowEls[r].Elements<A.TableCell>().ToList();
            for (var c = 0; c < Math.Min(cells.Length, tcs.Count); c++)
            {
                var tc = tcs[c];
                var tb = tc.GetFirstChild<A.TextBody>();
                if (tb == null)
                {
                    tb = new A.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph());
                    tc.AppendChild(tb);
                }

                tb.RemoveAllChildren<A.Paragraph>();
                tb.AppendChild(new A.Paragraph(new A.Run(new A.Text { Text = cells[c] ?? "" })));
            }
        }

        return null;
    }

    internal static string? DuplicateSlideAfter(PresentationPart presentationPart, int sourceSlideIndex1)
    {
        var slideIds = presentationPart.Presentation?.SlideIdList is { } sil
            ? GetOrderedSlideIds(sil).ToList()
            : null;
        if (slideIds == null || slideIds.Count == 0)
            return "[错误] 无幻灯片。";
        if (sourceSlideIndex1 < 1 || sourceSlideIndex1 > slideIds.Count)
            return "[错误] 源幻灯片序号超出范围。";

        var sourceId = slideIds[sourceSlideIndex1 - 1];
        if (sourceId.RelationshipId?.Value == null)
            return "[错误] 无法解析源幻灯片。";
        var sourcePart = presentationPart.GetPartById(sourceId.RelationshipId.Value) as SlidePart;
        if (sourcePart?.Slide == null)
            return "[错误] 无法打开源幻灯片。";
        if (SlidePartHasImages(sourcePart))
            return "[错误] 当前不支持复制含嵌入图片的幻灯片（请新建页后手动插入图片）。";

        var layout = sourcePart.SlideLayoutPart;
        if (layout == null)
            return "[错误] 源幻灯片缺少版式。";

        var clone = (Slide)sourcePart.Slide.CloneNode(true)!;
        var newPart = presentationPart.AddNewPart<SlidePart>();
        newPart.Slide = clone;
        newPart.AddPart(layout);
        var newRid = presentationPart.GetIdOfPart(newPart);
        var newSlideId = new SlideId { Id = NextSlideNumericId(presentationPart), RelationshipId = newRid };
        var list = presentationPart.Presentation!.SlideIdList!;
        list.InsertAfter(newSlideId, sourceId);
        return null;
    }
}
