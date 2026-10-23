using Microsoft.UI.Xaml.Media.Animation;
using Sefirah.Data.Items;

namespace Sefirah.Views.Settings;

public sealed partial class DevicesPage : Page
{
    public Sefirah.ViewModels.Settings.DevicesViewModel ViewModel { get; } = Ioc.Default.GetRequiredService<Sefirah.ViewModels.Settings.DevicesViewModel>();

    public DevicesPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        SetupBreadcrumb();
    }

    private void SetupBreadcrumb()
    {
        BreadcrumbBar.ItemsSource = new ObservableCollection<BreadcrumbBarItemModel>
        {
            new("Devices.Title".GetLocalizedResource(), typeof(DevicesPage))
        };
        BreadcrumbBar.ItemClicked += BreadcrumbBar_ItemClicked;
    }

    private void BreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        var items = BreadcrumbBar.ItemsSource as ObservableCollection<BreadcrumbBarItemModel>;
        var clickedItem = items?[args.Index];
        
        if (clickedItem?.PageType != null && clickedItem.PageType != typeof(DevicesPage))
        {
            // Navigate back to devices page
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }

    private void ToggleConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Sefirah.Data.Models.PairedDevice device }) ViewModel.ToggleConnection(device);
    }

    private void ViewDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Sefirah.Data.Models.PairedDevice device }) ViewModel.SelectDevice(device);
    }

    private void DeviceSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Sefirah.Data.Models.PairedDevice device }) ViewModel.OpenDeviceSettings(device);
    }

    private async void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Sefirah.Data.Models.PairedDevice device }) await ViewModel.RemoveDevice(device);
    }

    private void OpenDeviceDiscovery(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(DeviceDiscoveryPage), null, new SuppressNavigationTransitionInfo());
    }
}
