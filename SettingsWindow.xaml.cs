using System.Collections.ObjectModel;
using System.Linq;
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

            RssiThresholdBox.Text = ConfigManager.Default.RssiThreshold.ToString();
            ManualAddressBox.Text = ConfigManager.Default.DeviceAddress;
            ExpectedNameBox.Text = ConfigManager.Default.ExpectedDeviceName;   // 加载名称
        }

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            _devices.Clear();
            var list = await _btManager.ScanDevicesAsync();
            foreach (var d in list) _devices.Add(d);
            if (_devices.Count == 0)
                MessageBox.Show("未扫描到任何候选设备。\n请确认手机已开启 BLE-Anchor 广播并靠近电脑。");
        }

        private void DeviceListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (DeviceListBox.SelectedItem is BluetoothDeviceInfo info)
                ManualAddressBox.Text = info.Address.ToString("X12");
        }

        private void SaveManualAddress_Click(object sender, RoutedEventArgs e)
        {
            var addr = ManualAddressBox.Text.Trim().Replace(":", "").Replace("-", "").ToUpper();
            if (string.IsNullOrWhiteSpace(addr) || addr.Length != 12)
            {
                MessageBox.Show("地址格式错误（需12位十六进制）。");
                return;
            }
            ConfigManager.Default.DeviceAddress = addr;
            ConfigManager.Default.DeviceName = "手动输入";
            ConfigManager.Save();
            MessageBox.Show("地址已保存。");
        }

        private void RecordRssiButton_Click(object sender, RoutedEventArgs e)
        {
            int r = _btManager.RecordAndGetRssi();
            LastRssiText.Text = $"上次记录: {r} dBm (日志: data/rssi_log.txt)";
            MessageBox.Show($"已记录 RSSI: {r} dBm\n日志保存在程序目录 data 文件夹下。");
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(RssiThresholdBox.Text, out int th))
            {
                MessageBox.Show("阈值请输入整数。");
                return;
            }

            ConfigManager.Default.RssiThreshold = th;
            ConfigManager.Default.ExpectedDeviceName = ExpectedNameBox.Text.Trim();   // 保存名称

            var addr = ManualAddressBox.Text.Trim().Replace(":", "").Replace("-", "").ToUpper();
            if (!string.IsNullOrWhiteSpace(addr) && addr.Length == 12)
            {
                ConfigManager.Default.DeviceAddress = addr;
                ConfigManager.Default.DeviceName = "手动配置";
            }

            ConfigManager.Save();
            _btManager.UpdateThreshold(th);
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
