using DanLoopServer.Models;
using DanLoopServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DanLoopServer.Controllers;

[Authorize(Roles = Roles.Admin)]
public sealed class AgentRuntimeController(StateStore store) : Controller
{
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(
        bool enabled, bool useStreaming, bool allowFileRead, bool allowFileWrite, bool allowCommands,
        bool allowGitCommit, bool allowGitPush, int maxSteps, int maxOutputTokens,
        int commandTimeoutSeconds, int maxToolOutputCharacters, List<string>? allowedCommands, string? customCommands)
    {
        var commands = (allowedCommands ?? []).Concat((customCommands ?? "").Split(['\r', '\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(command => Path.GetFileName(command) ?? "").Where(command => command.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
        await store.UpdateAsync(state =>
        {
            state.AgentRuntime.Settings = new AgentRuntimeSettings
            {
                Enabled = enabled,
                UseStreaming = useStreaming,
                AllowFileRead = allowFileRead,
                AllowFileWrite = allowFileWrite,
                AllowCommands = allowCommands,
                AllowGitCommit = allowGitCommit,
                AllowGitPush = allowGitPush,
                MaxSteps = Math.Clamp(maxSteps, 1, 50),
                MaxOutputTokens = Math.Clamp(maxOutputTokens, 32, 32_768),
                CommandTimeoutSeconds = Math.Clamp(commandTimeoutSeconds, 5, 600),
                MaxToolOutputCharacters = Math.Clamp(maxToolOutputCharacters, 1000, 100_000),
                AllowedCommands = commands
            };
            return true;
        });
        TempData["Message"] = "Advanced tool configuration saved.";
        return Redirect("/#tools");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RestoreDefaults()
    {
        await store.UpdateAsync(state => { state.AgentRuntime.Settings = new AgentRuntimeSettings(); return true; });
        TempData["Message"] = "Secure tool defaults restored.";
        return Redirect("/#tools");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearAudit()
    {
        await store.UpdateAsync(state => { state.AgentRuntime.AuditTrail.Clear(); return true; });
        TempData["Message"] = "Tool activity history cleared.";
        return Redirect("/#tools");
    }
}
