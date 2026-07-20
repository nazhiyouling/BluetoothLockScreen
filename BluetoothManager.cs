using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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

        // 蓝牙设备
        private BluetoothLEDevice _device;
        private GattSession _session;
        private BluetoothLEAdvertisementWatcher _rssiWatcher;

        // 配置
        private int _rssiThreshold;
        private string _targetDeviceAddress;
        private ulong _targetAddressUlong;

        // 状态
        private bool _isMonitoring = false;
        private int _currentRssi = int.MinValue;
        private readonly List<int> _rssiLog = new List<int>();

        // 重连相关
        private bool _isReconnecting = false;
        private readonly object _reconnectLock = new object();
        private Task _reconnectTask;

        public BluetoothManager(Action<string> updateStatus, Action<int> updateRssi, Action<string> updateDeviceName)
        {
            _updateStatus = updateStatus;
            _updateRssi = updateRssi;
            _updateDeviceName = updateDeviceName;
            _rssiThreshold = ConfigManager.Default.RssiThreshold;
        }

        /// <summary>扫描附近 BLE 设备</summary>
        public async Task<List<BluetoothDeviceInfo>> ScanDevicesAsync()
        {
            var devices = new List<BluetoothDeviceInfo>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
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

        /// <summary>测试连接：仅获取一次 RSSI</summary>
        public async Task<int?> TestConnectionAsync(string addressHexString)
        {
            try
            {
                ulong address = Convert.ToUInt64(addressHexString, 16);
                using (var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address))
                {
                    if (device == null) return null;

                    var tcs = new TaskCompletionSource<int?>();
                    var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
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
                    await Task.WhenAny(tcs.Task, Task.Delay(5000));
                    watcher.Stop();
                    return await tcs.Task;
                }
            }
            catch { return null; }
        }

        /// <summary>开始监控指定设备（使用 MAC 地址字符串）</summary>
        public async Task StartMonitoringAsync(string addressHexString)
        {
            if (_isMonitoring) throw new InvalidOperationException("已经在监控中。");

            _targetDeviceAddress = addressHexString;
            _targetAddressUlong = Convert.ToUInt64(addressHexString, 16);

            await ConnectAndMonitorAsync();

            _isMonitoring = true;
            _updateStatus("监控中...");
        }

        /// <summary>执行真正的连接和监控设置</summary>
        private async Task ConnectAndMonitorAsync()
        {
            // 先断开旧连接（如果有）
            CleanupConnection();

            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_targetAddressUlong);
            if (_device == null)
                throw new Exception("无法找到指定的蓝牙设备，请确认手机已开启 BLE 广播。");

            _updateDeviceName(_device.Name);

            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            if (_session == null)
                throw new Exception("无法创建 GATT 会话。");

            _session.MaintainConnection = true;       // 系统级保活
            _session.SessionStatusChanged += OnSessionStatusChanged;

            StartRssiWatcher();
        }

        /// <summary>停止监控（手动）</summary>
        public void StopMonitoring()
        {
            _isMonitoring = false;
            _updateStatus("已停止监控");
            CleanupConnection();
        }

        /// <summary>清理连接和 RSSI 监听器</summary>
        private void CleanupConnection()
        {
            if (_rssiWatcher != null)
            {
                _rssiWatcher.Stop();
                _rssiWatcher = null;
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

        /// <summary>更新 RSSI 阈值</summary>
        public void UpdateThreshold(int newThreshold) => _rssiThreshold = newThreshold;

        /// <summary>记录当前 RSSI</summary>
        public void RecordCurrentRssi()
        {
            lock (_rssiLog) { _rssiLog.Add(_currentRssi); }
        }

        /// <summary>保存 RSSI 日志文件</summary>
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
                            writer.WriteLine($"{DateTime.Now:HH:mm:ss} - {rssi} dBm");
                    }
                }
                catch { /* 忽略写入错误 */ }
            }
        }

        /// <summary>启动 BLE 广告包监听（获取 RSSI）</summary>
        private void StartRssiWatcher()
        {
            _rssiWatcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            _rssiWatcher.Received += (s, e) =>
            {
                if (e.BluetoothAddress == _targetAddressUlong)
                {
                    int rssi = e.RawSignalStrengthInDBm;
                    _currentRssi = rssi;
                    _updateRssi(rssi);

                    // RSSI 低于阈值触发锁屏
                    if (_isMonitoring && rssi < _rssiThreshold)
                    {
                        LockWorkStation();
                        _updateStatus("已触发锁屏（RSSI过低）");
                    }
                }
            };
            _rssiWatcher.Start();
        }

        /// <summary>GATT 会话状态变化（连接断开时触发）</summary>
        private async void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status == GattSessionStatus.Closed)  // 断开连接
            {
                // 如果是主动停止监控，则不再重连
                if (!_isMonitoring) return;

                _updateStatus("蓝牙连接断开，正在尝试重连...");

                // 防止重复重连
                lock (_reconnectLock)
                {
                    if (_isReconnecting) return;
                    _isReconnecting = true;
                }

                // 开始重连任务
                _reconnectTask = ReconnectAsync();
                await _reconnectTask;
            }
        }

        /// <summary>自动重连逻辑：最多尝试 30 秒</summary>
        private async Task ReconnectAsync()
        {
            int maxAttempts = 6;      // 每 5 秒尝试一次，最多 6 次 = 30 秒
            bool reconnected = false;

            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    await Task.Delay(5000);   // 间隔 5 秒

                    // 再次检查监控状态，若已手动停止则退出
                    if (!_isMonitoring) break;

                    await ConnectAndMonitorAsync();
                    reconnected = true;
                    _updateStatus("蓝牙已重新连接");
                    break;
                }
                catch
                {
                    // 忽略单次重连失败，继续尝试
                }
            }

            lock (_reconnectLock) { _isReconnecting = false; }

            // 如果所有尝试均失败，则认为设备真的远离，执行锁屏
            if (!reconnected && _isMonitoring)
            {
                LockWorkStation();
                _updateStatus("已触发锁屏（蓝牙断开且无法重连）");
                StopMonitoring();
            }
        }

        public void Dispose()
        {
            StopMonitoring();
        }
    }
}
