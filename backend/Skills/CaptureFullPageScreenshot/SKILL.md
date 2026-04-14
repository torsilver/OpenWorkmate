---
name: CaptureFullPageScreenshot
title: 整页截图保存到下载
description: 当用户要求将当前浏览器标签页整页截图并保存到下载文件夹时使用此技能。
---

当用户要求截取当前页面整页并保存到下载时，请按顺序执行以下步骤，不要将截图数据（base64）放入对话。
1. 先调用 **`Browser`** 插件工具 **`capture_full_page`**（无参数），获取返回内容中的截图引用（格式为 screenshot:xxx）。
2. 再调用 **`File`** 插件工具 **`save_screenshot_to_downloads`**，第一个参数传入上一步得到的截图引用（即 screenshot:xxx），第二个参数可为空（使用默认文件名）或按用户指定的文件名。
3. 若某一步失败，根据工具返回的中文信息简要告知用户原因。
4. 成功后告知用户：「已保存到 [路径]」。
