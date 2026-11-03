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
4. 同一 msgid 重复回调不会创建第二个工单（去重键是 Source + 外部 msgid）。

如果流式回复一直不结束：

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
