# Open in Obsidian

> 双击任意 `.md` 文件，Obsidian 直接打开**这个文件本身**——不弹窗、不闪黑框、不用重启。

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
     │  URL 编码文件路径
     ▼
obsidian://open?path=E%3A%5C%E6%96%87%E6%A1%A3%5Clinux%E5%91%BD%E4%BB%A4.md
     │
     ▼
Obsidian 打开并跳转到该文件 ✅
```

### 特点

- **零依赖**：不需要下载任何东西，用 Windows 自带的 .NET Framework 编译器现场编译一个约 50 行的转发程序——源码就在 `src/`，装的是什么一目了然
- **零弹窗**：`/target:winexe` 编译的 GUI 程序，没有控制台窗口，什么都不闪
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

装完双击一个 vault 里的 `.md` 试试。

> **如果双击还是打开别的程序**：说明你之前给 `.md` 设置过的默认应用（受 Windows ACL 保护的 UserChoice 键）优先级更高。右键任意 `.md` → 打开方式 → 选择其他应用 → 选 **Markdown File (Obsidian)** 并勾选「始终使用此应用」，一次即可。

## 卸载

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1
```

会移除文件关联、还原 `.md` 之前的默认设置（安装时自动备份），并询问是否删除编译出的 exe。

## 已知限制

- **只能打开 vault（仓库）内的文件**。`obsidian://open?path=` 是 Obsidian 官方协议的硬限制：文件必须属于某个已添加的 vault，孤立的 `.md`（比如随手下载的）无法打开。如果你经常处理 vault 外的 md，建议搭配 Typora / VS Code 使用
- 双击后 Obsidian 不会自动最小化到后台再跳出来——它会把窗口带到前台并打开文件，这属于 Obsidian 自身行为
- 仅支持 Windows（macOS / Linux 的文件关联机制完全不同）

## 常见问题

**Q：为什么不用 PowerShell 脚本直接做转发？**
可以，但每次双击都会闪一下控制台黑框（`-WindowStyle Hidden` 也躲不过 shell 调用时先创建控制台的那一瞬）。

**Q：为什么不用 wscript/VBS？**
`wscript.exe` 被很多安全策略标记为 LOLBin（ living-off-the-land 攻击常用程序），企业环境里经常被直接拦截，不如编译出来的普通 exe 干净。

**Q：这个 exe 会不会偷偷干别的？**
源码只有 50 行，就在 `src/OpenInObsidian.cs`：读路径 → URL 编码 → 派发 URI，catch 里什么都不做。安装脚本没有下载任何东西，用的是系统自带编译器，全程可审计。

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
└── README.md
```

## 许可证

[MIT](LICENSE) —— 随便用，欢迎 PR 和 issue。

---

## English summary

Double-clicking a `.md` file in Windows only *launches* Obsidian, which restores the last workspace and **ignores the file you clicked** — a known Obsidian design limitation. This project fixes it by registering a file association that forwards the clicked path to Obsidian via the official `obsidian://open?path=...` URI, using a tiny **windowless GUI helper compiled on-the-fly** from ~50 lines of C# (no PowerShell console flash, no wscript LOLBin blocks, no downloads, no admin rights, changes take effect immediately).

```powershell
# install
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
# uninstall
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1
```

Limitation: the file must live inside an Obsidian vault (that's an upstream URI protocol constraint).
