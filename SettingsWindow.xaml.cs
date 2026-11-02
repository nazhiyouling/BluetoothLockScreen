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
            ExpectedNameBox.Text = ConfigManager.Default.ExpectedDeviceName;
            ManualAddressBox.Text = ConfigManager.Default.DeviceAddress;
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

            string expectedName = ExpectedNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(expectedName))
            {
                MessageBox.Show("期望设备名称不能为空。");
                return;
            }
            if (expectedName.Length > 32)
            {
                MessageBox.Show("期望设备名称过长，建议不超过 32 个字符。");
                return;
            }

            ConfigManager.Default.RssiThreshold = th;
            ConfigManager.Default.ExpectedDeviceName = expectedName;

            string addr = ManualAddressBox.Text.Trim().Replace(":", "").Replace("-", "").ToUpper();
            if (!string.IsNullOrWhiteSpace(addr) && addr.Length == 12)
            {
                ConfigManager.Default.DeviceAddress = addr;
                ConfigManager.Default.DeviceName = expectedName;
            }

            ConfigManager.Save();
            _btManager.UpdateThreshold(th);
            _btManager.UpdateExpectedDeviceName(expectedName);

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
        public int Rssi { get; set; } = int.MinValue;
    }
}
