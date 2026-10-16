# QoderSecs v1 规格

状态：已批准

批准日期：2026-10-15

## 1. 产品目标

QoderSecs 是供封测厂 CIM 工程师自用的 SECS/GEM 模拟器，主要作为 Host 测试真实机台参数，也能作为 Equipment 测试 CIM 系统。

v1 要替代日常使用中的 SecsExpress，重点改善界面效率、大 recipe 稳定性和请求/回复可追踪性。

## 2. 技术基线

- C#、.NET 8（2026-10-16 从 .NET 10 下调，理由：同事电脑普遍没装 .NET 10 运行时）
- WPF + MVVM
- App 目标框架：`net8.0-windows`
- Core 和 Tests 目标框架：`net8.0`
- Secs4Net 3.1.0
- Secs4Net.Sml 3.1.0
- MSTest 4.0.2
- 只支持 HSMS-SS
- `win-x64`、框架依赖、单文件发布；目标机需要 .NET 8 Windows Desktop Runtime
- 程序图标：蓝底白字的几何 S（`src/QoderSecs.App/app.ico`，16/24/32/48 位图条目 + 256 PNG 条目）

除上述包外，新增 NuGet 依赖必须再次批准。

## 3. 解决方案结构

```text
QoderSecs.sln
src/
├─ QoderSecs.Core/       协议、消息库、自动回复、配置和日志
└─ QoderSecs.App/        WPF 界面与 ViewModel
tests/
└─ QoderSecs.Tests/      单元测试、契约测试和回环集成测试
```

依赖方向：

```text
QoderSecs.App → QoderSecs.Core → Secs4Net / Secs4Net.Sml
QoderSecs.Tests → QoderSecs.Core
```

Core 不引用 WPF。Host 和 Equipment 使用同一套代码。

## 4. 连接管理

- 一个进程只运行一条连接；多连接通过启动多个程序实例实现。
- Role（Host/Equipment）和连接模式（Active/Passive）是独立配置。
- 连接参数只有一份，没有下拉选择。常改的是 IP、Port 和 Active/Passive；T3、T5、T6、T7、T8 和 Linktest 间隔在同一个面板里预设好，要改也能改。
- 参数改动自动落盘。文件不存在、内容损坏或参数越界时退回默认值，原因写在状态栏，不拦住使用。
- 顶栏摘要显示 `Role · Mode · IP:Port · Dev n · T3 ns`。
- 连接期间锁定连接参数。
- 多个实例共用 exe 旁的这一份参数文件，谁后存谁生效（点「连接」时也会存一次）；日志目录带进程号，不会互相覆盖。要同时跑两条参数不同的连接，有两种办法：两个实例都先开好、各自改好参数再分别点连接；或者把 exe 复制一份到另一个目录，两边各有一份 `QoderSecsData/settings.json`，互不影响。注意先连上的那个实例会把参数写回文件，之后再启动的实例读到的是它写的那份。
- 程序只提供 `127.0.0.1` 的默认参数，不写入真实设备信息。

运行数据位于 exe 旁：

```text
QoderSecsData/
├─ settings.json
└─ logs/
   └─ <配置 Id>/
      └─ <yyyyMMdd_HHmmssfff_pid>/
         ├─ effective-profile.json
         ├─ sml.log
         └─ detail.log
```

数据目录早先叫 `data`，辨识度太低（拷到同事机器上和别的工具挤在一起，看不出是谁的、也不敢删）。现在启动时如果发现只有老的 `data`、没有 `QoderSecsData`，就整体改名搬过去并在状态栏说一声；搬不动（被别的实例占着）就保留老目录原样、程序改用新目录，不删任何东西。

## 5. 消息库

- 程序启动时消息库为空，只保存在内存中。
- 样本消息库 `common-commands.sxml` 内嵌在 exe 里。启动时发现 exe 旁没有 `samples/common-commands.sxml` 就解出来一份，解出来之后它就是普通文件——改它、删它、换成自己的，程序都不再过问。这么做的唯一目的：只拿到一个 exe 的人也有东西能导入，不用先去找人要 `.sxml`。
- 解出来不等于加载。程序不自动读它：样本里的消息带着 AutoReply 字段，启动就加载等于程序内置了回复规则，和下面第 6 节的约定冲突；清空消息库后重启又冒回来，也容易被当成 bug。
- 消息库还没有来源时（本次会话没导入过、也没导出过），第一次打开导入对话框停在 `samples` 目录；有了来源就按系统记住的位置走，不每次都跳回来。
- 导入 `.sxml` 会替换当前消息库。
- 导出是唯一的消息库持久化方式。
- “清空消息库”需要防误触。
- 导入、导出和清空位于“文件”二级菜单；“新消息”独立显示。
- 消息按 SxFy 升序显示。
- 搜索支持消息名称、`S10F3` 和 `10 3`。

### 5.1 SXML 兼容

以原始 `XDocument` 作为权威数据，只局部修改已编辑节点。

必须保留：

- DataDictionary
- Maps
- 未知元素和属性
- 注释及消息顺序
- 未编辑消息的 XML 结构

导出承诺 XML 结构和未知内容等价，不承诺文件字节、缩进和空元素写法完全一致。导出文件必须能被 SecsExpress 打开。

### 5.2 结构编辑

支持类型：

- L、A、B、Boolean
- I1、I2、I4、I8
- U1、U2、U4、U8
- F4、F8

每个元素使用类型图标。选中节点后可以添加子项、添加同级和删除；同时提供右键菜单和快捷键。

值不合法时标红并禁止发送。v1 不支持数值数组；导入遇到不支持的数组时保留原 XML，但标记为不可编辑、不可发送。

SML 页签用于预览和粘贴一条完整消息，不支持批量消息和局部子树粘贴。正文超过预览上限时不生成预览，只显示大小和一句说明。

## 6. 收发与自动回复

- 从消息库选择消息发送。
- W-bit 请求必须按 SystemBytes 严格匹配 Secondary。
- 不按 SxFy 或到达时间猜测事务关系。
- T3 超时要明确显示。
- 超时后到达的回复标记为“无匹配请求（迟到）”。
- 收到无人等待的 Secondary 时明确报告。

v1 的自动回复只有一种来源：消息库里那条 SxFy 上配的 AutoReply 字段（值是另一条消息的 Name）。默认不开启，S1F1→S1F2、S1F13→S1F14 这类常用回复也由使用者在库里配好；程序不内置任何规则，也不按 SxF(y+1) 推导。

收到带 W 的 Primary 时，按名字精确找到目标消息，把它的结构发出去，W 位强制为 0。没配、目标名找不到或目标结构发不出去，都不回复，记录标“未自动回复”；发出去了标“已自动回复”。没有全局开关，逐条按库里的配置生效。收到的 Primary 串行处理，一条回完再收下一条。

自动回复必须通过收到请求的 `PrimaryMessageWrapper.TryReplyAsync` 发送，以保持原 SystemBytes。v1 不提供手动回复。

## 7. 日志

右栏包含：

- 上半区：通信记录；
- 下半区：所选记录的时间和 SML 正文。

通信记录显示方向、SxFy、名称、SystemBytes 和状态。请求与回复关联、耗时、待回复、T3 超时和迟到回复必须清楚可见。控制消息不进通信记录。

两个区域分别执行智能滚动：

- 默认跟随最新内容；
- 用户向上滚动或拖离底部后暂停；
- 新日志继续接收；
- 用户重新滚到底部后恢复。

不提供“跟随最新”“复制 SML”“存入消息库”按钮，也不在详情区重复显示方向、头、来源和大小摘要。

磁盘输出：

- `sml.log`：完整 SML；
- `detail.log`：连接事件、HSMS 头、SystemBytes、耗时、异常和回复来源。

日志由单消费者后台队列顺序写入。大消息通过 `SmlWriter.WriteSmlToAsync` 流式写入，不在 UI 线程格式化或落盘。

## 8. 大消息与界面性能

- 不设置类似 4KB 的应用层消息上限。
- 通信列表只长期持有轻量元数据，不持有完整大消息文本。
- 列表启用 UI 虚拟化。
- 大 SML 默认折叠，需要时异步分段读取日志。
- 界面里的文本框有字符上限：SML 预览 256KB，详情区一次 512KB，超出的部分指路到 `sml.log`。
- 收发线程上的日志失败（磁盘写不进去、会话已经关闭、正文格式化不了）只丢这条正文，记录降级成写明原因的短正文，连接不受影响。
- 网络、SXML、SML 和文件操作不得阻塞 UI 线程。
- 必须验证 1MB 和 10MB B 数据双向收发。

## 9. UI 布局

采用“左边选、中间改、右边看”的三栏布局：

- 顶部：参数摘要、可展开参数面板、连接状态、连接/断开。
- 左栏：消息库、搜索、新消息和文件菜单。
- 中栏：消息属性、图形结构树、属性编辑、SML 页签和发送。
- 右栏：通信记录和 SML 详情。

界面不照搬 SecsExpress 的多窗口布局。

## 10. v1 不做

- SECS-I
- 完整 GEM 数据模型和状态机
- 条件自动回复、脚本、变量和流程编排
- 内置自动回复规则，以及按 SxF(y+1) 推导回复
- 数值数组编辑
- 多候选自动回复选择
- 手动回复
- 一个进程内的多连接
- 独立 hex 面板

## 11. 开发阶段

### 阶段 0：工程基线

建立 solution、Core、App、Tests、Git 忽略规则和规格文件。

### 阶段 1：连接参数与 WPF 空壳

实现连接参数文件、运行目录、三栏窗口和连接参数面板。

### 阶段 2：消息库

实现 SXML 导入导出、保真、搜索、树编辑、校验和 SML 粘贴。

### 阶段 3：HSMS 连接

实现 Active/Passive、连接状态、发送、接收、SystemBytes 关联和 T3。

### 阶段 4：自动回复与通信记录

实现按库里配置的自动回复、未自动回复、孤立 Secondary、通信列表和详情。

### 阶段 5：磁盘日志与大消息

实现异步日志、流式 SML、大消息折叠、分段读取和 1MB/10MB 测试。

### 阶段 6：发布

完成双实例回环、自测、回归和单文件发布。

## 12. 每阶段完成标准

每个阶段都必须满足：

1. `dotnet build QoderSecs.sln --configuration Release` 零错误、零警告；
2. `dotnet test QoderSecs.sln --configuration Release --no-build` 全部通过；
3. 完成本阶段要求的回环或人工验收；
4. 说明已验证和无法自动验证的内容；
5. 检查差异和敏感信息后创建一个新的本地 Git 提交。

失败时不得跳过测试、绕过检查或报告完成。

## 13. 最终验收

- Host Active 与 Equipment Passive 在 `127.0.0.1` 互通。
- Equipment Active 与 Host Passive 在 `127.0.0.1` 互通。
- SystemBytes 匹配、T3、迟到回复和自动回复行为正确。
- 1MB、10MB 消息双向收发时 UI 不冻结。
- SML 和详细日志完整有序。
- 导出的 SXML 可由 SecsExpress 打开。（这一条只能由用户手动确认：参考资料里的 exe 不运行。程序这边能保证的是导出文件与 SecsExpress 原文件逐字节等价，见 §5.1。）
- 发布目录只有 `QoderSecs.App.exe` 一个文件（样本库内嵌在里面）。首次启动会在 exe 旁边解出 `samples/common-commands.sxml`、建出 `QoderSecsData/`，所以这个目录必须可写。程序启动后消息库是空的，样本要用户自己导入。
- 发布命令成功：

```text
dotnet publish src/QoderSecs.App/QoderSecs.App.csproj \
  --configuration Release \
  --runtime win-x64 \
  --self-contained false \
  -p:PublishSingleFile=true
```
