---
name: Word / Docx
version: 1.0.6
description: Open XML / DOCX structure (runs, styles, sections, fields). Use with Taskly Word kernel tools for create-edit; load word_cn_default_formal for Chinese formal layout defaults (GB/T 9704 preset). For legacy .doc/.dot inputs before Open XML tools, load office_legacy_to_openxml. PDF 抽取/合并见技能 Pdf / Pdf（get_pdf_text、pdf_merge），勿用 office_legacy 处理 .pdf。
changelog: word_document_create paragraphs is string[]; no server-side JSON-dump rejection (rely on shape guidance + parser).
metadata: {"clawdbot":{"emoji":"📘","os":["linux","darwin","win32"]}}
---

## Taskly（本仓库）

- **PDF 附件**：抽取文本、合并多份 PDF、简单纯文本生成 PDF → 按需加载技能 **`Pdf / Pdf`**（`get_attachment_path` → **`get_pdf_info`** / **`get_pdf_text`** / **`pdf_merge`** / **`pdf_document_create`**）。**.pdf 不要**走 `office_legacy_*`。
- **旧版 Word 二进制（`.doc` / `.dot`）**：Open XML 工具（如 `word_body_read`）**不会**直接读这些扩展名。须先按需 **`load_user_skill_instructions`**（`skillId` 填 **`office_legacy_to_openxml`**），按该技能调用 **`office_legacy_save_as_open_xml`** 得到 `.docx` 后，再使用下文所述 **`word_*`** 与 OOXML 知识；Excel `.xls`、PPT `.ppt` 同理见该技能。
- **落盘与改稿**：优先用服务端 **Word 插件** 工具（如 `word_document_create`、CurrentDocument 系列），而不是手改 ZIP 内的 `document.xml`，除非你是在做互通性排查或离线批处理。
- **`word_document_create`**
  - **`paragraphs`（形状约定）**：**`string[]`**。每项为 Markdown 片段（`|`、空行、`#` / `##` / `###`、`- `）；多项并列等价于旧版单字符串里用 `|` 分段。应**避免**在**单个数组元素**内整段粘贴 JSON 字符串数组字面量或含大量 `\"` / `\",\"` 的抓取转储——否则整段会落成一段正文，版式与可读性差；优先拆成多项自然语言 Markdown。
  - **推荐形态示例**：`["# 标题\n摘要一段。", "## 第二节\n- 要点甲\n- 要点乙"]`（或一项内仍用 `|` 细分）。
  - **`documentPreset`**：`default` 或 `cnGovGbt9704`（中文正式稿须配合技能 `word_cn_default_formal`）。
- **中文正式稿版式**（页边距、仿宋/层次、行距等操作约定）：用内置技能 **`word_cn_default_formal`**（`load_user_skill_instructions`）与本技能的 **OOXML 结构知识** 互补——本段不写国标细则，避免与那边重复。

以下正文侧重 **Open XML 结构与常见坑**，英文表述便于对照官方与第三方文档。

## Structure

- DOCX is a ZIP containing XML files—`word/document.xml` has main content, `word/styles.xml` has styles
- Text splits into runs (`<w:r>`)—each run has uniform formatting; one word may span multiple runs
- Paragraphs (`<w:p>`) contain runs—never assume one paragraph = one text block
- Sections control page layout—headers/footers, margins, orientation are per-section

## Styles vs Direct Formatting

- Styles (Heading 1, Normal) are named and reusable—direct formatting is inline and overrides style
- Removing direct formatting reveals underlying style—useful for cleanup
- Character styles apply to runs, paragraph styles to paragraphs—they layer together
- Linked styles can be both—applying to paragraph or selected text behaves differently

## Lists & Numbering

- Numbering is complex: `abstractNum` defines pattern, `num` references it, paragraphs reference `numId`
- Restart numbering not automatic—need explicit `<w:numPr>` with restart flag
- Bullets and numbers share the numbering system—both use `numId`
- Indentation controlled separately from numbering—list can exist without visual indent

## Headers, Footers, Sections

- Each section can have different headers/footers—first page, odd, even pages
- Section breaks: next page, continuous, even/odd page—affects pagination
- Headers/footers stored in separate XML files—referenced by section properties
- Page numbers are fields, not static text—update on open or print

## Track Changes & Comments

- Track changes stores original and revised in same document—accept/reject to finalize
- Deleted text still present with `<w:del>` wrapper—don't assume visible = all content
- Comments reference ranges via bookmark IDs—`<w:commentRangeStart>` to `<w:commentRangeEnd>`
- Revision IDs track who changed what—metadata persists even after accepting

## Fields & Dynamic Content

- Fields have code and cached result—`{ DATE \@ "yyyy-MM-dd" }` vs displayed date
- TOC, page numbers, cross-references are fields—update fields to refresh
- Hyperlinks can be fields or direct `<w:hyperlink>`—both valid
- MERGEFIELD for mail merge—placeholder until merge executes

## Compatibility

- Compatibility mode limits features to earlier Word version—check `w:compat` settings
- Page size defaults vary by tool and region—set US Letter vs A4 explicitly or pagination and table widths can drift
- LibreOffice/Google Docs: complex formatting may shift—test roundtrip
- Embedded fonts may not transfer—fallback fonts substitute
- DOCM contains macros (security risk); DOC is legacy binary format

## Common Pitfalls

- Empty paragraphs for spacing—prefer space before/after in paragraph style
- Manual page breaks inside paragraphs—use section breaks for layout control
- Images in headers: relationship IDs are per-part—same image needs separate relationship in header
- Copy-paste brings source styles—can pollute style gallery with duplicates
