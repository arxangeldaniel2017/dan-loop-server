using System.Security.Claims;
using DanLoopServer.Models;
using DanLoopServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DanLoopServer.Controllers;

public sealed class AccountController(StateStore store, PasswordService passwords) : Controller
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
