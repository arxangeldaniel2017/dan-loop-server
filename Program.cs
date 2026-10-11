using DanLoopServer.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

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
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddSingleton<AgentToolExecutor>();
builder.Services.AddHttpClient<RemoteModelClient>(client => client.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient<AgentRuntimeClient>(client => client.Timeout = TimeSpan.FromMinutes(15));
builder.Services.AddSingleton<AgentRuntimeService>();
builder.Services.AddHttpClient<GitHubService>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHostedService<JobWorker>();

var app = builder.Build();
await app.Services.GetRequiredService<StateStore>().InitializeAsync();
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/home/error");
    app.UseHsts();
}

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
app.UseAuthorization();

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
app.Run();

