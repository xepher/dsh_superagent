using DshSuperagent.Core.Application;
using DshSuperagent.Core.Providers.Llm;
using DshSuperagent.Core.Providers.PullRequests;
using DshSuperagent.Core.Providers.Sources;
using DshSuperagent.Core.Storage;
using DshSuperagent.Domain.Sources;
using DshSuperagent.Providers.Common;
using DshSuperagent.Providers.Feishu;
using DshSuperagent.Providers.GitHub;
using DshSuperagent.Providers.Gitea;
using DshSuperagent.Providers.OpenAiCompatible;
using DshSuperagent.Providers.WeCom;
using DshSuperagent.Web.Data;
using DshSuperagent.Web.Endpoints;
using DshSuperagent.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Composition root only: every business rule below is enforced by Domain and Application services.
// Providers are selected by configuration, never by the domain.
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin");
    options.Conventions.AuthorizePage("/Index");
    options.Conventions.AuthorizePage("/Logout");
});

var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
var keyDirectory = Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keyDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
    .SetApplicationName("dsh-superagent");

var connectionString = builder.Configuration.GetConnectionString("Superagent")
    ?? $"Data Source={Path.Combine(dataDirectory, "superagent.db")}";
builder.Services.AddDbContextFactory<AppDbContext>(options =>
{
    options.UseSqlite(connectionString);
    if (builder.Environment.IsEnvironment("Testing"))
    {
        options.EnableSensitiveDataLogging();
    }
});
builder.Services.AddScoped<AppDbContext>(provider =>
    provider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddSingleton<IStreamReplyStore, EfStreamReplyStore>();
builder.Services.AddSingleton<IWorkQueue, InMemoryWorkQueue>();
builder.Services.AddSingleton<AdminCredentials>();
builder.Services.AddSingleton<LoginThrottle>();

builder.Services.AddHttpClient("wecom", client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("llm", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient("github", client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient("gitea", client => client.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services.AddSingleton<IProviderCommandRunner, ProviderCommandRunner>();
builder.Services.AddSingleton<ISourceProvider, WeComAibotSourceProvider>();
builder.Services.AddSingleton<ISourceProvider, FeishuSourceProvider>();
builder.Services.AddSingleton<ISourceProvider, GitHubSourceProvider>();
builder.Services.AddSingleton<ISourceProvider, GiteaSourceProvider>();
builder.Services.AddSingleton<ILlmProvider, OpenAiCompatibleLlmProvider>();
builder.Services.AddSingleton<IPullRequestProvider, GitHubPullRequestProvider>();
builder.Services.AddSingleton<IPullRequestProvider, GiteaPullRequestProvider>();
builder.Services.AddSingleton<SourceProviderRegistry>();
builder.Services.AddSingleton<LlmProviderRegistry>();
builder.Services.AddSingleton<PullRequestProviderRegistry>();

builder.Services.AddScoped<ISourceRepository, EfSourceRepository>();
builder.Services.AddScoped<IPolicyRepository, EfPolicyRepository>();
builder.Services.AddScoped<IThreadRepository, EfThreadRepository>();
builder.Services.AddScoped<ITicketRepository, EfTicketRepository>();
builder.Services.AddScoped<IChannelRepository, EfChannelRepository>();
builder.Services.AddScoped<EfConfigurationStore>();
builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
builder.Services.AddScoped<IDomainEventSink, EfDomainEventSink>();

builder.Services.AddScoped<IConfigurationStore, EfConfigurationStore>();
builder.Services.AddScoped<IPolicyAssessor, RuleAndModelPolicyAssessor>();
builder.Services.AddScoped<InboundIngestionService>();
builder.Services.AddScoped<EvalProcessor>();
builder.Services.AddScoped<TicketExecutionService>();
builder.Services.AddScoped<ChannelDeliveryService>();
builder.Services.AddScoped<RepositoryScanService>();
builder.Services.AddScoped<PolicyChangeService>();
builder.Services.AddSingleton<ISourceProvider, RepositoryScanSourceProvider>();
builder.Services.AddScoped<ITicketExecutor, RepositoryQuestionExecutor>();
builder.Services.AddScoped<ITicketExecutor, RepositoryFixExecutor>();
builder.Services.AddHostedService<WorkQueueProcessor>();
builder.Services.AddHostedService<ChannelDeliveryWorker>();
builder.Services.AddHostedService<RepositoryReviewScheduler>();
builder.Services.AddHostedService<StaleWorkRecoveryWorker>();
builder.Services.AddHostedService<SourceReceiverWorker>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.AccessDeniedPath = "/Login";
        options.Cookie.Name = "dsh.superagent.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    if (!await db.Policies.AnyAsync())
    {
        db.Policies.Add(Policy.CreateDefault());
        await db.SaveChangesAsync();
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
    context.Response.Headers.TryAdd("X-Frame-Options", "DENY");
    context.Response.Headers.TryAdd("Referrer-Policy", "no-referrer");
    context.Response.Headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapSourceCallbacks();
app.MapGet("/health", () =>
{
    return Results.Json(new
    {
        status = "ok",
        version = typeof(Program).Assembly.GetName().Version?.ToString(),
        time = DateTimeOffset.UtcNow,
    });
});

app.Run();

public partial class Program;
