# OpenXmlPowerTools 与幻灯片复制（`ppt_slide_duplicate`）说明

> **命名**：文件名保留「OpenXmlPowerTools」便于检索历史讨论；**当前后端未引用任何 `*OpenXmlPowerTools*` NuGet 包**，复制页由 **Open XML SDK** 自研逻辑完成。

---

## 1. 工程现状

| 项 | 说明 |
|----|------|
| **Open XML SDK** | `OfficeCopilot.Server.csproj` 使用 `DocumentFormat.OpenXml` **3.4.1**（与 `net10.0` 一致）。 |
| **NuGet** | **无** `OpenXmlPowerTools`、`Codeuctivity.OpenXmlPowerTools`、`OpenXmlPowerTools.Core` 等引用。 |
| **工具** | `ppt_slide_duplicate` → `PptPlugin.PptSlideDuplicate` → `PptOpenXmlHelpers.EditPresentationInMemory` → **`PptOpenXmlHelpers.DuplicateSlideAfter`**。 |

---

## 2. 为何未采用 OpenXmlPowerTools 系 NuGet

曾希望用 Eric White 一脉的 **`PresentationBuilder` + `SlideSource`** 重建 `.pptx`，以一次处理多类子部件与关系。实际结论：

1. **`OpenXmlPowerTools`（经典包 ID，如 4.5.3.2）**  
   针对 **Open XML SDK 2.x**（如 2.7.2）编译，并依赖 **PowerShell** 相关包。与项目统一的 **SDK 3.4.x** 并跑时，易出现 **`MissingMethodException`** 等运行时 ABI 问题；**同一进程内也不宜再「单独装一份老 OpenXml」** 给该库用。

2. **`Codeuctivity.OpenXmlPowerTools`**  
   依赖 **OpenXml 3.3** 量级、维护活跃，但 **源码中已移除 `PresentationBuilder` / `SlideSource` 等 PPTX 合并实现**（仓库 `OpenXmlPowerTools` 目录下无对应源码），**无法**按旧文档示例调用合并 API。

3. **其他 fork（备忘）**  

| 包 | OpenXml 代数 / 备注 |
|----|---------------------|
| `OpenXmlPowerTools.Core` | 声明 **≥ 3.0**；NuGet 长期仅 **1.0.0**，功能面需单独核对是否含所需合并能力。 |
| `OpenXmlPowerTools-Net6` | **2.19.x**，与全栈 **3.x** 统一版本冲突风险大。 |
| `OpenXmlPowerTools-NetStandard` | **2.8.x** 线，偏旧。 |

---

## 3. 当前 `ppt_slide_duplicate` 实现要点（自研）

在 **`PresentationPart` 内**、对指定 **1-based 幻灯片序号**：

1. **`Slide.CloneNode(true)`** 得到幻灯片 XML 副本。  
2. 遍历源 **`SlidePart.ImageParts`**：在新 **`SlidePart`** 上 **`AddImagePart` + `FeedData`**，并用 **`GetIdOfPart`** 建立 **旧关系 id → 新关系 id** 映射。  
3. **`RemapBlipEmbeds`**：在克隆树上遍历 **`DocumentFormat.OpenXml.Drawing.Blip`**，重写 **`r:embed`**，使嵌入图指向新部件。  
4. **`newPart.AddPart`** 复用源 **`SlideLayoutPart`**；在 **`SlideIdList`** 中于源 **`SlideId` 之后** 插入新页。

**单元测试**：`backend.Tests/Unit/PptPluginOpenXmlTests.cs`（含纯文本复制、**带嵌入图复制**及 OpenXml 校验等）。

---

## 4. 能力边界

| 类型 | 说明 |
|------|------|
| **已覆盖** | 常见 **栅格嵌入图**（`ImagePart` + **`a:blip/@r:embed`**）。 |
| **未保证** | **ChartPart**、**音视频**、**OLE**、**VML 老图**、**备注页 `NotesSlidePart` 上的图** 等；失败时以工具返回或日志为准。 |

---

## 5. 后续若仍要「整库级合并幻灯片」

- 寻找 **仍提供 `PresentationBuilder`（或等价能力）且声明依赖 OpenXml 3.x** 的 fork，做 **PoC** 并跑通 **`PptPluginOpenXmlTests`**；或  
- **独立辅助进程** 加载旧 SDK + 旧 PowerTools（部署与排错成本高）；或  
- 在本仓库 **按需扩展** `DuplicateSlideAfter`（例如按关系类型枚举并复制 **ChartPart** 等）。

本文档随 **`ppt_slide_duplicate` 实现或依赖策略变更** 时应同步更新。
