# Open in Obsidian

> 双击任意 `.md` 文件，Obsidian 直接打开**这个文件本身**——不弹窗、不闪黑框、不用重启。

**简体中文** | [English](README_EN.md)

[![Windows](https://img.shields.io/badge/platform-Windows-blue)]() [![No dependencies](https://img.shields.io/badge/dependencies-none-green)]() [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

如果你在 Windows 上用 Obsidian，大概率遇到过下面这三个让人抓狂的问题：

**问题一：双击 `.md` 文件，打开的却是「上次浏览的文件」**

Obsidian 会忽略命令行传入的文件路径，启动后直接恢复上一次的工作区。所以哪怕你在资源管理器里双击的是 `会议记录.md`，弹出来的可能还是昨天看的 `购物清单.md`。这是 Obsidian 的[已知设计限制](https://forum.obsidian.md/t/open-file-from-explorer-opens-last-opened-file/)，不是你的系统坏了。

**问题二：想用脚本修，结果每次双击都闪一个命令行黑框**

用 PowerShell 包装可以解决问题一，但 PowerShell 是控制台程序，`-WindowStyle Hidden` 也挡不住注册表 shell 调用时先闪一下黑框；换 wscript 又会被安全策略当 LOLBin 拦截。

**问题三：没有把某个目录加进 Obsidian 的 vault，双击那里的 `.md` 就用不上 Obsidian**

`obsidian://open?path=` 只在**已注册的 vault 里**查找路径（官方文档原话：它会"搜索包含该路径的最具体仓库"）。路径不在任何 vault 里，协议就无事发生——所以要么把目录一个个加进 vault，要么只能改用别的编辑器打开。

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
     ├─ 在 vault 外 → 挂进「桥接仓库」再派发同一个协议 ✅
     │     在桥接仓库里建一个目录联接指向该文件夹，路径于是变成了仓库内路径
     │     obsidian://open?path=…%5Cvault%5C%E4%B8%8B%E8%BD%BD%20(a1b2c3d4e5f6)%5Ca.md
     │     → Obsidian 当作仓库内文件打开，可编辑、可搜索、可被链接
     │
     └─ 桥接用不了（未注册 / 盘符根目录 / 路径过长）→ 回落到本地编辑器 ✅
           （fallback-editor.txt 指定的程序 → Typora → VS Code → 记事本）
```

### 特点

- **零依赖**：不需要下载任何东西，用 Windows 自带的 .NET Framework 编译器现场编译一个转发程序（约 940 行源码，含 vault 检测、桥接挂载与回落逻辑）——源码就在 `src/`，装的是什么一目了然
- **零弹窗**：`/target:winexe` 编译的 GUI 程序，没有控制台窗口，什么都不闪
- **vault 外文件也能用 Obsidian 打开**：自动把文件所在目录以目录联接挂进一个专用的「桥接仓库」，于是仓库外的 `.md` 同样在 Obsidian 里打开，可编辑、可搜索。真实文件不移动，你自己的 vault 一个都不动（机制见下文）
- **拿不准就回落**：桥接不可用时（桥接仓库没注册、文件在盘符根目录、路径过长）自动改用 Typora / VS Code / 记事本打开，绝不会"双击没反应"（可用 `fallback-editor.txt` 指定编辑器，见常见问题）
- **即时生效**：安装后调用 `SHChangeNotify` 通知 Explorer，**无需重启/注销**
- **仅改当前用户注册表（HKCU）**：不要管理员权限，卸载脚本一键还原

## 桥接仓库是怎么工作的

Obsidian 官方文档明确支持在 vault 里使用符号链接和目录联接（junction）来存放 vault 之外的文件，并且会把联接背后的文件当作仓库内文件索引和编辑。本项目用的就是这个机制。

```
C:\Users\<你>\AppData\Local\OpenInObsidian\vault\   ← 桥接仓库（安装时创建）
├── .obsidian\                    ← 空仓库的配置
└── 下载 (a1b2c3d4e5f6)\          ← 目录联接，指向 E:\下载
    └── 某笔记.md                  ← Obsidian 看到的路径
```

- **真实文件不动**：`E:\下载\某笔记.md` 还在原地，联接只是给它开了一扇 Obsidian 认得的门
- **不碰你自己的 vault**：桥接仓库独立存在，你原有的 vault 的文件树、搜索、关系图都不会多出东西
- **联接名 = 文件夹名 + 路径哈希**：哈希保证 `E:\docs` 和 `D:\docs` 不会撞车；文件夹名以 `.` 开头会被去掉（Obsidian 会隐藏点开头的目录）
- **不需要管理员权限**：目录联接不像符号链接那样需要 `SeCreateSymbolicLinkPrivilege`
- **注册是必需的**：安装脚本会把桥接仓库写进 `%APPDATA%\obsidian\obsidian.json`（Obsidian 的仓库列表）。**写之前 Obsidian 必须完全退出**，否则它会用内存里的列表覆盖掉；脚本检测到 Obsidian 在运行会直接拒绝写入并提示你

## 安装

前提：Windows 10/11 + 已安装 Obsidian（任意安装方式，含绿色便携版）+ .NET Framework（系统自带）。

在仓库根目录打开 PowerShell：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

脚本会自动：

1. 定位 Obsidian.exe（优先读 Obsidian 自己注册的 `obsidian://` 协议处理器路径，找不到时尝试常见安装目录；也可以 `-ObsidianPath "D:\path\to\Obsidian.exe"` 手动指定）
2. 编译 `src\OpenInObsidian.cs` → `%LOCALAPPDATA%\OpenInObsidian\OpenInObsidian.exe`
3. 创建桥接仓库 `%LOCALAPPDATA%\OpenInObsidian\vault`
4. 把桥接仓库注册进 Obsidian 的仓库列表（备份 `obsidian.json` 后写入；**Obsidian 必须在关闭状态**）
5. 注册文件关联并设为 `.md` 默认打开方式
6. 通知 Explorer 立即生效

装完双击任意 `.md` 试试——vault 里的直接打开，vault 外的会自动挂进桥接仓库后打开。想给"桥接用不了"的情况指定回落编辑器，把它的完整路径写进 `%LOCALAPPDATA%\OpenInObsidian\fallback-editor.txt`（一行）即可。

> **如果双击还是打开别的程序**：说明你之前给 `.md` 设置过的默认应用（受 Windows ACL 保护的 UserChoice 键）优先级更高。右键任意 `.md` → 打开方式 → 选择其他应用 → 选 **Markdown File (Obsidian)** 并勾选「始终使用此应用」，一次即可。

## 卸载

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1
```

会移除文件关联、还原 `.md` 之前的默认设置（安装时自动备份）、从 Obsidian 的仓库列表里摘掉桥接仓库，并删除桥接仓库目录。

> 桥接仓库里全是目录联接，所以卸载脚本会**先逐个用非递归方式删掉联接**，确认没有残留后才删除目录——递归删除会顺着联接把真实文件夹清空。

## 已知限制

- **桥接仓库必须已注册**。没注册成功（比如安装时 Obsidian 正在运行）时，vault 外文件会退回用回落编辑器打开，不会"双击没反应"。安装脚本会把状态打在屏幕上，也留有 `obsidian.json.bak` 备份
- **盘符根目录下的 `.md` 不走桥接**。把整个磁盘根目录联接进仓库，会让 Obsidian 扫描到 `System Volume Information` 而加载仓库失败（EINVAL），而且会顺带索引整个系统盘。这类文件直接走回落编辑器，原因会记进 `last-error.log`。把它们放进任意一个文件夹（如 `C:\笔记\`）就正常了
- **在父目录里打开文件时，指向子目录的联接会被重建**。Obsidian 要求联接目标之间互不包含，所以先挂 `E:\a\b` 再挂 `E:\a` 时，前一个联接会被删掉换成后一个。如果你此时正用 Obsidian 编辑着 `E:\a\b` 里的文件，建议先保存
- **Obsidian 官方对符号链接/联接的措辞是「风险自负」**，原文提到可能造成数据丢失或损坏，尤其不建议与各类同步工具混用（同步工具对链接的处理各不相同）。真实文件不会因为本工具而移动或改写，但如果你把桥接目录里的内容纳入云同步，请先确认同步工具的行为
- **Obsidian 正在运行时，首次双击某个新文件夹里的文件会先等约 1.2 秒**。运行中的 Obsidian 靠文件监听器发现刚建的联接，协议打开文件又要查它的内存索引，不等一下那一次打开可能落空。Obsidian 没在运行时不等——它启动加载仓库时本来就会重新扫描磁盘，冷启动的耗时花在 Obsidian 自身
- **桥接仓库会自我瘦身**。每次运行都会自动清掉两类联接：目标已失效的悬空联接（此前一个悬空联接会让之后所有 vault 外文件都退回到回落编辑器），以及目标会把仓库套进自身的联接（比如仓库装在 `C:\Users\...` 下时把 `C:\Users` 整个挂进来——Obsidian 的索引器会顺着它递归爬，把挂载内容反复索引，表现为加载极慢甚至卡死；这类文件夹退回到回落编辑器）。另外只保留最近使用的 10 个挂载，更早的自动删除——只删联接，从不动真实文件夹，避免仓库越积越大、每次加载越来越慢
- **Obsidian 没在运行时，双击仓库外文件会让它把「桥接仓库」记为上次打开的仓库**。Obsidian 下次直接启动时可能落在桥接仓库而不是你自己的仓库——它是个空仓库，看着像"笔记全没了"。切回自己的仓库即可；想避免就先启动 Obsidian 再双击文件
- 双击后 Obsidian 不会自动最小化到后台再跳出来——它会把窗口带到前台并打开文件，这属于 Obsidian 自身行为
- 仅支持 Windows（macOS / Linux 的文件关联机制完全不同）

## 替代方案

这个项目不是第一个解决该问题的工具，按需求选合适的就好：

| | 本项目 (open-in-obsidian) | [ObsidianShell](https://github.com/Chaoses-Ib/ObsidianShell) |
|---|---|---|
| 安装 | 一条命令，现场用系统编译器编译 | 下载预编译安装包 |
| 仓库内容 | 纯源码，无任何二进制 | 预编译 exe |
| vault 外文件 | 挂进桥接仓库后用 Obsidian 编辑；桥接不可用时回落 | VaultRecent 模式可直接用 Obsidian 编辑 |
| 零弹窗 | ✅ | ✅ |
| 功能面 | 极简，只解决「双击打开这个文件」 | 丰富：CLI、右键菜单、启动器工作流等 |

- **[ObsidianShell](https://github.com/Chaoses-Ib/ObsidianShell)**：功能更全的同类工具，也是本项目桥接思路的参考。它的 VaultRecent/Recent 模式同样通过目录联接把孤立文件挂进一个「Recent vault」，另外还支持多库共用配置、CLI、右键菜单和启动器工作流。需要这些就选它
- **手动 PowerShell / VBS 脚本**：不用装任何东西，但每次双击会闪控制台黑框，wscript 方案还常被安全策略当 LOLBin 拦截
- **换一个编辑器做 .md 默认程序**（Typora / VS Code）：如果你不强依赖 Obsidian，这永远是最简单的方案

## 常见问题

**Q：为什么不用 PowerShell 脚本直接做转发？**
可以，但每次双击都会闪一下控制台黑框（`-WindowStyle Hidden` 也躲不过 shell 调用时先创建控制台的那一瞬）。

**Q：为什么不用 wscript/VBS？**
`wscript.exe` 被很多安全策略标记为 LOLBin（ living-off-the-land 攻击常用程序），企业环境里经常被直接拦截，不如编译出来的普通 exe 干净。

**Q：这个 exe 会不会偷偷干别的？**
源码只有一个文件 `src/OpenInObsidian.cs`：读路径 → 读 vault 列表判断归属 → 在 vault 内就派发 URI；在 vault 外就在桥接仓库里建一个目录联接，再派发 URI；都不行才调用回落编辑器。catch 里只写日志、不弹窗。安装脚本没有下载任何东西，用的是系统自带编译器，全程可审计。

**Q：vault 外的 `.md` 双击后会怎样？**
程序在桥接仓库里建一个指向该文件夹的目录联接（已存在就复用），然后派发指向联接内部路径的 `obsidian://open`，Obsidian 就把它当仓库内文件打开。真实文件不移动、不改名。如果桥接用不了（桥接仓库未注册、文件在盘符根目录、路径超长），才改用回落编辑器：优先用 `%LOCALAPPDATA%\OpenInObsidian\fallback-editor.txt` 里指定的程序（一行，编辑器 exe 的完整路径），否则依次寻找 Typora → VS Code，都没有就退到记事本。

**Q：会不会把我的文件删掉？**
不会。程序只在桥接仓库里创建和删除**联接**，从不删真实文件。删除联接用的是非递归的 `RemoveDirectory`——递归删除会顺着联接进到目标目录里，所以代码里刻意避开了它。测试里专门有一条断言：删掉联接后目标文件夹必须完好。真实文件只有在 Obsidian 里编辑时才会被改写，和平时一样。

**Q：桥接仓库会不会越堆越多？**
会积累，每个打开过的文件夹一条联接，但每条只是一个几十字节的联接记录，不是文件副本。同一文件夹再次打开会复用，不会重复创建；父目录被挂载后，其下冗余的子目录联接会被自动清理。想清空就把桥接仓库目录整个删掉，下次双击会重新建。

**Q：桥接目录里的内容会被同步工具带走吗？**
真实文件始终在原来的位置，本工具不移动它们。但如果你把某个被桥接的目录本身放在同步盘里（比如 OneDrive / 百度同步盘），同步工具看到的是它原本的路径，行为不受影响。反过来，**不要**把 `%LOCALAPPDATA%\OpenInObsidian\vault` 纳入同步——Obsidian 官方也明确警告联接与同步工具混用可能产生冲突。

**Q：Obsidian 弹出加载仓库失败的报错（EINVAL），提到 `System Volume Information`？**
别把整个磁盘根目录（如 `E:\`）添加为 vault。Obsidian 加载仓库时要扫描根目录，撞上系统保护文件夹（隐藏 + 拒绝访问）就扫描失败，整个仓库都打不开。解决：移除整盘 vault，只把具体目录（如 `E:\文档`）加为仓库。本项目出于同样原因，对盘符根目录下的 `.md` 也直接走回落编辑器，不做桥接。

**Q：双击没反应或行为不对，怎么排查？**
程序自身从不在屏幕上弹任何东西，但会把最近一次事件写到 `%LOCALAPPDATA%\OpenInObsidian\last-error.log`（单文件，只保留最后一次）。里面有两类内容：

- 内部错误（比如联接创建失败），附带 Win32 错误码
- **为什么走了回落编辑器**，例如 `fallback | file sits directly in a drive root (C:)`

所以"这个文件为什么用 Notepad++ 打开了"这类问题，看一眼这个文件就有答案。大部分问题重跑 install.ps1 即可解决。

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
├── tests/
│   ├── run-tests.ps1        # 一键跑测试（临时目录编译运行，不碰真实配置；csc 定位与 install.ps1 需保持同步）
│   └── TestDriver.cs        # 40 项单元测试（vault 解析/嵌套匹配/联接命名/联接增删/回落配置/错误日志）
├── LICENSE
├── README.md                # 中文文档
└── README_EN.md             # English docs
```

## 运行测试

改了源码想验证？不用安装，一条命令：

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
```

测试在临时目录编译源码并用反射驱动执行，覆盖 vault 解析（中文路径、正斜杠归一化、前缀重叠边界）、畸形/缺失配置的降级行为、回落编辑器配置读取和错误日志，以及桥接部分：联接命名与稳定哈希、目录联接的创建/读取/删除往返、嵌套目录的冲突清理。

联接测试会在临时目录里创建真实的目录联接（目标也在临时目录里），全程不启动 Obsidian、不碰你真实的 `obsidian.json`。其中两条断言专门盯着数据安全：删掉联接后目标文件夹必须还在、冲突清理后子目录内容必须完好。

## 许可证

[MIT](LICENSE) —— 随便用，欢迎 PR 和 issue。

## 其他语言

- [English README](README_EN.md)
