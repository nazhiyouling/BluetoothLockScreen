using System;
using System.Windows;
using System.Windows.Media;

namespace BluetoothLockScreen
{
    public partial class MainWindow : Window
    {
        private BluetoothManager _btManager;
        private bool _isMonitoring = false;

        public MainWindow()
        {
            InitializeComponent();

            // 初始化蓝牙管理器，传入UI更新委托
            _btManager = new BluetoothManager(
                status => Dispatcher.Invoke(() => StatusText.Text = status),
                rssi => Dispatcher.Invoke(() => RssiText.Text = $"RSSI: {rssi} dBm"),
                deviceName => Dispatcher.Invoke(() => DeviceNameText.Text = deviceName)
            );
        }

        private async void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isMonitoring)
            {
                // 检查是否已选择设备（蓝牙地址已保存）
                var savedAddress = Properties.Settings.Default.DeviceAddress;
                if (string.IsNullOrWhiteSpace(savedAddress))
                {
                    MessageBox.Show("请先在设置界面选择一个蓝牙设备。", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                try
                {
                    await _btManager.StartMonitoringAsync(savedAddress);
                    _isMonitoring = true;
                    StartStopButton.Content = "⏹\n停止监控";
                    StartStopButton.Background = new SolidColorBrush(Colors.IndianRed);
                    StartStopButton.Foreground = new SolidColorBrush(Colors.White);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"启动监控失败：{ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                _btManager.StopMonitoring();
                _isMonitoring = false;
                StartStopButton.Content = "▶\n开始监控";
                StartStopButton.Background = new SolidColorBrush(Colors.LightGreen);
                StartStopButton.Foreground = new SolidColorBrush(Colors.Black);
                StatusText.Text = "未开始监控";
                RssiText.Text = "RSSI: --";
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(_btManager);
            settingsWindow.Owner = this;
            settingsWindow.ShowDialog(); // 模态对话框，关闭后设备可能已更新
        }

        protected override void OnClosed(EventArgs e)
        {
            // 关闭软件时生成RSSI日志
            _btManager?.SaveRssiLog();
            _btManager?.Dispose();
            base.OnClosed(e);
        }
    }
}
