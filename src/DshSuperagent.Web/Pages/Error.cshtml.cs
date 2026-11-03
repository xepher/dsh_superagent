using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DshSuperagent.Web.Pages;

public sealed class ErrorModel : PageModel
{
    public string Message { get; private set; } = "服务器处理请求时发生错误。";

    public void OnGet(int? statusCode = null)
    {
        if (statusCode is not null)
        {
            Message = $"HTTP {statusCode}";
        }
    }
}
