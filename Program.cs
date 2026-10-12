using DanLoopServer.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["Storage:KeyPath"] ?? "/data/keys"))
    .SetApplicationName("dan-loop-server");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/account/login";
        options.AccessDeniedPath = "/account/denied";
        options.Cookie.Name = "dan-loop.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync("Too many sign-in attempts. Try again later.", cancellationToken);
    };
    options.AddPolicy("login", context => RateLimitPartition.GetSlidingWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 12,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<S3ObjectStorage>();
builder.Services.AddSingleton<AiConnectionCatalogService>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<LoginSecurityService>();
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddSingleton<ProjectPlanService>();
builder.Services.AddSingleton<JobCancellationRegistry>();
builder.Services.AddSingleton<AgentToolExecutor>();
builder.Services.AddHttpClient<RemoteModelClient>(client => client.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient<AiModelDiscoveryService>(client => client.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddHttpClient<TurnstileService>(client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<AiGenerationService>(client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient<AgentRuntimeClient>(client => client.Timeout = TimeSpan.FromMinutes(15));
builder.Services.AddHttpClient(nameof(BunnyStorageClient), client => client.Timeout = TimeSpan.FromMinutes(15));
builder.Services.AddSingleton<AgentRuntimeService>();
builder.Services.AddHttpClient<GitHubService>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<GitProviderService>(client => client.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddHostedService<GitHubSyncWorker>();
builder.Services.AddHostedService<S3SyncWorker>();

var app = builder.Build();
await app.Services.GetRequiredService<StateStore>().InitializeAsync();
await app.Services.GetRequiredService<AiConnectionCatalogService>().InitializeAsync();
await app.Services.GetRequiredService<S3ObjectStorage>().InitializeAsync();
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/home/error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        return Task.CompletedTask;
    });
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.Use(async (context, next) =>
{
    var store = context.RequestServices.GetRequiredService<StateStore>();
    var path = context.Request.Path;
    if (!store.IsSetup && !path.StartsWithSegments("/account/setup") && !path.StartsWithSegments("/health") && !path.StartsWithSegments("/css") && !path.StartsWithSegments("/js"))
    {
        context.Response.Redirect("/account/setup");
        return;
    }
    await next();
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
app.Run();

