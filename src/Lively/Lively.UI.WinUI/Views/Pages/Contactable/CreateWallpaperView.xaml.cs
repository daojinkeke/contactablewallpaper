using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lively.Ai;
using Lively.Common;
using Lively.Common.Helpers;
using Lively.Common.Services;
using Lively.Grpc.Client;
using Lively.Models.Ai;
using Lively.UI.Shared.Navigation;
using Lively.UI.WinUI.Services.Contactable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace Lively.UI.WinUI.Views.Pages.Contactable
{
    public sealed partial class CreateWallpaperView : Page
    {
        private static readonly string[] ColorPalette =
        {
            "#22D3EE", "#6EA8FE", "#34D399", "#F472B6", "#F59E0B", "#94A3B8",
        };

        private static readonly string[] ExamplePrompts =
        {
            "深空里的黑洞，吸积盘缓慢旋转，粒子随鼠标轻轻偏移",
            "赛博城市夜景，霓虹雨丝，镜头缓慢推进",
            "极简白色房间，一束晨光缓慢移动，尘埃漂浮",
            "水墨远山，雾气流动，留白处有几只飞鸟",
            "像素风的夜晚海岸，波光闪烁，星星缓慢移动",
        };

        private readonly ContactableWallpaperService wallpaperService = App.Services.GetRequiredService<ContactableWallpaperService>();
        private readonly AiCliRunner aiCliRunner = App.Services.GetRequiredService<AiCliRunner>();
        private readonly IDisplayManagerClient displayManager = App.Services.GetRequiredService<IDisplayManagerClient>();
        private readonly IContactableNavigator navigator = App.Services.GetRequiredService<IContactableNavigator>();
        private readonly IFileService fileService = App.Services.GetRequiredService<IFileService>();
        private readonly AiSettingsStore aiSettingsStore = new();
        private readonly WallpaperSessionStore sessionStore = new();
        private AiSettings inlineAiSettings;

        private readonly ObservableCollection<RecentItemVm> recentItems = new();
        private readonly Dictionary<string, Button> colorButtons = new();

        private string selectedStyleKey = "cyber";
        private string selectedColorHex = "#22D3EE";
        private GeneratedWallpaper current;
        private CancellationTokenSource generationCts;
        private CancellationTokenSource depthCts;
        private CancellationTokenSource reviseCts;
        private WallpaperSession activeSession;
        private int selectedVersion;
        private bool isRevising;
        private string selectedPhotoPath;
        private bool isGenerating;
        private bool isDepthGenerating;
        private bool isBusy;
        private bool isNavigated;
        private bool isLoadingEco;
        private bool webViewReady;
        private bool viewBuilt;

        public CreateWallpaperView()
        {
            this.InitializeComponent();
            RecentList.ItemsSource = recentItems;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            isNavigated = true;

            if (!viewBuilt)
            {
                BuildStyleCards();
                BuildColorSwatches();
                BuildExamples();
                viewBuilt = true;
            }

            UpdateSelectionText();
            PromptCounter.Text = $"{PromptBox.Text?.Length ?? 0} / 2000";

            var configured = LoadInlineAiSettings(expandWhenUnconfigured: true);
            if (!configured)
            {
                ShowError("尚未配置 AI 服务：请在生成按钮下方填写 BaseUrl、Model 和 API Key。", true);
            }
            else
            {
                ErrorBar.IsOpen = false;
            }

            await RefreshRecentAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            isNavigated = false;
            try
            {
                generationCts?.Cancel();
                depthCts?.Cancel();
                reviseCts?.Cancel();
            }
            catch (Exception)
            {
                // ignore
            }

            try
            {
                ResultWebView.Close();
            }
            catch (Exception)
            {
                // WebView2 未初始化时会抛异常，忽略。
            }
            webViewReady = false;
            base.OnNavigatedFrom(e);
        }

        #region 静态 UI 构建

        private void BuildStyleCards()
        {
            var options = new List<StyleOption>();
            foreach (var style in ContactableStyles.All)
            {
                options.Add(new StyleOption
                {
                    Key = style.Key,
                    Name = style.Name,
                    Description = style.Description,
                    ColorHex = style.ColorHex,
                    PreviewBrush = BuildPreviewBrush(style.GradientStartHex, style.GradientEndHex),
                });
            }

            StyleGrid.ItemsSource = options;
        }

        private static Brush BuildPreviewBrush(string startHex, string endHex)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
            };
            brush.GradientStops.Add(new GradientStop
            {
                Color = ContactableStyles.ParseColor(startHex, Colors.Black),
                Offset = 0,
            });
            brush.GradientStops.Add(new GradientStop
            {
                Color = ContactableStyles.ParseColor(endHex, Colors.Black),
                Offset = 1,
            });
            return brush;
        }

        private void BuildColorSwatches()
        {
            foreach (var hex in ColorPalette)
            {
                var color = ContactableStyles.ParseColor(hex, Colors.SteelBlue);
                var button = new Button
                {
                    Width = 34,
                    Height = 34,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(17),
                    BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(color),
                    Tag = hex,
                };
                AutomationProperties.SetName(button, $"配色 {hex}");
                ToolTipService.SetToolTip(button, hex);
                button.Click += ColorSwatch_Click;
                colorButtons[hex] = button;
                ColorPanel.Children.Add(button);
            }

            UpdateColorSelection();
        }

        private void BuildExamples()
        {
            foreach (var prompt in ExamplePrompts)
            {
                var button = new Button
                {
                    Content = prompt.Length > 18 ? prompt.Substring(0, 18) + "…" : prompt,
                    Tag = prompt,
                };
                ToolTipService.SetToolTip(button, prompt);
                button.Click += Example_Click;
                ExamplePanel.Children.Add(button);
            }
        }

        private void UpdateSelectionText()
        {
            var style = ContactableStyles.Find(selectedStyleKey);
            SelectedStyleText.Text = $"已选：{style.Name} · {selectedColorHex}";
        }

        private void UpdateColorSelection()
        {
            Brush accent = null;
            try
            {
                accent = (Brush)Application.Current.Resources["ContactableAccentBrush"];
            }
            catch (Exception)
            {
                // ignore
            }

            foreach (var pair in colorButtons)
            {
                var selected = string.Equals(pair.Key, selectedColorHex, StringComparison.OrdinalIgnoreCase);
                pair.Value.BorderThickness = new Thickness(selected ? 2 : 0);
                pair.Value.BorderBrush = selected ? accent : null;
            }
        }

        #endregion

        #region 输入事件

        private void StyleCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string key)
                return;

            var style = ContactableStyles.Find(key);
            selectedStyleKey = style.Key;
            selectedColorHex = style.ColorHex;
            UpdateSelectionText();
            UpdateColorSelection();
        }

        private void ColorSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string hex)
            {
                selectedColorHex = hex;
                UpdateSelectionText();
                UpdateColorSelection();
            }
        }

        private void Example_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string prompt)
            {
                PromptBox.Text = prompt;
                PromptBox.Focus(FocusState.Programmatic);
            }
        }

        private void PromptBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PromptCounter.Text = $"{PromptBox.Text?.Length ?? 0} / 2000";
        }

        private void GoSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SetInlineAiPanelVisible(true);
            InlineBaseUrlBox.Focus(FocusState.Programmatic);
        }

        #endregion

        #region 就地 AI 配置

        private bool LoadInlineAiSettings(bool expandWhenUnconfigured)
        {
            inlineAiSettings = aiSettingsStore.Load(applyEnvironmentOverrides: false);
            InlineBaseUrlBox.Text = inlineAiSettings.BaseUrl ?? string.Empty;
            InlineModelBox.Text = inlineAiSettings.Model ?? string.Empty;
            InlineApiKeyBox.Password = string.Empty;

            isLoadingEco = true;
            EcoToggle.IsOn = inlineAiSettings.EcoMode;
            UpdateEcoState();
            isLoadingEco = false;

            var configured = aiSettingsStore.IsConfigured(inlineAiSettings);
            UpdateInlineAiState(configured);
            if (expandWhenUnconfigured && !configured)
                SetInlineAiPanelVisible(true);

            return configured;
        }

        private void UpdateInlineAiState(bool configured)
        {
            InlineAiStatus.Text = configured ? "AI 服务：已配置 · 自己的 API Key" : "AI 服务：未配置";
            InlineAiKeyState.Text = configured ? "已配置" : "未配置";
        }

        private void SetInlineAiPanelVisible(bool visible)
        {
            InlineAiPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            InlineAiToggleButton.Content = visible ? "收起配置" : "配置 AI";
        }

        private void InlineAiToggleButton_Click(object sender, RoutedEventArgs e)
        {
            SetInlineAiPanelVisible(InlineAiPanel.Visibility != Visibility.Visible);
        }

        private void InlineAiSaveButton_Click(object sender, RoutedEventArgs e)
        {
            var baseUrl = InlineBaseUrlBox.Text.Trim();
            if (baseUrl.Length > 0 &&
                (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) ||
                 (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
            {
                ShowStatus(InfoBarSeverity.Error, "BaseUrl 需要是 http/https 的完整地址。");
                return;
            }

            try
            {
                inlineAiSettings ??= aiSettingsStore.Load(applyEnvironmentOverrides: false);
                inlineAiSettings.BaseUrl = baseUrl;
                inlineAiSettings.Model = InlineModelBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(inlineAiSettings.Provider))
                    inlineAiSettings.Provider = "openai-compatible";

                var newKey = InlineApiKeyBox.Password;
                if (!string.IsNullOrWhiteSpace(newKey))
                    aiSettingsStore.SetApiKey(inlineAiSettings, newKey.Trim());

                aiSettingsStore.Save(inlineAiSettings);
                InlineApiKeyBox.Password = string.Empty;

                var configured = aiSettingsStore.IsConfigured(inlineAiSettings);
                UpdateInlineAiState(configured);
                if (configured)
                    ErrorBar.IsOpen = false;

                ShowStatus(InfoBarSeverity.Success, configured
                    ? "AI 配置已保存，下一次生成立即生效。"
                    : "已保存，但配置仍不完整：请填写 BaseUrl、模型和 API Key。");
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"保存 AI 配置失败：{ex.Message}");
            }
        }

        private void InlineAiClearButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                inlineAiSettings ??= aiSettingsStore.Load(applyEnvironmentOverrides: false);
                aiSettingsStore.SetApiKey(inlineAiSettings, string.Empty);
                aiSettingsStore.Save(inlineAiSettings);
                InlineApiKeyBox.Password = string.Empty;
                UpdateInlineAiState(aiSettingsStore.IsConfigured(inlineAiSettings));
                ShowStatus(InfoBarSeverity.Success, "已清除本机保存的 API Key。");
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"清除密钥失败：{ex.Message}");
            }
        }

        #endregion

        #region 节能模式

        private void EcoToggle_Toggled(object sender, RoutedEventArgs e)
        {
            UpdateEcoState();
            if (isLoadingEco)
                return;

            try
            {
                inlineAiSettings ??= aiSettingsStore.Load(applyEnvironmentOverrides: false);
                inlineAiSettings.EcoMode = EcoToggle.IsOn;
                aiSettingsStore.Save(inlineAiSettings);
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"保存节能模式失败：{ex.Message}");
            }
        }

        private void UpdateEcoState()
        {
            EcoStateText.Text = EcoToggle.IsOn ? "已开启" : "已关闭";
        }

        /// <summary>
        /// 续接旧会话时把开关同步到该会话的模式，避免界面与会话实际行为不一致。
        /// 只改界面，不回写持久配置；下次进入页面仍读用户上次的显式选择。
        /// </summary>
        private void ReflectSessionEco(WallpaperSession session)
        {
            EcoSessionHint.Visibility = session != null && session.Eco ? Visibility.Visible : Visibility.Collapsed;
            if (session == null)
                return;

            isLoadingEco = true;
            EcoToggle.IsOn = session.Eco;
            UpdateEcoState();
            isLoadingEco = false;
        }

        #endregion

        #region 生成

        private async void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            if (isGenerating || isBusy || isRevising)
                return;

            var prompt = PromptBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                ShowError("请先写一句描述，例如“深空里的黑洞，吸积盘缓慢旋转”。", false);
                return;
            }

            var settings = aiSettingsStore.Load();
            if (!aiSettingsStore.IsConfigured(settings))
            {
                SetInlineAiPanelVisible(true);
                ShowError("尚未配置 AI 服务：请在生成按钮下方填写 BaseUrl、Model 和 API Key。", true);
                return;
            }

            string projectFolder;
            try
            {
                projectFolder = wallpaperService.CreateProjectFolder();
            }
            catch (Exception ex)
            {
                ShowError($"无法创建生成目录：{ex.Message}", false);
                return;
            }

            var outputPath = Path.Combine(projectFolder, "stage", "index.html");
            ResetConversation();
            generationCts = new CancellationTokenSource();
            SetGenerating(true);
            ErrorBar.IsOpen = false;
            GenerateProgress.Value = 0;
            GenerateStatus.Text = "正在启动生成器…";

            var progress = new Progress<AiGenerationProgress>(value =>
            {
                if (!isGenerating)
                    return;
                GenerateProgress.Value = value.Percent;
                GenerateStatus.Text = string.IsNullOrWhiteSpace(value.Status) ? "生成中…" : value.Status;
            });

            var options = new AiGenerationOptions
            {
                Prompt = prompt,
                Style = selectedStyleKey,
                Color = selectedColorHex,
                OutputPath = outputPath,
                Eco = EcoToggle.IsOn,
            };

            AiGenerationResult result;
            try
            {
                result = await aiCliRunner.GenerateAsync(options, progress, generationCts.Token);
            }
            catch (Exception ex)
            {
                result = new AiGenerationResult { ErrorMessage = $"生成器异常：{ex.Message}" };
            }
            finally
            {
                generationCts?.Dispose();
                generationCts = null;
            }

            if (!isNavigated)
                return;

            SetGenerating(false);

            if (result.IsCanceled)
            {
                DeleteProject(projectFolder);
                GenerateProgress.Value = 0;
                GenerateStatus.Text = "已取消";
                ShowStatus(InfoBarSeverity.Warning, "已取消生成。");
                return;
            }

            if (!result.IsSuccess)
            {
                DeleteProject(projectFolder);
                GenerateStatus.Text = "生成失败";
                var needsConfig = result.ErrorCode is AiErrorCodes.NotConfigured
                    or AiErrorCodes.AuthenticationFailed
                    or AiErrorCodes.ModelNotFound;
                ShowError(result.ErrorMessage ?? "生成失败，请重试。", needsConfig);
                return;
            }

            var styleInfo = ContactableStyles.Find(selectedStyleKey);
            var item = new GeneratedWallpaper
            {
                ProjectFolder = projectFolder,
                HtmlPath = outputPath,
                Title = BuildTitle(prompt, styleInfo),
                Prompt = prompt,
                Style = selectedStyleKey,
                Color = selectedColorHex,
                CreatedAt = DateTime.Now,
                SessionId = result.SessionId,
            };
            item.ThumbnailPath = await Task.Run(() =>
                wallpaperService.CreateThumbnail(projectFolder, item.Title, selectedStyleKey, selectedColorHex));
            wallpaperService.WriteMeta(item);

            GenerateProgress.Value = 100;
            GenerateStatus.Text = "生成完成";
            await ShowResultAsync(item);
            await RefreshRecentAsync();
            ShowStatus(InfoBarSeverity.Success, "生成完成，可以预览、保存到壁纸库或立即应用。");
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                generationCts?.Cancel();
                GenerateStatus.Text = "正在取消…";
                CancelButton.IsEnabled = false;
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private void SetGenerating(bool generating)
        {
            isGenerating = generating;
            GenerateButton.IsEnabled = !generating;
            CancelButton.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = generating;
            GenerateRing.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
            GenerateRing.IsActive = generating;
            PromptBox.IsEnabled = !generating;
            StyleGrid.IsEnabled = !generating;
            foreach (var swatch in colorButtons.Values)
                swatch.IsEnabled = !generating;
        }

        private static string BuildTitle(string prompt, ContactableStyleInfo style)
        {
            var text = (prompt ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 22)
                text = text.Substring(0, 22) + "…";
            if (string.IsNullOrWhiteSpace(text))
                text = "Contactable Wallpaper";
            return $"{text} · {style.Name}";
        }

        private void DeleteProject(string projectFolder)
        {
            if (string.IsNullOrWhiteSpace(projectFolder))
                return;
            wallpaperService.DeleteRecent(new GeneratedWallpaper { ProjectFolder = projectFolder });
        }

        #endregion

        #region 深度视差

        private async void PickPhotoButton_Click(object sender, RoutedEventArgs e)
        {
            if (isDepthGenerating || isBusy)
                return;

            try
            {
                var files = await fileService.PickFileAsync(new (string, string[])[]
                {
                    ("图片", new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }),
                });
                var path = files?.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(path))
                    return;

                selectedPhotoPath = path;
                PhotoPathText.Text = path;
                DepthGenerateButton.IsEnabled = true;
                DepthStatus.Text = "照片已就绪，点“生成视差壁纸”。";
                DepthProgress.Value = 0;
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"选择照片失败：{ex.Message}");
            }
        }

        private async void DepthGenerateButton_Click(object sender, RoutedEventArgs e)
        {
            if (isDepthGenerating || isBusy)
                return;

            if (string.IsNullOrWhiteSpace(selectedPhotoPath) || !File.Exists(selectedPhotoPath))
            {
                ShowStatus(InfoBarSeverity.Warning, "请先选择一张照片。");
                return;
            }

            string projectFolder;
            try
            {
                projectFolder = wallpaperService.CreateProjectFolder();
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"无法创建生成目录：{ex.Message}");
                return;
            }

            var outputDir = Path.Combine(projectFolder, "stage");
            var title = $"视差 · {Path.GetFileNameWithoutExtension(selectedPhotoPath)}";
            depthCts = new CancellationTokenSource();
            SetDepthGenerating(true);
            DepthProgress.Value = 0;
            DepthStatus.Text = "正在启动深度生成器…";

            var progress = new Progress<AiGenerationProgress>(value =>
            {
                if (!isDepthGenerating)
                    return;
                DepthProgress.Value = value.Percent;
                DepthStatus.Text = string.IsNullOrWhiteSpace(value.Status) ? "处理中…" : value.Status;
            });

            var options = new DepthGenerationOptions
            {
                ImagePath = selectedPhotoPath,
                OutputDirectory = outputDir,
                Title = title,
            };

            DepthGenerationResult result;
            try
            {
                result = await aiCliRunner.GenerateDepthAsync(options, progress, depthCts.Token);
            }
            catch (Exception ex)
            {
                result = new DepthGenerationResult { ErrorMessage = $"生成器异常：{ex.Message}" };
            }
            finally
            {
                depthCts?.Dispose();
                depthCts = null;
            }

            if (!isNavigated)
                return;

            SetDepthGenerating(false);

            if (result.IsCanceled)
            {
                DeleteProject(projectFolder);
                DepthProgress.Value = 0;
                DepthStatus.Text = "已取消";
                ShowStatus(InfoBarSeverity.Warning, "已取消视差生成。");
                return;
            }

            if (!result.IsSuccess)
            {
                DeleteProject(projectFolder);
                DepthStatus.Text = "生成失败";
                ShowStatus(InfoBarSeverity.Error, result.ErrorMessage ?? "视差生成失败，请重试。");
                return;
            }

            var item = new GeneratedWallpaper
            {
                ProjectFolder = projectFolder,
                HtmlPath = result.HtmlPath ?? Path.Combine(outputDir, "index.html"),
                ThumbnailPath = result.ThumbnailPath,
                Title = title,
                Prompt = selectedPhotoPath,
                Style = "depth",
                CreatedAt = DateTime.Now,
                IsPackaged = true,
            };
            wallpaperService.WriteMeta(item);

            DepthProgress.Value = 100;
            DepthStatus.Text = "生成完成";
            await ShowResultAsync(item);
            await RefreshRecentAsync();
            ShowStatus(InfoBarSeverity.Success, "视差壁纸已生成，可以预览、保存到壁纸库或立即应用。");
        }

        private void DepthCancelButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                depthCts?.Cancel();
                DepthStatus.Text = "正在取消…";
                DepthCancelButton.IsEnabled = false;
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private void SetDepthGenerating(bool generating)
        {
            isDepthGenerating = generating;
            DepthGenerateButton.IsEnabled = !generating && !string.IsNullOrWhiteSpace(selectedPhotoPath);
            DepthCancelButton.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
            DepthCancelButton.IsEnabled = generating;
            DepthRing.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
            DepthRing.IsActive = generating;
            PickPhotoButton.IsEnabled = !generating;
        }

        #endregion

        #region 结果预览与应用

        private async Task ShowResultAsync(GeneratedWallpaper item)
        {
            try
            {
                current = item;
                ResultCard.Visibility = Visibility.Visible;
                ResultTitle.Text = item.Title;
                ResultPath.Text = $"文件：{item.HtmlPath}";
                ResultImage.Source = LoadBitmap(item.ThumbnailPath, 960);
                ResultImage.Visibility = Visibility.Visible;
                ResultWebView.Visibility = Visibility.Collapsed;
                SaveToLibraryButton.IsEnabled = true;
                ApplyButton.IsEnabled = true;

                string displayPath;
                try
                {
                    displayPath = LoadConversation(item);
                }
                catch (Exception ex)
                {
                    // 会话目录损坏或版本文件缺失时不要让整页崩溃。
                    displayPath = item.HtmlPath;
                    activeSession = null;
                    ConversationPanel.Visibility = Visibility.Visible;
                    ConversationBody.Visibility = Visibility.Collapsed;
                    LegacyConversationHint.Text = $"读取会话失败：{ex.Message}";
                    LegacyConversationHint.Visibility = Visibility.Visible;
                    ConversationSessionText.Text = item.SessionId ?? "无会话";
                    ShowStatus(InfoBarSeverity.Warning, $"读取会话失败：{ex.Message}");
                }

                if (!await EnsureWebViewAsync())
                    return;

                NavigateResultPreview(displayPath);
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"显示结果失败：{ex.Message}");
            }
        }

        private void NavigateResultPreview(string htmlPath)
        {
            try
            {
                var folder = Path.GetDirectoryName(htmlPath);
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    return;

                ResultWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "contactable.local", folder, CoreWebView2HostResourceAccessKind.Allow);
                ResultImage.Visibility = Visibility.Collapsed;
                ResultWebView.Visibility = Visibility.Visible;
                ResultWebView.CoreWebView2.Navigate($"https://contactable.local/{Path.GetFileName(htmlPath)}");
            }
            catch (Exception ex)
            {
                ResultWebView.Visibility = Visibility.Collapsed;
                ResultImage.Visibility = Visibility.Visible;
                ShowStatus(InfoBarSeverity.Warning, $"网页预览不可用（{ex.Message}），已显示缩略图。");
            }
        }

        private async Task<bool> EnsureWebViewAsync()
        {
            if (webViewReady)
                return true;
            try
            {
                var userDataDir = Path.Combine(
                    PackageUtil.ValidateAndResolvePath(Constants.CommonPaths.TempWebView2Dir),
                    "ContactableCreatePreview");
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                    null, userDataDir, new CoreWebView2EnvironmentOptions());
                await ResultWebView.EnsureCoreWebView2Async(environment);
                webViewReady = true;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async void SaveToLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy || current == null)
                return;

            isBusy = true;
            SaveToLibraryButton.IsEnabled = false;
            ApplyButton.IsEnabled = false;
            SaveRing.Visibility = Visibility.Visible;
            SaveRing.IsActive = true;
            SaveProgress.Visibility = Visibility.Visible;
            try
            {
                var path = await EnsureInLibraryAsync(current);
                ShowStatus(InfoBarSeverity.Success, $"已保存到壁纸库：{path}");
                SaveToLibraryButton.IsEnabled = false;
                ApplyButton.IsEnabled = true;
                await RefreshRecentAsync();
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"保存失败：{ex.Message}");
                SaveToLibraryButton.IsEnabled = true;
                ApplyButton.IsEnabled = true;
            }
            finally
            {
                isBusy = false;
                SaveRing.IsActive = false;
                SaveRing.Visibility = Visibility.Collapsed;
                SaveProgress.Visibility = Visibility.Collapsed;
            }
        }

        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy || current == null)
                return;
            await ApplyItemAsync(current);
        }

        private async Task ApplyItemAsync(GeneratedWallpaper item)
        {
            var monitor = displayManager.PrimaryMonitor;
            if (monitor == null)
            {
                ShowStatus(InfoBarSeverity.Error, "没有可用的显示器。");
                return;
            }

            isBusy = true;
            ApplyButton.IsEnabled = false;
            SaveToLibraryButton.IsEnabled = false;
            SaveRing.Visibility = Visibility.Visible;
            SaveRing.IsActive = true;
            SaveProgress.Visibility = Visibility.Visible;
            try
            {
                var libraryFolder = await EnsureInLibraryAsync(item);
                var (ok, message) = await wallpaperService.ApplyAsync(libraryFolder, monitor);
                ShowStatus(ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                    ok ? $"已应用到 {monitor.DisplayName}。" : message);
                await RefreshRecentAsync();
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Error, $"应用失败：{ex.Message}");
            }
            finally
            {
                isBusy = false;
                ApplyButton.IsEnabled = current != null;
                SaveToLibraryButton.IsEnabled = current != null && string.IsNullOrWhiteSpace(current.LibraryFolderPath);
                SaveRing.IsActive = false;
                SaveRing.Visibility = Visibility.Collapsed;
                SaveProgress.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 确保生成结果已在壁纸库里。深度视差产物本身是完整壁纸目录，整目录导入；
        /// AI 生成产物则由 HTML 重新打包。
        /// </summary>
        private async Task<string> EnsureInLibraryAsync(GeneratedWallpaper item)
        {
            if (!string.IsNullOrWhiteSpace(item.LibraryFolderPath) && Directory.Exists(item.LibraryFolderPath))
                return item.LibraryFolderPath;

            var path = item.IsPackaged
                ? await wallpaperService.ImportWallpaperDirectoryAsync(Path.GetDirectoryName(item.HtmlPath), item.Title)
                : await wallpaperService.SaveToLibraryAsync(item);
            item.LibraryFolderPath = path;
            wallpaperService.WriteMeta(item);
            return path;
        }

        #endregion

        #region 对话续改

        /// <summary>
        /// 展示结果时同步加载会话：返回应预览的 HTML 路径（有会话时取最新版本）。
        /// </summary>
        private string LoadConversation(GeneratedWallpaper item)
        {
            ResetConversation();

            if (item.IsPackaged)
            {
                ConversationPanel.Visibility = Visibility.Collapsed;
                return item.HtmlPath;
            }

            if (string.IsNullOrWhiteSpace(item.SessionId) ||
                !sessionStore.TryLoad(item.SessionId, out var session) ||
                session.Versions.Count == 0)
            {
                ConversationPanel.Visibility = Visibility.Visible;
                ConversationSessionText.Text = "无会话";
                ConversationBody.Visibility = Visibility.Collapsed;
                LegacyConversationHint.Visibility = Visibility.Visible;
                return item.HtmlPath;
            }

            activeSession = session;
            ReflectSessionEco(session);
            ConversationPanel.Visibility = Visibility.Visible;
            ConversationSessionText.Text = session.SessionId;
            ConversationVersionText.Text = session.Eco ? "节能模式" : string.Empty;

            RebuildVersionRail();
            RebuildDialogue();

            var latest = session.Versions.OrderBy(x => x.Version).Last();
            selectedVersion = latest.Version;
            UpdateVersionRailSelection();
            ApplyVersionButton.IsEnabled = true;
            SaveVersionButton.IsEnabled = true;

            var path = sessionStore.GetVersionPath(session.SessionId, latest.Version);
            if (File.Exists(path))
            {
                ResultPath.Text = $"版本 v{latest.Version}：{path}";
                return path;
            }
            return item.HtmlPath;
        }

        private void ResetConversation()
        {
            activeSession = null;
            selectedVersion = 0;
            ConversationPanel.Visibility = Visibility.Collapsed;
            ConversationBody.Visibility = Visibility.Visible;
            LegacyConversationHint.Visibility = Visibility.Collapsed;
            ConversationSessionText.Text = "无会话";
            ConversationVersionText.Text = string.Empty;
            EcoSessionHint.Visibility = Visibility.Collapsed;
            VersionRail.Children.Clear();
            DialogueList.ItemsSource = null;
            ReviseBox.Text = string.Empty;
            ReviseProgress.Value = 0;
            ReviseStatus.Text = "提交后会生成新版本，旧版本仍保留在版本走廊里。";
            ApplyVersionButton.IsEnabled = false;
            SaveVersionButton.IsEnabled = false;
            ReviseButton.IsEnabled = true;
            ReviseCancelButton.Visibility = Visibility.Collapsed;
            ReviseRing.IsActive = false;
            ReviseRing.Visibility = Visibility.Collapsed;
        }

        private void RebuildVersionRail()
        {
            VersionRail.Children.Clear();
            if (activeSession == null)
                return;

            foreach (var version in activeSession.Versions.OrderBy(x => x.Version))
            {
                var button = new Button
                {
                    Content = version.Score.HasValue ? $"v{version.Version} · {version.Score:0.#}" : $"v{version.Version}",
                    Tag = version.Version,
                    MinWidth = 64,
                    Height = 32,
                    Padding = new Thickness(10, 0, 10, 0),
                    FontSize = 12,
                    CornerRadius = new CornerRadius(8),
                };
                button.Click += VersionButton_Click;
                VersionRail.Children.Add(button);
            }

            UpdateVersionRailSelection();
        }

        private void UpdateVersionRailSelection()
        {
            var accent = GetContactableBrush("ContactableAccentBrush");
            var hairline = GetContactableBrush("ContactableHairlineBrush");
            var chipText = GetContactableBrush("ContactableChipTextBrush");
            foreach (var child in VersionRail.Children)
            {
                if (child is not Button button || button.Tag is not int version)
                    continue;

                var isSelected = version == selectedVersion;
                button.BorderBrush = isSelected ? accent : hairline;
                button.BorderThickness = new Thickness(isSelected ? 2 : 1);
                button.Foreground = isSelected ? accent : chipText;
                button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        /// <summary>
        /// 主题画刷放在 App.xaml 的 ThemeDictionaries 里，程序化取用需要按当前主题查。
        /// </summary>
        private Brush GetContactableBrush(string key)
        {
            var resources = Application.Current.Resources;
            var preferred = ActualTheme == ElementTheme.Light ? "Light" : "Dark";
            foreach (var themeKey in new[] { preferred, "HighContrast" })
            {
                if (resources.ThemeDictionaries.TryGetValue(themeKey, out var dictionary) &&
                    dictionary is ResourceDictionary themeDictionary &&
                    themeDictionary.TryGetValue(key, out var value) &&
                    value is Brush brush)
                {
                    return brush;
                }
            }
            return null;
        }

        private void RebuildDialogue()
        {
            var turns = new List<ConversationTurnVm>();
            if (activeSession != null)
            {
                foreach (var version in activeSession.Versions.OrderBy(x => x.Version))
                {
                    if (version.Version <= 1)
                    {
                        turns.Add(new ConversationTurnVm
                        {
                            Caption = "v1 · 初始生成",
                            Body = string.IsNullOrWhiteSpace(activeSession.Prompt) ? "（没有记录初始提示词）" : activeSession.Prompt,
                        });
                        continue;
                    }

                    turns.Add(new ConversationTurnVm
                    {
                        Caption = version.Score.HasValue
                            ? $"v{version.Version} · 你的诉求 · 评分 {version.Score:0.#}/10"
                            : $"v{version.Version} · 你的诉求",
                        Body = string.IsNullOrWhiteSpace(version.Instruction) ? "（没有记录诉求）" : version.Instruction,
                    });
                    if (!string.IsNullOrWhiteSpace(version.Summary))
                    {
                        turns.Add(new ConversationTurnVm
                        {
                            Caption = $"v{version.Version} · 这一版改了什么",
                            Body = version.Summary,
                        });
                    }
                }
            }

            DialogueList.ItemsSource = turns;
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    DialogueScroller.ChangeView(null, DialogueScroller.ScrollableHeight, null, true);
                }
                catch (Exception)
                {
                    // 布局尚未完成时忽略。
                }
            });
        }

        private async void VersionButton_Click(object sender, RoutedEventArgs e)
        {
            if (activeSession == null || isRevising || sender is not Button { Tag: int version })
                return;

            var path = sessionStore.GetVersionPath(activeSession.SessionId, version);
            if (!File.Exists(path))
            {
                ShowStatus(InfoBarSeverity.Warning, $"v{version} 的文件不存在，可能已被清理。");
                return;
            }

            selectedVersion = version;
            UpdateVersionRailSelection();
            ResultPath.Text = $"版本 v{version}：{path}";
            if (await EnsureWebViewAsync())
                NavigateResultPreview(path);
        }

        private void SetRevising(bool revising)
        {
            isRevising = revising;
            ReviseButton.IsEnabled = !revising;
            ReviseCancelButton.IsEnabled = revising;
            ReviseCancelButton.Visibility = revising ? Visibility.Visible : Visibility.Collapsed;
            ReviseRing.IsActive = revising;
            ReviseRing.Visibility = revising ? Visibility.Visible : Visibility.Collapsed;
            ReviseBox.IsEnabled = !revising;
            GenerateButton.IsEnabled = !revising;
            VersionRail.IsHitTestVisible = !revising;
            ApplyVersionButton.IsEnabled = !revising && activeSession != null;
            SaveVersionButton.IsEnabled = !revising && activeSession != null;
        }

        private async void ReviseButton_Click(object sender, RoutedEventArgs e)
        {
            if (isRevising || isBusy || isGenerating)
                return;

            if (activeSession == null)
            {
                ShowStatus(InfoBarSeverity.Warning, "当前结果没有可续接的会话。");
                return;
            }

            var instruction = ReviseBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(instruction))
            {
                ShowStatus(InfoBarSeverity.Warning, "请先写一句修改诉求。");
                return;
            }

            var settings = aiSettingsStore.Load();
            if (!aiSettingsStore.IsConfigured(settings))
            {
                SetInlineAiPanelVisible(true);
                ShowError("尚未配置 AI 服务：请在生成按钮下方填写 BaseUrl、Model 和 API Key。", true);
                return;
            }

            reviseCts = new CancellationTokenSource();
            SetRevising(true);
            ReviseProgress.Value = 0;
            ReviseStatus.Text = "正在启动修改器…";
            ErrorBar.IsOpen = false;

            var progress = new Progress<AiGenerationProgress>(value =>
            {
                if (!isRevising)
                    return;
                ReviseProgress.Value = value.Percent;
                ReviseStatus.Text = string.IsNullOrWhiteSpace(value.Status) ? "正在修改…" : value.Status;
            });

            var options = new AiRevisionOptions
            {
                SessionId = activeSession.SessionId,
                Instruction = instruction,
                Eco = EcoToggle.IsOn,
            };

            AiRevisionResult result;
            try
            {
                result = await aiCliRunner.ReviseAsync(options, progress, reviseCts.Token);
            }
            catch (Exception ex)
            {
                result = new AiRevisionResult { ErrorMessage = $"修改器异常：{ex.Message}" };
            }
            finally
            {
                reviseCts?.Dispose();
                reviseCts = null;
                SetRevising(false);
            }

            if (!isNavigated)
                return;

            if (result.IsCanceled)
            {
                ReviseProgress.Value = 0;
                ReviseStatus.Text = "已取消修改，保留当前版本。";
                ShowStatus(InfoBarSeverity.Warning, "已取消修改，当前版本未变。");
                return;
            }

            if (!result.IsSuccess)
            {
                ReviseStatus.Text = "修改失败";
                var needsConfig = result.ErrorCode is AiErrorCodes.NotConfigured
                    or AiErrorCodes.AuthenticationFailed
                    or AiErrorCodes.ModelNotFound;
                ShowError(result.ErrorMessage ?? "修改失败，请重试。", needsConfig);
                return;
            }

            var sessionId = activeSession.SessionId;
            if (!sessionStore.TryLoad(sessionId, out var session) || session.Versions.Count == 0)
            {
                ReviseStatus.Text = "修改完成，但读取会话失败。";
                ShowStatus(InfoBarSeverity.Warning, "修改完成，但读取会话失败，请点刷新后重试。");
                return;
            }

            activeSession = session;
            ConversationVersionText.Text = session.Eco ? "节能模式" : string.Empty;
            RebuildVersionRail();
            RebuildDialogue();

            var latest = session.Versions.OrderBy(x => x.Version).Last();
            selectedVersion = latest.Version;
            UpdateVersionRailSelection();

            var versionPath = sessionStore.GetVersionPath(sessionId, latest.Version);
            if (File.Exists(versionPath))
            {
                current.HtmlPath = versionPath;
                current.SessionId = sessionId;
                ResultPath.Text = $"版本 v{latest.Version}：{versionPath}";
                if (await EnsureWebViewAsync())
                    NavigateResultPreview(versionPath);
            }

            ReviseBox.Text = string.Empty;
            ReviseProgress.Value = 100;
            ReviseStatus.Text = latest.Score.HasValue
                ? $"已生成 v{latest.Version} · 评分 {latest.Score:0.#}/10"
                : $"已生成 v{latest.Version}";
            ShowStatus(InfoBarSeverity.Success,
                string.IsNullOrWhiteSpace(latest.Summary)
                    ? $"已生成 v{latest.Version}，可以在版本走廊里回看或应用。"
                    : $"已生成 v{latest.Version}。{latest.Summary}");
            await RefreshRecentAsync();
        }

        private void ReviseCancelButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ReviseStatus.Text = "正在取消修改…";
                ReviseCancelButton.IsEnabled = false;
                reviseCts?.Cancel();
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private GeneratedWallpaper CreateVersionItem()
        {
            if (activeSession == null || selectedVersion <= 0)
                return null;

            var path = sessionStore.GetVersionPath(activeSession.SessionId, selectedVersion);
            if (!File.Exists(path))
                return null;

            var suffix = selectedVersion > 1 ? $" · v{selectedVersion}" : string.Empty;
            return new GeneratedWallpaper
            {
                // 会话版本不是独立项目目录，赋空可避免改写原记录的 meta.json。
                ProjectFolder = null,
                HtmlPath = path,
                ThumbnailPath = current?.ThumbnailPath,
                Title = string.IsNullOrWhiteSpace(current?.Title) ? "Contactable Wallpaper" : current.Title + suffix,
                Prompt = current?.Prompt,
                Style = current?.Style,
                Color = current?.Color,
                CreatedAt = current?.CreatedAt ?? DateTime.Now,
                SessionId = activeSession.SessionId,
            };
        }

        private async void ApplyVersionButton_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy || isRevising || activeSession == null)
                return;

            var item = CreateVersionItem();
            if (item == null)
            {
                ShowStatus(InfoBarSeverity.Warning, $"v{selectedVersion} 的文件不存在。");
                return;
            }
            await ApplyItemAsync(item);
        }

        private async void SaveVersionButton_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy || isRevising || activeSession == null)
                return;

            var item = CreateVersionItem();
            if (item == null)
            {
                ShowStatus(InfoBarSeverity.Warning, $"v{selectedVersion} 的文件不存在。");
                return;
            }

            isBusy = true;
            ApplyVersionButton.IsEnabled = false;
            SaveVersionButton.IsEnabled = false;
            ReviseRing.IsActive = true;
            ReviseRing.Visibility = Visibility.Visible;
            ReviseStatus.Text = $"正在保存 v{selectedVersion}…";
            try
            {
                var path = await EnsureInLibraryAsync(item);
                ReviseStatus.Text = $"已保存 v{selectedVersion} 到壁纸库";
                ShowStatus(InfoBarSeverity.Success, $"已将 v{selectedVersion} 保存到壁纸库：{path}");
                await RefreshRecentAsync();
            }
            catch (Exception ex)
            {
                ReviseStatus.Text = "保存失败";
                ShowStatus(InfoBarSeverity.Error, $"保存 v{selectedVersion} 失败：{ex.Message}");
            }
            finally
            {
                isBusy = false;
                ReviseRing.IsActive = false;
                ReviseRing.Visibility = Visibility.Collapsed;
                ApplyVersionButton.IsEnabled = activeSession != null && !isRevising;
                SaveVersionButton.IsEnabled = activeSession != null && !isRevising;
            }
        }

        #endregion

        #region 最近生成

        private async Task RefreshRecentAsync()
        {
            IReadOnlyList<GeneratedWallpaper> scanned;
            try
            {
                scanned = await Task.Run(() => wallpaperService.ScanRecent(12));
            }
            catch (Exception ex)
            {
                ShowStatus(InfoBarSeverity.Warning, $"读取最近生成失败：{ex.Message}");
                return;
            }

            if (!isNavigated)
                return;

            recentItems.Clear();
            foreach (var item in scanned)
            {
                recentItems.Add(new RecentItemVm
                {
                    Source = item,
                    Title = string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileName(item.ProjectFolder) : item.Title,
                    Prompt = item.Prompt,
                    StyleName = item.IsPackaged ? "深度视差" : ContactableStyles.Find(item.Style).Name,
                    CreatedText = item.CreatedAt.ToString("MM-dd HH:mm"),
                    LibraryStateText = string.IsNullOrWhiteSpace(item.LibraryFolderPath) ? "未入库" : "已入库",
                    Thumbnail = LoadBitmap(item.ThumbnailPath, 200),
                });
            }

            RecentCount.Text = recentItems.Count == 0 ? string.Empty : $"{recentItems.Count} 条";
            RecentEmptyPanel.Visibility = recentItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RecentList.Visibility = recentItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void ReuseRecent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not RecentItemVm vm || vm.Source == null)
                return;

            if (!vm.Source.IsPackaged)
            {
                PromptBox.Text = vm.Source.Prompt ?? string.Empty;
                selectedStyleKey = ContactableStyles.Find(vm.Source.Style).Key;
                selectedColorHex = string.IsNullOrWhiteSpace(vm.Source.Color)
                    ? ContactableStyles.Find(vm.Source.Style).ColorHex
                    : vm.Source.Color;
                UpdateSelectionText();
                UpdateColorSelection();
            }

            await ShowResultAsync(vm.Source);
            ShowStatus(InfoBarSeverity.Success, vm.Source.IsPackaged
                ? "已载入视差壁纸结果，可保存到壁纸库或立即应用。"
                : "已回填最近生成的描述、风格与配色。");
        }

        private async void ApplyRecent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not RecentItemVm vm || vm.Source == null)
                return;
            await ApplyItemAsync(vm.Source);
        }

        private async void DeleteRecent_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy || sender is not Button button || button.Tag is not RecentItemVm vm || vm.Source == null)
                return;

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "删除生成记录",
                Content = $"确定删除“{vm.Title}”吗？\n\n目录：{vm.Source.ProjectFolder}\n已保存到壁纸库的副本不会被删除。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            wallpaperService.DeleteRecent(vm.Source);
            if (current != null && string.Equals(current.ProjectFolder, vm.Source.ProjectFolder, StringComparison.OrdinalIgnoreCase))
                current = null;
            await RefreshRecentAsync();
            ShowStatus(InfoBarSeverity.Success, "已删除生成记录。");
        }

        #endregion

        #region 状态

        private void ShowError(string message, bool offerSettings)
        {
            ErrorBar.Severity = InfoBarSeverity.Error;
            ErrorBar.Message = message;
            ErrorBar.IsOpen = true;
            GoSettingsButton.Visibility = offerSettings ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowStatus(InfoBarSeverity severity, string message)
        {
            StatusBar.Severity = severity;
            StatusBar.Message = message;
            StatusBar.IsOpen = true;
        }

        private static BitmapImage LoadBitmap(string path, int decodeWidth)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth };
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                return bitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion
    }
}
