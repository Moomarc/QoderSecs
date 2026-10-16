# QoderSecs

一个自用的 SECS/GEM 模拟器：既能作为 Host 去测试真实机台的参数，也能作为 Equipment 来测试 CIM 系统。只支持 HSMS-SS，只做 Windows 桌面版，界面目前只有中文。

<p align="center">
  <img src="docs/design/screenshots/main-window.png" alt="QoderSecs 主界面：左边消息库，中间消息编辑（结构 / SML 两个页签），右边通信记录和详情" width="860">
</p>

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

发布产物是单个 `QoderSecs.App.exe`，Releases 里可以直接下载。目标机需要 [.NET 8 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（x64，框架依赖，所以 exe 里不含运行时）。自己构建：

```
dotnet publish src/QoderSecs.App/QoderSecs.App.csproj \
  --configuration Release \
  --runtime win-x64 \
  --self-contained false \
  -p:PublishSingleFile=true
```

第一次启动时，程序会在 exe 旁边解出 `samples/common-commands.sxml`，并建出 `QoderSecsData/`，所以这个目录必须可写。解出来的样本只是一个普通文件，改它、删它、换成自己的都不受影响。

注意：程序启动后消息库是空的。样本不会自动加载——要测试得自己在「文件」菜单里导入。

自己测一对连接：开两个 exe 实例，一个填 Active、一个填 Passive，地址用 `127.0.0.1`，先点 Passive 那边再点 Active。程序只提供 `127.0.0.1` 的默认参数，不含任何真实设备信息。

## 能干什么

- **连接**：Role（Host / Equipment）和 Active / Passive 是两个独立设置，填 IP、Port、Dev ID 和 T3/T5/T6/T7/T8 就能连。参数改动自动落盘，连接期间锁定。一个进程跑一条连接，要两条就开两个实例。
- **消息库**：导入导出 SecsExpress 的 `.sxml`。以原始 XML 为权威数据，只局部改写编辑过的节点，DataDictionary、Maps、未知元素和属性、注释和消息顺序都保留。搜索支持消息名、`S10F3` 和 `10 3`，消息按 SxFy 升序排列。
- **结构编辑**：树状编辑 L、A、B、Boolean、I1/I2/I4/I8、U1/U2/U4/U8、F4/F8，加子项、加同级、删除都有菜单和快捷键。值不合法时标红并禁止发送。SML 页签可预览一条完整消息，也可粘贴一条进来。
- **收发**：W-bit 请求严格按 SystemBytes 配对 Secondary，不按 SxFy 或到达时间猜。T3 超时明确显示，超时后才到的回复标成迟到回复，没有人等待的 Secondary 会明确报出来。
- **自动回复**：唯一来源是库里那条 SxFy 上配的 `AutoReply` 字段，值是另一条消息的 Name。收到带 W 的 Primary 时把它的结构发出去、W 位强制为 0；没配、找不到或发不出去就不回复并记录未自动回复。默认不开启，程序不内置任何规则，也不按 `SxF(y+1)` 推导。
- **通信记录与日志**：记录方向、SxFy、名称、SystemBytes、状态和耗时，请求与回复的配对关系直接可见；两个区域各自智能滚动。磁盘上 `sml.log` 是完整 SML，`detail.log` 是连接事件、HSMS 头、SystemBytes、耗时、异常和回复来源。
- **大消息**：不设 4KB 那类应用层上限。列表只长期持有轻量元数据并开启虚拟化，大 SML 默认折叠。1MB 和 10MB 的 B 数据双向收发验证过，界面不冻结。界面文本框的显示上限（预览 256KB、详情一次 512KB）只是显示限制，超出部分指向 `sml.log`。
- **数据**：都放在 exe 旁的 `QoderSecsData/`（`settings.json` 和 `logs/<配置 Id>/<时间_进程号>/`）。日志目录带进程号，多个实例不会互相覆盖；参数文件共用一份，谁后保存谁生效。

## 不做

SECS-I；完整 GEM 数据模型和状态机；条件自动回复、脚本、变量和流程编排；内置自动回复规则和按 `SxF(y+1)` 推导回复；数值数组编辑；多候选自动回复选择；手动回复；单进程多连接；独立 hex 面板。

## 开发

```
dotnet build QoderSecs.sln --configuration Release
dotnet test  QoderSecs.sln --configuration Release --no-build
```

回环测试在 `127.0.0.1` 上起一对 Active / Passive 实例互连，覆盖 SystemBytes 配对、T3 超时、迟到回复、自动回复和断开清理。

依赖：Secs4Net 3.1.0、Secs4Net.Sml 3.1.0（协议）、MSTest 4.0.2（只在测试里）。C# / .NET 8，WPF + MVVM。

这个项目推荐用 AI Agent 来写、来读：提需求、读代码、改代码、跑测试、查协议行为都可以直接交给它。仓库也是按这个标准组织的——核心逻辑不碰 WPF、测试一条命令跑完、模块职责单一，Agent 拿到一句话需求就能定位到该改的地方。

## 致谢

- SecsExpress —— 日常测试中一直在用的 SECS/GEM 模拟器。本项目的消息库格式、命令组织和使用习惯都重点参考了它。
- [Secs4Net](https://github.com/mkjeff/secs4net)（MIT）—— SECS/GEM 协议实现。

## 许可证

MIT，见 [LICENSE](LICENSE)。
