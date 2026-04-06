using System.ComponentModel;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Plugins;

public sealed class PptPlugin
{
    private readonly ILogger<PptPlugin>? _logger;

    public PptPlugin(ILogger<PptPlugin>? logger = null) => _logger = logger;

    private static string LogTextPreview(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        var s = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (s.Length > maxChars) return s[..maxChars] + "…(truncated)";
        return s;
    }
    [ToolFunction("ppt_document_create")]
    [Description("创建新的空白 PPT 文件（至少含一张可编辑幻灯片）。若文件已存在则覆盖。新建后可用 ppt_slide_write / ppt_slide_insert 编辑。勿用 shell 重定向伪造 .pptx。路径支持环境变量与相对路径；须对应当前登录用户，勿用 Public/%PUBLIC% 代替用户主目录。")]
    public string PptDocumentCreate(
        [Description("优先仅文件名或相对路径（当前用户下约定目录，常为 Downloads）；须 .pptx 或 .pptm；绝对路径用 %USERPROFILE%\\…")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            if (File.Exists(filePath))
                File.Delete(filePath);
            var macro = filePath.EndsWith(".pptm", StringComparison.OrdinalIgnoreCase);
            PptPresentationCreator.CreateBlankPresentation(filePath, macro);
            return $"成功：已创建演示文稿（共 1 张幻灯片）：{filePath}";
        }
        catch (Exception ex) { return $"[错误] 创建失败: {ex.Message}"; }
    }

    [ToolFunction("ppt_slides_list")]
    [Description("列出 PPT 演示文稿中所有幻灯片的序号与简要信息（按播放顺序）。filePath 支持环境变量与相对路径（相对当前用户约定目录，多为 Downloads）。回答用户时必须引用并归纳本工具输出中的要点，勿假设用户能看到工具原始返回。")]
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
            var slideIds = PptOpenXmlHelpers.GetOrderedSlideIds(part.Presentation.SlideIdList);
            if (slideIds.Count == 0)
                return "演示文稿中无幻灯片。";
            var sb = new StringBuilder();
            sb.AppendLine($"共 {slideIds.Count} 张幻灯片（按播放顺序）：");
            for (int i = 0; i < slideIds.Count; i++)
            {
                var slideId = slideIds[i];
                if (slideId?.RelationshipId?.Value == null) continue;
                try
                {
                    var slidePart = (SlidePart?)part.GetPartById(slideId.RelationshipId.Value);
                    var preview = PptOpenXmlHelpers.GetSlidePlainText(slidePart?.Slide, 80);
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

    [ToolFunction("ppt_slide_read")]
    [Description("按播放顺序读取指定幻灯片全文；可选附带带文本形状的编号列表（Name、占位符类型、预览），便于 ppt_slide_write 使用 shapeIndex/shapeName。回答用户时必须引用并归纳本工具输出中的正文与要点，勿假设用户能看到工具原始返回。")]
    public string PptSlideRead(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("幻灯片序号，从 1 开始，与播放顺序一致")] int slideIndex = 1,
        [Description("为 true 时附加「形状列表」段落（默认 true）")] bool includeShapeDetails = true)
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
            var slideIds = PptOpenXmlHelpers.GetOrderedSlideIds(part.Presentation.SlideIdList);
            if (slideIndex > slideIds.Count)
                return $"[错误] 幻灯片序号 {slideIndex} 超出范围，当前共 {slideIds.Count} 张。";
            var slideId = slideIds[slideIndex - 1];
            if (slideId?.RelationshipId?.Value == null)
                return "[错误] 无法获取该幻灯片。";
            var slidePart = (SlidePart?)part.GetPartById(slideId.RelationshipId.Value);
            var slide = slidePart?.Slide;
            if (slide == null)
                return "[错误] 无法打开该幻灯片。";
            var fullText = PptOpenXmlHelpers.GetSlidePlainText(slide, int.MaxValue);
            var sb = new StringBuilder();
            sb.AppendLine($"[幻灯片 {slideIndex}]");
            sb.AppendLine(fullText);
            if (includeShapeDetails)
            {
                sb.AppendLine();
                sb.AppendLine("[形状列表（仅含可编辑文本框，编号供 shapeIndex 使用）]");
                sb.AppendLine(PptOpenXmlHelpers.FormatShapeListForSlide(slide));
            }

            var result = sb.ToString().TrimEnd();
            if (result.Length > 8000) result = result[..8000] + "\n...(已截断)";
            return result;
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [ToolFunction("ppt_slide_write")]
    [Description("向指定幻灯片写入文本。优先使用 shapeIndex（1 起，见 ppt_slide_read 形状列表）或 shapeName；否则按 placeholderType 匹配占位符：title、body、subtitle、ctrTitle。须先 ppt_document_create 或打开已有合法 pptx。text 支持用 |、空行或换行分段，服务端拆成多行；- 或 * 行首为项目符号。")]
    public string PptSlideWrite(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("幻灯片序号，从 1 开始")] int slideIndex = 1,
        [Description("占位符类型：title、body、subtitle、ctrTitle（shapeIndex>0 时可忽略）")] string placeholderType = "title",
        [Description("写入内容：可用 | 或空行/换行分段；- 或 * 开头为项目符号")] string text = "",
        [Description("可选：按 ppt_slide_read 列出的形状编号写入，0 表示不用")] int shapeIndex = 0,
        [Description("可选：与形状 Name 匹配（不区分大小写）")] string shapeName = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        if (slideIndex < 1) return "[错误] slideIndex 必须大于等于 1。";
        try
        {
            var err = PptOpenXmlHelpers.EditPresentationInMemory(filePath, doc =>
            {
                var part = doc.PresentationPart;
                if (part?.Presentation?.SlideIdList == null) return "[错误] 演示文稿中无幻灯片。";
                var slideIds = PptOpenXmlHelpers.GetOrderedSlideIds(part.Presentation.SlideIdList);
                if (slideIndex > slideIds.Count) return $"[错误] 幻灯片序号 {slideIndex} 超出范围，当前共 {slideIds.Count} 张。";
                var slideId = slideIds[slideIndex - 1];
                if (slideId?.RelationshipId?.Value == null) return "[错误] 无法获取该幻灯片。";
                var slidePart = (SlidePart?)part.GetPartById(slideId.RelationshipId.Value);
                var slide = slidePart?.Slide;
                if (slide == null) return "[错误] 无法打开该幻灯片。";
                var shapeTree = slide.CommonSlideData?.ShapeTree;
                if (shapeTree == null) return "[错误] 该幻灯片无形状树。";
                Shape? targetShape = null;
                if (shapeIndex > 0)
                    targetShape = PptOpenXmlHelpers.FindShapeByIndex(shapeTree, shapeIndex);
                if (targetShape == null && !string.IsNullOrWhiteSpace(shapeName))
                    targetShape = PptOpenXmlHelpers.FindShapeByName(shapeTree, shapeName);
                if (targetShape == null && PptOpenXmlHelpers.TryParsePlaceholderType(placeholderType, out var phKind))
                    targetShape = PptOpenXmlHelpers.FindShapeByPlaceholder(shapeTree, phKind);
                if (targetShape == null)
                {
                    var hint = PptOpenXmlHelpers.FormatShapeListForSlide(slide);
                    return $"[错误] 未找到可写入的形状。请使用 ppt_slide_read(includeShapeDetails:true) 查看编号后传入 shapeIndex，或检查 placeholderType/shapeName。\n当前页形状：\n{hint}";
                }

                PptOpenXmlHelpers.SetShapeText(targetShape, text ?? "");
                // 勿在此处调用 slide.Save()：与 Presentation.Save() 组合时曾导致其他 SlidePart 被空内容覆盖（insert 页丢字）
                return null;
            });
            return err ?? "成功：已写入幻灯片文本。";
        }
        catch (Exception ex) { return $"[错误] 写入失败: {ex.Message}"; }
    }

    [ToolFunction("ppt_slide_insert")]
    [Description("插入新幻灯片：克隆插入锚点页的 Slide 再写入标题/正文（避免「先改首页再插入」时 Open XML 保存丢字）。当前最小模板版式无正文占位符时，正文会写入标题文本框的后续段落（ppt_slide_read 仍可读全文）；若克隆源页含 body 占位符则标题、正文分形状写入。position=0：插到最前；position=k：紧接第 k 页之后；position≥总页数：末尾。")]
    public string PptSlideInsert(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("插入位置：0=最前；k=第 k 页之后；≥页数=末尾")] int position = 0,
        [Description("新幻灯片标题文本")] string titleText = "",
        [Description("正文：可用 | 或空行/换行分段；- 或 * 开头为项目符号")] string bodyText = "")
    {
        var rawFilePath = filePath;
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        var titleEmpty = string.IsNullOrEmpty(titleText);
        var bodyEmpty = string.IsNullOrEmpty(bodyText);
        _logger?.LogInformation(
            "[Ppt] ppt_slide_insert invoked rawFilePath={RawFilePath} resolvedFilePath={ResolvedFilePath} position={Position} titleLen={TitleLen} bodyLen={BodyLen} titleEmpty={TitleEmpty} bodyEmpty={BodyEmpty} titlePreview={TitlePreview} bodyPreview={BodyPreview}",
            rawFilePath ?? "(null)",
            filePath,
            position,
            titleText?.Length ?? 0,
            bodyText?.Length ?? 0,
            titleEmpty,
            bodyEmpty,
            LogTextPreview(titleText, 160),
            LogTextPreview(bodyText, 400));
        if (titleEmpty && bodyEmpty)
            _logger?.LogWarning(
                "[Ppt] ppt_slide_insert: titleText and bodyText are both empty — new slide will only show layout placeholders unless you call ppt_slide_write for that slideIndex.");

        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        try
        {
            var err = PptOpenXmlHelpers.EditPresentationInMemory(filePath, doc =>
            {
                var part = doc.PresentationPart;
                if (part?.Presentation?.SlideIdList == null) return "[错误] 演示文稿结构异常。";
                var slideIdList = part.Presentation.SlideIdList;
                var ordered = PptOpenXmlHelpers.GetOrderedSlideIds(slideIdList);
                var count = ordered.Count;
                uint maxId = 0;
                foreach (var sid in ordered)
                { if (sid.Id?.Value != null && sid.Id.Value > maxId) maxId = sid.Id.Value; }
                maxId++;
                if (count == 0)
                    return "[错误] 演示文稿中无幻灯片，无法插入。";

                // 克隆参考页再改字：从零组装的 Slide 在「先 write 第 1 页再 insert」时保存会丢 a:t；克隆已有 Slide 可避免该 SDK/版式组合问题。
                var anchor0Based = position <= 0 ? 0 : Math.Min(position, count) - 1;
                var anchorSlideId = ordered[anchor0Based];
                if (anchorSlideId.RelationshipId?.Value == null)
                    return "[错误] 无法解析插入锚点幻灯片。";
                var sourcePart = part.GetPartById(anchorSlideId.RelationshipId.Value) as SlidePart;
                if (sourcePart?.Slide == null)
                    return "[错误] 无法打开参考幻灯片。";
                var layout = sourcePart.SlideLayoutPart;
                if (layout == null)
                    return "[错误] 参考幻灯片缺少版式，无法插入。";

                var clone = (Slide)sourcePart.Slide.CloneNode(true)!;
                var slidePart = part.AddNewPart<SlidePart>();
                slidePart.AddPart(layout);
                slidePart.Slide = clone;

                var tree = slidePart.Slide.CommonSlideData?.ShapeTree;
                if (tree == null)
                    return "[错误] 新幻灯片无形状树。";

                var titleShape =
                    PptOpenXmlHelpers.FindShapeByPlaceholder(tree, PlaceholderValues.Title)
                    ?? PptOpenXmlHelpers.FindShapeByPlaceholder(tree, PlaceholderValues.CenteredTitle);
                if (titleShape == null)
                {
                    var list = PptOpenXmlHelpers.EnumerateTextShapes(tree).ToList();
                    if (list.Count > 0)
                        titleShape = list[0];
                }

                var bodyShape = PptOpenXmlHelpers.FindShapeByPlaceholder(tree, PlaceholderValues.Body);
                if (bodyShape == null)
                {
                    foreach (var sp in PptOpenXmlHelpers.EnumerateTextShapes(tree))
                    {
                        if (string.Equals(
                                PptOpenXmlHelpers.GetShapeName(sp),
                                PptOpenXmlHelpers.InsertedSlideBodyShapeName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            bodyShape = sp;
                            break;
                        }
                    }
                }

                if (titleShape != null)
                {
                    if (bodyShape != null)
                    {
                        PptOpenXmlHelpers.SetShapeText(titleShape, titleText ?? "", 2800);
                        PptOpenXmlHelpers.SetShapeText(bodyShape, bodyText ?? "");
                    }
                    else if (!string.IsNullOrEmpty(bodyText))
                    {
                        // 最小模板版式无正文占位符时，追加带 p:ph 的 Body 框会在保存时被丢掉；将正文作为标题框内后续段落写入（GetSlidePlainText 仍可读到全部 a:t）。
                        var merged = string.IsNullOrEmpty(titleText) ? bodyText! : $"{titleText}\n{bodyText}";
                        PptOpenXmlHelpers.SetShapeText(titleShape, merged, 2800);
                    }
                    else
                        PptOpenXmlHelpers.SetShapeText(titleShape, titleText ?? "", 2800);
                }
                // 与 MS Learn「Insert a new slide」一致：InsertAfter(new SlideId(), prevSlideId) 后设置 Id/RelationshipId
                SlideId newSlideId = new SlideId();
                SlideId? prevSlideId = null;
                if (position > 0 && count > 0)
                    prevSlideId = ordered[Math.Min(position, count) - 1];
                if (prevSlideId == null)
                {
                    var first = ordered.FirstOrDefault();
                    if (first == null)
                        slideIdList.AppendChild(newSlideId);
                    else
                        slideIdList.InsertBefore(newSlideId, first);
                }
                else
                    slideIdList.InsertAfter(newSlideId, prevSlideId);
                newSlideId.Id = maxId;
                newSlideId.RelationshipId = part.GetIdOfPart(slidePart);
                var countAfter = ordered.Count + 1;
                _logger?.LogInformation(
                    "[Ppt] ppt_slide_insert applied slideCountBefore={CountBefore} slideCountAfter={CountAfter} positionArg={Position}",
                    count,
                    countAfter,
                    position);
                return null;
            });
            if (err != null)
                _logger?.LogWarning("[Ppt] ppt_slide_insert failed: {Message}", err);
            return err ?? "成功：已插入新幻灯片。";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Ppt] ppt_slide_insert exception resolvedFilePath={FilePath}", filePath);
            return $"[错误] 插入失败: {ex.Message}";
        }
    }

    [ToolFunction("ppt_slide_delete")]
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
            var err = PptOpenXmlHelpers.EditPresentationInMemory(filePath, doc =>
            {
                var part = doc.PresentationPart;
                if (part?.Presentation?.SlideIdList == null) return "[错误] 演示文稿中无幻灯片。";
                var slideIdList = part.Presentation.SlideIdList;
                var slideIds = PptOpenXmlHelpers.GetOrderedSlideIds(slideIdList);
                if (slideIndex > slideIds.Count) return $"[错误] 幻灯片序号 {slideIndex} 超出范围，当前共 {slideIds.Count} 张。";
                var slideId = slideIds[slideIndex - 1];
                if (slideId?.RelationshipId?.Value == null) return "[错误] 无法获取该幻灯片。";
                var slidePart = (SlidePart?)part.GetPartById(slideId.RelationshipId.Value);
                slideId.Remove();
                if (slidePart != null) part.DeletePart(slidePart);
                return null;
            });
            return err ?? "成功：已删除该幻灯片。";
        }
        catch (Exception ex) { return $"[错误] 删除失败: {ex.Message}"; }
    }

    [ToolFunction("ppt_slide_image_add")]
    [Description("向指定幻灯片插入本地图片（PNG/JPEG/GIF/BMP）。默认放在左上角附近固定区域。")]
    public string PptSlideImageAdd(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("幻灯片序号，从 1 开始")] int slideIndex = 1,
        [Description("本地图片文件路径")] string imagePath = "",
        [Description("可选：距左 EMU，默认 457200")] long offsetXEmu = 457200,
        [Description("可选：距顶 EMU，默认 1900000")] long offsetYEmu = 1900000,
        [Description("可选：宽度 EMU，默认 4000000")] long widthEmu = 4000000,
        [Description("可选：高度 EMU，默认 2250000")] long heightEmu = 2250000)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        imagePath = OpenXmlHelpers.ResolvePath(imagePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return "[错误] 请提供存在的图片文件路径。";
        try
        {
            var err = PptOpenXmlHelpers.EditPresentationInMemory(filePath, doc =>
            {
                var part = doc.PresentationPart!;
                var slidePart = PptOpenXmlHelpers.GetSlidePartByIndex(part, slideIndex, out var slideErr);
                if (slidePart == null) return slideErr ?? "[错误] 无法打开幻灯片。";
                return PptOpenXmlHelpers.AddImageToSlide(slidePart, imagePath, offsetXEmu, offsetYEmu, widthEmu, heightEmu);
            });
            return err ?? "成功：已插入图片。";
        }
        catch (Exception ex) { return $"[错误] 插入图片失败: {ex.Message}"; }
    }

    [ToolFunction("ppt_notes_read")]
    [Description("读取指定幻灯片的演讲者备注文本。")]
    public string PptNotesRead(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("幻灯片序号，从 1 开始")] int slideIndex = 1)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = PresentationDocument.Open(filePath, false);
            var part = doc.PresentationPart!;
            var slidePart = PptOpenXmlHelpers.GetSlidePartByIndex(part, slideIndex, out var slideErr);
            if (slidePart == null) return slideErr ?? "[错误] 无法打开幻灯片。";
            var notes = slidePart.GetPartsOfType<NotesSlidePart>().FirstOrDefault();
            var text = PptOpenXmlHelpers.ReadNotesText(notes);
            return string.IsNullOrEmpty(text) ? "（无备注）" : text!;
        }
        catch (Exception ex) { return $"[错误] 读取备注失败: {ex.Message}"; }
    }

    [ToolFunction("ppt_notes_write")]
    [Description("写入指定幻灯片的演讲者备注（纯文本）；若无备注页则创建。")]
    public string PptNotesWrite(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("幻灯片序号，从 1 开始")] int slideIndex = 1,
        [Description("备注文本")] string text = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        try
        {
            var err = PptOpenXmlHelpers.EditPresentationInMemory(filePath, doc =>
            {
                var part = doc.PresentationPart!;
                var slidePart = PptOpenXmlHelpers.GetSlidePartByIndex(part, slideIndex, out var slideErr);
                if (slidePart == null) return slideErr ?? "[错误] 无法打开幻灯片。";
                PptOpenXmlHelpers.WriteNotesPlainText(slidePart, text ?? "");
                return null;
            });
            return err ?? "成功：已写入备注。";
        }
        catch (Exception ex) { return $"[错误] 写入备注失败: {ex.Message}"; }
    }

    [ToolFunction("ppt_slides_reorder")]
    [Description("按新顺序重排全部幻灯片。newOrder 为逗号分隔的序号列表，表示播放顺序：第 1 个数字=原第几页放到首位。例：3,2,1 表示倒序。长度须等于总页数。")]
    public string PptSlidesReorder(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("如 2,3,1")] string newOrder = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(newOrder)) return "[错误] 请提供 newOrder，如 2,3,1。";
        try
        {
            var parts = newOrder.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var order = new List<int>();
            foreach (var p in parts)
            {
                if (!int.TryParse(p, out var n)) return $"[错误] 无法解析序号：{p}";
                order.Add(n);
            }

            var err = PptOpenXmlHelpers.EditPresentationInMemory(filePath, doc =>
            {
                var presentationPart = doc.PresentationPart!;
                if (!PptOpenXmlHelpers.ReorderSlides(presentationPart, order))
                    return "[错误] newOrder 长度或数值与当前幻灯片数量不一致。";
                return null;
            });
            return err ?? "成功：已重排幻灯片。";
        }
        catch (Exception ex) { return $"[错误] 重排失败: {ex.Message}"; }
    }

    [ToolFunction("ppt_table_create")]
    [Description("在指定幻灯片末尾添加一个简单表格（DrawingML）。行 1–20，列 1–10。")]
    public string PptTableCreate(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("幻灯片序号")] int slideIndex = 1,
        [Description("行数")] int rows = 2,
        [Description("列数")] int cols = 2)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        try
        {
            var err = PptOpenXmlHelpers.EditPresentationInMemory(filePath, doc =>
            {
                var part = doc.PresentationPart!;
                var slidePart = PptOpenXmlHelpers.GetSlidePartByIndex(part, slideIndex, out var slideErr);
                if (slidePart?.Slide?.CommonSlideData?.ShapeTree == null)
                    return slideErr ?? "[错误] 无法打开幻灯片。";
                var tree = slidePart.Slide.CommonSlideData.ShapeTree;
                return PptOpenXmlHelpers.AddSimpleTable(tree, rows, cols, 457200, 2746380, 8000000, 3000000);
            });
            return err ?? "成功：已添加表格。";
        }
        catch (Exception ex) { return $"[错误] 创建表格失败: {ex.Message}"; }
    }

    [ToolFunction("ppt_table_write_cells")]
    [Description("向该页第一张表格写入文本。rowsCsv：多行用 | 分隔，行内单元格用英文逗号分隔（内容勿含未转义逗号）。")]
    public string PptTableWriteCells(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("幻灯片序号")] int slideIndex = 1,
        [Description("如 A1,B1|A2,B2")] string rowsCsv = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(rowsCsv)) return "[错误] 请提供 rowsCsv。";
        try
        {
            var err = PptOpenXmlHelpers.EditPresentationInMemory(filePath, doc =>
            {
                var part = doc.PresentationPart!;
                var slidePart = PptOpenXmlHelpers.GetSlidePartByIndex(part, slideIndex, out var slideErr);
                if (slidePart?.Slide == null) return slideErr ?? "[错误] 无法打开幻灯片。";
                return PptOpenXmlHelpers.WriteFirstTableCells(slidePart.Slide, rowsCsv);
            });
            return err ?? "成功：已写入表格单元格。";
        }
        catch (Exception ex) { return $"[错误] 写入表格失败: {ex.Message}"; }
    }

    [ToolFunction("ppt_hyperlink_add")]
    [Description("为指定幻灯片上某一文本形状的首个文本 Run 设置点击超链接（外部 URL）。优先 shapeIndex，否则 shapeName。")]
    public string PptHyperlinkAdd(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("幻灯片序号")] int slideIndex = 1,
        [Description("绝对 URL，如 https://example.com")] string url = "",
        [Description("文本形状编号，见 ppt_slide_read")] int shapeIndex = 1,
        [Description("可选形状 Name")] string shapeName = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        try
        {
            var err = PptOpenXmlHelpers.EditPresentationInMemory(filePath, doc =>
            {
                var part = doc.PresentationPart!;
                var slidePart = PptOpenXmlHelpers.GetSlidePartByIndex(part, slideIndex, out var slideErr);
                if (slidePart?.Slide?.CommonSlideData?.ShapeTree == null)
                    return slideErr ?? "[错误] 无法打开幻灯片。";
                var tree = slidePart.Slide.CommonSlideData.ShapeTree;
                Shape? shape = shapeIndex > 0 ? PptOpenXmlHelpers.FindShapeByIndex(tree, shapeIndex) : null;
                if (shape == null && !string.IsNullOrWhiteSpace(shapeName))
                    shape = PptOpenXmlHelpers.FindShapeByName(tree, shapeName);
                if (shape == null) return "[错误] 未找到形状，请检查 shapeIndex 或 shapeName。";
                return PptOpenXmlHelpers.SetFirstRunHyperlink(slidePart, shape, url);
            });
            return err ?? "成功：已添加超链接。";
        }
        catch (Exception ex) { return $"[错误] 添加超链接失败: {ex.Message}"; }
    }

    [ToolFunction("ppt_slide_duplicate")]
    [Description("在紧接指定页之后复制一张内容相同的幻灯片（嵌入图随 SlidePart 的 ImagePart 一并复制；复杂图表/视频等可能不完整）。")]
    public string PptSlideDuplicate(
        [Description("PPT 文件完整路径")] string filePath,
        [Description("要复制的幻灯片序号，从 1 开始")] int slideIndex = 1)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidatePptExtension(filePath, out var extErr)) return extErr;
        try
        {
            var err = PptOpenXmlHelpers.EditPresentationInMemory(filePath, doc =>
            {
                var part = doc.PresentationPart!;
                return PptOpenXmlHelpers.DuplicateSlideAfter(part, slideIndex);
            });
            return err ?? "成功：已复制幻灯片（插入在源页之后）。";
        }
        catch (Exception ex) { return $"[错误] 复制失败: {ex.Message}"; }
    }

    // Standard 16:9 slide dimensions: 12192000 x 6858000 EMU (25.4cm x 19.05cm)
}
