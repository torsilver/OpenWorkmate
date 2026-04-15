---
name: Excel / XLSX
slug: excel-xlsx
version: 1.0.2
description: Taskly Excel Open XML（.xlsx/.xlsm）读写：区域读写、公式、合并单元格、数据验证、条件格式、超链、工作表与图表列表。.xls 须先 office_legacy 转 .xlsx。检索：表格 xlsx 工作表 公式 合并 验证。
changelog: Taskly 工具链对齐；移除 ClawHub 专用段落
metadata: {"clawdbot":{"emoji":"📗","requires":{"bins":[]},"os":["linux","darwin","win32"]}}
---

## Taskly（本仓库）

- **插件**：**`Excel`**。工具均以 **`excel_`** 为前缀；路径为**本机完整路径**（附件请先用 **`get_attachment_path`**）。
- **旧版 `.xls`**：Open XML 工具**不会**当普通 `.xlsx` 处理。须按需 **`load_user_skill_instructions`**（`skillId`：**`office_legacy_to_openxml`**），调用 **`office_legacy_save_as_open_xml`** 得到 **`.xlsx`** 后，再使用下文 **`excel_*`**。
- **扩展名**：读写目标须 **`.xlsx`** 或 **`.xlsm`**；**勿**将 `.md`/`.txt`/`.csv`/`.xls` 当作输出扩展名（`excel_range_write` 会校验并可能规范化路径）。
- **单元格地址**：一律 **A1 字符串**（如 `startCell`=`A1`，`endCell`=`D10`），与工具参数一致；**勿**与「0/1 基列号」混用。
- **常用流程**：`excel_sheets_list` → `excel_range_read` / `excel_range_write`；大表用 `excel_range_read` 的 **`maxRows`** 或收紧 **`endCell`** 控制体积。
- **`excel_range_write`**：参数 **`data`** 为合法 **JSON 二维数组**（双引号、标准 JSON）；文件不存在时会**创建**工作簿。具体错误以工具返回的 **`[错误]`** 文案为准并**转述用户**。
- **其它工具（按需）**：`excel_formula_write`、`excel_cells_merge` / `excel_cells_unmerge`、`excel_named_ranges_*`、`excel_column_width_set`、`excel_row_height_set`、`excel_validations_*`、`excel_conditional_format_*`、`excel_hyperlink_set`、`excel_sheet_add` / `excel_sheet_remove`、`excel_charts_list`。
- **与 PDF**：从 PDF 取数见技能 **`Pdf / Pdf`**（`get_pdf_text` 等）；结构化落表仍用 **`excel_*`**。

以下「通用 Excel / OOXML 知识」便于推理；**实际操作以工具描述与返回为准**。

## Core rules

### 1. Dates are serial numbers

Excel stores dates as days since 1900-01-01 (Windows) or 1904-01-01 (Mac legacy). Check workbook date system before converting. Time is fractional: 0.5 = noon, 0.25 = 6 AM.

### 2. The 1900 leap year bug

Excel incorrectly treats 1900 as a leap year. Serial 60 represents Feb 29, 1900 (invalid date). Account for this when calculating dates before March 1, 1900.

### 3. 15-digit precision limit

Numbers beyond 15 digits silently truncate. Use TEXT format for: phone numbers, IDs, credit cards, any long numeric identifiers. Leading zeros also require TEXT.

### 4. Formulas vs cached values

Cells may contain both formula and cached result. Use `excel_range_read` with **`includeFormulas: true`** when you need the formula string; otherwise you get displayed/cached values.

### 5. Merged cells

Only the top-left cell of a merged range holds the value. Reading other cells in the merge returns empty. Use `excel_cells_merge` / `excel_cells_unmerge` and re-read if layout changes.

### 6. Cross-platform consumers

Windows vs Mac Excel can differ in date system. LibreOffice/Google Sheets may not support all features. When the file is for an unknown consumer, prefer conservative formulas and formats.

### 7. Large sheets

Prefer bounded reads (`maxRows`, explicit `endCell`) instead of dumping entire sheets into the model context.

## Common traps

- **Type inference on read** → Numbers stored as text stay text; may need explicit conversion before numeric validation or conditional formats that compare numbers.
- **Newlines in cells** → `\n` may need wrap formatting in Excel for display.
- **External references** → `[Book.xlsx]Sheet!A1` breaks when the source file moves.
- **Password protection** → Not the same as encryption at rest; tool errors must be surfaced to the user.
- **XLSM** → Contains macros (security risk); only use when needed.
- **XLS** → Legacy binary; convert via **`office_legacy_to_openxml`** first.

## Related skills

- **`office_legacy_to_openxml`**：`.xls` → `.xlsx`。
- **`Word / Docx`**：报告类排版与长文档。
- **`Pdf / Pdf`**：从 PDF 抽取文本再入表。
