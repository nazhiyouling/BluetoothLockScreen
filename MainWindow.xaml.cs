using System;
using System.Drawing;            // 用于托盘图标
using System.Windows;
using System.Windows.Forms;     // 托盘控件
using System.Windows.Media;

namespace BluetoothLockScreen
{
    public partial class MainWindow : Window
    {
        private BluetoothManager _btManager;
        private bool _isMonitoring = false;
        private NotifyIcon _notifyIcon;          // 托盘图标
        private bool _forceClose = false;        // 是否强制退出（不最小化）

        public MainWindow()
        {
            InitializeComponent();

            _btManager = new BluetoothManager(
                status => Dispatcher.Invoke(() => StatusText.Text = status),
                rssi => Dispatcher.Invoke(() => RssiText.Text = $"RSSI: {rssi} dBm"),
                deviceName => Dispatcher.Invoke(() => DeviceNameText.Text = deviceName)
            );

            // 初始化托盘图标
            InitializeTrayIcon();
        }

        /// <summary>
        /// 创建系统托盘图标及右键菜单
        /// </summary>
        private void InitializeTrayIcon()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = new Icon("Resources/app.ico"),
                Visible = false,
                Text = "蓝牙锁屏监控"
            };

            // 右键菜单：显示窗口、退出程序
            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("显示窗口", null, ShowWindowFromTray);
            contextMenu.Items.Add("退出程序", null, ExitApplication);
            _notifyIcon.ContextMenuStrip = contextMenu;

            // 双击托盘图标恢复窗口
            _notifyIcon.MouseDoubleClick += (sender, args) => ShowWindowFromTray(null, null);
        }

        /// <summary>
        /// 窗口关闭事件：根据设置决定隐藏到托盘或直接退出
        /// </summary>
        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_forceClose && ConfigManager.Default.MinimizeToTray)
            {
                // 不关闭，仅隐藏窗口
                e.Cancel = true;
                Hide();
                _notifyIcon.Visible = true;
            }
            else
            {
                // 完全退出：清理托盘图标
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
            _notifyIcon.Visible = false;
        }

        /// <summary>
        /// 通过托盘菜单完全退出程序
        /// </summary>
        private void ExitApplication(object sender, EventArgs e)
        {
            _forceClose = true;   // 跳过最小化逻辑
            Close();              // 触发 Closing 事件并真正退出
        }

        // ---------- 原有功能代码 ----------
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
