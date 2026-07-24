using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BluetoothLockScreen
{
    public class BluetoothManager : IDisposable
    {
        // 锁屏 API
        [DllImport("user32.dll")]
        private static extern void LockWorkStation();

        // UI 更新委托
        private readonly Action<string> _updateStatus;
        private readonly Action<int> _updateRssi;
        private readonly Action<string> _updateDeviceName;

        // 蓝牙设备相关
        private BluetoothLEDevice _device;
        private GattSession _session;
        private BluetoothLEAdvertisementWatcher _watcher;
        private int _rssiThreshold;
        private int _currentRssi = int.MinValue;
        private List<int> _rssiLog = new List<int>();

        // 状态控制
        private bool _isMonitoring = false;
        private string _deviceAddressStr;              // 保存的蓝牙地址字符串
        private Timer _reconnectTimer;                 // 保活检查定时器
        private bool _isReconnecting = false;          // 防止重连重入

        private const int ReconnectIntervalMs = 5000;  // 每5秒检查一次

        public BluetoothManager(Action<string> updateStatus, Action<int> updateRssi, Action<string> updateDeviceName)
        {
            _updateStatus = updateStatus;
            _updateRssi = updateRssi;
            _updateDeviceName = updateDeviceName;
            _rssiThreshold = ConfigManager.Default.RssiThreshold;

            // 初始化保活定时器
            _reconnectTimer = new Timer(ReconnectIntervalMs);
            _reconnectTimer.AutoReset = true;
            _reconnectTimer.Elapsed += OnReconnectTimerElapsed;
        }

        // ---------- 扫描 ----------
        public async Task<List<BluetoothDeviceInfo>> ScanDevicesAsync()
        {
            var devices = new List<BluetoothDeviceInfo>();
            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            var tcs = new TaskCompletionSource<bool>();
            watcher.Received += (s, e) =>
            {
                // 不再过滤无名称的设备，避免漏掉 BLE 锚点
                string displayName;
                if (!string.IsNullOrEmpty(e.Advertisement.LocalName))
                {
                    displayName = e.Advertisement.LocalName;
                }
                else
                {
                    displayName = "未知设备";
                }
                displayName += $" ({e.BluetoothAddress:X12})";

                var info = new BluetoothDeviceInfo
                {
                    Address = e.BluetoothAddress,
                    DisplayName = displayName
                };
                if (!devices.Exists(d => d.Address == info.Address))
                    devices.Add(info);
            };
            watcher.Stopped += (s, e) => tcs.TrySetResult(true);

            watcher.Start();
            await Task.Delay(5000);
            watcher.Stop();
            await tcs.Task;

            return devices;
        }

        // ---------- 监控控制 ----------
        public async Task StartMonitoringAsync(string addressHexString)
        {
            if (_isMonitoring)
                throw new InvalidOperationException("已经在监控中。");

            _deviceAddressStr = addressHexString;
            await ConnectAndMonitorAsync(Convert.ToUInt64(addressHexString, 16));

            StartReconnectTimer();
            _isMonitoring = true;
            _updateStatus("监控中...");
        }

        public void StopMonitoring()
        {
            _isMonitoring = false;
            _updateStatus("已停止监控");

            StopReconnectTimer();
            CleanupConnection();
        }

        // ---------- 设置 ----------
        public void UpdateThreshold(int newThreshold)
        {
            _rssiThreshold = newThreshold;
        }

        // ---------- RSSI 日志 ----------
        public void RecordCurrentRssi()
        {
            lock (_rssiLog)
            {
                _rssiLog.Add(_currentRssi);
            }
        }

        public void SaveRssiLog()
        {
            lock (_rssiLog)
            {
                if (_rssiLog.Count == 0) return;

                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rssi_log.txt");
                try
                {
                    using (var writer = new StreamWriter(logPath, true))
                    {
                        writer.WriteLine($"--- RSSI记录 ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ---");
                        foreach (var rssi in _rssiLog)
                        {
                            writer.WriteLine($"{DateTime.Now:HH:mm:ss} - {rssi} dBm");
                        }
                    }
                }
                catch { }
            }
        }

        // ---------- 测试连接 ----------
        public async Task<int?> TestConnectionAsync(string addressHexString)
        {
            try
            {
                ulong address = Convert.ToUInt64(addressHexString, 16);
                using (var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address))
                {
                    if (device == null) return null;

                    var tcs = new TaskCompletionSource<int?>();
                    var watcher = new BluetoothLEAdvertisementWatcher
                    {
                        ScanningMode = BluetoothLEScanningMode.Active
                    };
                    watcher.Received += (s, e) =>
                    {
                        if (e.BluetoothAddress == address)
                        {
                            tcs.TrySetResult(e.RawSignalStrengthInDBm);
                            watcher.Stop();
                        }
                    };
                    watcher.Stopped += (s, e) => tcs.TrySetResult(null);
                    watcher.Start();
                    var delayTask = Task.Delay(5000);
                    var resultTask = await Task.WhenAny(tcs.Task, delayTask);
                    watcher.Stop();
                    if (resultTask is Task<int?> rssiTask)
                        return await rssiTask;
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        // ---------- 内部连接逻辑 ----------
        private async Task ConnectAndMonitorAsync(ulong address)
        {
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_device == null)
            {
                throw new Exception(
                    "无法找到指定的蓝牙设备。\n\n" +
                    "可能的原因：\n" +
                    "1. 手机未运行 BLE-Anchor APP 或未点击“开始广播”；\n" +
                    "2. 输入的蓝牙地址有误（请核对大小写及冒号）；\n" +
                    "3. 手机蓝牙已关闭或距离过远。\n\n" +
                    "请先在手机上打开 BLE-Anchor 并点击“开始广播”，然后重试。");
            }

            _updateDeviceName(_device.Name);

            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            if (_session == null)
                throw new Exception("无法创建GATT会话。");

            _session.MaintainConnection = true;
            _session.SessionStatusChanged += OnSessionStatusChanged;

            StartRssiWatcher(address);
        }

        private void StartRssiWatcher(ulong targetAddress)
        {
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            _watcher.Received += (s, e) =>
            {
                if (e.BluetoothAddress == targetAddress)
                {
                    int rssi = e.RawSignalStrengthInDBm;
                    _currentRssi = rssi;
                    _updateRssi(rssi);

                    if (rssi < _rssiThreshold && _isMonitoring)
                    {
                        LockWorkStation();
                        _updateStatus("已触发锁屏（RSSI过低）");
                    }
                }
            };

            _watcher.Start();
        }

        private void CleanupConnection()
        {
            if (_watcher != null)
            {
                _watcher.Stop();
                _watcher = null;
            }

            if (_session != null)
            {
                _session.SessionStatusChanged -= OnSessionStatusChanged;
                _session.MaintainConnection = false;
                _session.Dispose();
                _session = null;
            }

            if (_device != null)
            {
                _device.Dispose();
                _device = null;
            }
        }

        // ---------- 保活与自动重连 ----------
        private void StartReconnectTimer()
        {
            _reconnectTimer.Start();
        }

        private void StopReconnectTimer()
        {
            _reconnectTimer.Stop();
        }

        private async void OnReconnectTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (_isReconnecting || !_isMonitoring || string.IsNullOrEmpty(_deviceAddressStr))
                return;

            try
            {
                bool needReconnect = _device == null || _device.ConnectionStatus != BluetoothConnectionStatus.Connected;
                if (needReconnect)
                {
                    _isReconnecting = true;
                    _updateStatus("连接断开，正在重连...");
                    CleanupConnection();

                    ulong address = Convert.ToUInt64(_deviceAddressStr, 16);
                    await ConnectAndMonitorAsync(address);
                    _updateStatus("已重连，监控中...");
                }
            }
            catch
            {
                // 重连失败，下次定时器继续尝试
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status == GattSessionStatus.Closed)
            {
                LockWorkStation();
                _updateStatus("已触发锁屏（蓝牙断开）");
                // 定时器会自动尝试重连
            }
        }

        public void Dispose()
        {
            StopMonitoring();
            _reconnectTimer?.Dispose();
        }
    }
}
