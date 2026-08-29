# Open in Obsidian

> 双击任意 `.md` 文件，Obsidian 直接打开**这个文件本身**——不弹窗、不闪黑框、不用重启。

**简体中文** | [English](README_EN.md)

[![Windows](https://img.shields.io/badge/platform-Windows-blue)]() [![No dependencies](https://img.shields.io/badge/dependencies-none-green)]() [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

如果你在 Windows 上用 Obsidian，大概率遇到过下面这两个让人抓狂的问题：

**问题一：双击 `.md` 文件，打开的却是「上次浏览的文件」**

Obsidian 会忽略命令行传入的文件路径，启动后直接恢复上一次的工作区。所以哪怕你在资源管理器里双击的是 `会议记录.md`，弹出来的可能还是昨天看的 `购物清单.md`。这是 Obsidian 的[已知设计限制](https://forum.obsidian.md/t/open-file-from-explorer-opens-last-opened-file/)，不是你的系统坏了。

**问题二：想用脚本修，结果每次双击都闪一个命令行黑框**

用 PowerShell 包装可以解决问题一，但 PowerShell 是控制台程序，`-WindowStyle Hidden` 也挡不住注册表 shell 调用时先闪一下黑框；换 wscript 又会被安全策略当 LOLBin 拦截。

## 本项目的方案

利用 Obsidian 官方的 URI 协议 `obsidian://open?path=...`（它能精确打开指定文件），配合一个**用 C# 现场编译的无窗口 GUI 程序**做转发：

```
双击 .md 文件
     │
     ▼
Windows 文件关联（Obsidian.md ProgId）
     │
     ▼
OpenInObsidian.exe   ← GUI 子系统程序，天生无控制台窗口，零闪烁
     │  读取 %APPDATA%\obsidian\obsidian.json，判断文件在不在某个 vault 内
     │
     ├─ 在 vault 内 → URL 编码路径，派发官方协议
     │     obsidian://open?path=E%3A%5C%E6%96%87%E6%A1%A3%5Clinux%E5%91%BD%E4%BB%A4.md
     │     → Obsidian 打开并跳转到该文件 ✅
     │
     └─ 在 vault 外 → 自动回落到本地编辑器打开 ✅
           （fallback-editor.txt 指定的程序 → Typora → VS Code → 记事本）
```

### 特点

- **零依赖**：不需要下载任何东西，用 Windows 自带的 .NET Framework 编译器现场编译一个小巧的转发程序（约 200 行源码，含 vault 检测与回落逻辑）——源码就在 `src/`，装的是什么一目了然
- **零弹窗**：`/target:winexe` 编译的 GUI 程序，没有控制台窗口，什么都不闪
- **vault 外文件自动回落**：双击不在任何 vault 里的 `.md`（比如随手下载的），会自动改用 Typora / VS Code / 记事本打开——因为 Obsidian 官方协议打不开 vault 外文件（可在 `fallback-editor.txt` 自定义，见常见问题）
- **即时生效**：安装后调用 `SHChangeNotify` 通知 Explorer，**无需重启/注销**
- **仅改当前用户注册表（HKCU）**：不要管理员权限，卸载脚本一键还原

## 安装

前提：Windows 10/11 + 已安装 Obsidian（任意安装方式，含绿色便携版）+ .NET Framework（系统自带）。

在仓库根目录打开 PowerShell：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

脚本会自动：

1. 定位 Obsidian.exe（优先读 Obsidian 自己注册的 `obsidian://` 协议处理器路径，找不到时尝试常见安装目录；也可以 `-ObsidianPath "D:\path\to\Obsidian.exe"` 手动指定）
2. 编译 `src\OpenInObsidian.cs` → `%LOCALAPPDATA%\OpenInObsidian\OpenInObsidian.exe`
3. 注册文件关联并设为 `.md` 默认打开方式
4. 通知 Explorer 立即生效

装完双击一个 vault 里的 `.md` 试试。vault 外的 `.md` 会自动回落到 Typora / VS Code / 记事本打开；想指定其他编辑器，把它的完整路径写进 `%LOCALAPPDATA%\OpenInObsidian\fallback-editor.txt`（一行）即可。

> **如果双击还是打开别的程序**：说明你之前给 `.md` 设置过的默认应用（受 Windows ACL 保护的 UserChoice 键）优先级更高。右键任意 `.md` → 打开方式 → 选择其他应用 → 选 **Markdown File (Obsidian)** 并勾选「始终使用此应用」，一次即可。

## 卸载

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1
```

会移除文件关联、还原 `.md` 之前的默认设置（安装时自动备份），并询问是否删除编译出的 exe。

## 已知限制

- **vault 外文件不能用 Obsidian 本体编辑**。`obsidian://open?path=` 是 Obsidian 官方协议的硬限制。本项目对此做了兜底：vault 外的 `.md` 会自动改用回落编辑器打开（`fallback-editor.txt` 指定的程序，否则 Typora → VS Code → 记事本）。如果你想把整个磁盘目录都纳入 Obsidian，也可以直接把目录添加为 vault。想用 Obsidian 编辑任意孤立文件，可以看下面替代方案里 ObsidianShell 的 VaultRecent 模式
- 双击后 Obsidian 不会自动最小化到后台再跳出来——它会把窗口带到前台并打开文件，这属于 Obsidian 自身行为
- 仅支持 Windows（macOS / Linux 的文件关联机制完全不同）

## 替代方案

这个项目不是第一个解决该问题的工具，按需求选合适的就好：

| | 本项目 (open-in-obsidian) | [ObsidianShell](https://github.com/Chaoses-Ib/ObsidianShell) |
|---|---|---|
| 安装 | 一条命令，现场用系统编译器编译 | 下载预编译安装包 |
| 仓库内容 | 纯源码，无任何二进制 | 预编译 exe |
| vault 外文件 | 回落到 Typora / VS Code / 记事本 | VaultRecent 模式可直接用 Obsidian 编辑 |
| 零弹窗 | ✅ | ✅ |
| 功能面 | 极简，只解决「双击打开这个文件」 | 丰富：CLI、右键菜单、启动器工作流等 |

- **[ObsidianShell](https://github.com/Chaoses-Ib/ObsidianShell)**：功能更全的同类工具。它的 VaultRecent/Recent 模式通过目录联接（junction）把任意孤立文件临时挂进一个「Recent vault」，从而直接用 Obsidian 编辑 vault 外文件——这一点比本项目的「回落到别的编辑器」更彻底。适合想把 Obsidian 当万能 Markdown 编辑器的重度用户
- **手动 PowerShell / VBS 脚本**：不用装任何东西，但每次双击会闪控制台黑框，wscript 方案还常被安全策略当 LOLBin 拦截
- **换一个编辑器做 .md 默认程序**（Typora / VS Code）：如果你不强依赖 Obsidian，这永远是最简单的方案

## 常见问题

**Q：为什么不用 PowerShell 脚本直接做转发？**
可以，但每次双击都会闪一下控制台黑框（`-WindowStyle Hidden` 也躲不过 shell 调用时先创建控制台的那一瞬）。

**Q：为什么不用 wscript/VBS？**
`wscript.exe` 被很多安全策略标记为 LOLBin（ living-off-the-land 攻击常用程序），企业环境里经常被直接拦截，不如编译出来的普通 exe 干净。

**Q：这个 exe 会不会偷偷干别的？**
源码只有一个文件 `src/OpenInObsidian.cs`：读路径 → 读 vault 列表判断归属 → 派发 URI 或调用回落编辑器，catch 里什么都不做。安装脚本没有下载任何东西，用的是系统自带编译器，全程可审计。

**Q：vault 外的 `.md` 双击后会怎样？**
Obsidian 官方协议打不开 vault 外文件，所以这类文件会自动改用回落编辑器打开：优先用 `%LOCALAPPDATA%\OpenInObsidian\fallback-editor.txt` 里指定的程序（一行，编辑器 exe 的完整路径），否则依次寻找 Typora → VS Code，都没有就退到记事本。删掉该文件则恢复自动检测。

**Q：Obsidian 弹出加载仓库失败的报错（EINVAL），提到 `System Volume Information`？**
别把整个磁盘根目录（如 `E:\`）添加为 vault。Obsidian 加载仓库时要扫描根目录，撞上系统保护文件夹（隐藏 + 拒绝访问）就扫描失败，整个仓库都打不开。解决：移除整盘 vault，只把具体目录（如 `E:\文档`）加为仓库；根目录下零散的 `.md` 交给本项目的回落编辑器处理即可。

**Q：`.md` 图标会变吗？**
会沿用 Obsidian 的图标（注册时把 `DefaultIcon` 指向了 Obsidian.exe）。

**Q：装完要不要重启？**
不需要。安装脚本已调用 `SHChangeNotify` 广播关联变更，Explorer 立刻感知。极少数情况（第三方安全软件接管了文件关联）下注销一次即可。

## 项目结构

```
open-in-obsidian/
├── src/
│   └── OpenInObsidian.cs    # 转发器源码（安装时现场编译，仓库不含二进制）
├── scripts/
│   ├── install.ps1          # 一键安装
│   └── uninstall.ps1        # 一键卸载
├── LICENSE
├── README.md                # 中文文档
└── README_EN.md             # English docs
```

## 许可证

[MIT](LICENSE) —— 随便用，欢迎 PR 和 issue。

## 其他语言

- [English README](README_EN.md)
