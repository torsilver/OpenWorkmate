# WPS / Office：右键菜单与临时输入框可行性说明

本文档整理讨论结论，便于产品与开发对照。依据主要为 [Microsoft Learn - Office Add-ins](https://learn.microsoft.com/en-us/office/dev/add-ins/) 当前公开文档，以及本仓库内 WPS 加载项模板代码。WPS 官方站点部分内容需在浏览器中自行查阅 [WPS 开放平台](https://open.wps.cn/) 并以真机为准。

---

## 1. 与现有「侧边栏对话」的区别


| 维度    | 侧边栏（任务窗格）          | 讨论中的「临时」能力                                                             |
| ----- | ------------------ | ---------------------------------------------------------------------- |
| UI 形态 | 固定在侧栏的 Web 视图，长期存在 | 希望是**体积小**、**临时**、**以当前选区为上下文**的输入/对话                                  |
| 理想交互  | 已有实现               | 产品曾设想类似**聊天气泡**、在**鼠标附近**出现；与 **Office 官方 Dialog API** 能力不完全一致（见第 4 节） |


---

## 2. 产品设想（两条）

### 设想 A：选区 → 对话框 → 预填内容，用户再对 AI 提要求

用户选中一段文本或单元格 → 通过右键（或其它入口）打开小窗 → 窗内已带入选区内容 → 用户补充指令 → 调用 AI → 可选写回文档。

### 设想 B：选区 → 临时小窗 → 多轮对话处理当前选区

在临时窗内进行多轮对话，会话范围主要围绕打开时已确定的选区；关闭窗口即结束本次「临时会话」。

两条设想在技术路径上相近，区别主要在窗内状态复杂度与是否多轮。

---

## 3. 右键菜单扩展：官方能挂在哪里

### Microsoft Office（Office.js 任务窗格加载项）

- 文档：[Add-in commands](https://learn.microsoft.com/en-us/office/dev/add-ins/design/add-in-commands)、[Create add-in commands](https://learn.microsoft.com/en-us/office/dev/add-ins/develop/create-addin-commands)、[OfficeMenu](https://learn.microsoft.com/en-us/javascript/api/manifest/officemenu)。
- 扩展点支持 **ContextMenu**，但 `OfficeMenu` 的 `id` **仅两个合法值**：


| `id`              | 含义                        | 适用                            |
| ----------------- | ------------------------- | ----------------------------- |
| `ContextMenuText` | 用户**选中文本**后，在选中内容上右键出现的菜单 | Word、Excel、PowerPoint、OneNote |
| `ContextMenuCell` | 在**单元格**上右键出现的菜单          | **仅 Excel**                   |


因此：**无法在「任意空白处右键」稳定出现自定义项**；能覆盖的典型场景是 **先选中文本再右键**，以及 **Excel 单元格右键**。

命令可 **ShowTaskpane** 或 **ExecuteFunction**（读选区、打开对话框等），需清单中配置 **VersionOverrides**、**FunctionFile**、图标资源等。本仓库 [office-addin/manifest.xml](office-addin/manifest.xml) 当前为极简 TaskPaneApp，**尚未声明加载项命令**。

### WPS（JS 加载项）

- 使用与 Office 类似的 Custom UI（本仓库 [wps-addin-new/public/ribbon.xml](wps-addin-new/public/ribbon.xml)）。
- 除 `<ribbon>` 外，规范上可并列 `<contextMenus>`，通过 **idMso** 挂到内置右键菜单；**具体 id 与文字/表格/演示各组件是否一致，必须以 WPS 真机验证**（建议 `wpsjs debug`）。
- 回调可与现有 [wps-addin-new/src/components/ribbon.js](wps-addin-new/src/components/ribbon.js) 中 `OnAction` 共用，用 `control.Id` 区分。

---

## 4. 「鼠标附近的聊天气泡」vs 官方对话框 API

### Microsoft Office

已读文档：

- [Use the Office dialog API in Office Add-ins](https://learn.microsoft.com/en-us/office/dev/add-ins/develop/dialog-api-in-office-add-ins)
- [Office.DialogOptions](https://learn.microsoft.com/en-us/javascript/api/office/office.dialogoptions)
- [Office.UI.displayDialogAsync](https://learn.microsoft.com/en-us/javascript/api/office/office.ui?view=common-js#office-office-ui-displaydialogasync-member(1))

结论摘要：


| 诉求                   | 结论                                                                                                                                       |
| -------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- |
| 在**鼠标/选区旁**锚定弹出      | **按文档不支持**。文档写明对话框 **始终在屏幕居中**（*"always opens in the center of the screen"*）。`DialogOptions` 无 `x`/`y` 或相对光标坐标。                          |
| 较小体积的弹窗              | **部分满足**：`height`/`width` 为相对显示的百分比；文档对最终尺寸有**像素下限**描述（如 width 约 **150px**、height 约 **250px** 量级，以 Learn 页面为准）。可做「比侧边栏小」的窗体，但**不是贴边气泡**。 |
| Office 网页版           | `displayInIframe: true` 时为文档区域上的**浮动 IFrame**，体验更轻，但**仍无**相对鼠标定位 API。                                                                    |
| 窗内纯文本 + 调 AI + 仅当前选区 | **可行**：在宿主（function file 或任务窗格）用 `Word.run` / `Excel.run` 读选区，再 `displayDialogAsync`；选区数据宜用存储或短参数传递，**避免超长 URL**。                        |
| 改文档                  | 微软建议**不要在对话框内主导文档编辑**；宜用 `messageParent` 等与宿主通信，由宿主执行 `Word.run` / `Excel.run`。                                                          |


**总结**：在 **Office Web Add-in** 技术栈下，**「严格贴光标的聊天气泡」应视为不可实现**；可降级为 **居中/网页版 iframe 浮动小窗**。

### WPS

- 模板中调用：`Application.ShowDialog(url, title, width, height, modal)`，见 [wps-addin-new/src/components/ribbon.js](wps-addin-new/src/components/ribbon.js)。**未见**坐标参数。
- **更小宽高**的紧凑窗体、仅文本输入、选区经 `PluginStorage` 或 URL 传入，**高概率可行**。
- **是否支持贴鼠标/选区定位**：需查阅 WPS 官方文档或真机试验证，**本文档不断言**。

---

## 5. 设想 A / B 与两端技术映射（简表）


| 环节     | Microsoft Office                               | WPS                            |
| ------ | ---------------------------------------------- | ------------------------------ |
| 触发     | 右键 → `ExecuteFunction`（需清单声明）                  | 右键 → `ribbon.OnAction`         |
| 读选区    | `Word.run` / `Excel.run` 等                     | `Application.Selection` 等（按组件） |
| 打开临时窗  | `Office.context.ui.displayDialogAsync`（**同域**） | `Application.ShowDialog(...)`  |
| 选区传入窗体 | URL 参数（短）、OfficeRuntime.storage、消息通道等          | URL、`PluginStorage` 等          |
| 窗内 AI  | 普通 HTTPS 请求/WebSocket；文档 API 在对话框内受限           | 同类；写回建议在宿主或约定回调                |
| 超时     | **函数命令**约 5 分钟（Add-in commands 文档）；对话框可长期打开    | 依 WPS 行为与 `modal` 实测           |


---

## 6. 本仓库现状与后续落地要点


| 项目                                                                     | 现状                               |
| ---------------------------------------------------------------------- | -------------------------------- |
| [office-addin/manifest.xml](office-addin/manifest.xml)                 | 无 `VersionOverrides`，无右键/功能区命令   |
| [wps-addin-new/public/ribbon.xml](wps-addin-new/public/ribbon.xml)     | 仅有 `<ribbon>`，无 `<contextMenus>` |
| [wps-addin-new/src/router/index.js](wps-addin-new/src/router/index.js) | 已有 `/dialog` 路由，可复用为临时窗页面        |


**Office 落地**：增加 VersionOverrides、各 Host 的 ContextMenu 扩展点、FunctionFile、`Office.actions.associate`、图标资源。

**WPS 落地**：在 `ribbon.xml` 增加 `contextMenus`（idMso 需实测），`OnAction` 分支读选区并 `ShowDialog` 或写 `PluginStorage`。

---

## 7. 建议与兜底

1. 若接受 **「先选中文本 / Excel 单元格再右键」**，两端均可做技术验证。
2. 若必须 **任意区域右键**，Office 官方通道**不满足**；WPS 依赖 idMso 与实测，可能需 **Ribbon + 任务窗格** 兜底。
3. **贴鼠标气泡**：Office **按文档不可行**；WPS **待官方/实测**。产品若坚持该交互，需单独评估是否换技术栈或调整预期（居中/iframe 小窗）。

---

## 8. 后续验证清单（可选）

- Office：最小 PoC — `ContextMenuText` + `ExecuteFunction` + `displayDialogAsync` + 选区传递。
- WPS：`contextMenus` + `OnAction` + `wpsjs debug` 在文字/表格/演示中验证。
- 两端：`messageParent` / 宿主写回或 WPS 侧写回路径。
- WPS：`ShowDialog` 是否支持位置锚定（若官方有文档或新版本 API）。

---

## 9. 参考链接

- [Add-in commands](https://learn.microsoft.com/en-us/office/dev/add-ins/design/add-in-commands)
- [Create add-in commands](https://learn.microsoft.com/en-us/office/dev/add-ins/develop/create-addin-commands)
- [OfficeMenu](https://learn.microsoft.com/en-us/javascript/api/manifest/officemenu)
- [Dialog API](https://learn.microsoft.com/en-us/office/dev/add-ins/develop/dialog-api-in-office-add-ins)
- [Office.DialogOptions](https://learn.microsoft.com/en-us/javascript/api/office/office.dialogoptions)

