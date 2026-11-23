using System;
using System.Threading.Tasks;
using Lively.Ai;
using Lively.Grpc.Client;
using Lively.Models;
using Lively.Models.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lively.UI.WinUI.Views.Pages.Contactable
{
    public sealed partial class ContactableSettingsView : Page
    {
        private readonly IUserSettingsClient userSettings = App.Services.GetRequiredService<IUserSettingsClient>();
        private readonly AiSettingsStore aiSettingsStore = new();
        private AiSettings aiSettings;
        private bool isLoading;
        private bool isSaving;

        public ContactableSettingsView()
        {
            // IsOn="True" 会在 InitializeComponent 期间触发 Toggled，
            // 此时后面的控件尚未创建，先屏蔽事件避免空引用导致整页导航失败。
            isLoading = true;
            this.InitializeComponent();
            isLoading = false;
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // 每次进入（包括从其他页面切回来）都重新读设置，而不是只用启动时的缓存。
            if (isLoading || isSaving)
                return;
            LoadSettings();
        }

        private void LoadSettings()
        {
            isLoading = true;
            try
            {
                // 每次都从 Core 重新读一次，避免只显示启动时缓存的旧值。
                userSettings.Load<SettingsModel>();

                var settings = userSettings.Settings;
                if (settings != null)
                {
                    InteractionToggle.IsOn = settings.InteractionEnabled;
                    WheelToggle.IsOn = settings.InteractionForwardWheel;
                    MiddleToggle.IsOn = settings.InteractionForwardMiddleButton;
                    StartupToggle.IsOn = settings.Startup;
                }

                UpdateDependentState();

                aiSettings = aiSettingsStore.Load(applyEnvironmentOverrides: false);
                BaseUrlBox.Text = aiSettings.BaseUrl ?? string.Empty;
                ModelBox.Text = aiSettings.Model ?? string.Empty;
                ApiKeyBox.Password = string.Empty;
                AiStatusText.Text = aiSettingsStore.IsConfigured(aiSettings) ? "已配置" : "未配置";
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"读取设置失败：{ex.Message}");
            }
            finally
            {
                isLoading = false;
            }
        }

        private void InteractionToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (isLoading)
                return;
            UpdateDependentState();
        }

        private void UpdateDependentState()
        {
            if (WheelToggle == null || MiddleToggle == null || WheelLabel == null || MiddleLabel == null)
                return;

            var enabled = InteractionToggle.IsOn;
            WheelToggle.IsEnabled = enabled;
            MiddleToggle.IsEnabled = enabled;
            WheelLabel.Opacity = enabled ? 1 : 0.5;
            MiddleLabel.Opacity = enabled ? 1 : 0.5;
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (isSaving)
                return;

            var baseUrl = BaseUrlBox.Text.Trim();
            if (baseUrl.Length > 0 &&
                (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) ||
                 (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
            {
                ShowStatus(InfoBarSeverity.Error, "BaseUrl 需要是 http/https 的完整地址。");
                return;
            }

            isSaving = true;
            SaveButton.IsEnabled = false;
            SaveRing.Visibility = Visibility.Visible;
            SaveRing.IsActive = true;

            try
            {
                var settings = userSettings.Settings;
                if (settings == null)
                    throw new InvalidOperationException("无法从 Core 读取设置。");

                settings.InteractionEnabled = InteractionToggle.IsOn;
                settings.InteractionForwardWheel = WheelToggle.IsOn;
                settings.InteractionForwardMiddleButton = MiddleToggle.IsOn;
                settings.Startup = StartupToggle.IsOn;
                await userSettings.SaveAsync<SettingsModel>();

                aiSettings ??= aiSettingsStore.Load(applyEnvironmentOverrides: false);
                aiSettings.BaseUrl = baseUrl;
                aiSettings.Model = ModelBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(aiSettings.Provider))
                    aiSettings.Provider = "openai-compatible";
                var newKey = ApiKeyBox.Password;
                if (!string.IsNullOrWhiteSpace(newKey))
                    aiSettingsStore.SetApiKey(aiSettings, newKey.Trim());
                aiSettingsStore.Save(aiSettings);

                ApiKeyBox.Password = string.Empty;
                AiStatusText.Text = aiSettingsStore.IsConfigured(aiSettings) ? "已配置" : "未配置";

                ShowStatus(InfoBarSeverity.Success, "设置已保存。");
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"保存失败：{ex.Message}");
            }
            finally
            {
                isSaving = false;
                SaveButton.IsEnabled = true;
                SaveRing.IsActive = false;
                SaveRing.Visibility = Visibility.Collapsed;
            }
        }

        private void ClearKeyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                aiSettings ??= aiSettingsStore.Load(applyEnvironmentOverrides: false);
                aiSettingsStore.SetApiKey(aiSettings, string.Empty);
                aiSettingsStore.Save(aiSettings);
                ApiKeyBox.Password = string.Empty;
                AiStatusText.Text = aiSettingsStore.IsConfigured(aiSettings) ? "已配置" : "未配置";
                ShowStatus(InfoBarSeverity.Success, "已清除本机保存的 API Key。");
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"清除密钥失败：{ex.Message}");
            }
        }

        private void ShowStatus(InfoBarSeverity severity, string message)
        {
            StatusBar.Severity = severity;
            StatusBar.Message = message;
            StatusBar.IsOpen = true;
        }
    }
}
