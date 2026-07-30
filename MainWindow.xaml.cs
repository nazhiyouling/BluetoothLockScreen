using System;
using System.Drawing;
using System.IO;
using System.Reflection;
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
            // 动态版本号
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            Title = $"蓝牙锁屏监控 V{ver.Major}.{ver.Minor:D2}.{ver.Build:D2}" + (ver.Revision > 0 ? $".{ver.Revision:D4}" : "");

            _btManager = new BluetoothManager(
                s => Dispatcher.Invoke(() => StatusText.Text = s),
                r => Dispatcher.Invoke(() => RssiText.Text = $"RSSI: {r} dBm"),
                n => Dispatcher.Invoke(() => DeviceNameText.Text = n));

            Loaded += (_, _) => { Activate(); Topmost = true; Topmost = false; };
            InitTray();
        }

        private void InitTray()
        {
            try
            {
                Icon icon;
                var streamInfo = System.Windows.Application.GetResourceStream(new Uri("Resources/app.ico", UriKind.Relative));
                if (streamInfo != null)
                {
                    using (var s = streamInfo.Stream) icon = new Icon(s);
                }
                else if (File.Exists("Resources/app.ico"))
                {
                    icon = new Icon("Resources/app.ico");
                }
                else icon = System.Drawing.SystemIcons.Application;

                _notifyIcon = new NotifyIcon { Icon = icon, Visible = false, Text = "蓝牙锁屏监控" };
                var menu = new ContextMenuStrip();
                menu.Items.Add("显示窗口", null, (_, _) => { Show(); Activate(); WindowState = WindowState.Normal; _notifyIcon.Visible = false; });
                menu.Items.Add("退出程序", null, (_, _) => { _forceExit = true; Close(); });
                _notifyIcon.ContextMenuStrip = menu;
                _notifyIcon.MouseDoubleClick += (_, _) => { Show(); Activate(); WindowState = WindowState.Normal; _notifyIcon.Visible = false; };
            }
            catch { _notifyIcon = null; }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_forceExit || _notifyIcon == null)
            {
                _notifyIcon?.Dispose();
                _btManager?.Dispose();
                System.Windows.Application.Current.Shutdown();
            }
            else
            {
                e.Cancel = true;
                Hide();
                _notifyIcon.Visible = true;
            }
        }

        private async void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isMonitoring)
            {
                var addr = ConfigManager.Default.DeviceAddress;
                if (string.IsNullOrWhiteSpace(addr)) { MessageBox.Show("请先在设置中配置蓝牙设备。"); return; }
                try
                {
                    await _btManager.StartMonitoringAsync(addr);
                    _isMonitoring = true;
                    StartStopButton.Content = "⏹\n停止监控";
                    StartStopButton.Background = new SolidColorBrush(Colors.IndianRed);
                    StartStopButton.Foreground = new SolidColorBrush(Colors.White);
                }
                catch (Exception ex) { MessageBox.Show($"启动失败：{ex.Message}"); }
            }
            else
            {
                _btManager.StopMonitoring();
                _isMonitoring = false;
                StartStopButton.Content = "▶\n开始监控";
                StartStopButton.Background = new SolidColorBrush(Colors.LightGreen);
                StartStopButton.Foreground = new SolidColorBrush(Colors.Black);
                StatusText.Text = "未开始监控"; RssiText.Text = "RSSI: --";
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            new SettingsWindow(_btManager) { Owner = this }.ShowDialog();
        }
    }
}
