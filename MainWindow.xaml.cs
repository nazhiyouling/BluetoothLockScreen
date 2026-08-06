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
        private bool _isAutoStarting = true;   // 标记是否为自动启动（首次加载）

        public MainWindow()
        {
            InitializeComponent();

            // 动态版本号
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

                // 启动时自动扫描并连接 BLE-Anchor，静默处理异常
                try
                {
                    await _btManager.AutoConnectAndMonitorAsync();
                    _isMonitoring = true;
                    StartStopButton.Content = "⏹\n停止监控";
                    StartStopButton.Background = new SolidColorBrush(Colors.IndianRed);
                    StartStopButton.Foreground = new SolidColorBrush(Colors.White);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("自动连接失败: " + ex.Message);
                    // 不弹窗，仅记录日志
                }
                finally
                {
                    // 无论连接是否成功，都隐藏窗口并显示托盘图标（仅自动启动时）
                    if (_isAutoStarting && _notifyIcon != null)
                    {
                        Hide();
                        _notifyIcon.Visible = true;
                        _isAutoStarting = false;  // 后续手动操作不再自动隐藏
                    }
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
                // 手动开始监控：尝试使用已保存地址或自动扫描
                string addr = ConfigManager.Default.DeviceAddress;
                try
                {
                    if (!string.IsNullOrWhiteSpace(addr))
                        await _btManager.StartMonitoringAsync(addr);
                    else
                        await _btManager.AutoConnectAndMonitorAsync();

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
            new SettingsWindow(_btManager) { Owner = this }.ShowDialog();
        }
    }
}
