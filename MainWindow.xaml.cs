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
        private bool _forceExit = false;         // 是否强制退出（跳过隐藏）

        public MainWindow()
        {
            InitializeComponent();

            _btManager = new BluetoothManager(
                status => Dispatcher.Invoke(() => StatusText.Text = status),
                rssi => Dispatcher.Invoke(() => RssiText.Text = $"RSSI: {rssi} dBm"),
                deviceName => Dispatcher.Invoke(() => DeviceNameText.Text = deviceName)
            );

            // 确保窗口可见
            Loaded += (s, e) =>
            {
                Show();
                Activate();
                WindowState = WindowState.Normal;
                Topmost = true;
                Topmost = false;
            };

            // 初始化托盘图标（三重保险加载）
            InitializeTrayIcon();
        }

        /// <summary>
        /// 初始化系统托盘图标
        /// </summary>
        private void InitializeTrayIcon()
        {
            try
            {
                Icon trayIcon = null;

                // 方式 1：从嵌入的 WPF 资源加载
                var iconUri = new Uri("Resources/app.ico", UriKind.Relative);
                StreamResourceInfo streamInfo = System.Windows.Application.GetResourceStream(iconUri);
                if (streamInfo != null)
                {
                    using (var stream = streamInfo.Stream)
                    {
                        trayIcon = new Icon(stream);
                    }
                }
                // 方式 2：从发布目录中的 Resources 文件夹加载
                else if (File.Exists("Resources/app.ico"))
                {
                    trayIcon = new Icon("Resources/app.ico");
                }
                // 方式 3：使用系统默认图标
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

                // 右键菜单
                var contextMenu = new ContextMenuStrip();
                contextMenu.Items.Add("显示窗口", null, ShowWindowFromTray);
                contextMenu.Items.Add("退出程序", null, ExitApplication);
                _notifyIcon.ContextMenuStrip = contextMenu;

                _notifyIcon.MouseDoubleClick += (s, e) => ShowWindowFromTray(null, null);
            }
            catch
            {
                // 托盘创建失败：程序仍可正常使用，关闭窗口直接退出
                _notifyIcon = null;
            }
        }

        /// <summary>
        /// 窗口关闭事件：固定最小化到托盘，托盘不可用时直接退出
        /// </summary>
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

        /// <summary>
        /// 从托盘恢复窗口
        /// </summary>
        private void ShowWindowFromTray(object sender, EventArgs e)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            if (_notifyIcon != null)
                _notifyIcon.Visible = false;
        }

        /// <summary>
        /// 托盘菜单“退出程序”
        /// </summary>
        private void ExitApplication(object sender, EventArgs e)
        {
            _forceExit = true;
            Close();
        }

        // ---------- 原有功能（开始/停止监控、设置） ----------
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
