# Windows 打包（`windows-packaging`）

本目录收口 **Windows 下一键构建** 与 **WiX MSI 安装包**（可选）。不在仓库根增加打包入口脚本。

**手工打包步骤（先决条件、分步、仅 MSI）**：见 **[打包说明.md](./打包说明.md)**。

## 先决条件（构建机）

- **.NET SDK**（与仓库一致，当前为 .NET 10）
- **Node.js + npm**（构建 `office-addin` 无额外 build 时仅需复制；`wps-addin-new` 需 `npm run build`）
- **OpenSSL**（可选：首次打 Chrome CRX 时若 `keys/taskly-chrome-extension.pem` 不存在，可自动生成；也可手动放置 PEM）
- **WiX Toolset CLI**（**默认会打 MSI**，需本机可用 `wix`）：推荐安装全局工具  
  `dotnet tool install --global wix`  
  安装后**新开终端**，确保能运行 `wix --version`。若只想生成 `stage`、不要 MSI，运行脚本时加 **`-BuildInstaller:$false`**。

**AI 后台双目标框架**：`backend/OfficeCopilot.Server.csproj` 同时编 `net10.0-windows`（托盘、Office Interop 等）与 `net10.0`（无上述专用代码）。打包脚本默认前者；可选 `-BackendTargetFramework net10.0`。**`-FrameworkDependent`** 表示自包含 vs 依赖本机 .NET 运行时，与选哪个 TFM 无关（详见 **`打包说明.md`**）。

## 一键打包

在**仓库根**执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows-packaging\scripts\Pack-Taskly.ps1
```

常用参数：

| 参数 | 说明 |
|------|------|
| `-FrameworkDependent` | 框架依赖发布（不加则默认 **自包含**） |
| `-BackendTargetFramework` | AI 后台 TFM：`net10.0-windows`（默认）或 `net10.0` |
| `-SkipChrome` | 跳过 CRX / `updates.xml`（调试后端与加载项时） |
| `-BuildInstaller` | 默认 **`$true`**（生成 **`windows-packaging/dist/Taskly.msi`**）；**`-BuildInstaller:$false`** 跳过 WiX、只打 stage |
| `-StaticHostPort 3000` | 安装版 Office manifest 与 Chrome 更新 URL 中的 HTTPS 端口（默认 3000） |

输出目录：**`windows-packaging/stage/`**（中间产物：后端、Gateway、`Taskly.StaticHost`、Office/WPS 静态文件、`taskly-chrome` 等）。

**MSI**：默认安装到 **`Program Files\Taskly`**（`perMachine`），通常需要管理员权限。

- **Start Taskly**：以**隐藏窗口**启动 Gateway、HTTPS 静态站；AI 后台使用 **`--tray` 托盘模式**（不再弹出多个黑色控制台窗口；若仍偶现闪窗，与 .NET 控制台子进程有关，可再迭代）。
- **Taskly client setup (Office Chrome WPS)**：打开本机说明页 `Taskly-ClientSetup.html`，按步骤完成 Office 旁加载、Chrome 策略脚本、WPS 指引。**MSI 无法在无交互下自动完成浏览器/Office 商店式安装**，需各端一次性配置。
- **Install-ChromePolicy.ps1**（打包含 CRX 时生成）：在安装目录运行一次，写入 Chrome `ExtensionInstallForcelist`（需先启动 Taskly 以便拉取 `updates.xml`）。

本机试跑（不打安装包时）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows-packaging\stage\Start-Taskly.ps1
```

- **Office**：在 Word/Excel 中使用「上传我的加载项」选择 `stage/office-addin/manifest-install.xml`。
- **HTTPS 证书**：`Taskly.StaticHost` 默认使用本机 **dotnet 开发证书**（`localhost`）。若浏览器或 Office 不信任，请执行：`dotnet dev-certs https --trust`。
- **Chrome（方案 A）**：启动后访问 `https://localhost:3000/taskly-chrome/updates.xml` 应可下载清单；按 `stage/Chrome-policy-hint.txt`（若已生成）注册 `ExtensionInstallForcelist`，并在 `chrome://policy` 确认。

### 单独编译 MSI（已有 stage）

与 `Pack-Taskly.ps1` 中 WiX 参数一致（**需要 UI 扩展 + launcher bindpath**）。在仓库根将 `$root` 换成你的路径，或见 **`打包说明.md`**。

```powershell
$root = $PWD   # 或 d:\CodeBase\_Taskly
wix build "$root\windows-packaging\installer\Taskly.wxs" `
  -ext WixToolset.UI.wixext `
  -bindpath "stage=$root\windows-packaging\stage" `
  -bindpath "launcher=$root\windows-packaging\installer\launchers" `
  -arch x64 `
  -out "$root\windows-packaging\dist\Taskly.msi"
```

## 子脚本

- `scripts/Build-ChromeCrx.ps1`：单独打 CRX + `updates.xml`（由 `Pack-Taskly.ps1` 调用）。
- `scripts/node-tools/`：`crx` 与 `pack-crx.cjs`，首次由脚本自动 `npm install`。

## 密钥

- **`keys/taskly-chrome-extension.pem`**：Chrome 扩展签名私钥，**勿提交**（已在 `.gitignore`）。
- 扩展 ID 由私钥决定；更换密钥后需同步策略与 `user-config.json` 中的 `chromeExtensionId`。

## 策略脚本

- `policies/Set-TasklyChromeExtensionPolicy.ps1`：写入当前用户或本机（`-MachineScope`）的 `ExtensionInstallForcelist`。
- `policies/Remove-TasklyChromeExtensionPolicy.ps1`：仅移除 Taskly 对应条目。

## 运行时说明

- **`Taskly.StaticHost`**：单一本机 HTTPS 静态站，根路径服务 Office 加载项；`/wps` 服务 WPS 构建产物；`/taskly-chrome` 服务 CRX 与 `updates.xml`。
- 安装版配置由打包时生成的 `Taskly.StaticHost/appsettings.Pack.json`（与 `appsettings.json` 合并加载）指定相对路径。

## 关于 WiX

[WiX Toolset](https://wixtoolset.org/) 是 .NET 基金会下的 **Windows 安装包（MSI）** 工具链，与 Visual Studio / Windows 安装生态常见搭配一致；本仓库用其替代第三方 `Setup.exe` 打包器，便于企业侧用 **组策略分发 MSI** 等标准方式部署。
