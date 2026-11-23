using Lively.Common.Extensions;
using Lively.Common.Services;
using Lively.Models.Enums;
using Lively.Models.Interaction;

namespace Lively.Core.Interaction
{
    /// <summary>
    /// Short-circuit policy chain, order is fixed by docs/contracts/06-interaction-spec.md §4.2:
    /// master switch → wallpaper type → desktop foreground → fullscreen app → power → user whitelist.
    /// </summary>
    public class InteractionPolicy : IInteractionPolicy
    {
        private readonly IUserSettingsService userSettings;

        public InteractionPolicy(IUserSettingsService userSettings)
        {
            this.userSettings = userSettings;
        }

        public bool IsEnabled => userSettings.Settings.InteractionEnabled;

        public bool ShouldForward(InteractionContext context)
        {
            if (context == null || !IsEnabled)
                return false;

            var settings = userSettings.Settings;

            // Wallpaper type whitelist.
            if (!context.Category.IsDeviceInputAllowed())
                return false;

            // Only forward when the desktop is the foreground window.
            if (!context.IsDesktopForeground)
                return false;

            // Fullscreen application active (paused when rule is not ignore.)
            if (context.IsFullscreenAppActive && settings.AppFullscreenPause != AppRules.ignore)
                return false;

            // Battery power.
            if (context.IsOnBattery && settings.BatteryPause != AppRules.ignore)
                return false;

            // User whitelist (process name / window class) is not implemented yet:
            // the model required by docs/contracts/06-interaction-spec.md §7.3 does not exist.
            return true;
        }
    }
}
