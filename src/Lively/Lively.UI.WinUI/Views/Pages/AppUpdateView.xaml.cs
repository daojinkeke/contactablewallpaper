using Lively.Grpc.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Lively.UI.WinUI.Views.Pages
{
    /// <summary>
    /// 静态更新页：只展示当前版本与本产品自有更新通道说明，不访问、不跳转任何外部站点。
    /// </summary>
    public sealed partial class AppUpdateView : Page
    {
        public AppUpdateView()
        {
            this.InitializeComponent();

            try
            {
                var version = App.Services.GetRequiredService<IDesktopCoreClient>().AssemblyVersion;
                VersionText.Text = $"Contactable Wallpaper v{version}";
            }
            catch
            {
                VersionText.Text = "Contactable Wallpaper";
            }
        }
    }
}
