---
name: PPT / Pptx
slug: ppt-pptx
version: 1.2.0
description: Create and edit PowerPoint (.pptx/.pptm) via Open XML on disk (Chrome) or current deck via task pane. Includes document create, structured read/write, images, notes, reorder, tables, hyperlinks, duplicate slide.
metadata: {"clawdbot":{"emoji":"📊","os":["linux","darwin","win32"]}}
---

## When to Use

User needs to **create** a new deck or **read/write** slides in a file path (.pptx, .pptm) from **Chrome** (backend OpenXml), or work on the **current** presentation from **Office PowerPoint / WPS 演示** task pane (`current_ppt_*`).

- **New file on disk**: always call **`ppt_document_create`** first. Do **not** use `run_command` or shell redirection to create `.pptx` (invalid OOXML).
- **Edit existing file**: `ppt_slide_read` with shape list → `ppt_slide_write` with `shapeIndex` or `placeholderType`.

## Structure

- Slide order = **`Presentation.SlideIdList`** order (not arbitrary part enumeration).
- Slide index in tools is **1-based**.
- Text lives under DrawingML `a:t` inside shapes.

## File vs Current Document

| Client | File path (backend) | Task pane |
|--------|---------------------|-----------|
| Chrome | Full **`Ppt`** plugin: `ppt_document_create`, list/read/write/insert/delete, image, notes, reorder, table, hyperlink, duplicate | N/A |
| Office PPT | Not exposed | `current_ppt_*` → `ppt_*` RPC：幻灯片列表/读写/插入/删、插图、重排、表格、超链、复制；**演讲者备注**在 PowerPoint 任务窗格不可用（Office.js 限制），用 Chrome+文件路径或 WPS |
| WPS 演示 | Not exposed | 同上；**备注读写**可用；`ppt_document_create` 可 `SaveAs` 到路径 |

## Tool summary (file path)

| Tool | Role |
|------|------|
| `ppt_document_create` | New deck; overwrites if exists |
| `ppt_slides_list` / `ppt_slide_read` | Inspect; `includeShapeDetails` for shape indices |
| `ppt_slide_write` | `shapeIndex` / `shapeName` / `placeholderType` (title, body, subtitle, ctrTitle) |
| `ppt_slide_insert` | Clones anchor slide then fills title/body; `position`: 0 = first; k = after slide k; ≥ count = append. On **minimal** template (title-only layout), body text is stored as **extra paragraphs in the title shape**—`ppt_slide_read` still shows all text; use `ppt_slide_write(..., body)` only if that slide has a real body placeholder. |
| `ppt_slide_delete` | Remove slide by index |
| `ppt_slide_image_add` | Local image → slide |
| `ppt_notes_read` / `ppt_notes_write` | Speaker notes |
| `ppt_slides_reorder` | Permutation string e.g. `3,2,1` |
| `ppt_table_create` / `ppt_table_write_cells` | Simple grid; cells `row1a,row1b\|row2a` |
| `ppt_hyperlink_add` | Clickable URL on shape’s first run |
| `ppt_slide_duplicate` | Clone slide after source (copies slide `ImagePart`s + remaps `blip/@embed`; charts/media not fully covered) |

## Format Limits

- **PPTX / PPTM** only. **.ppt** binary → ask user to save as .pptx/.pptm.
- Charts/SmartArt heavy edits → not supported here; use dedicated tools or scripts.

## Common Pitfalls

- Skipping **`ppt_document_create`** then calling `ppt_slide_insert` → file missing error.
- After `ppt_slide_insert` on a **title-only** deck, editing “body” with `ppt_slide_write` may require `shapeIndex` from `ppt_slide_read` (body merged into title box).
- `ppt_table_write_cells`: commas inside cell text break parsing unless you avoid commas in content.
- Duplicate slide + images → use insert + manual copy or avoid duplicate tool.
