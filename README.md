# QoderSecs

自用的 SECS/GEM 模拟器，Windows 桌面版，界面只有中文。可以做 Host 去测真实机台的参数，也可以做 Equipment 来测 CIM 系统。

传输层现状：

- **HSMS-SS（TCP）**：完整可用，是日常在用的那条路。
- **SECS-I（串行块传输）**：只做到协议层——块格式、ENQ/EOT/ACK/NAK 握手、多块拼消息、SECS-II 正文换算都写了并且有单元测试，但没接串口、没接会话层、界面上没有入口。当前版本用不了。

<p align="center">
  <img src="docs/design/screenshots/main-window.png" alt="QoderSecs 主界面：左边消息库，中间消息编辑（结构 / SML 两个页签），右边通信记录和详情" width="860">
</p>

## 功能

**连接**
- Role（Host / Equipment）和 Active / Passive 是两个独立设置。
- 可填 IP、Port、Dev ID 和 T3/T5/T6/T7/T8；改动自动落盘，连接期间锁定。
- 一个进程跑一条连接，要两条就开两个实例。

**消息库**
- 导入导出 SecsExpress 的 `.sxml`；也可以把 `.sxml` / `.xml` 直接拖到左栏，库里已有消息时先确认再整体替换。
- 以原始 XML 为权威数据，只局部改写编辑过的节点；DataDictionary、Maps、未知元素和属性、注释、消息顺序都保留。
- 消息行的 SxFy 后面带 W 位记号：`W` 表示需要回复，`-` 表示不需要。
- 搜索支持消息名、`S10F3`、`10 3`；消息按 SxFy 升序排列。
- 消息名必须非空且不能重名；SxFy 可以重复——三条 `S2F41` 分别叫 START、STOP、PP_SELECT 是正常用法。
- 条目可新建、复制、删除。复制出来的除名字外全部照抄，名字取「原名 Copy」，紧跟原条之后；删除前弹确认，并报出有几条自动回复指向它——删完这些引用一并置空。不做撤销。

**结构编辑**
- 树状编辑 L、A、B、Boolean、I1/I2/I4/I8、U1/U2/U4/U8、F4/F8。
- 数值和 Boolean 支持数组，`<I2> 0 0` 这种一个元素多个值可以直接编辑也能发出去；值框里多个值用空格分隔，存进 SXML 就是同一个 `<Value>` 里空格分开，和 SecsExpress 导出的写法一致。A、J、B 的 `[个数]` 是字节数，正文按一段对待。
- 加子项、加同级、删除都有菜单和快捷键；双击那一行或按 F2 就地在树里改名称和值，边敲边校验，点那一行以外就收框。
- 节点可以复制粘贴，也可以拖动：结构页里拖动是搬动，字典页里拖进别的条目放下去的是副本、原来那一条不动（条目互不隶属），顶层条目之间拖动是排队。值不合法时标红并禁止发送。
- SML 页签可预览整条消息，也可以粘贴一条 SML 覆盖当前消息的头和结构。

**数据字典**
- 结构页把任意节点「存为条目」，字典页当素材库用。
- 菜单：新增数据（选类型）、删除条目、复制、粘贴；条目内部可以就地改名称和值、加子项、删某一层，操作和结构页同一套。
- 条目之间互不隶属；条目和消息之间是复制关系，改哪一边都不影响对方。
- 从字典拖不进消息，跨段只能复制粘贴。

**收发与配对**
- W-bit 请求的 Secondary 严格按 SystemBytes 配对，不按 SxFy 或到达时间猜。
- T3 超时明确显示；超时后才到的回复标成迟到回复；没有人等待的 Secondary 明确报出来。
- 等回复期间可以点「取消等待」，发送按键立刻放开；点「断开」同样会叫停在途的等待。取消掉的只是「等」这件事——消息已经发出去了，对方后来真回了会记成迟到回复。

**自动回复**
- 唯一来源是库里那条 SxFy 上配的 `AutoReply` 字段，值是另一条消息的 Name。
- 收到带 W 的 Primary 时把它的结构发出去，W 位强制为 0；没配、找不到或发不出去就不回复，并记录未自动回复。
- 默认不开启，不内置任何规则，不按 `SxF(y+1)` 推导。
- 那一栏的候选写成「SxF · 名字」，字段标题带上本条的 SxF 和 W 位；不是本条 F+1 那一号的标「不配对」，照样可选，程序仍按名字精确回。
- 引用按名字存，所以目标被改名或删除时指着它的 `AutoReply` 一并置空，候选栏里也不再出现旧名字；状态栏会写明置空了哪几条。

**通信记录与日志**
- 记录方向、SxFy、名称、SystemBytes、状态和耗时，请求与回复的配对关系直接可见；两个区域各自智能滚动。
- 内存里保留最近 5000 条，超出的最早的被丢掉，标题上说明丢了几条。
- 磁盘上 `sml.log` 是完整 SML，`detail.log` 是连接事件、协议头、SystemBytes、耗时、异常和回复来源。
- 每个配置保留最近 20 个会话日志目录。

**大消息**
- 不设 4KB 那类应用层上限；列表只长期持有轻量元数据并开启虚拟化，大 SML 默认折叠。
- 1MB 和 10MB 的 B 数据双向收发验证过，界面不冻结。
- 文本框的显示上限（预览 256KB、详情一次 512KB）只是显示限制，超出部分指向 `sml.log`。

**数据位置**
- 都在 exe 旁的 `QoderSecsData/`：`settings.json` 和 `logs/<配置 Id>/<时间_进程号>/`。
- 日志目录带进程号，多个实例不互相覆盖；参数文件共用一份，谁后保存谁生效。

## 项目结构

```
QoderSecs.sln
src/
├─ QoderSecs.Core/    协议、消息库、自动回复、配置和日志（不引用 WPF）
└─ QoderSecs.App/     WPF 界面与 ViewModel
tests/
└─ QoderSecs.Tests/   单元测试、契约测试和 HSMS 回环集成测试
```

Host 和 Equipment 是同一套代码，角色只是配置里的一个字段。

## 上手

发布产物是单个 `QoderSecs.App.exe`，Releases 里可以直接下载。目标机需要 [.NET 8 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（x64，框架依赖，exe 里不含运行时）。自己构建：

```
dotnet publish src/QoderSecs.App/QoderSecs.App.csproj \
  --configuration Release \
  --runtime win-x64 \
  --self-contained false \
  -p:PublishSingleFile=true
```

第一次启动时，程序会在 exe 旁边解出 `samples/` 下的两份样本库，并建出 `QoderSecsData/`，所以这个目录必须可写。解出来的样本只是普通文件，改它们、删它们、换成自己的都不受影响。启动后消息库是空的，样本不自动加载，要用的自己导入或拖进左栏。

`common-commands.sxml` 里是 23 条 GEM 常用命令的骨架：结构留着、变量名按标准填（`SVID`、`RPTID`、`CEID`、`CEED`、`RCMD`、`PPID`、`TID`、`MAP_XY`、`WAFER_MAP` 这些），数值一律空着，编号和取值按你手上那台机填。S12 上下图那两条（S12F9、S12F16）同样只给形状，图正文粘进去就能发。数据字典不在样本里预置，要用在「数据字典」页自己加。

`wafer-map.sxml` 是把 S12 上下图那一条流走通用的：只有 S12F9（带 W）和 S12F16 两条，但填好了一张 86×87 的合成图正文（7482 字符），片号 `02`、批次号写成 `MASKEDLOT1`，而且 S12F9 已经配好自动回复指向 S12F16。导进 Equipment 那一侧、连上对头发一条 S12F9，就能看到回过来的同一张图。这一份的存在理由就是把收发验一遍，所以它不受「骨架只留形状、不预置 AutoReply」那条口径约束；图、批号、片号全是编出来的，不含任何真实机台和批次数据。

本机测一对连接：开两个 exe 实例，一个填 Active、一个填 Passive，地址用 `127.0.0.1`，先点 Passive 那边再点 Active。程序只提供 `127.0.0.1` 的默认参数，不含任何真实设备信息。

## 没做

SECS-I 的串口和会话层（协议层在 `src/QoderSecs.Core/Connection/Secs1*.cs`，还没接线）；SECS-I 的控制块（SELECT、ENABLE 那一类）和多点共线；完整 GEM 数据模型和状态机；条件自动回复、脚本、变量和流程编排；内置自动回复规则和按 `SxF(y+1)` 推导；多候选自动回复选择；手动回复；独立 hex 面板；单进程多连接；撤销。

## 开发

```
dotnet build QoderSecs.sln --configuration Release
dotnet test  QoderSecs.sln --configuration Release --no-build
```

回环测试在 `127.0.0.1` 上起一对 Active / Passive 实例互连，覆盖 SystemBytes 配对、T3 超时、迟到回复、自动回复和断开清理。SECS-I 那部分不需要硬件：它跑在一段抽象 `Stream` 上，测试用内存里的双向管道喂字节。

依赖：Secs4Net 3.1.0、Secs4Net.Sml 3.1.0（协议）、MSTest 4.0.2（只在测试里）。C# / .NET 8，WPF + MVVM。

这个项目推荐用 AI Agent 来写、来读：核心逻辑不碰 WPF、测试一条命令跑完、模块职责单一，Agent 拿到一句话需求就能定位到该改的地方。

## 致谢

- SecsExpress —— 日常测试中一直在用的 SECS/GEM 模拟器。本项目的消息库格式、命令组织和使用习惯都重点参考了它。
- [Secs4Net](https://github.com/mkjeff/secs4net)（MIT）—— SECS/GEM 协议实现。

## 许可证

MIT，见 [LICENSE](LICENSE)。
