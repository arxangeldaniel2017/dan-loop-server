using System.Security.Claims;
using DanLoopServer.Models;
using DanLoopServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DanLoopServer.Controllers;

public sealed class AccountController(StateStore store, PasswordService passwords, RemoteModelClient modelClient) : Controller
{
    [AllowAnonymous, HttpGet]
    public IActionResult Setup() => store.IsSetup ? RedirectToAction(nameof(Login)) : View();

    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Setup(string username, string password, string confirmPassword)
    {
        if (store.IsSetup) return RedirectToAction(nameof(Login));
        username = username.Trim();
        if (username.Length is < 3 or > 64) ModelState.AddModelError("", "Username must be 3–64 characters.");
        if (password.Length < 10) ModelState.AddModelError("", "Password must be at least 10 characters.");
        if (password != confirmPassword) ModelState.AddModelError("", "Passwords do not match.");
        if (!ModelState.IsValid) return View();
        var user = await store.UpdateAsync(state =>
        {
            if (state.Users.Any(x => x.Role == Roles.Admin)) return null;
            var admin = new AppUser { Username = username, PasswordHash = passwords.Hash(password), Role = Roles.Admin };
            state.Users.Add(admin);
            return admin;
        });
        if (user is null) return RedirectToAction(nameof(Login));
        await SignInAsync(user);
        return RedirectToAction(nameof(ConfigureModel));
    }

    [Authorize(Roles = Roles.Admin), HttpGet]
    public IActionResult ConfigureModel() => View();

    [Authorize(Roles = Roles.Admin), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfigureModel(string? name, string? modelId, string? baseUrl, string? apiKey)
    {
        name = name?.Trim() ?? "";
        modelId = modelId?.Trim() ?? "";
        baseUrl = baseUrl?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(name)) ModelState.AddModelError("", "Model name is required.");
        if (string.IsNullOrWhiteSpace(modelId)) ModelState.AddModelError("", "Model ID is required.");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            ModelState.AddModelError("", "A valid HTTP(S) model URL is required.");
        if (!ModelState.IsValid) return View();

        await store.UpdateAsync(state =>
        {
            state.Models.Add(new RemoteModel
            {
                Name = name,
                ModelId = modelId,
                BaseUrl = baseUrl.TrimEnd('/'),
                ProtectedApiKey = modelClient.ProtectKey(apiKey ?? ""),
                IsEnabled = true
            });
            return true;
        });
        return RedirectToAction(nameof(CreateProject));
    }

    [Authorize(Roles = Roles.Admin), HttpGet]
    public async Task<IActionResult> CreateProject()
    {
        ViewBag.Models = await store.ReadAsync(state => state.Models.Where(x => x.IsEnabled).ToList());
        return View();
    }

    [Authorize(Roles = Roles.Admin), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateProject(string? name, string? description, string? systemPrompt, Guid? defaultModelId)
    {
        name = name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            ModelState.AddModelError("", "Project name is required.");
            ViewBag.Models = await store.ReadAsync(state => state.Models.Where(x => x.IsEnabled).ToList());
            return View();
        }

        await store.UpdateAsync(state =>
        {
            var validModelId = defaultModelId is not null && state.Models.Any(x => x.Id == defaultModelId && x.IsEnabled)
                ? defaultModelId
                : null;
            state.Projects.Add(new Project
            {
                Name = name,
                Description = description?.Trim() ?? "",
                SystemPrompt = systemPrompt?.Trim() ?? "",
                DefaultModelId = validModelId
            });
            return true;
        });
        return RedirectToAction("Index", "Home");
    }

    [AllowAnonymous, HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (!store.IsSetup) return RedirectToAction(nameof(Setup));
        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string username, string password, string? returnUrl = null)
    {
        var user = await store.ReadAsync(state => state.Users.FirstOrDefault(x =>
            x.IsEnabled && string.Equals(x.Username, username.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (user is null || !passwords.Verify(password, user.PasswordHash))
        {
            ModelState.AddModelError("", "Invalid username or password.");
            return View();
        }
        await SignInAsync(user);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    public IActionResult Denied() => View();

    private Task SignInAsync(AppUser user)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        return HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }
}
