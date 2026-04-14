---
name: Pdf / Pdf
version: 1.0.0
description: PDF 文本抽取、页数与加密信息、纯文本生成新 PDF、多文件合并；附件本地路径、扫描件 OCR、加密失败提示。检索：pdf 附件 扫描件 合并 纯文本导出 get_pdf_text pdf_merge。复杂排版请用 Word 技能 + word_*，勿用本技能冒充版式引擎。
metadata: {"clawdbot":{"emoji":"📕","os":["linux","darwin","win32"]}}
---

## Taskly（本仓库）

- **插件**：**`Pdf`**。本地路径上的 `.pdf` 读写；不涉及 `.doc`/`.xls` 等旧二进制（那些见技能 **`office_legacy_to_openxml`**）。**纯 PDF 不要**调用 `office_legacy_save_as_open_xml`。
- **附件**：对话里若有 `attachment:xxx`，先用 **`get_attachment_path`**（`File` 插件）得到本机完整路径，再调用下文 **`get_pdf_*` / `pdf_*`**。
- **读流程**：先 **`get_pdf_info`**（页数、是否加密、标题/作者）→ 再 **`get_pdf_text`**（可选 `firstPage` / `lastPage` 为 **1-based** 闭区间、`maxChars` 有默认与硬上限）。返回含 **`[已截断：`** 时表示需缩小页范围或增大 `maxChars`。
- **扫描件 / 几乎无字**：`get_pdf_text` 若提示可能为扫描件或抽取为空，对**图片型 PDF** 应按项目约定走 **`get_attachment_path` → `ocr_image`**（`Ocr` / `MCP_OCR`，需用户已配置 OCR 模型），勿编造正文。
- **加密**：若返回以 **`失败：`** 开头（如已加密且无密码），**完整转述给用户**，勿称已成功读取。
- **新建简单 PDF**：**`pdf_document_create`** — `outputPath` **必须**以 `.pdf` 结尾（勿用 `.md`/`.txt`）；正文可用 **`\f`（换页符）** 分页；`overwrite` 控制是否覆盖已存在文件。适合纯文本报告；**复杂排版、公文版式**请用 **`word_document_create`** 等 **`word_*`** 或加载 **`word_cn_default_formal`**，不要用本工具冒充 Word。
- **合并**：**`pdf_merge`** — `inputPdfPaths` 至少 **两个** 有效路径，每行一个或用 **`;`** 分隔；`outputPath` 须 `.pdf`；顺序即合并顺序。
- **与 Word / Excel / PPT**：表格、样式、幻灯片、修订等一律用对应 Open XML 技能与 **`word_*` / `excel_*` / `ppt_*`**；PDF 侧仅做抽取、轻量生成与合并。

## Capabilities and limits

- **抽取引擎**：文本层用 PdfPig 式布局抽取；无文本层的页面不会「猜字」。
- **写入引擎**：PDFsharp 式简单排版；字体以常见宿主字体为主，复杂脚本可能显示不佳（见工具说明）。
- **合并**：按页导入顺序拼接；若某个输入无效，整次调用失败并以返回文案为准。

## Common pitfalls

- 把 **`.pdf`** 误当成旧 Office：不要走 `office_legacy_*`。
- **大文件**：分页读取 + 控制 `maxChars`，避免一次拉满上下文。
- **「转 Word」**：无单一魔法工具；典型路径是抽取文本/表格结构后由模型整理，再用 **`word_*`** 落盘（必要时 OCR）。

## Related skills

- **`Word / Docx`**：可编辑文档、正式版式、OOXML 结构。
- **`office_legacy_to_openxml`**：仅用于 `.doc`/`.xls`/`.ppt` 等需转 Open XML 时。
