using System.Collections.ObjectModel;
using System.Windows;

namespace BluetoothLockScreen
{
    public partial class SettingsWindow : Window
    {
        private readonly BluetoothManager _btManager;
        private ObservableCollection<BluetoothDeviceInfo> _pairedDevices = new ObservableCollection<BluetoothDeviceInfo>();

        public SettingsWindow(BluetoothManager btManager)
        {
            InitializeComponent();
            _btManager = btManager;
            PairedDeviceListBox.ItemsSource = _pairedDevices;

            // 初始化控件值
            RssiThresholdBox.Text = ConfigManager.Default.RssiThreshold.ToString();
            if (!string.IsNullOrWhiteSpace(ConfigManager.Default.DeviceAddress))
            {
                ManualAddressBox.Text = ConfigManager.Default.DeviceAddress;
            }
        }

        private async void RefreshPairedButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshPairedButton.IsEnabled = false;
            _pairedDevices.Clear();

            try
            {
                var devices = await _btManager.GetPairedDevicesAsync();
                foreach (var device in devices)
                {
                    _pairedDevices.Add(device);
                }
                if (_pairedDevices.Count == 0)
                {
                    MessageBox.Show("未找到任何已配对的蓝牙设备。\n请先在 Windows 设置中配对手机。",
                                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"获取已配对设备失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RefreshPairedButton.IsEnabled = true;
            }
        }

        private void PairedDeviceListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var selected = PairedDeviceListBox.SelectedItem as BluetoothDeviceInfo;
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
            int lastRssi = _btManager.RecordAndGetRssi();
            LastRssiText.Text = $"上次记录: {lastRssi} dBm   (日志文件: data/rssi_log.txt)";
            MessageBox.Show($"当前 RSSI 值已记录：{lastRssi} dBm\n日志文件保存在程序目录下的 data/rssi_log.txt",
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
                var selectedDevice = PairedDeviceListBox.SelectedItem as BluetoothDeviceInfo;
                if (selectedDevice != null)
                {
                    ConfigManager.Default.DeviceAddress = selectedDevice.Address.ToString("X12");
                    ConfigManager.Default.DeviceName = selectedDevice.DisplayName;
                }
                else
                {
                    MessageBox.Show("请先从已配对设备列表中选择一个设备，或手动输入蓝牙地址。", "提示",
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
