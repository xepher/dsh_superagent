# DDD 阅读笔记：从 Source 到 Ticket

这份文档解释本实现的领域模型，以及它如何对应 dsh-superagent-prototype 的设计。面板里的“领域模型说明”页是它的精简版。

## 1. 依赖方向

```text
DshSuperagent.Domain
        ▲
        │  只依赖领域
DshSuperagent.Core（Application）
        ▲
        │  端口 / 用例
DshSuperagent.Providers.*（Adapters）
DshSuperagent.Web（Host + Infrastructure + Panel）
```

- **Domain** 没有 NuGet、没有 ASP.NET、没有 EF、没有企业微信。
- **Core** 依赖 Domain，定义端口（`ISourceProvider`、`ILlmProvider`、`IPullRequestProvider`、仓储、队列、事件）。
- **Providers** 只实现端口；WeCom/OpenAI/GitHub/Gitea 的 DTO、加密、HTTP 都留在适配器里。
- **Web** 是组合根与基础设施：EF Core、Data Protection、Cookie 认证、Razor Pages、后台 Worker。

这条依赖规则是可检查的：Domain 项目没有对其他项目的 `ProjectReference`。

## 2. 聚合与不变量

| 聚合 | 根 | 内部实体 / 值对象 | 守住的不变量 |
| --- | --- | --- | --- |
| Source | `Source` | Configuration | Provider 可替换；读取位置属于 Source；Provider 配置不是 Policy。 |
| Policy | `Policy` | `PolicyRule`、`SourcePolicy`、`AdmissionTable`、`AllowedUser` | 版本化；Gate 是确定性代码；规则/准入表/accept mode 同属一个版本。 |
| Thread | `Thread` | `Subscription` | 最多一个活动 Eval；新 Trigger 进入下一批；订阅不授予权限。 |
| Eval | `Eval` | `Decision` | 只有 Eval 能创建 Ticket；旧 Policy 版本会重新排队；observe 记录 would accept。 |
| Ticket | `Ticket` | `Mandate`、`Input`、`Assignment`、`Report` | 阶段由 Report/Input/Assignment 决定；人工停止撤回 Mandate；结束后可被人工跟进恢复。 |
| Channel | `Channel` | —（消息用独立实体） | 消息先存储再发送；失败退避重试；只有目标不存在才丢弃。 |

Trigger 是不可变的独立实体，按 `(SourceId, ExternalId)` 唯一。它不属于某个聚合的可变集合，因此重复回调不会重复创建 Ticket。

Correlation 是独立实体：它只增加上下文，不参与路由。Subscription 由 Thread 持有；Ticket 通过订阅接收后续消息。

## 3. 两条路径

```text
消息 / 扫描发现
      │
      ▼
Source Provider 解析 ──► InboundEvent
      │
      ▼
Trigger（去重、不可变）
      │
      ├── 已有工单拥有该 Thread（人工跟进）──► Input ──► Executor（不经过 Eval）
      │
      └── 其他 ──► Gate（确定性）──► Eval（研判）──► Decision
                                                       │
                                          accept ──────┴──► Ticket + Mandate
                                          ignore / stop / unavailable
```

- **Eval 路径**：新请求、推断候选、停止命令。Gate 先判断发送者和准入表；Eval 再对规则做研判；Decision 决定接单、忽略、停止或不可用。
- **订阅路径**：Thread 上已有开放 Ticket 时，只有允许列表内人员（或明确授权）的后续消息才作为 Input 交给原 Executor；不经过 Eval，不授予新权限。未列用户默认被准入表 `Ignore` 拒绝；结束的 Ticket 只有通过 Mandate 重新校验才会恢复。
- **Context-only 订阅**：只把消息交给 Ticket 作为上下文，不恢复结束的 Ticket，也不授予权限。

本实现中 `InboundIngestionService.SelectOwner` 实现 ownership；`EvalProcessor` 实现 Gate/Assessment/Decision；`Ticket.AddContextInput` 实现上下文扇出。

## 4. Gate 与 Assessment 的分工

**Gate 是代码**（`Policy.Gate`）：

- 明确 `@` 的允许用户 → `Explicit`
- 允许用户在有 Ticket 的 Thread 上跟进 → `Explicit`
- 未列用户在有 Ticket 的 Thread 上跟进 → 先看准入表；默认 `Ignore` 拒绝，`Inferred` 才成为推断候选（`off` 模式拒绝）
- 准入表 `Explicit` 单元格 → 即使未列也 `Explicit`
- 机器人必须在 Source 的机器人列表中，并且 `@` 或 Source 接收全部消息
- 系统 Source（代码扫描）产生的候选 → `Inferred`

**Assessment 是模型层**（`IPolicyAssessor`）：

- 只对已准入的 `Inferred` 候选运行。
- 对每条启用规则逐一判断；至少命中一条才能接单。
- 输出 `accept`、命中的规则 ID、理由、scope（answer/fix）。
- 未配置模型时使用确定性规则匹配，不会把“没有模型”变成“自动接单”。

**Decision 与 accept mode**：

| accept mode | 明确请求 | 推断候选 |
| --- | --- | --- |
| `off` | 接单 | 拒绝（Gate 直接 NotAdmitted） |
| `observe` | 接单 | 研判；正的结论记录为 `WouldAccept`，不建单 |
| `auto` | 接单 | 研判通过后建单 |

## 5. Mandate

Mandate 是“为什么这次工作可以做”的事实：

- Policy 版本
- 强度（explicit/inferred）
- 范围（Answer/Fix）
- 命中的规则
- 请求者身份与接收范围
- 依据（明确请求/跟进/准入表/机器人规则）

它只在三种情况下变化：

1. Eval accept → 创建
2. 人工 stop → 撤回
3. 恢复前重新校验失败 → 撤销

Policy 变化不会立即撤销运行中的 Mandate；只会让旧版本 Eval 重新排队，并在工单下一次恢复前重新校验。

## 6. Ticket 阶段

阶段由最新 Report、Input 投递失败、Assignment 状态共同决定，Ticket 自己定义状态，不照搬 Executor Session。

| 阶段 | 条件 |
| --- | --- |
| PersonDelivery | Input 投递失败 |
| PersonAnswer | 有 Attention（本实现保留字段，当前主要通过 Report 的 HumanAction 表达） |
| PersonReview | 最新 Report 是 `ready` 且有 PR |
| PersonBlocked / PersonFailed | 最新 Report 是 blocked/failed |
| WorkingQueued | 无 Assignment 或 pending |
| Idle | 最新 Report 是 waiting |
| Working | 其他 |
| Ended | 结束 |

结束规则：

- `ready` 没有 PR → 完成，结束。
- `ready` 有 PR → 等待人工复核，保持开放。
- `dropped` → 不需要处理，结束。
- 人工 stop → 结束，Mandate 撤回，不再接收输入。
- 人工跟进 → 重新校验 Mandate 后恢复。

## 7. 存储工作（stored work）与一致性

原型强调“先存储，再做”。本实现遵循：

- Trigger、Eval、Ticket、Input、Assignment、Report、ChannelMessage 都先写入数据库。
- 队列只保存 ID；Worker 再按 ID 从数据库加载。
- **入队发生在 `SaveChanges` 之后**，否则 Worker 可能读不到尚未提交的 Eval/Ticket。
- Channel 消息先入库；`ChannelDeliveryWorker` 轮询待发送消息，失败按指数退避重试；三次失败后显式标记 dropped，卡在 Sending 的消息由恢复 Worker 重置。
- 启动时恢复 queued Eval/Assignment，并重置被中断的 Running Eval/Assignment；内存队列对同一 WorkItem 去重，Worker 用 in-flight 集合防止并发重复执行。Running 超过 15 分钟的 Eval/Assignment 由恢复 Worker 重置；Eval 三次恢复仍无进展则变为 unavailable 并释放 Thread。

EF 持久化细节：

- Guid 主键由领域对象生成，因此新增子实体加入导航集合后，`AppDbContext.SaveChanges` 会在检测变更前把未跟踪的子实体显式标记为 `Added`，避免 EF 把带主键的新对象误判为 `Modified`。
- SQLite 不支持 `ORDER BY DateTimeOffset`，统一用 `DateTimeOffsetToBinaryConverter` 存储为可排序的整数。
- Source 配置、LLM/PR 配置通过 Data Protection 加密后落库。
- Domain 聚合直接由 EF 映射；`DshSuperagent.Web/Data` 是基础设施，不是领域层。

## 8. 领域事件

领域事件只用于可观测性，不驱动路由：

- `TriggerRecorded`
- `EvalCompleted`
- `TicketAccepted`
- `ReportRecorded`
- `TicketEnded`
- `PolicyChanged`

`EfDomainEventSink` 在 `SaveChanges` 之前把它们写成 AuditEvent。路由仍由应用服务显式完成，避免“事件处理器偷偷改变授权”。

## 9. 读模型

面板的列表页直接使用 EF 查询做读模型（CQRS 的读侧），不强制加载完整聚合。详情/命令页使用聚合与领域方法。这是有意的取舍：查询简单、页面快，但所有写操作仍通过领域对象与用例。

## 10. 代码阅读顺序

1. `Domain/Sources/Source.cs`：Source 与配置。
2. `Domain/Sources/Policy.cs`：Policy、规则、准入表、Gate。
3. `Domain/Threads/Thread.cs`：Trigger、Thread、Subscription、Disposition。
4. `Domain/Authorization/Eval.cs`：Eval、Decision。
5. `Domain/Work/Ticket.cs`：Mandate、Ticket、Input、Assignment、Report。
6. `Domain/Authorization/MandateRevalidator.cs`：恢复前重新校验。
7. `Core/Application/InboundIngestionService.cs`：入口、Gate、订阅/ownership、Eval 排队。
8. `Core/Application/EvalProcessor.cs`：研判与唯一创建 Ticket 的地方。
9. `Core/Application/TicketExecutionService.cs`：Assignment → Executor → Report → Channel。
10. `Core/Application/RepositoryScanService.cs`：自主发现的 Source 实现。
11. `Core/Application/ChannelDeliveryService.cs`：存储工作的投递与重试。
12. `Providers.WeCom/WeComAibotSourceProvider.cs`：Provider 适配器边界。

## 11. 为什么没有 AgentJob

`AgentJob` 会把“执行”变成一个可以绕过 Eval、Mandate 和 Report 的后台任务。这里刻意没有这个聚合：

- 工作只能由 Ticket 承载。
- 每次执行都必须有 Assignment。
- 结果只能通过 Report 回写。
- 渠道写入只能通过 ChannelMessage。

这样，任何“机器人自己决定去做一件事”的路径都必须先回答：谁授权的？范围是什么？依据哪个 Policy 版本？做完后如何报告？

## 12. DeepSeek 想表达的设计观

- **注意力是稀缺资源**：默认 observe、默认不主动、只在需要人行动或可使用结果时发言。
- **少概念、少设置**：业务需要变成某个概念的属性，或一段提示词，而不是新增一个聚合。
- **用 Mandate 约束自治**：规则写清“什么值得做”，Mandate 记录“这次为什么可以做”，Executor 只在范围内行动。
- **把外部世界挡在领域外**：企业微信、GitHub、Gitea、LLM 都是适配器；换渠道/模型不改授权逻辑。
- **诚实交付**：PR 不是 ready；补丁未验证不是修复；失败就报告失败。
- **从窄开始**：短允许列表、observe 模式、proactive 工作先只记录。

## 13. 本实现的取舍

- 没有实现 GitHub Issue/PR 事件 Source；因此 PR 合并/关闭不会自动形成 Trigger。工单详情提供人工停止/重试/排入执行。
- 没有自动 merge。
- Eval 的 Assessment 在单进程内同步完成；没有多轮工具调用或 Eval 超时重试的完整版。
- Policy 变化会给旧版本的开放 Ticket 写 PolicyChange Input；没有实现完整的“差异比较”（哪些规则真正受影响）。
- Correlation 表已存在，但当前 Provider 没有产生跨 Thread 关联。
- 单实例内存队列 + SQLite；多实例需要替换队列与数据库。
- PR 分支名使用「仓库 + finding 签名 + 基线 commit」的确定性 hash；同一 commit 的同一问题重跑会复用分支，跨 commit 不会冲突。
- 测试使用假 LLM/PR，不调用真实外部服务；WeCom 加密/签名使用官方协议实现。
