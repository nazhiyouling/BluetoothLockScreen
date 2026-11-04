using System;
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

            // 基础配置
            RssiThresholdBox.Text = ConfigManager.Default.RssiThreshold.ToString();
            ExpectedNameBox.Text = ConfigManager.Default.ExpectedDeviceName ?? "";
            ManualAddressBox.Text = ConfigManager.Default.DeviceAddress ?? "";

            // 高级时间参数
            LowRssiCountBox.Text = ConfigManager.Default.LowRssiCount.ToString();
            QuickScanCooldownBox.Text = ConfigManager.Default.QuickScanCooldownSec.ToString();
            QuickScanTimeoutBox.Text = ConfigManager.Default.QuickScanTimeoutSec.ToString();
            ReconnectIntervalBox.Text = ConfigManager.Default.ReconnectIntervalSec.ToString();
            DisconnectCountBox.Text = ConfigManager.Default.DisconnectCount.ToString();
            PacketTimeoutBox.Text = ConfigManager.Default.PacketTimeoutSec.ToString();
            GattGraceBox.Text = ConfigManager.Default.GattGraceSec.ToString();
            LockCooldownBox.Text = ConfigManager.Default.LockCooldownSec.ToString();
        }

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            ScanButton.IsEnabled = false;
            try
            {
                _devices.Clear();
                var list = await _btManager.ScanDevicesAsync();
                foreach (var d in list) _devices.Add(d);

                if (_devices.Count == 0)
                {
                    MessageBox.Show(
                        "未扫描到任何候选设备。\n请确认手机已开启 BLE-Anchor 广播并靠近电脑。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
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
                ManualAddressBox.Text = info.Address.ToString("X12");
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // 校验 RSSI 阈值
            if (!int.TryParse(RssiThresholdBox.Text, out int threshold))
            {
                MessageBox.Show("RSSI 阈值请输入整数（例如 -83）。", "输入错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 校验高级时间参数
            if (!TryParseRange(LowRssiCountBox.Text, 1, 10, "连续低RSSI次数", out int lowRssiCount)) return;
            if (!TryParseRange(QuickScanCooldownBox.Text, 1, 300, "快速扫描冷却", out int quickScanCooldown)) return;
            if (!TryParseRange(QuickScanTimeoutBox.Text, 1, 300, "快速扫描超时", out int quickScanTimeout)) return;
            if (!TryParseRange(ReconnectIntervalBox.Text, 1, 300, "重连定时器间隔", out int reconnectInterval)) return;
            if (!TryParseRange(DisconnectCountBox.Text, 1, 10, "连续断开次数", out int disconnectCount)) return;
            if (!TryParseRange(PacketTimeoutBox.Text, 1, 300, "看门狗超时", out int packetTimeout)) return;
            if (!TryParseRange(GattGraceBox.Text, 1, 300, "GATT关闭宽限期", out int gattGrace)) return;
            if (!TryParseRange(LockCooldownBox.Text, 1, 300, "锁屏冷却", out int lockCooldown)) return;

            // 期望名称与地址
            string expectedName = (ExpectedNameBox.Text ?? "").Trim();
            string addr = (ManualAddressBox.Text ?? "").Trim()
                .Replace(":", "").Replace("-", "").ToUpper();

            if (!string.IsNullOrEmpty(addr) && addr.Length != 12)
            {
                MessageBox.Show("蓝牙地址格式错误：应为 12 位十六进制（可带冒号）。",
                    "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 写入配置
            ConfigManager.Default.RssiThreshold = threshold;
            ConfigManager.Default.ExpectedDeviceName = expectedName;
            ConfigManager.Default.LowRssiCount = lowRssiCount;
            ConfigManager.Default.QuickScanCooldownSec = quickScanCooldown;
            ConfigManager.Default.QuickScanTimeoutSec = quickScanTimeout;
            ConfigManager.Default.ReconnectIntervalSec = reconnectInterval;
            ConfigManager.Default.DisconnectCount = disconnectCount;
            ConfigManager.Default.PacketTimeoutSec = packetTimeout;
            ConfigManager.Default.GattGraceSec = gattGrace;
            ConfigManager.Default.LockCooldownSec = lockCooldown;

            if (!string.IsNullOrEmpty(addr))
            {
                ConfigManager.Default.DeviceAddress = addr;
                ConfigManager.Default.DeviceName = expectedName;
            }

            ConfigManager.Save();

            // 通知管理器热更新
            _btManager.UpdateSettings(
                threshold,
                expectedName,
                lowRssiCount,
                quickScanCooldown,
                quickScanTimeout,
                reconnectInterval,
                disconnectCount,
                packetTimeout,
                gattGrace,
                lockCooldown);

            MessageBox.Show("设置已保存并立即生效。", "保存成功",
                MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }

        private bool TryParseRange(string text, int min, int max, string fieldName, out int value)
        {
            if (!int.TryParse(text, out value))
            {
                MessageBox.Show($"{fieldName} 请输入整数。", "输入错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (value < min || value > max)
            {
                MessageBox.Show($"{fieldName} 范围应为 {min} ~ {max}。", "输入错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
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
        public int Rssi { get; set; } = int.MinValue;
    }
}
