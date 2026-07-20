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
            // 显示已保存的设备地址（如果有）
            if (!string.IsNullOrWhiteSpace(ConfigManager.Default.DeviceAddress))
            {
                ManualAddressBox.Text = ConfigManager.Default.DeviceAddress;
            }
        }

        /// <summary>
        /// 直接保存手动输入的蓝牙地址（不要求扫描）
        /// </summary>
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

            // 立即写入配置（保存设置时可再确认）
            ConfigManager.Default.DeviceAddress = address;
            ConfigManager.Default.DeviceName = "手动输入: " + address;
            ConfigManager.Save();
            MessageBox.Show("蓝牙地址已保存，可以开始监控。", "保存成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
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
            // 选中设备时自动填入地址框（方便查看）
            var selected = DeviceListBox.SelectedItem as BluetoothDeviceInfo;
            if (selected != null)
            {
                ManualAddressBox.Text = selected.Address.ToString("X12");
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(RssiThresholdBox.Text, out int threshold))
            {
                MessageBox.Show("请输入有效的整数阈值。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ConfigManager.Default.RssiThreshold = threshold;

            // 优先使用手动输入的地址，如果为空则从列表选择（若列表有选择）
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
