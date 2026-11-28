using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Lively.Ai;
using Lively.Grpc.Client;
using Lively.Models;
using Lively.Models.Ai;
using Lively.UI.WinUI.Services.Contactable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lively.UI.WinUI.Views.Pages.Contactable
{
    public sealed partial class ContactableSettingsView : Page
    {
        private readonly IUserSettingsClient userSettings = App.Services.GetRequiredService<IUserSettingsClient>();
        private readonly ContactableWallpaperService wallpaperService = App.Services.GetRequiredService<ContactableWallpaperService>();
        private readonly AiSettingsStore aiSettingsStore = new();
        private readonly AiProviderDetector aiProviderDetector = new();
        private AiSettings aiSettings;
        private bool isLoading;
        private bool isSaving;
        private bool isDetecting;
        private bool suppressModelSelection;

        public ContactableSettingsView()
        {
            // IsOn="True" 会在 InitializeComponent 期间触发 Toggled，
            // 此时后面的控件尚未创建，先屏蔽事件避免空引用导致整页导航失败。
            isLoading = true;
            this.InitializeComponent();
            isLoading = false;

            var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            InstallPathText.Text = $"安装目录（仅程序文件）：{installDir}";
            WallpaperPathText.Text = wallpaperService.WallpaperRoot;
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
                    KeyboardToggle.IsOn = settings.InteractionForwardKeyboard;
                    MiddleToggle.IsOn = settings.InteractionForwardMiddleButton;
                    NetworkToggle.IsOn = settings.AllowExternalNetwork;
                    StartupToggle.IsOn = settings.Startup;
                }

                UpdateDependentState();

                WallpaperPathText.Text = wallpaperService.WallpaperRoot;

                aiSettings = aiSettingsStore.Load(applyEnvironmentOverrides: false);
                ApiKeyBox.Password = string.Empty;
                BaseUrlBox.Text = aiSettings.BaseUrl ?? string.Empty;
                ModelBox.Text = aiSettings.Model ?? string.Empty;

                var hasModel = !string.IsNullOrWhiteSpace(aiSettings.Model);
                suppressModelSelection = true;
                ModelCombo.Items.Clear();
                if (hasModel)
                    ModelCombo.Items.Add(aiSettings.Model);
                ModelCombo.SelectedIndex = hasModel ? 0 : -1;
                ModelCombo.IsEnabled = hasModel;
                suppressModelSelection = false;

                var configured = aiSettingsStore.IsConfigured(aiSettings);
                AiStatusText.Text = configured ? "已配置" : "未配置";
                DetectStatusText.Text = configured
                    ? $"已配置 · {AiProviderDetector.DescribeProvider(aiSettings.Provider, aiSettings.BaseUrl)}，粘贴新 Key 可重新识别"
                    : "粘贴 API Key 后自动识别服务商与模型";
                // 旧配置有 BaseUrl 但缺 Key/模型时，直接展开手动区，便于补齐。
                ManualExpander.IsExpanded = !configured && !string.IsNullOrWhiteSpace(aiSettings.BaseUrl);
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
            if (WheelToggle == null || MiddleToggle == null || KeyboardToggle == null ||
                WheelLabel == null || MiddleLabel == null || KeyboardLabel == null)
                return;

            var enabled = InteractionToggle.IsOn;
            WheelToggle.IsEnabled = enabled;
            MiddleToggle.IsEnabled = enabled;
            KeyboardToggle.IsEnabled = enabled;
            WheelLabel.Opacity = enabled ? 1 : 0.5;
            MiddleLabel.Opacity = enabled ? 1 : 0.5;
            KeyboardLabel.Opacity = enabled ? 1 : 0.5;
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (isSaving || isDetecting)
                return;

            var newKey = ApiKeyBox.Password?.Trim() ?? string.Empty;
            aiSettings ??= aiSettingsStore.Load(applyEnvironmentOverrides: false);
            string detectedProvider = null;

            // 新填了 Key 且没有显式展开手动配置时，先自动识别服务商与模型。
            if (!string.IsNullOrEmpty(newKey) && !ManualExpander.IsExpanded)
            {
                var detection = await RunDetectionAsync(newKey);
                if (detection == null)
                    return; // 失败提示已展示，等待用户手动补全。
                detectedProvider = detection.Provider;
            }

            var baseUrl = BaseUrlBox.Text.Trim();
            var model = (ModelCombo.SelectedItem as string) ?? ModelBox.Text.Trim();
            if (!AiProviderDetector.IsValidBaseUrl(baseUrl))
            {
                ShowStatus(InfoBarSeverity.Error, "BaseUrl 需要是 http/https 的完整地址。");
                return;
            }

            var keyToUse = !string.IsNullOrEmpty(newKey) ? newKey : aiSettingsStore.GetApiKey(aiSettings);
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(model) || string.IsNullOrEmpty(keyToUse))
            {
                ManualExpander.IsExpanded = true;
                ShowStatus(InfoBarSeverity.Error, "请粘贴 API Key 自动识别，或展开「手动配置」填写 BaseUrl、模型和 API Key。");
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
                settings.InteractionForwardKeyboard = KeyboardToggle.IsOn;
                settings.InteractionForwardMiddleButton = MiddleToggle.IsOn;
                settings.AllowExternalNetwork = NetworkToggle.IsOn;
                settings.Startup = StartupToggle.IsOn;
                await userSettings.SaveAsync<SettingsModel>();

                PersistAiSettings(baseUrl, model, newKey, detectedProvider, isProbed: false);
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

        private async void DetectButton_Click(object sender, RoutedEventArgs e)
        {
            if (isDetecting || isSaving)
                return;

            var newKey = ApiKeyBox.Password?.Trim() ?? string.Empty;
            aiSettings ??= aiSettingsStore.Load(applyEnvironmentOverrides: false);
            var key = !string.IsNullOrEmpty(newKey) ? newKey : aiSettingsStore.GetApiKey(aiSettings);
            if (string.IsNullOrEmpty(key))
            {
                ShowStatus(InfoBarSeverity.Error, "请先粘贴 API Key。");
                ApiKeyBox.Focus(FocusState.Programmatic);
                return;
            }

            var detection = await RunDetectionAsync(key);
            if (detection == null)
                return;

            if (PersistAiSettings(detection.BaseUrl, detection.SelectedModel, newKey, detection.Provider, isProbed: true))
                ShowStatus(InfoBarSeverity.Success,
                    $"已识别服务商 {detection.DisplayName}，模型 {detection.SelectedModel} 已保存；可在下拉框中更换。");
        }

        private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressModelSelection)
                return;
            if (ModelCombo.SelectedItem is string model)
                ModelBox.Text = model;
        }

        /// <summary>
        /// 仅凭 Key（新填或已保存）识别服务商与可用模型；成功时回填界面，失败时展开手动配置。
        /// </summary>
        private async Task<AiProviderDetection> RunDetectionAsync(string key)
        {
            SetDetecting(true);
            DetectStatusText.Text = "正在识别服务商…";
            try
            {
                var detection = await aiProviderDetector.DetectAsync(key);
                if (!detection.IsSuccess)
                {
                    DetectStatusText.Text = "未能自动识别，请在下方手动填写";
                    ManualExpander.IsExpanded = true;
                    ShowStatus(InfoBarSeverity.Warning, detection.ErrorMessage);
                    return null;
                }

                suppressModelSelection = true;
                ModelCombo.Items.Clear();
                foreach (var model in detection.Models)
                    ModelCombo.Items.Add(model);
                ModelCombo.SelectedIndex = detection.Models.Count > 0 ? 0 : -1;
                ModelCombo.IsEnabled = detection.Models.Count > 0;
                suppressModelSelection = false;

                BaseUrlBox.Text = detection.BaseUrl;
                ModelBox.Text = detection.SelectedModel;
                ManualExpander.IsExpanded = false;
                DetectStatusText.Text = $"已识别：{detection.DisplayName} · 可用模型 {detection.Models.Count} 个";
                return detection;
            }
            catch (Exception ex)
            {
                DetectStatusText.Text = "识别失败，可手动配置";
                ManualExpander.IsExpanded = true;
                ShowStatus(InfoBarSeverity.Error, $"自动识别失败：{ex.Message}");
                return null;
            }
            finally
            {
                SetDetecting(false);
            }
        }

        private void SetDetecting(bool detecting)
        {
            isDetecting = detecting;
            DetectButton.IsEnabled = !detecting;
            ApiKeyBox.IsEnabled = !detecting;
            ModelCombo.IsEnabled = !detecting && ModelCombo.Items.Count > 0;
            DetectRing.IsActive = detecting;
            DetectRing.Visibility = detecting ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool PersistAiSettings(string baseUrl, string model, string newKey, string providerId, bool isProbed)
        {
            try
            {
                aiSettings ??= aiSettingsStore.Load(applyEnvironmentOverrides: false);
                if (!string.IsNullOrWhiteSpace(providerId))
                    aiSettings.Provider = providerId;
                else if (string.IsNullOrWhiteSpace(aiSettings.Provider) ||
                         aiSettings.Provider == "openai-compatible")
                    aiSettings.Provider = "openai-compatible";
                aiSettings.BaseUrl = baseUrl;
                aiSettings.Model = model;
                if (!string.IsNullOrWhiteSpace(newKey))
                    aiSettingsStore.SetApiKey(aiSettings, newKey);
                aiSettingsStore.Save(aiSettings);

                ApiKeyBox.Password = string.Empty;
                var configured = aiSettingsStore.IsConfigured(aiSettings);
                AiStatusText.Text = configured ? "已配置" : "未配置";
                if (!isProbed)
                {
                    DetectStatusText.Text = configured
                        ? $"已配置 · {AiProviderDetector.DescribeProvider(aiSettings.Provider, aiSettings.BaseUrl)}"
                        : DetectStatusText.Text;
                }
                return true;
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"保存 AI 配置失败：{ex.Message}");
                return false;
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
                DetectStatusText.Text = "粘贴 API Key 后自动识别服务商与模型";
                ShowStatus(InfoBarSeverity.Success, "已清除本机保存的 API Key。");
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"清除密钥失败：{ex.Message}");
            }
        }

        private void OpenLibraryFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenFolder(wallpaperService.WallpaperRoot, "壁纸库");
        }

        private void OpenDraftsFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenFolder(wallpaperService.ProjectsRoot, "生成草稿");
        }

        private void OpenFolder(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                ShowStatus(InfoBarSeverity.Warning, $"{label}目录不存在：{path}");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"打开目录失败：{ex.Message}");
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
