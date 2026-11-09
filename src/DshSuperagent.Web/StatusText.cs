using DshSuperagent.Domain.Channels;

namespace DshSuperagent.Web;

/// <summary>
/// Message status is transport truth, not decoration: "已存入，等待客户端拉取" means the provider
/// stored the content and nobody has seen it yet, so an operator must not read it as delivered.
/// </summary>
public static class StatusText
{
    public static string Label(MessageStatus status) => status switch
    {
        MessageStatus.Pending => "待投递",
        MessageStatus.Sending => "投递中",
        MessageStatus.AwaitingPull => "已存入，等待客户端拉取",
        MessageStatus.Sent => "已送达",
        MessageStatus.Failed => "失败",
        MessageStatus.Dropped => "已丢弃",
        _ => status.ToString(),
    };

    public static string Badge(MessageStatus status) => status switch
    {
        MessageStatus.Sent => "success",
        MessageStatus.Failed or MessageStatus.Dropped => "danger",
        _ => "warning",
    };
}
