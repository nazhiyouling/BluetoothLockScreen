using System.Collections.ObjectModel;
using System.Windows;

namespace BluetoothLockScreen
{
    public partial class SettingsWindow : Window
    {
        private readonly BluetoothManager _btManager;
        private ObservableCollection<BluetoothDeviceInfo> _devices = new ObservableCollection<BluetoothDeviceInfo>();

        public SettingsWindow(BluetoothManager btManager)
        {
            InitializeComponent();
            _btManager = btManager;
            DeviceListBox.ItemsSource = _devices;

            // 读取当前阈值
            RssiThresholdBox.Text = ConfigManager.Default.RssiThreshold.ToString();
        }

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            ScanButton.IsEnabled = false;
            ScanStatus.Text = "正在扫描...";
            _devices.Clear();

            try
            {
                var devices = await _btManager.ScanDevicesAsync();
                foreach (var device in devices)
                {
                    _devices.Add(device);
                }
                ScanStatus.Text = $"扫描完成，发现 {_devices.Count} 个设备";
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"扫描失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                ScanStatus.Text = "扫描失败";
            }
            finally
            {
                ScanButton.IsEnabled = true;
            }
        }

        private void DeviceListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // 不作处理
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(RssiThresholdBox.Text, out int threshold))
            {
                MessageBox.Show("请输入有效的整数阈值。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ConfigManager.Default.RssiThreshold = threshold;

            var selectedDevice = DeviceListBox.SelectedItem as BluetoothDeviceInfo;
            if (selectedDevice != null)
            {
                ConfigManager.Default.DeviceAddress = selectedDevice.Address.ToString();
                ConfigManager.Default.DeviceName = selectedDevice.DisplayName;
            }
            else
            {
                MessageBox.Show("请先选择一个蓝牙设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ConfigManager.Save();
            _btManager.UpdateThreshold(threshold);
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void RecordRssiButton_Click(object sender, RoutedEventArgs e)
        {
            _btManager.RecordCurrentRssi();
            MessageBox.Show("当前RSSI值已记录。", "记录成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public class BluetoothDeviceInfo
    {
        public ulong Address { get; set; }
        public string DisplayName { get; set; } = "";
        public override string ToString() => DisplayName;
    }
}
