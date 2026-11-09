# 企业微信智能机器人接入

本文只讲企业微信侧配置。领域模型与整体架构见 [DDD.md](DDD.md)。

## 1. 创建智能机器人

在企业微信管理后台创建一个“智能机器人”，记录：

- **Token**
- **EncodingAESKey**（43 位）
- 机器人的**名称**（例如“代码助手”）

本实现使用企业微信**自建智能机器人回调**，回调报文是加密 JSON；`receiveId` 对自建智能机器人通常留空。

## 2. 启动服务

```bash
SUPERAGENT_ADMIN_PASSWORD='change-me' \
ASPNETCORE_URLS='http://0.0.0.0:8080' \
dotnet DshSuperagent.Web.dll
```

企业微信要求回调地址为公网可达的 HTTPS。建议放在 Nginx/Caddy 后面由代理终止 TLS：

```text
https://superagent.example.com  →  http://127.0.0.1:8080
```

如果使用反向代理，确保转发 `X-Forwarded-Proto` 和 `Host`，否则面板显示的 Source 回调 URL 可能仍是 http。

## 3. 在面板新增 Source

打开「Source」→「新增 Source」：

- Provider：企业微信智能机器人
- 名称：例如“研发群”
- 默认代码仓库：选择要回答/修复的仓库
- 回调 Token：后台的 Token
- EncodingAESKey：后台的 43 位 key
- 机器人名称：必须与群里 `@` 的名称一致，例如“代码助手”
- 额外 `@` 别名：可选
- AibotId：可选，填写后只接受该机器人 ID 的回调
- 回复方式：
  - 流式消息（推荐）：立即回复“正在分析”，完成后返回最终答案
  - response_url：只发送最终答案，回调不立即回复
- response_url 主机白名单：默认 `https://qyapi.weixin.qq.com`
- ReceiveId：通常留空

保存后页面会显示：

```text
https://你的域名/api/sources/{source-id}/callback
```

把这个地址填到企业微信后台的“接收消息服务器 URL”。企业微信会立即发 GET 验证：

- 校验 `msg_signature`
- 解密 `echostr`
- 原样返回明文

如果验证失败，检查 Token、EncodingAESKey 是否与后台完全一致，以及反向代理是否改写了查询字符串。

## 4. 配置允许用户

打开该 Source 的「策略」页：

```text
u1, 张三
u2, 李四
```

- 每行一个企微 `userid`，可附显示名。
- 允许列表为空时拒绝所有人。
- 群聊消息必须 `@机器人名称` 才会进入领域；单聊直接进入。
- 未列用户会在群里收到一条拒绝提示，但不会创建工单。
- 机器人列表用于允许自动化账号产生 `inferred` 候选；企业微信自建机器人通常不需要。
- 准入表控制“未 @ 的消息”和“未列用户 @ 的消息”是否成为候选。

## 5. 测试回调

最简单的方式：

1. 在群里 `@机器人 帮助`，应收到使用说明。
2. `@机器人 用户列表页的分页逻辑在哪里？`，应先收到处理中，再收到回答。
3. 未列用户 `@机器人`，应收到“未在 Source 的允许列表中”。
4. 同一 msgid 重复回调不会创建第二个工单（去重键是 Source + 外部 msgid）。重复回调保留首次回复的内容与流式 ID，但使用本次回调 URL 中的 nonce 和当前秒级时间戳重新签名，不能原样重放已保存的加密响应外层。

### 回复成功的判断与诊断日志

`/help` 是内置命令，不依赖代码仓库、模型服务或 PR Provider；只有 Source 和允许用户配置也应能回复。

流式回复与 `response_url` 是两条独立链路，切换模式只能辅助定位，不能作为另一条链路已经修复的证据：

- **流式消息**：企微的 Stream ID 是一次性应答地址，只能承载一条回复。Provider 写入后返回 `AwaitingPull`，Channel 消息状态也是 `AwaitingPull`（已存入等待企微刷新拉取，不代表客户端已经收到）；只有 `response_url` / 应用消息这类确认送达的链才是 `Sent`。需要分别确认初始消息显示、收到刷新回调、最终内容显示且流式结束。
- **同一线程连续提问**：一张工单会聚合多条提问，回答只投给**最新的、仍可写的应答地址**。若你在同一线程里连问两次，旧问题的 Stream ID 会在新回答发出后以它当前的内容收尾（不会编造新文案），答案只出现在最后一次提问的气泡里。
- **response_url**：只有 HTTP 成功且返回合法 JSON、`errcode=0`，程序才将本次投递视为成功；HTTP 200 的非零业务错误、缺失有效 `errcode` 或非 JSON 响应均作为失败处理。接口确认仍不等同于客户端展示凭据。

### 加密回包必须是原样 UTF-8

企微会自行解析加密回包的外层 JSON 与解密后的明文 JSON。回包必须用**原样 UTF-8** 序列化，不能把中文转成 `\uXXXX`，也不能把 Base64 密文里的 `+` 转成 `\u002B`。

.NET 的 `JsonSerializer` 默认使用会转义的编码器；本实现因此在 WeCom Provider 里改用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`（`WireJson`）。症状是：服务端 HTTP 200、回包能自验签解密，但企微仍在 1~3 秒后重复回调同一 `msgid`，且从不发流式刷新；客户端看不到任何消息。改用原样 UTF-8 后，企微立刻接受回包并开始刷新流式内容。

`WeComFlowTests.Encrypted_Ack_Is_Raw_Utf8_Without_Json_Escapes` 固定住这个约定。

排查流式模式完全无回复时，按顺序查看日志：

1. `SourceCallback` 的“回调开始”：确认请求到达，使用 Source ID 与 Request ID 对应同一次请求。
2. `WeComAibotSourceProvider` 的“初始流式响应已构造”：记录 Stream ID 与加密响应字节数，不记录消息正文。
3. `SourceCallback` 的“回调响应已准备”“回调 HTTP 写出完成”：查看状态码、响应大小及耗时；写出完成不代表企微接受或展示。
4. `WeComAibotSourceProvider` 的“企微流式刷新”：`Found` 表示流式状态存在，`Finished` 表示生成是否结束；使用 Stream ID 对应初始响应。
5. 若出现“企微重复回调”，本次只是对首次保存的回复重新签名，不会创建新 Trigger 或新流式状态；此前“初始流式响应已构造”中的新 Stream ID 不会用于重复回调的实际响应。
6. 若初始响应已写出但没有刷新记录，应继续核对回调时限、加密响应协议以及公网代理链路，不能仅凭 Channel 的 `AwaitingPull` 判断流式成功。

诊断日志不记录 Token、EncodingAESKey、签名、回调查询字符串、完整 response_url 或消息正文。部署新代码并重启后日志才会生效；没有历史刷新日志不代表历史上一定没有刷新请求。

如果流式回复一直停在“已收到，正在分析代码仓库…”：

- 检查「工单」页的 Channel 消息：回答是否挂在了一条更早消息的 Channel 上（`ChannelMessages.ChannelId` 对应的不是最新提问）。旧版本会把回答写回建单时那条消息的 Stream ID，该 Stream 已结束，企微不会再拉取。
- 当前版本回答会写到最新的、仍可写的应答地址；若一个都不可写，工单上会出现 `DeliveryError`，而不是静默当作已发送。
- 检查「工单」页是否有失败 Report。
- 检查「运行设置」中的 Unavailable Eval / 失败 Channel 消息。
- 检查服务日志中的 `Channel 消息投递失败`。
- 确认模型 API 可达；没有模型时问答会降级为本地代码检索，仍应完成。
- 确认 `response_url` 模式没有把 URL 主机改错；默认使用企微官方主机，也可在 Source 中配置为其他受控 HTTPS 主机。

## 6. 回调安全

- `msg_signature` 使用 Token、timestamp、nonce、encrypt 排序后 SHA1。
- AES-256-CBC，PKCS#7 填充到 32 字节；IV 取 AESKey 前 16 字节。
- 明文结构：16 字节 random + 4 字节网络序 msg_len + msg + receiveId。
- `response_url` 只允许 HTTPS、默认端口、配置的白名单主机和 `/cgi-bin/aibot/response` 路径。
- Source 配置（含 Token/AESKey）通过 Data Protection 加密落库。

## 7. 常见问题

**群聊没有反应**

- 消息没有 `@` 机器人名称，或名称与 Source 配置不一致。
- 用户不在允许列表。
- Source 被停用（回调返回 404）。

**URL 验证失败**

- Token / EncodingAESKey 填错。
- URL 不是公网 HTTPS，或代理没有转发完整的 query string。
- 服务在反向代理后没有正确识别 scheme/host；检查回调 URL 显示值。

**回复“会话已结束或过期”**

- 流式消息在 48 小时后清理；重新提问即可。
- Worker 未完成或 Channel 投递失败；查看工单页与日志。

**PR 没有创建**

- PR Provider 默认 dry-run；到「PR Provider」关闭 dry-run。
- Token 权限不足，或仓库 owner/repo 无法从 RemoteUrl 识别；在仓库设置中显式填写 owner/repo。
- 仓库没有开启「允许自动修复」，或 Policy accept mode 不是 `auto`（推断候选）。
- 补丁验证命令失败；工单 Report 会显示失败原因，补丁仍可在详情下载。
