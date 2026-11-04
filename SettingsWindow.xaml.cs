using System.Collections.ObjectModel;
using System.Windows;

namespace BluetoothLockScreen
{
    public partial class SettingsWindow : Window
    {
        private readonly BluetoothManager _btManager;
        private readonly ObservableCollection<BluetoothDeviceInfo> _devices =
            new ObservableCollection<BluetoothDeviceInfo>();

        public SettingsWindow(BluetoothManager btManager)
        {
            InitializeComponent();
            _btManager = btManager;

            DeviceListBox.ItemsSource = _devices;

            // 初始化控件值
            RssiThresholdBox.Text = ConfigManager.Default.RssiThreshold.ToString();
            ExpectedNameBox.Text = ConfigManager.Default.ExpectedDeviceName ?? "";
            ManualAddressBox.Text = ConfigManager.Default.DeviceAddress ?? "";
        }

        // ---------------- 扫描候选设备 ----------------
        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            ScanButton.IsEnabled = false;
            try
            {
                _devices.Clear();
                var list = await _btManager.ScanDevicesAsync();
                foreach (var d in list)
                    _devices.Add(d);

                if (_devices.Count == 0)
                {
                    MessageBox.Show(
                        "未扫描到任何候选设备。\n请确认手机已开启 BLE-Anchor 广播并靠近电脑。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"扫描失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ScanButton.IsEnabled = true;
            }
        }

        private void DeviceListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (DeviceListBox.SelectedItem is BluetoothDeviceInfo info)
            {
                ManualAddressBox.Text = info.Address.ToString("X12");
            }
        }

        // ---------------- 记录 RSSI ----------------
        private void RecordRssiButton_Click(object sender, RoutedEventArgs e)
        {
            int r = _btManager.RecordAndGetRssi();
            LastRssiText.Text = $"上次记录: {r} dBm (日志: data/rssi_log.txt)";
            MessageBox.Show(
                $"已记录 RSSI: {r} dBm\n日志保存在程序目录 data 文件夹下。",
                "记录成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---------------- 保存设置 ----------------
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // 1. 校验阈值
            if (!int.TryParse(RssiThresholdBox.Text, out int threshold))
            {
                MessageBox.Show("RSSI 阈值请输入整数（例如 -83）。", "输入错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 2. 期望设备名称：允许为空
            string expectedName = (ExpectedNameBox.Text ?? "").Trim();

            // 3. 地址：允许为空；非空时校验 12 位十六进制
            string addr = (ManualAddressBox.Text ?? "").Trim()
                .Replace(":", "")
                .Replace("-", "")
                .ToUpper();

            if (!string.IsNullOrEmpty(addr) && addr.Length != 12)
            {
                MessageBox.Show("蓝牙地址格式错误：应为 12 位十六进制（可带冒号）。",
                    "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 4. 写入配置
            ConfigManager.Default.RssiThreshold = threshold;
            ConfigManager.Default.ExpectedDeviceName = expectedName;

            if (!string.IsNullOrEmpty(addr))
            {
                ConfigManager.Default.DeviceAddress = addr;
                ConfigManager.Default.DeviceName = expectedName;
            }

            ConfigManager.Save();

            // 5. 通知管理器更新内存状态
            _btManager.UpdateThreshold(threshold);
            _btManager.UpdateExpectedDeviceName(expectedName);

            // 6. 提示
            if (string.IsNullOrEmpty(expectedName))
            {
                MessageBox.Show(
                    "已保存。期望设备名称为空，下次启动将不会自动连接。\n请在填写设备名称后再启动监控。",
                    "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    $"已保存。\n期望设备名称: {expectedName}\n下次启动将自动扫描并连接该设备。",
                    "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            DialogResult = true;
            Close();
        }

        // ---------------- 取消 ----------------
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    // 扫描列表使用的数据模型
    public class BluetoothDeviceInfo
    {
        public ulong Address { get; set; }
        public string DisplayName { get; set; } = "";
        public int Rssi { get; set; } = int.MinValue;
    }
}
