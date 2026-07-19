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

        private readonly Action<string> _updateStatus;
        private readonly Action<int> _updateRssi;
        private readonly Action<string> _updateDeviceName;

        private BluetoothLEDevice _device;
        private GattSession _session;
        private BluetoothLEAdvertisementWatcher _watcher;
        private int _rssiThreshold;
        private int _currentRssi = int.MinValue;
        private List<int> _rssiLog = new List<int>();

        private bool _isMonitoring = false;

        public BluetoothManager(Action<string> updateStatus, Action<int> updateRssi, Action<string> updateDeviceName)
        {
            _updateStatus = updateStatus;
            _updateRssi = updateRssi;
            _updateDeviceName = updateDeviceName;
            _rssiThreshold = ConfigManager.Default.RssiThreshold;
        }

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

        public async Task StartMonitoringAsync(string addressHexString)
        {
            if (_isMonitoring)
                throw new InvalidOperationException("已经在监控中。");

            ulong address = Convert.ToUInt64(addressHexString, 16);
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_device == null)
                throw new Exception("无法找到指定的蓝牙设备，请确认设备已开启并在范围内。");

            _updateDeviceName(_device.Name);

            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            if (_session == null)
                throw new Exception("无法创建GATT会话。");

            _session.MaintainConnection = true;
            _session.SessionStatusChanged += OnSessionStatusChanged;

            StartRssiWatcher(address);
            _isMonitoring = true;
            _updateStatus("监控中...");
        }

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

        public void UpdateThreshold(int newThreshold)
        {
            _rssiThreshold = newThreshold;
        }

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

        private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            // 断连或会话关闭都触发锁屏
            if (args.Status == GattSessionStatus.Closed)
            {
                LockWorkStation();
                _updateStatus("已触发锁屏（蓝牙断开）");
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
