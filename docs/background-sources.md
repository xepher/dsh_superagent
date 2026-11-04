# 飞书、GitHub 与 Gitea 后台来源

本实现对照原版 `deepseek-ai/dsh-superagent-prototype` 提交 `8c32a9dc5d72c033816c6a8110b37f40cf94fba4` 的来源接入方式：飞书使用 `lark-cli` 机器人事件流，GitHub 使用 `gh` 轮询；Gitea 使用自身 REST API 复用相同的来源语义。企业微信 HTTP 回调、代码扫描和已有 PR 创建能力均保留。

## 通用设置与运行边界

1. 在管理面板 **Source** 新增对应 Provider。每个代码托管 Source 监听一个 `owner/name`，不是整个账号的所有仓库。
2. 明确选择对应的本地代码仓库，尤其是已配置多个本地仓库时。监听的远端仓库不等于自动克隆或自动授权本地目录。
3. 配置 Source Policy：飞书允许用户填 `open_id`，GitHub/Gitea 填账号 `login`。接收范围、`@` 与准入、允许列表是不同层次；空允许列表仍拒绝用户。需要自主接单时还要显式配置准入表及 accept mode。
4. CLI 必须提前安装在宿主机，并能由运行 Web 服务的操作系统账号访问。填写可执行文件或绝对路径，不支持 shell 命令字符串；Windows 使用原生可执行文件，不使用 `.cmd`/`.bat` 作为 shell 包装器。
5. 点击“测试配置”会访问实际 CLI/平台：它不是离线验证，也不会执行模型推理。自动接收只针对已启用且必填字段完整的 Source 启动。

普通 CLI 命令最多运行 90 秒；内部超时作为错误上报并重试，外部停止令牌仍保留取消语义。

后台约每 5 秒对账。停用或配置指纹变化时先取消并等待旧接收器退出，再启动新接收器。读取位置、重试状态和健康信息保存在现有 `Source.ReadPosition`，无需新增数据库表；旧的内部来源纯文本位置保持兼容。停用不删除游标和历史。

首次启动按当前时间建立基线，不默认导入整个仓库或聊天的历史。之后处理成功才推进流游标，并保留 5 秒重叠，由 Source 外部事件 ID 去重。分页到达安全上限、响应无效或接收失败会报错，不假装读取成功。平台中已经删除、无权读取或未送达应用的内容无法保证补回。

仓库、服务端或身份一旦绑定后，不允许混用同一个 Source 的历史。更换这些身份请新建 Source；账号 Token 正常轮换且身份不变不受影响。当前仍是 SQLite 单实例，不支持多实例同时运行同一 Source。

## 飞书：`feishu`

- 按 `lark-cli` 官方说明配置自建应用机器人身份。`lark-cli auth status` 必须报告 bot ready，并通过 `lark-cli config strict-mode bot` 限制为机器人身份，不能回退为用户身份。
- 在飞书应用后台开放接收消息事件、读取所需群/私聊历史和机器人发消息权限，发布应用并加入目标群。需要读取群内非 @ 消息时，还需开通相应权限；`listen=all` 不能绕过平台权限。
- 配置 `cli`（默认 `lark-cli`）；事件接收调用 `event consume im.message.receive_v1 --as bot`，无需应用公网 HTTP 回调。
- `chats` 示例：

```json
[
  { "chatId": "oc_example", "listen": "mentions" },
  { "chatId": "oc_other", "listen": "all" }
]
```

- `directMessages` 默认 true；`otherChats` 默认 `none`，可改为 `mentions`。这不自动把所有私聊用户加入 Policy 允许列表。
- 显式群、已知私聊、近期话题和订阅线程会补收；尚未通过事件发现的私聊不能凭空枚举恢复。非开放工单维持的历史话题按 14 天窗口恢复。
- 事件流要求 45 秒内报告 ready，退出后退避重连；消息事件和历史共用标准化与去重链路。自身机器人消息不回灌。
- 单次历史扫描最多 200 页，重复 token 或超过上限会报错并保留游标，不静默截断。
- 回复使用 `im +messages-reply --reply-in-thread --as bot`，持久 ChannelMessage ID 作为 idempotency key；已删除/撤回目标的永久错误不反复投递。

目前落入领域的是文本/结构化消息的文本表示；没有实现原版全部多媒体能力，例如图片下载、视觉模型输入与富媒体渲染。不要把本次来源接入视为原版全部功能一比一移植。

## GitHub：`github`

- 安装 `gh`，为智能体准备有目标仓库写权限的专用账号。
- 填 `cli`、必填的预期 `account` login、`repository=owner/name`。
- 可填绝对路径 `configDir`，预先在该 `GH_CONFIG_DIR` 下完成 `gh auth login`。子进程清除继承的 `GH_TOKEN`/`GITHUB_TOKEN`，防止环境 Token 覆盖选定身份；实际 `/user` 必须匹配预期账号。
- API 固定 `github.com`，本版不支持 GitHub Enterprise。CLI 不经 shell 启动，POST JSON 通过 stdin，不记录完整命令参数。
- 默认接收该仓库通知及订阅讨论；可选择仓库新 Issue、新 PR、普通/代码评论、默认分支 CI 变化。开启接收选项不等于授权自主执行。
- 轮询最少 60 秒，并遵守返回的 `X-Poll-Interval`；临时错误退避。通知目标分别处理，一个失败不阻止其他通知读取；连续 24 小时无法读取的通知目标会记录告警后放弃该次通知。
- Issue/PR 正文修改以内容版本去重；评论和审查以事件版本去重。订阅 PR 检查审查、CI 及合并/关闭状态。
- 普通讨论回复 Issue/PR conversation；代码评论按 GitHub 官方 [review reply 端点](https://docs.github.com/en/rest/pulls/comments#create-a-reply-for-a-review-comment) 回复顶层 review comment，根评论删除后回退 conversation。持久投递 ID 标记与自身账号共同用于重试去重。
- 为覆盖远端已写入但本地确认前崩溃的窗口，发送前仍扫描已有评论寻找标记，最多 200 页；超大讨论可能增加延迟/配额消耗，达到上限会失败而不是盲目重复发送。

## Gitea：`gitea`

- 填完整 `endpoint`，如 `https://git.example.com/api/v1` 或 `https://example.com/gitea/api/v1`，以及专用账号 Token、`repository=owner/name`。
- 非回环实例必须 HTTPS；拒绝地址内凭据、查询和片段。不跟随重定向，认证请求限制在配置 API 根路径内。Token 通过现有 Source 配置加密存储。
- Token 需要读取用户、仓库、通知、Issue/PR/审查/提交状态和发表评论的权限；账号需有仓库写权限。
- 接收选项、游标、重试、订阅与生命周期语义参考 GitHub；不依赖或假设 `gh` 支持 Gitea。
- 单次请求包含响应头和正文在内合计限制 60 秒，限制响应体 8 MiB；超时进入退避，不会因响应头先到达而无限等待正文。
- 契约对照 [Gitea v1.24.6 OpenAPI](https://github.com/go-gitea/gitea/blob/v1.24.6/templates/swagger/v1_json.tmpl)：代码评论读取 `/pulls/{index}/reviews/{id}/comments`，不调用不存在的 GitHub 式 `/pulls/comments`。回复落到 PR conversation，不承诺 Gitea 行内线程回复。
- CI 使用 Gitea 提交状态，不把 GitHub Check Runs 当作通用 API；本版不实现 Gitea Actions 全部运行日志。

## 与 PR 创建和工单的关系

**Source 配置与 PR Provider 配置独立**：Source 用于接收和讨论；已有 GitHub Git Data API / Gitea Contents API Provider 继续负责创建 PR，并保持原 dry-run 配置。需要真实 PR 时须单独配置其 Token 和仓库 Owner/Name，建议使用同一智能体身份。

已生成且能匹配已启用 Source 的 PR 会关联到开放工单。该讨论的 `ContextOnly` 订阅同样先检查当前 Policy Gate，仅准入的事件补充既有工单上下文；未准入的人或机器人只记录 Filtered。即使准入也不执行 `/stop`、不创建新工单或扩大 Mandate。没有开放工单时只保留消息历史，处置记为 Filtered，不误报已补充上下文。合并会完成相关开放工单；关闭未合并会将其结束为 dropped，并把结果排入原 Channel。没有匹配 Source 时仍须人工跟进；系统不会自动 merge。

接入后的新请求仍经过 Trigger → Policy → Eval → Ticket，未建立新的授权旁路。所有真实平台接收、发送、权限及 CLI 版本兼容性需要部署后用专用测试账号验收；编译和 Mock 回归源码不能证明真实接入已通过。
