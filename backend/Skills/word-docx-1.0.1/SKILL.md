---
name: Word / Docx
version: 1.0.3
description: Open XML / DOCX structure (runs, styles, sections, fields). Use with Taskly Word kernel tools for create-edit; load word_cn_default_formal for Chinese formal layout defaults (GB/T 9704 preset). For legacy .doc/.dot inputs before Open XML tools, load office_legacy_to_openxml.
changelog: Cross-link to office_legacy_to_openxml skill.
metadata: {"clawdbot":{"emoji":"📘","os":["linux","darwin","win32"]}}
---

## Taskly（本仓库）

- **旧版 Word 二进制（`.doc` / `.dot`）**：Open XML 工具（如 `word_body_read`）**不会**直接读这些扩展名。须先按需 **`load_user_skill_instructions`**（`skillId` 填 **`office_legacy_to_openxml`**），按该技能调用 **`office_legacy_save_as_open_xml`** 得到 `.docx` 后，再使用下文所述 **`word_*`** 与 OOXML 知识；Excel `.xls`、PPT `.ppt` 同理见该技能。
- **落盘与改稿**：优先用服务端 **Word 插件** 工具（如 `word_document_create`、CurrentDocument 系列），而不是手改 ZIP 内的 `document.xml`，除非你是在做互通性排查或离线批处理。
- **`word_document_create`**：`paragraphs` 用 Markdown（`|` / 空行 / `#` / 列表）；可选 `documentPreset`（`default` 与 `cnGovGbt9704`）；不要把 JSON 数组字面量或抓取到的转义串整块当正文（会被拒绝写盘）。
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
