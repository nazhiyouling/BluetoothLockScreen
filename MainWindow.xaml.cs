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
                // 从嵌入的 WPF 资源中加载 app.ico
                var iconUri = new Uri("Resources/app.ico", UriKind.Relative);
                StreamResourceInfo streamInfo = Application.GetResourceStream(iconUri);

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
                    // 如果资源未找到，使用系统默认图标（确保程序不崩溃）
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

                // 双击托盘图标恢复窗口
                _notifyIcon.MouseDoubleClick += (sender, args) => ShowWindowFromTray(null, null);
            }
            catch (Exception ex)
            {
                // 托盘初始化完全失败，放弃托盘，但不影响主窗口
                System.Diagnostics.Debug.WriteLine("托盘图标初始化失败: " + ex.Message);
                _notifyIcon = null;
            }
        }

        /// <summary>
        /// 窗口关闭事件：根据设置决定隐藏到托盘或直接退出
        /// </summary>
        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_forceClose && ConfigManager.Default.MinimizeToTray && _notifyIcon != null)
            {
                // 隐藏窗口并显示托盘图标
                e.Cancel = true;
                Hide();
                _notifyIcon.Visible = true;
            }
            else
            {
                // 真正退出，清理资源
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
                _btManager?.Dispose();
                System.Windows.Application.Current.Shutdown();
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
            _forceClose = true;
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
