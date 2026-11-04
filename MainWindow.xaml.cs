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
        private bool _isAutoStarting = true;

        public MainWindow()
        {
            InitializeComponent();

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            Title = $"蓝牙锁屏监控 V{ver.Major}.{ver.Minor:D2}.{ver.Build:D2}" +
                    (ver.Revision > 0 ? $".{ver.Revision:D4}" : "");

            _btManager = new BluetoothManager(
                s => Dispatcher.Invoke(() => StatusText.Text = s),
                r => Dispatcher.Invoke(() => RssiText.Text = $"RSSI: {r} dBm"),
                n => Dispatcher.Invoke(() => DeviceNameText.Text = n));

            InitTray();

            Loaded += async (_, _) =>
            {
                Activate();
                Topmost = true;
                Topmost = false;

                // 首次启动：如果期望名称为空，不自动连接
                if (string.IsNullOrEmpty(ConfigManager.Default.ExpectedDeviceName?.Trim()))
                {
                    StatusText.Text = "请先在设置中填写期望设备名称";
                    DeviceNameText.Text = "未配置";
                    _isMonitoring = false;
                    StartStopButton.Content = "▶\n开始监控";
                    StartStopButton.Background = new SolidColorBrush(Colors.LightGreen);
                    StartStopButton.Foreground = new SolidColorBrush(Colors.Black);
                }
                else
                {
                    try
                    {
                        bool started = await _btManager.AutoConnectAndMonitorAsync();
                        if (started)
                        {
                            _isMonitoring = true;
                            StartStopButton.Content = "⏹\n停止监控";
                            StartStopButton.Background = new SolidColorBrush(Colors.IndianRed);
                            StartStopButton.Foreground = new SolidColorBrush(Colors.White);
                        }
                        else
                        {
                            StatusText.Text = "未配置设备名称，请先在设置中填写";
                            _isMonitoring = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("自动连接失败: " + ex.Message);
                        StatusText.Text = "自动连接失败，请检查设置";
                    }
                }

                // 自动隐藏到托盘
                if (_isAutoStarting && _notifyIcon != null)
                {
                    Hide();
                    _notifyIcon.Visible = true;
                    _isAutoStarting = false;
                }
            };
        }

        private void InitTray()
        {
            try
            {
                Icon icon;
                var streamInfo = System.Windows.Application.GetResourceStream(
                    new Uri("Resources/app.ico", UriKind.Relative));
                if (streamInfo != null)
                {
                    using (var s = streamInfo.Stream) icon = new Icon(s);
                }
                else if (File.Exists("Resources/app.ico"))
                {
                    icon = new Icon("Resources/app.ico");
                }
                else icon = System.Drawing.SystemIcons.Application;

                _notifyIcon = new NotifyIcon
                {
                    Icon = icon,
                    Visible = false,
                    Text = "蓝牙锁屏监控"
                };

                var menu = new ContextMenuStrip();
                menu.Items.Add("显示窗口", null, (_, _) =>
                {
                    Show();
                    Activate();
                    WindowState = WindowState.Normal;
                    _notifyIcon.Visible = false;
                });
                menu.Items.Add("退出程序", null, (_, _) =>
                {
                    _forceExit = true;
                    Close();
                });
                _notifyIcon.ContextMenuStrip = menu;
                _notifyIcon.MouseDoubleClick += (_, _) =>
                {
                    Show();
                    Activate();
                    WindowState = WindowState.Normal;
                    _notifyIcon.Visible = false;
                };
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
                if (string.IsNullOrEmpty(ConfigManager.Default.ExpectedDeviceName?.Trim()))
                {
                    System.Windows.MessageBox.Show(
                        "请先在设置中填写期望设备名称，再开始监控。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                try
                {
                    string addr = ConfigManager.Default.DeviceAddress;
                    bool started = await _btManager.StartMonitoringAsync(addr);
                    if (!started)
                    {
                        System.Windows.MessageBox.Show(
                            "启动失败：期望设备名称为空，请先在设置中填写。",
                            "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    _isMonitoring = true;
                    StartStopButton.Content = "⏹\n停止监控";
                    StartStopButton.Background = new SolidColorBrush(Colors.IndianRed);
                    StartStopButton.Foreground = new SolidColorBrush(Colors.White);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"启动监控失败：{ex.Message}");
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
            var dlg = new SettingsWindow(_btManager) { Owner = this };
            dlg.ShowDialog();

            // 保存后如果名称为空，更新界面提示
            if (string.IsNullOrEmpty(ConfigManager.Default.ExpectedDeviceName?.Trim()))
            {
                StatusText.Text = "请先在设置中填写期望设备名称";
                DeviceNameText.Text = "未配置";
            }
        }
    }
}
