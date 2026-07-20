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
        private string _deviceAddressStr;                  // 保存的蓝牙地址字符串
        private Timer _reconnectTimer;                     // 保活检查定时器
        private bool _isReconnecting = false;              // 防止重连重入

        // 断开自动重连间隔（毫秒）
        private const int ReconnectIntervalMs = 5000;      // 每5秒检查一次

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

        /// <summary>
        /// 扫描附近的 BLE 设备（只返回有名称的设备）
        /// </summary>
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
                if (!string.IsNullOrEmpty(e.Advertisement.LocalName))
                {
                    var info = new BluetoothDeviceInfo
                    {
                        Address = e.BluetoothAddress,
                        DisplayName = $"{e.Advertisement.LocalName} ({e.BluetoothAddress:X})"
                    };
                    if (!devices.Exists(d => d.Address == info.Address))
                        devices.Add(info);
                }
            };
            watcher.Stopped += (s, e) => tcs.TrySetResult(true);

            watcher.Start();
            await Task.Delay(5000);
            watcher.Stop();
            await tcs.Task;

            return devices;
        }

        /// <summary>
        /// 开始监控指定蓝牙设备（建立连接并启动 RSSI 观察）
        /// </summary>
        public async Task StartMonitoringAsync(string addressHexString)
        {
            if (_isMonitoring)
                throw new InvalidOperationException("已经在监控中。");

            _deviceAddressStr = addressHexString;
            await ConnectAndMonitorAsync(Convert.ToUInt64(addressHexString, 16));

            // 启动保活定时器
            StartReconnectTimer();
            _isMonitoring = true;
            _updateStatus("监控中...");
        }

        /// <summary>
        /// 停止监控，关闭连接并释放资源
        /// </summary>
        public void StopMonitoring()
        {
            _isMonitoring = false;
            _updateStatus("已停止监控");

            StopReconnectTimer();

            CleanupConnection();
        }

        /// <summary>
        /// 更新锁屏阈值
        /// </summary>
        public void UpdateThreshold(int newThreshold)
        {
            _rssiThreshold = newThreshold;
        }

        /// <summary>
        /// 记录当前 RSSI 到内存列表（关闭时写入日志）
        /// </summary>
        public void RecordCurrentRssi()
        {
            lock (_rssiLog)
            {
                _rssiLog.Add(_currentRssi);
            }
        }

        /// <summary>
        /// 保存所有记录的 RSSI 到日志文件
        /// </summary>
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
                catch { /* 写入失败静默处理 */ }
            }
        }

        // ------------------------- 保活与自动重连 -------------------------

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
            // 防止重入
            if (_isReconnecting || !_isMonitoring || string.IsNullOrEmpty(_deviceAddressStr))
                return;

            try
            {
                // 检查设备连接状态
                bool needReconnect = _device == null || _device.ConnectionStatus != BluetoothConnectionStatus.Connected;
                if (needReconnect)
                {
                    _isReconnecting = true;
                    _updateStatus("连接断开，正在重连...");
                    CleanupConnection();  // 清理旧连接

                    ulong address = Convert.ToUInt64(_deviceAddressStr, 16);
                    await ConnectAndMonitorAsync(address);
                    _updateStatus("已重连，监控中...");
                }
            }
            catch (Exception ex)
            {
                _updateStatus($"重连失败：{ex.Message}");
                // 下次定时器触发继续尝试
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        // 实际建立连接和 RSSI 监听
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

            // 创建 GATT 会话并设置保持连接
            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            if (_session == null)
                throw new Exception("无法创建GATT会话。");

            _session.MaintainConnection = true;
            _session.SessionStatusChanged += OnSessionStatusChanged;

            // 启动 RSSI 监听
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

                    // RSSI 低于阈值立即锁屏
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

        private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status == GattSessionStatus.Closed)
            {
                // 连接断开立即锁屏，然后定时器会自动重连
                LockWorkStation();
                _updateStatus("已触发锁屏（蓝牙断开）");
                // 注意：不要在这里直接调用 StopMonitoring，让定时器负责重连
                // 但需要标记断开状态，防止重复锁屏（在定时器重连前再次触发）
            }
        }

        public void Dispose()
        {
            StopMonitoring();
            _reconnectTimer?.Dispose();
        }
    }
}
