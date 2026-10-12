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
        bool enabled, bool useStreaming, bool allowFileRead, bool allowFileWrite, bool allowGitHubPublish,
        int maxSteps, int maxOutputTokens, int maxFileOutputTokens, int maxToolOutputCharacters)
    {
        await store.UpdateAsync(state =>
        {
            state.AgentRuntime.Settings = new AgentRuntimeSettings
            {
                Enabled = enabled,
                UseStreaming = useStreaming,
                AllowFileRead = allowFileRead,
                AllowFileWrite = allowFileWrite,
                AllowGitHubPublish = allowGitHubPublish,
                MaxSteps = Math.Clamp(maxSteps, 1, 50),
                MaxOutputTokens = Math.Clamp(maxOutputTokens, 32, 16_384),
                MaxFileOutputTokens = Math.Clamp(maxFileOutputTokens, 32, 131_072),
                MaxToolOutputCharacters = Math.Clamp(maxToolOutputCharacters, 1000, 100_000)
            };
            state.Messages.Add(new AppMessage { Level = MessageLevels.Info, Text = "Advanced AI workspace configuration saved." });
            return true;
        });
        return Redirect("/#tools");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RestoreDefaults()
    {
        await store.UpdateAsync(state =>
        {
            state.AgentRuntime.Settings = new AgentRuntimeSettings();
            state.Messages.Add(new AppMessage { Level = MessageLevels.Info, Text = "Secure AI workspace defaults restored." });
            return true;
        });
        return Redirect("/#tools");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearAudit()
    {
        await store.UpdateAsync(state =>
        {
            state.AgentRuntime.AuditTrail.Clear();
            state.Messages.Add(new AppMessage { Level = MessageLevels.Info, Text = "Tool activity history cleared." });
            return true;
        });
        return Redirect("/#tools");
    }
}
