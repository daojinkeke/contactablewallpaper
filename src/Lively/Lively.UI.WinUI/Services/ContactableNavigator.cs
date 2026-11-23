using Lively.UI.Shared.Navigation;
using Lively.UI.WinUI.Views.Pages.Contactable;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;

namespace Lively.UI.WinUI.Services
{
    /// <summary>
    /// Navigates the product shell pages (create, library, preview, settings).
    /// </summary>
    public class ContactableNavigator : IContactableNavigator
    {
        /// <inheritdoc/>
        public event EventHandler<ContactablePageType>? ContentPageChanged;

        public object? RootFrame { get; set; }

        /// <inheritdoc/>
        public object? Frame { get; set; }

        public ContactablePageType? CurrentPage { get; private set; } = null;

        public void NavigateTo(ContactablePageType contentPage, object navArgs = null)
        {
            if (CurrentPage == contentPage)
                return;

            InternalNavigateTo(contentPage, new DrillInNavigationTransitionInfo(), navArgs);
        }

        public void Reload()
        {
            if (CurrentPage == null)
                return;

            InternalNavigateTo(CurrentPage.Value, new EntranceNavigationTransitionInfo());
        }

        private void InternalNavigateTo(ContactablePageType contentPage, NavigationTransitionInfo transition, object navArgs = null)
        {
            Type pageType = contentPage switch
            {
                ContactablePageType.createWallpaper => typeof(CreateWallpaperView),
                ContactablePageType.wallpaperLibrary => typeof(WallpaperLibraryView),
                ContactablePageType.wallpaperPreview => typeof(WallpaperPreviewView),
                ContactablePageType.settings => typeof(ContactableSettingsView),
                _ => throw new NotImplementedException(),
            };

            if (Frame is Frame f)
            {
                f.Navigate(pageType, navArgs, transition);

                CurrentPage = contentPage;
                ContentPageChanged?.Invoke(this, contentPage);
            }
        }
    }
}
