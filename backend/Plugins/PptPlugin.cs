using System.ComponentModel;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Drawing;
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

    private static string GetSlideTextSummary(OpenXmlElement? slide, int maxChars)
    {
        if (slide == null) return "(无文本)";
        var texts = slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>().Select(t => t.Text ?? "").ToList();
        var combined = string.Join(" ", texts).Trim();
        if (string.IsNullOrEmpty(combined)) return "(无文本)";
        if (combined.Length > maxChars) combined = combined[..maxChars] + "...";
        return combined;
    }
}
