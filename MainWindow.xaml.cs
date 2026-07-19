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

            // 确保窗口在加载后绝对可见
            this.Loaded += (s, e) =>
            {
                this.Show();
                this.Activate();
                this.WindowState = WindowState.Normal;
                this.Topmost = true;   // 临时置顶，确保用户看到
                this.Topmost = false;  // 恢复
            };

            InitializeTrayIcon();

            // 启动时若检测到 MinimizeToTray 为 true，但托盘创建失败，自动关闭该选项
            if (ConfigManager.Default.MinimizeToTray && _notifyIcon == null)
            {
                ConfigManager.Default.MinimizeToTray = false;
                ConfigManager.Save();
                System.Windows.MessageBox.Show(
                    "托盘图标创建失败，已自动关闭“关闭时最小化到托盘”选项。\n程序将正常退出。",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void InitializeTrayIcon()
        {
            try
            {
                Icon trayIcon = null;
                var iconUri = new Uri("Resources/app.ico", UriKind.Relative);
                StreamResourceInfo streamInfo = System.Windows.Application.GetResourceStream(iconUri);

                if (streamInfo != null)
                {
                    using (var stream = streamInfo.Stream)
                    {
                        trayIcon = new Icon(stream);
                    }
                }
                else
                {
                    // 资源不存在，尝试从文件系统加载
                    if (File.Exists("Resources/app.ico"))
                    {
                        trayIcon = new Icon("Resources/app.ico");
                    }
                    else
                    {
                        trayIcon = System.Drawing.SystemIcons.Application;
                    }
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
                System.Diagnostics.Debug.WriteLine("托盘初始化失败: " + ex.Message);
                _notifyIcon = null;
            }
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 仅当托盘可用且用户开启最小化到托盘时才隐藏窗口
            if (!_forceClose && _notifyIcon != null && ConfigManager.Default.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                _notifyIcon.Visible = true;
            }
            else
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

        // 原有功能
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
