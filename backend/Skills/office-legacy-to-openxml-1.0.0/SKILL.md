---
name: office_legacy_to_openxml
title: Office 旧格式转 Open XML
version: 1.0.1
description: 当路径为 .doc/.dot/.xls/.ppt 且需用 Word/Excel/PPT 内核工具处理时，先调用 office_legacy_save_as_open_xml（本机已装 Microsoft Office），再对输出的 .docx/.xlsx/.pptx 使用 Open XML 工具；失败时原样展示工具返回并引导用户手动另存为。
changelog: 与 word-docx 技能交叉引用。
metadata: {"clawdbot":{"emoji":"📄","os":["win32"]}}
---

## Taskly（本仓库）

### 何时必须先转换

- 用户或附件路径为 **`.doc`、`.dot`、`.xls`、`.ppt`**（Office 97–2003 二进制），且后续要用 **`word_*` / `excel_*` / `ppt_*`** 等 **仅支持 Open XML** 的内核工具时。
- **不要**对 `.docx` / `.xlsx` / `.pptx` 调用 `office_legacy_save_as_open_xml`（工具会报错）；直接走对应 Open XML 工具即可。

### 推荐顺序

1. 调用 **`office_legacy_save_as_open_xml`**（插件 `OfficeLegacy`），参数 `inputPath` 为本地完整路径（可用 `get_attachment_path` 或用户给出的路径）；`outputPath` 可省略（同目录生成 `原名_converted.docx` 等）。
2. 若返回以 **`已转换并保存到:`** 开头，提取其中的路径，再调用 **`word_body_read` / `excel_range_read` / `ppt_slides_list`** 等继续任务。
3. 若返回以 **`[错误]`** 开头：**完整转述给用户**（含 COM/超时/未安装 Office 等说明），并建议用户在对应 Office 应用中 **「另存为」** `.docx` / `.xlsx` / `.pptx` 后重试。

### 与 Word / Docx 技能的关系

- 转成 **`.docx`** 之后，若任务涉及 **Open XML 结构、样式、字段、分节** 等读写策略，可再按需 **`load_user_skill_instructions`**，`skillId` 填 **`Word / Docx`**（内置技能 `word-docx-1.0.1`），与 **`word_cn_default_formal`** 等搭配方式见该技能正文开头说明。

### 前提与限制

- **Windows** + 后台为 **net10.0-windows** 构建；用户本机需安装 **Microsoft Word / Excel / PowerPoint** 中对应组件。
- 转换可能短暂启动 Office 进程；大文件可调大 `timeoutMs`（默认 90000）。
- **宏、加密、严重损坏文件** 可能失败；以工具返回为准，勿编造成功。
