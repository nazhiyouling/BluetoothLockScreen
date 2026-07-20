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
        private bool _forceExit = false;

        public MainWindow()
        {
            InitializeComponent();

            _btManager = new BluetoothManager(
                status => Dispatcher.Invoke(() => StatusText.Text = status),
                rssi => Dispatcher.Invoke(() => RssiText.Text = $"RSSI: {rssi} dBm"),
                deviceName => Dispatcher.Invoke(() => DeviceNameText.Text = deviceName)
            );

            // 保证窗口绝对可见，即使托盘初始化失败也不影响显示
            Loaded += (s, e) =>
            {
                Show();
                Activate();
                WindowState = WindowState.Normal;
                Topmost = true;
                Topmost = false;
            };

            // 初始化托盘（失败时_notifyIcon 为 null，不影响窗口）
            InitializeTrayIcon();
        }

        private void InitializeTrayIcon()
        {
            try
            {
                Icon trayIcon = null;

                // 1. 从嵌入资源加载
                var iconUri = new Uri("Resources/app.ico", UriKind.Relative);
                StreamResourceInfo streamInfo = System.Windows.Application.GetResourceStream(iconUri);
                if (streamInfo != null)
                {
                    using (var stream = streamInfo.Stream)
                    {
                        trayIcon = new Icon(stream);
                    }
                }
                // 2. 嵌入资源失败，尝试外部文件
                else if (File.Exists("Resources/app.ico"))
                {
                    trayIcon = new Icon("Resources/app.ico");
                }
                // 3. 还不行就用系统默认图标
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

                _notifyIcon.MouseDoubleClick += (s, e) => ShowWindowFromTray(null, null);
            }
            catch
            {
                // 托盘创建失败，程序仍可正常使用，只是关闭时会直接退出
                _notifyIcon = null;
            }
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_forceExit || _notifyIcon == null)
            {
                // 真正退出
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
                _btManager?.Dispose();
                System.Windows.Application.Current.Shutdown();
            }
            else
            {
                // 隐藏窗口，显示托盘图标
                e.Cancel = true;
                Hide();
                _notifyIcon.Visible = true;
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
            _forceExit = true;
            Close();
        }

        // 原有功能保持不变
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
