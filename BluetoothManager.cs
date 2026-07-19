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
        // 锁屏API
        [DllImport("user32.dll")]
        private static extern void LockWorkStation();

        // UI更新委托
        private readonly Action<string> _updateStatus;
        private readonly Action<int> _updateRssi;
        private readonly Action<string> _updateDeviceName;

        // 蓝牙相关
        private BluetoothLEDevice _device;
        private GattSession _session;
        private BluetoothLEAdvertisementWatcher _watcher;
        private int _rssiThreshold;
        private int _currentRssi = int.MinValue;
        private List<int> _rssiLog = new List<int>();

        // 状态控制
        private bool _isMonitoring = false;
        private string _deviceAddressStr;

        public BluetoothManager(Action<string> updateStatus, Action<int> updateRssi, Action<string> updateDeviceName)
        {
            _updateStatus = updateStatus;
            _updateRssi = updateRssi;
            _updateDeviceName = updateDeviceName;
            _rssiThreshold = Properties.Settings.Default.RssiThreshold;
        }

        /// <summary>
        /// 扫描附近的BLE设备（只返回有名称的设备）
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
                    // 避免重复
                    if (!devices.Exists(d => d.Address == info.Address))
                        devices.Add(info);
                }
            };
            watcher.Stopped += (s, e) => tcs.TrySetResult(true);

            watcher.Start();
            await Task.Delay(5000); // 扫描5秒
            watcher.Stop();
            await tcs.Task;

            return devices;
        }

        /// <summary>
        /// 开始监控指定蓝牙设备
        /// </summary>
        public async Task StartMonitoringAsync(string addressHexString)
        {
            if (_isMonitoring)
                throw new InvalidOperationException("已经在监控中。");

            _deviceAddressStr = addressHexString;
            ulong address = Convert.ToUInt64(addressHexString, 16);

            // 建立BLE连接
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_device == null)
                throw new Exception("无法找到指定的蓝牙设备，请确认设备已开启并在范围内。");

            _updateDeviceName(_device.Name);

            // 使用GATT会话保持连接并监控断开
            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            if (_session == null)
                throw new Exception("无法创建GATT会话。");

            _session.MaintainConnection = true;
            _session.SessionStatusChanged += OnSessionStatusChanged;

            // 启动RSSI观察器（监听广告包）
            StartRssiWatcher(address);

            _isMonitoring = true;
            _updateStatus("监控中...");
        }

        /// <summary>
        /// 停止监控
        /// </summary>
        public void StopMonitoring()
        {
            _isMonitoring = false;
            _updateStatus("已停止监控");

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

        /// <summary>
        /// 更新锁屏阈值
        /// </summary>
        public void UpdateThreshold(int newThreshold)
        {
            _rssiThreshold = newThreshold;
        }

        /// <summary>
        /// 记录当前RSSI（保存到内存列表，关闭时写入文件）
        /// </summary>
        public void RecordCurrentRssi()
        {
            lock (_rssiLog)
            {
                _rssiLog.Add(_currentRssi);
            }
        }

        /// <summary>
        /// 保存所有记录的RSSI到日志文件
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
                catch { /* 写入文件失败时静默处理 */ }
            }
        }

        private void StartRssiWatcher(ulong targetAddress)
        {
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            // 过滤只接收目标设备的广告包
            _watcher.AdvertisementFilter = new BluetoothLEAdvertisementFilter
            {
                Advertisement = new BluetoothLEAdvertisement
                {
                    // 可以留空，地址过滤在事件里做
                }
            };
            // 更严格的过滤：通过设置信号强度过滤器（无法直接按地址过滤，需在事件中判断）
            _watcher.Received += (s, e) =>
            {
                if (e.BluetoothAddress == targetAddress)
                {
                    int rssi = e.RawSignalStrengthInDBm;
                    _currentRssi = rssi;
                    _updateRssi(rssi);

                    // 如果RSSI超过阈值（大于，因为dBm负值）则锁屏
                    // 注意：-60 dBm比-70 dBm信号强，通常我们设定当rssi < threshold（更弱）时锁屏
                    if (rssi < _rssiThreshold && _isMonitoring)
                    {
                        LockWorkStation();
                        _updateStatus("已触发锁屏（RSSI过低）");
                    }
                }
            };

            _watcher.Start();
        }

        private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status == GattSessionStatus.Closed ||
                args.Status == GattSessionStatus.Disconnected)
            {
                // 连接断开，立即锁屏
                LockWorkStation();
                _updateStatus("已触发锁屏（蓝牙断开）");
                // 停止监控状态
                _isMonitoring = false;
                StopMonitoring();
            }
        }

        public void Dispose()
        {
            StopMonitoring();
        }
    }
}
