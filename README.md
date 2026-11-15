# WinKit

**一个可以持续添加实用小功能的 Windows 桌面工具箱。** 首版提供 NUL 文件清理：在指定文件夹中查找资源管理器难以正常删除的真实 `nul` 文件，经用户勾选和确认后永久删除。

技术栈：**C# / .NET 10 LTS / WPF**，Windows 10/11 x64。无第三方运行时包、无需账号、离线运行、默认普通用户权限。

## 使用

运行发布目录中的 `WinKit.exe`，无需安装 .NET。

1. 输入/粘贴文件夹路径，或点击 **浏览…** 打开系统文件夹选择对话框。
2. **开始扫描**，递归检查子目录。后台扫描可取消，不修改文件。
3. 检查完整文件路径。**默认不勾选**，可单选、全选或取消全选。
4. 点击 **删除所选**，确认永久删除；确认框默认为“否”。
5. 查看逐项状态。失败、占用、只读、拒绝删除均会保留原因，可查看操作记录。

> **删除不经过回收站，无法通过本工具撤销。** 仅处理精确名字 `nul` 的普通文件（忽略大小写），不处理 `null`、`nul.txt`、名为 `nul` 的目录或其他保留设备名称。不会自动提权、解除占用或清除只读属性。

## 界面和扩展

左侧工具导航，右侧各工具独立工作区；公共入口包含 **操作记录** 和 **设置**。顶部可随时切换 **浅色 / 夜间主题**，也可在设置页选择夜间主题；立即生效并记住选择，不影响扫描结果和勾选状态。设置还可选择是否记住上次文件夹，并查看本机日志位置。已有工具按实际分类展示，不放空的“敬请期待”。

应用内容和支持的 Windows 标题栏跟随主题。系统文件夹选择对话框、永久删除确认框由 Windows 绘制，外观仍依系统设置，不会为了主题更换原有安全确认流程。

配置和记录默认位于 `%LOCALAPPDATA%\WinKit`，仅保存在本机。

- [`src/WinKit.Core/`](src/WinKit.Core/)：原生安全文件操作，不依赖 WPF。
- [`src/WinKit.Desktop/`](src/WinKit.Desktop/)：应用外壳、工具注册、视图/视图模型和公共桌面服务。
- [`tests/WinKit.Core.Tests/`](tests/WinKit.Core.Tests/)：真实 Windows 文件回归测试。
- [`tests/WinKit.Desktop.Tests/`](tests/WinKit.Desktop.Tests/)：UI Automation 操作实际窗口和确认框。
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)：分层、添加工具和安全约束。
- [`docs/VALIDATION.md`](docs/VALIDATION.md)：验证方式及已知边界。
- [`docs/PUBLISHING.md`](docs/PUBLISHING.md)：公开推送检查、敏感信息处理与忽略范围。

## 开发与编译

需要 Windows 和 .NET 10 SDK。可以在仓库内准备固定版本 **10.0.404**，不修改系统 SDK 或 PATH：

```powershell
.\scripts\setup-sdk.ps1
.\scripts\build.ps1
.\scripts\test.ps1
```

本地 SDK 存放在 Git 忽略的 `.tools` 目录，下载后校验官方 SHA512。

启动开发版：

```powershell
.\src\WinKit.Desktop\bin\Release\net10.0-windows\WinKit.exe
```

发布自包含单文件和绿色版压缩包：

```powershell
.\scripts\publish.ps1
.\scripts\test-desktop.ps1
.\scripts\release.ps1
```

- EXE 输出：`artifacts/publish/win-x64/WinKit.exe`
- 绿色版：`artifacts/release/WinKit-0.1.1-win-x64.zip`，包含 EXE、中文使用说明和 SHA256。
- 生成物、SDK、bin/obj 不提交到 Git。

自包含运行时使 EXE 体积明显大于纯业务代码。首次启动可能将原生运行时组件解压到 .NET 本地缓存。“单文件交付”不表示运行期间不创建配置、日志或缓存。当前未做代码签名，Windows 可能提示未知发布者。

## 安全策略

- 严格的绝对路径和边界校验，不接受原始设备路径及歧义目录名。
- 不跟随符号链接、目录联接或其他重解析点，包括所选根的祖先。
- 相对已验证的原生目录句柄打开子项，持有目录句柄防路径替换。
- 删除前核对最新扫描记录、根目录和目标文件身份，通过验证后的文件句柄删除，不执行拼接 shell 删除命令。
- 文件替换或安全检查失败时拒绝操作，不降级为不安全删除。
- 扫描失败、超限、取消或无法访问目录都会明确显示，不冒充完整扫描。

完整实现限制与已验证范围以 [`docs/VALIDATION.md`](docs/VALIDATION.md) 和 [`核心安全说明`](src/WinKit.Core/SAFETY.md) 为准。

仓库自带 `NuGet.Config`，仅在编译/发布时使用官方 NuGet 源下载 Microsoft 构建及自包含运行时包，不修改用户全局 NuGet 配置。程序运行不访问此源。
