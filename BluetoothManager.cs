using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

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
        private string _deviceAddressStr;
        private Timer _reconnectTimer;
        private bool _isReconnecting = false;
        private const int ReconnectIntervalMs = 5000;

        private static readonly string DataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly string RssiLogPath = Path.Combine(DataFolder, "rssi_log.txt");
        private static readonly Guid OurServiceUuid = Guid.Parse("0000ABCD-0000-1000-8000-00805F9B34FB");

        public BluetoothManager(Action<string> status, Action<int> rssi, Action<string> name)
        {
            _updateStatus = status; _updateRssi = rssi; _updateDeviceName = name;
            _rssiThreshold = ConfigManager.Default.RssiThreshold;
            _reconnectTimer = new Timer(ReconnectIntervalMs) { AutoReset = true };
            _reconnectTimer.Elapsed += OnReconnectTimer;
            Directory.CreateDirectory(DataFolder);
        }

        // ---------- 扫描设备，优先显示我们的 UUID 设备 ----------
        public async Task<List<BluetoothDeviceInfo>> ScanDevicesAsync()
        {
            var dict = new Dictionary<ulong, BluetoothDeviceInfo>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            var tcs = new TaskCompletionSource<bool>();
            watcher.Received += (s, e) =>
            {
                bool ours = e.Advertisement.ServiceUuids.Contains(OurServiceUuid);
                string name = ours ? "BLE-Anchor" :
                              (!string.IsNullOrEmpty(e.Advertisement.LocalName) ? e.Advertisement.LocalName : "未知设备");
                if (!dict.ContainsKey(e.BluetoothAddress))
                {
                    dict[e.BluetoothAddress] = new BluetoothDeviceInfo
                    {
                        Address = e.BluetoothAddress,
                        DisplayName = $"{name} ({e.BluetoothAddress:X12})",
                        Rssi = e.RawSignalStrengthInDBm
                    };
                }
                else
                {
                    dict[e.BluetoothAddress].Rssi = e.RawSignalStrengthInDBm;
                    if (ours && !dict[e.BluetoothAddress].DisplayName.StartsWith("BLE-Anchor"))
                        dict[e.BluetoothAddress].DisplayName = $"BLE-Anchor ({e.BluetoothAddress:X12})";
                }
            };
            watcher.Stopped += (s, e) => tcs.TrySetResult(true);
            watcher.Start();
            await Task.Delay(5000);
            watcher.Stop();
            await tcs.Task;
            return dict.Values.OrderByDescending(d => d.Rssi).ToList();
        }

        // 获取已配对设备（备用）
        public async Task<List<BluetoothDeviceInfo>> GetPairedDevicesAsync()
        {
            var devices = new List<BluetoothDeviceInfo>();
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var infos = await DeviceInformation.FindAllAsync(selector);
            foreach (var info in infos)
            {
                if (info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out object prop))
                {
                    var addr = prop.ToString().Replace(":", "");
                    devices.Add(new BluetoothDeviceInfo { Address = Convert.ToUInt64(addr, 16), DisplayName = $"{info.Name} ({prop})" });
                }
            }
            return devices;
        }

        // ---------- 启动监控，先尝试用保存的地址连接，失败则自动扫描最新地址 ----------
        public async Task StartMonitoringAsync(string addressHex)
        {
            if (_isMonitoring) throw new InvalidOperationException("已在监控中");
            _deviceAddressStr = addressHex;
            await ConnectOrAutoScan();
            _reconnectTimer.Start();
            _isMonitoring = true;
            _updateStatus("监控中...");
        }

        public void StopMonitoring()
        {
            _isMonitoring = false;
            _updateStatus("已停止");
            _reconnectTimer.Stop();
            Cleanup();
        }

        public void UpdateThreshold(int t) => _rssiThreshold = t;

        public int RecordAndGetRssi()
        {
            int r = _currentRssi;
            lock (_rssiLog) _rssiLog.Add(r);
            AppendRssi(r);
            return r;
        }

        public async Task<int?> TestConnectionAsync(string addressHex)
        {
            try
            {
                ulong addr = Convert.ToUInt64(addressHex, 16);
                using (var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr))
                {
                    if (dev == null) return null;
                    var tcs = new TaskCompletionSource<int?>();
                    var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
                    w.Received += (s, e) => { if (e.BluetoothAddress == addr) { tcs.TrySetResult(e.RawSignalStrengthInDBm); w.Stop(); } };
                    w.Stopped += (s, e) => tcs.TrySetResult(null);
                    w.Start();
                    await Task.WhenAny(tcs.Task, Task.Delay(5000));
                    w.Stop();
                    return await tcs.Task;
                }
            }
            catch { return null; }
        }

        // ---------- 内部：尝试用指定地址连接，失败则扫描 UUID 匹配的设备 ----------
        private async Task ConnectOrAutoScan()
        {
            try
            {
                ulong addr = Convert.ToUInt64(_deviceAddressStr, 16);
                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                if (_device == null) throw new Exception("设备不在范围内");
            }
            catch
            {
                // 自动扫描
                _updateStatus("正在扫描目标设备...");
                var latestAddr = await FindOurDeviceAddress();
                if (latestAddr == null) throw new Exception("未找到 BLE-Anchor 设备，请确认手机广播已开启。");
                _deviceAddressStr = latestAddr;                    // 更新为最新地址
                ConfigManager.Default.DeviceAddress = latestAddr; // 保存到配置文件
                ConfigManager.Save();
                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(Convert.ToUInt64(latestAddr, 16));
                if (_device == null) throw new Exception("无法连接扫描到的设备。");
            }

            _updateDeviceName(_device.Name);
            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId)
                ?? throw new Exception("无法创建GATT会话");
            _session.MaintainConnection = true;
            _session.SessionStatusChanged += OnSessionClosed;
            StartRssiWatcher(Convert.ToUInt64(_deviceAddressStr, 16));
        }

        // 快速扫描找到 UUID 匹配的设备地址（返回最新的地址字符串）
        private async Task<string> FindOurDeviceAddress()
        {
            var tcs = new TaskCompletionSource<string>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.Received += (s, e) =>
            {
                if (e.Advertisement.ServiceUuids.Contains(OurServiceUuid))
                {
                    tcs.TrySetResult(e.BluetoothAddress.ToString("X12"));
                    watcher.Stop();
                }
            };
            watcher.Stopped += (s, e) => tcs.TrySetResult(null);
            watcher.Start();
            await Task.WhenAny(tcs.Task, Task.Delay(10000));
            watcher.Stop();
            return await tcs.Task;
        }

        private void StartRssiWatcher(ulong addr)
        {
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _watcher.Received += (s, e) =>
            {
                if (e.BluetoothAddress == addr)
                {
                    _currentRssi = e.RawSignalStrengthInDBm;
                    _updateRssi(_currentRssi);
                    if (_currentRssi < _rssiThreshold && _isMonitoring)
                    {
                        LockWorkStation();
                        _updateStatus("锁屏（RSSI过低）");
                    }
                }
            };
            _watcher.Start();
        }

        private void Cleanup()
        {
            _watcher?.Stop(); _watcher = null;
            if (_session != null) { _session.SessionStatusChanged -= OnSessionClosed; _session.MaintainConnection = false; _session.Dispose(); _session = null; }
            _device?.Dispose(); _device = null;
        }

        private async void OnReconnectTimer(object sender, ElapsedEventArgs e)
        {
            if (_isReconnecting || !_isMonitoring || string.IsNullOrEmpty(_deviceAddressStr)) return;
            try
            {
                if (_device?.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    _isReconnecting = true;
                    _updateStatus("重连中...");
                    Cleanup();
                    await ConnectOrAutoScan();
                    _updateStatus("已重连");
                }
            }
            catch { } finally { _isReconnecting = false; }
        }

        private void OnSessionClosed(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status == GattSessionStatus.Closed)
            {
                LockWorkStation();
                _updateStatus("锁屏（断开）");
            }
        }

        private void AppendRssi(int rssi)
        {
            try { File.AppendAllText(RssiLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {rssi} dBm\n"); } catch { }
        }

        public void Dispose()
        {
            StopMonitoring();
            _reconnectTimer?.Dispose();
        }
    }
}
