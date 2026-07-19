using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Resources;

namespace BluetoothLockScreen
{
    public partial class MainWindow : Window
    {
        private BluetoothManager _btManager;
        private bool _isMonitoring = false;
        private NotifyIcon _notifyIcon;
        private bool _forceClose = false;

        public MainWindow()
        {
            InitializeComponent();

            _btManager = new BluetoothManager(
                status => Dispatcher.Invoke(() => StatusText.Text = status),
                rssi => Dispatcher.Invoke(() => RssiText.Text = $"RSSI: {rssi} dBm"),
                deviceName => Dispatcher.Invoke(() => DeviceNameText.Text = deviceName)
            );

            InitializeTrayIcon();
        }

        /// <summary>
        /// 初始化系统托盘图标，从嵌入资源加载图标，避免依赖外部文件
        /// </summary>
        private void InitializeTrayIcon()
        {
            try
            {
                // 明确使用 System.Windows.Application 避免歧义
                var iconUri = new Uri("Resources/app.ico", UriKind.Relative);
                StreamResourceInfo streamInfo = System.Windows.Application.GetResourceStream(iconUri);

                Icon trayIcon;
                if (streamInfo != null)
                {
                    using (var stream = streamInfo.Stream)
                    {
                        trayIcon = new Icon(stream);
                    }
                }
                else
                {
                    trayIcon = System.Drawing.SystemIcons.Application;
                }

                _notifyIcon = new NotifyIcon
                {
                    Icon = trayIcon,
                    Visible = false,
                    Text = "蓝牙锁屏监控"
                };

                var contextMenu = new ContextMenuStrip();
                contextMenu.Items.Add("显示窗口", null, ShowWindowFromTray);
                contextMenu.Items.Add("退出程序", null, ExitApplication);
                _notifyIcon.ContextMenuStrip = contextMenu;

                _notifyIcon.MouseDoubleClick += (sender, args) => ShowWindowFromTray(null, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("托盘图标初始化失败: " + ex.Message);
                _notifyIcon = null;
            }
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_forceClose && ConfigManager.Default.MinimizeToTray && _notifyIcon != null)
            {
                e.Cancel = true;
                Hide();
                _notifyIcon.Visible = true;
            }
            else
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
                _btManager?.Dispose();
                System.Windows.Application.Current.Shutdown();
            }
        }

        private void ShowWindowFromTray(object sender, EventArgs e)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            if (_notifyIcon != null)
                _notifyIcon.Visible = false;
        }

        private void ExitApplication(object sender, EventArgs e)
        {
            _forceClose = true;
            Close();
        }

        private async void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isMonitoring)
            {
                var savedAddress = ConfigManager.Default.DeviceAddress;
                if (string.IsNullOrWhiteSpace(savedAddress))
                {
                    System.Windows.MessageBox.Show("请先在设置界面选择一个蓝牙设备。", "提示",
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
                    System.Windows.MessageBox.Show($"启动监控失败：{ex.Message}", "错误",
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
            settingsWindow.ShowDialog();
        }
    }
}
