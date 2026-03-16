using System.ComponentModel;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Plugins;

public sealed class PptPlugin
{
    [KernelFunction("ppt_slides_list")]
    [Description("列出 PPT 演示文稿中所有幻灯片的序号与简要信息（按播放顺序）。filePath 支持环境变量与相对路径（解析到下载目录）。")]
    public string PptSlidesList(
        [Description("PPT 文件完整路径，.pptx 或 .pptm")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = PresentationDocument.Open(filePath, false);
            var part = doc.PresentationPart;
            if (part?.Presentation?.SlideIdList == null)
                return "演示文稿中无幻灯片。";
            var slideIds = part.Presentation.SlideIdList.ChildElements;
            if (slideIds.Count == 0)
                return "演示文稿中无幻灯片。";
            var sb = new StringBuilder();
            sb.AppendLine($"共 {slideIds.Count} 张幻灯片（按播放顺序）：");
            for (int i = 0; i < slideIds.Count; i++)
            {
                var slideId = slideIds[i] as SlideId;
                if (slideId?.RelationshipId?.Value == null) continue;
                try
                {
                    var slidePart = (SlidePart?)part.GetPartById(slideId.RelationshipId.Value);
                    var preview = GetSlideTextSummary(slidePart?.Slide, 80);
                    sb.AppendLine($"  {i + 1}. {preview}");
                }
                catch
                {
                    sb.AppendLine($"  {i + 1}. (无法读取预览)");
                }
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("ppt_slide_read")]
    [Description("按播放顺序读取指定幻灯片的文本与形状摘要。slideIndex 从 1 开始。")]
    public string PptSlideRead(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("幻灯片序号，从 1 开始，与播放顺序一致")] int slideIndex = 1)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        if (slideIndex < 1)
            return "[错误] slideIndex 必须大于等于 1。";
        try
        {
            using var doc = PresentationDocument.Open(filePath, false);
            var part = doc.PresentationPart;
            if (part?.Presentation?.SlideIdList == null)
                return "[错误] 演示文稿中无幻灯片。";
            var slideIds = part.Presentation.SlideIdList.ChildElements;
            if (slideIndex > slideIds.Count)
                return $"[错误] 幻灯片序号 {slideIndex} 超出范围，当前共 {slideIds.Count} 张。";
            var slideId = slideIds[slideIndex - 1] as SlideId;
            if (slideId?.RelationshipId?.Value == null)
                return "[错误] 无法获取该幻灯片。";
            var slidePart = (SlidePart?)part.GetPartById(slideId.RelationshipId.Value);
            var slide = slidePart?.Slide;
            if (slide == null)
                return "[错误] 无法打开该幻灯片。";
            var fullText = GetSlideTextSummary(slide, int.MaxValue);
            var result = $"[幻灯片 {slideIndex}]\n{fullText}";
            if (result.Length > 8000) result = result[..8000] + "\n...(已截断)";
            return result;
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("ppt_slide_write")]
    [Description("按播放顺序向指定幻灯片的标题或正文占位符写入文本。slideIndex 从 1 开始。placeholderType 为 title 或 body。")]
    public string PptSlideWrite(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("幻灯片序号，从 1 开始")] int slideIndex = 1,
        [Description("占位符类型：title（标题）或 body（正文）")] string placeholderType = "title",
        [Description("要写入的文本")] string text = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        if (slideIndex < 1) return "[错误] slideIndex 必须大于等于 1。";
        var phType = placeholderType?.Trim().Equals("body", StringComparison.OrdinalIgnoreCase) == true
            ? PlaceholderValues.Body
            : PlaceholderValues.Title;
        try
        {
            using var doc = PresentationDocument.Open(filePath, true);
            var part = doc.PresentationPart;
            if (part?.Presentation?.SlideIdList == null) return "[错误] 演示文稿中无幻灯片。";
            var slideIds = part.Presentation.SlideIdList.ChildElements;
            if (slideIndex > slideIds.Count) return $"[错误] 幻灯片序号 {slideIndex} 超出范围，当前共 {slideIds.Count} 张。";
            var slideId = slideIds[slideIndex - 1] as SlideId;
            if (slideId?.RelationshipId?.Value == null) return "[错误] 无法获取该幻灯片。";
            var slidePart = (SlidePart?)part.GetPartById(slideId.RelationshipId.Value);
            var slide = slidePart?.Slide;
            if (slide == null) return "[错误] 无法打开该幻灯片。";
            var shapeTree = slide.CommonSlideData?.ShapeTree;
            if (shapeTree == null) return "[错误] 该幻灯片无形状树。";
            Shape? targetShape = null;
            foreach (var el in shapeTree.ChildElements)
            {
                if (el is not Shape sp) continue;
                var ph = sp.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<PlaceholderShape>();
                if (ph == null) continue;
                if (phType == PlaceholderValues.Title && ph.Type?.Value == PlaceholderValues.Title)
                { targetShape = sp; break; }
                if (phType == PlaceholderValues.Body && (ph.Type?.Value == PlaceholderValues.Body || (ph.Index?.Value == 1))) { targetShape = sp; break; }
            }
            if (targetShape == null) return "[错误] 未找到该占位符（title 或 body）。";
            SetShapeText(targetShape, text ?? "");
            return "成功：已写入幻灯片占位符。";
        }
        catch (Exception ex) { return $"[错误] 写入失败: {ex.Message}"; }
    }

    [KernelFunction("ppt_slide_insert")]
    [Description("在指定位置插入新幻灯片（可选标题与正文文本）。position 从 1 开始，表示插入后新幻灯片的序号；0 表示插入到最前。")]
    public string PptSlideInsert(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("插入位置：1 表示在第 1 页后插入（新页为第 2 页），0 表示插入到最前")] int position = 0,
        [Description("新幻灯片标题文本")] string titleText = "",
        [Description("新幻灯片正文文本")] string bodyText = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = PresentationDocument.Open(filePath, true);
            var part = doc.PresentationPart;
            if (part?.Presentation?.SlideIdList == null) return "[错误] 演示文稿结构异常。";
            var slideIdList = part.Presentation.SlideIdList;
            var slideIds = slideIdList.ChildElements;
            uint maxId = 0;
            foreach (SlideId sid in slideIds)
            { if (sid.Id?.Value != null && sid.Id.Value > maxId) maxId = sid.Id.Value; }
            maxId++;
            var newSlide = new Slide(
                new CommonSlideData(
                    new ShapeTree(
                        new NonVisualGroupShapeProperties(
                            new NonVisualDrawingProperties { Id = 1U, Name = "" },
                            new NonVisualGroupShapeDrawingProperties(),
                            new ApplicationNonVisualDrawingProperties()),
                        new GroupShapeProperties(new A.TransformGroup()),
                        CreateTitleShape(2, titleText ?? ""),
                        CreateBodyShape(3, bodyText ?? ""))),
                new ColorMapOverride(new A.MasterColorMapping()));
            var slidePart = part.AddNewPart<SlidePart>();
            slidePart.Slide = newSlide;
            SlidePart? refSlidePart = null;
            if (slideIds.Count > 0)
            {
                var refIndex = position <= 0 ? 0 : Math.Min(position - 1, slideIds.Count - 1);
                var refId = (SlideId)slideIds[refIndex];
                if (refId.RelationshipId?.Value != null)
                    refSlidePart = (SlidePart?)part.GetPartById(refId.RelationshipId.Value);
            }
            if (refSlidePart?.SlideLayoutPart != null)
                slidePart.AddPart(refSlidePart.SlideLayoutPart);
            var newSlideId = new SlideId { Id = maxId, RelationshipId = part.GetIdOfPart(slidePart) };
            if (position <= 0 || slideIds.Count == 0)
                slideIdList.InsertAt(newSlideId, 0);
            else if (position >= slideIds.Count)
                slideIdList.AppendChild(newSlideId);
            else
                slideIdList.InsertAfter(newSlideId, slideIds[position - 1] as OpenXmlElement);
            part.Presentation.Save();
            return "成功：已插入新幻灯片。";
        }
        catch (Exception ex) { return $"[错误] 插入失败: {ex.Message}"; }
    }

    [KernelFunction("ppt_slide_delete")]
    [Description("按播放顺序删除指定幻灯片。slideIndex 从 1 开始。")]
    public string PptSlideDelete(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("要删除的幻灯片序号，从 1 开始")] int slideIndex = 1)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        if (slideIndex < 1) return "[错误] slideIndex 必须大于等于 1。";
        try
        {
            using var doc = PresentationDocument.Open(filePath, true);
            var part = doc.PresentationPart;
            if (part?.Presentation?.SlideIdList == null) return "[错误] 演示文稿中无幻灯片。";
            var slideIdList = part.Presentation.SlideIdList;
            var slideIds = slideIdList.ChildElements;
            if (slideIndex > slideIds.Count) return $"[错误] 幻灯片序号 {slideIndex} 超出范围，当前共 {slideIds.Count} 张。";
            var slideId = slideIds[slideIndex - 1] as SlideId;
            if (slideId?.RelationshipId?.Value == null) return "[错误] 无法获取该幻灯片。";
            var slidePart = (SlidePart?)part.GetPartById(slideId.RelationshipId.Value);
            slideId.Remove();
            if (slidePart != null) part.DeletePart(slidePart);
            part.Presentation.Save();
            return "成功：已删除该幻灯片。";
        }
        catch (Exception ex) { return $"[错误] 删除失败: {ex.Message}"; }
    }

    private static void SetShapeText(Shape shape, string text)
    {
        var tb = shape.TextBody;
        if (tb == null) return;
        tb.RemoveAllChildren<A.Paragraph>();
        tb.AppendChild(new A.Paragraph(
            new A.Run(
                new A.Text { Text = text ?? "" })));
    }

    private static Shape CreateTitleShape(uint id, string titleText)
    {
        return new Shape(
            new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = id, Name = "Title 1" },
                new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties(new PlaceholderShape { Type = PlaceholderValues.Title })),
            new ShapeProperties(),
            new A.TextBody(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(new A.Run(new A.Text { Text = titleText }))));
    }

    private static Shape CreateBodyShape(uint id, string bodyText)
    {
        return new Shape(
            new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = id, Name = "Content Placeholder 1" },
                new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties(new PlaceholderShape { Type = PlaceholderValues.Body, Index = 1 })),
            new ShapeProperties(),
            new A.TextBody(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(new A.Run(new A.Text { Text = bodyText }))));
    }

    private static string GetSlideTextSummary(OpenXmlElement? slide, int maxChars)
    {
        if (slide == null) return "(无文本)";
        var texts = slide.Descendants<A.Text>().Select(t => t.Text ?? "").ToList();
        var combined = string.Join(" ", texts).Trim();
        if (string.IsNullOrEmpty(combined)) return "(无文本)";
        if (combined.Length > maxChars) combined = combined[..maxChars] + "...";
        return combined;
    }
}
