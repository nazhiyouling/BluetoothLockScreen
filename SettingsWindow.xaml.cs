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

            // 初始化控件值
            RssiThresholdBox.Text = ConfigManager.Default.RssiThreshold.ToString();
            if (!string.IsNullOrWhiteSpace(ConfigManager.Default.DeviceAddress))
            {
                ManualAddressBox.Text = ConfigManager.Default.DeviceAddress;
            }
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
            var selected = DeviceListBox.SelectedItem as BluetoothDeviceInfo;
            if (selected != null)
            {
                ManualAddressBox.Text = selected.Address.ToString("X12");
            }
        }

        private void SaveManualAddress_Click(object sender, RoutedEventArgs e)
        {
            string address = ManualAddressBox.Text.Trim().Replace(":", "").Replace("-", "").ToUpper();
            if (string.IsNullOrWhiteSpace(address) || address.Length != 12)
            {
                MessageBox.Show("请输入有效的蓝牙 MAC 地址（12位十六进制字符）。\n" +
                                "可以从 Windows 蓝牙设置 → 设备属性中复制。",
                                "地址格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ConfigManager.Default.DeviceAddress = address;
            ConfigManager.Default.DeviceName = "手动输入: " + address;
            ConfigManager.Save();
            MessageBox.Show("蓝牙地址已保存，可以开始监控。", "保存成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            string address = ManualAddressBox.Text.Trim().Replace(":", "").Replace("-", "").ToUpper();
            if (string.IsNullOrWhiteSpace(address) || address.Length != 12)
            {
                TestResultText.Text = "地址格式错误，请重新输入。";
                return;
            }

            TestResultText.Text = "正在测试...";
            int? rssi = await _btManager.TestConnectionAsync(address);
            if (rssi.HasValue)
            {
                TestResultText.Text = $"连接成功！当前 RSSI: {rssi.Value} dBm";
            }
            else
            {
                TestResultText.Text = "连接失败：无法找到该设备。\n请确保手机已运行 BLE-Anchor 并点击“开始广播”。";
            }
        }

        private void RecordRssiButton_Click(object sender, RoutedEventArgs e)
        {
            // 获取当前 RSSI 并记录（BluetoothManager 内部会存储到列表并立即写入文件）
            int lastRssi = _btManager.RecordAndGetRssi();   // 新增方法，返回记录的 RSSI 值
            LastRssiText.Text = $"上次记录: {lastRssi} dBm   (日志文件: rssi_log.txt)";
            MessageBox.Show($"当前 RSSI 值已记录：{lastRssi} dBm\n日志文件保存在程序目录下的 rssi_log.txt",
                            "记录成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(RssiThresholdBox.Text, out int threshold))
            {
                MessageBox.Show("请输入有效的整数阈值。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ConfigManager.Default.RssiThreshold = threshold;

            string manualAddr = ManualAddressBox.Text.Trim().Replace(":", "").Replace("-", "").ToUpper();
            if (!string.IsNullOrWhiteSpace(manualAddr) && manualAddr.Length == 12)
            {
                ConfigManager.Default.DeviceAddress = manualAddr;
                ConfigManager.Default.DeviceName = "手动输入: " + manualAddr;
            }
            else
            {
                var selectedDevice = DeviceListBox.SelectedItem as BluetoothDeviceInfo;
                if (selectedDevice != null)
                {
                    ConfigManager.Default.DeviceAddress = selectedDevice.Address.ToString("X12");
                    ConfigManager.Default.DeviceName = selectedDevice.DisplayName;
                }
                else
                {
                    MessageBox.Show("请先扫描选择设备或手动输入蓝牙地址。", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
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
    }

    public class BluetoothDeviceInfo
    {
        public ulong Address { get; set; }
        public string DisplayName { get; set; } = "";
        public override string ToString() => DisplayName;
    }
}
