// UI and transport boundaries for exercising the production DevicesViewModel without WinUI.
global using System.Collections.ObjectModel;
global using System.ComponentModel;
global using Sefirah.Data.Models;
using System.Runtime.CompilerServices;

public class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
[AttributeUsage(AttributeTargets.Method)] public sealed class RelayCommandAttribute : Attribute { }
public sealed class Ioc
{
    public static Ioc Default { get; } = new();
    public Dictionary<Type, object> Services { get; } = [];
    public T GetRequiredService<T>() => (T)Services[typeof(T)];
}
public static class TestResources
{
    public static string GetLocalizedResource(this string value) => value == "MultiDeviceConnectionSummary" ? "{0} / {1} devices connected" : value;
}
public interface IDeviceManager
{
    ObservableCollection<PairedDevice> PairedDevices { get; }
    ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; }
    PairedDevice? ActiveDevice { get; set; }
    event EventHandler<PairedDevice?> ActiveDeviceChanged;
    Task RemoveDevice(PairedDevice device);
}
public interface ISessionManager { void Connect(PairedDevice device); void DisconnectDevice(PairedDevice device, bool forced = false); }
public interface ISftpFeature { void Remove(string id); }
public class TestSftp : ISftpFeature { public void Remove(string id) { } }
public class TestDevices : IDeviceManager
{
    public ObservableCollection<PairedDevice> PairedDevices { get; } = [];
    public ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; } = [];
    public event EventHandler<PairedDevice?>? ActiveDeviceChanged;
    private PairedDevice? active;
    public PairedDevice? ActiveDevice { get => active; set { active = value; ActiveDeviceChanged?.Invoke(this, value); } }
    public Task RemoveDevice(PairedDevice device) { PairedDevices.Remove(device); return Task.CompletedTask; }
}
public class TestSessions : ISessionManager
{
    public List<PairedDevice> Connected { get; } = [];
    public List<(PairedDevice Device, bool Forced)> Disconnected { get; } = [];
    public void Connect(PairedDevice device) => Connected.Add(device);
    public void DisconnectDevice(PairedDevice device, bool forced = false) => Disconnected.Add((device, forced));
}
public enum ContentDialogResult { None, Primary }
public enum ContentDialogButton { Close }
public class ContentDialog
{
    public object? Title { get; set; }
    public object? Content { get; set; }
    public string? PrimaryButtonText { get; set; }
    public string? CloseButtonText { get; set; }
    public ContentDialogButton DefaultButton { get; set; }
    public object? XamlRoot { get; set; }
    public Task<ContentDialogResult> ShowAsync() => Task.FromResult(ContentDialogResult.None);
}
public static class App
{
    public static TestWindow MainWindow { get; } = new();
    public static void OpenDeviceSettingsWindow(PairedDevice device) { }
    public static void CloseDeviceSettingsWindow(string id) { }
}
public class TestWindow { public TestContent Content { get; } = new(); }
public class TestContent { public object XamlRoot { get; } = new(); }
namespace Microsoft.UI.Dispatching
{
    public class DispatcherQueue { public static DispatcherQueue GetForCurrentThread() => new(); }
}
namespace Sefirah.Data.Models
{
    public class DiscoveredDevice { }
    public class PairedDevice(string id) : ObservableObject
    {
        public string Id { get; } = id;
        public string Name => Id;
        private bool connected;
        public bool IsConnected { get => connected; set { connected = value; OnPropertyChanged(); } }
        public bool IsConnecting { get; set; }
        public bool IsConnectedOrConnecting => IsConnected || IsConnecting;
        public bool IsActiveDevice { get; set; }
    }
}
namespace Sefirah.Data.AppDatabase.Repository
{
    public class RemoteAppRepository { public void RemoveAllAppsForDevice(string id) { } }
    public class ContactRepository { public void DeleteAllContactsForDevice(string id) { } }
    public class SmsRepository { public void DeleteAllDataForDevice(string id) { } }
    public class CallLogRepository { public void DeleteAllCallLogsForDevice(string id) { } }
    public class NotificationRepository { public void RemoveNotificationsForDevice(string id) { } }
}
