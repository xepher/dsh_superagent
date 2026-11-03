using System.Security.Claims;
using DshSuperagent.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DshSuperagent.Web.Pages;

public sealed class LoginModel(AdminCredentials credentials, LoginThrottle throttle) : PageModel
{
    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }
    public bool ShowDefaultPasswordWarning { get; private set; }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;
        ShowDefaultPasswordWarning = credentials.IsDefaultPassword;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ShowDefaultPasswordWarning = credentials.IsDefaultPassword;
        var key = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (throttle.IsBlocked(key, out var retryAfter))
        {
            Error = $"尝试次数过多，请在 {Math.Ceiling(retryAfter.TotalMinutes)} 分钟后重试。";
            return Page();
        }

        if (string.IsNullOrEmpty(Password) || !credentials.Verify(Password))
        {
            throttle.Fail(key);
            Error = "密码不正确。";
            return Page();
        }

        throttle.Success(key);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "admin"),
                new Claim(ClaimTypes.Name, "管理员"),
                new Claim(ClaimTypes.Role, "Admin"),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });
        return Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl!) : RedirectToPage("/Index");
    }
}
