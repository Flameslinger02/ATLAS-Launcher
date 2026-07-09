using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// Shared interactive pre-launch gate: runs the <see cref="IMissionDependencyChecker"/> and, when the
/// rotation missions need addons no enabled mod provides, asks the user to Start Anyway or Cancel.
/// Only for interactive launch paths (Dashboard, profile workspace) — unattended launches (scheduler,
/// Discord, tray restarts) must never hang on a dialog and skip this gate.
/// </summary>
public static class MissionDependencyGate
{
    /// <summary>Returns true when the launch should proceed (no unmet deps, or the user overrode).</summary>
    public static async Task<bool> ConfirmAsync(
        IMissionDependencyChecker checker, IDialogService dialogs, ServerProfile profile)
    {
        var unmet = await checker.GetUnmetDependenciesAsync(profile).ConfigureAwait(true);
        if (unmet.Count == 0) return true;

        const int maxShown = 20;
        var list = string.Join("\n", unmet.Take(maxShown).Select(a => "  • " + a));
        if (unmet.Count > maxShown) list += $"\n  … and {unmet.Count - maxShown} more";

        return await dialogs.ConfirmAsync(
            "Missing mission dependencies",
            "The selected mission(s) require addons that no enabled mod appears to provide:\n\n" + list +
            "\n\nClients may fail to load or join. Start anyway?",
            "Start Anyway", "Cancel");
    }
}
