using CommunityToolkit.Mvvm.ComponentModel;

namespace Lively.UI.Shared.Navigation
{
    /// <summary>
    /// Menu entry model used by the product shell navigation.
    /// </summary>
    public partial class AppNavigationItem : ObservableObject
    {
        [ObservableProperty]
        private string name;

        /// <summary>
        /// Product owned vector icon path data (24x24 viewbox), never a Lively glyph.
        /// </summary>
        [ObservableProperty]
        private string iconPath;

        [ObservableProperty]
        private bool isAlert;

        [ObservableProperty]
        private int alert;

        [ObservableProperty]
        private ContactablePageType page;

        /// <summary>
        /// Used by the navigation item template as the Tag value.
        /// </summary>
        public object PageType => Page;
    }
}
