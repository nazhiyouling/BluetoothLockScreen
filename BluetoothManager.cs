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
        private BluetoothLEAdvertisementWatcher _rssiWatcher;
        private BluetoothLEAdvertisementWatcher _discoveryWatcher;
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

        // 新字段：设备固定识别码（从手机扫描响应中获取）
        private Guid _deviceGuid = Guid.Empty;

        public BluetoothManager(Action<string> status, Action<int> rssi, Action<string> name)
        {
            _updateStatus = status; _updateRssi = rssi; _updateDeviceName = name;
            _rssiThreshold = ConfigManager.Default.RssiThreshold;
            _reconnectTimer = new Timer(ReconnectIntervalMs) { AutoReset = true };
            _reconnectTimer.Elapsed += OnReconnectTimer;
            Directory.CreateDirectory(DataFolder);
        }

        // ---------- UI 扫描（保留，用于手动选择设备，不再作为核心依赖） ----------
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
                    dict[e.BluetoothAddress] = new BluetoothDeviceInfo { Address = e.BluetoothAddress, DisplayName = $"{name} ({e.BluetoothAddress:X12})", Rssi = e.RawSignalStrengthInDBm };
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

        public async Task<List<BluetoothDeviceInfo>> GetPairedDevicesAsync()
        {
            var devices = new List<BluetoothDeviceInfo>();
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var infos = await DeviceInformation.FindAllAsync(selector);
            foreach (var info in infos)
                if (info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out object prop))
                    devices.Add(new BluetoothDeviceInfo { Address = Convert.ToUInt64(prop.ToString().Replace(":", ""), 16), DisplayName = $"{info.Name} ({prop})" });
            return devices;
        }

        // ---------- 启动监控 ----------
        public async Task StartMonitoringAsync(string addressHex)
        {
            if (_isMonitoring) throw new InvalidOperationException("已在监控中");
            _deviceAddressStr = addressHex;
            await ConnectAndExtractGuid();
            StartReconnectTimer();
            _isMonitoring = true;
            _updateStatus("监控中...");
        }

        public void StopMonitoring()
        {
            _isMonitoring = false;
            _updateStatus("已停止");
            StopReconnectTimer();
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

        // ---------- 核心连接与 GUID 提取 ----------
        private async Task ConnectAndExtractGuid()
        {
            _discoveryWatcher?.Stop(); _discoveryWatcher = null;

            // 尝试用已知地址连接
            if (!string.IsNullOrEmpty(_deviceAddressStr))
            {
                try
                {
                    ulong addr = Convert.ToUInt64(_deviceAddressStr, 16);
                    _device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                    if (_device != null)
                    {
                        await SetupConnection(_device, addr);
                        await ExtractDeviceGuid(addr);
                        return;
                    }
                }
                catch { }
            }

            // 地址无效或连接失败，启动发现扫描（使用服务 UUID 或 GUID）
            _updateStatus("等待手机广播...");
            var foundAddr = await DiscoverDeviceByUuidOrGuid();
            _deviceAddressStr = foundAddr.ToString("X12");
            ConfigManager.Default.DeviceAddress = _deviceAddressStr;
            ConfigManager.Save();
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(foundAddr)
                ?? throw new Exception("无法连接设备");
            await SetupConnection(_device, foundAddr);
            await ExtractDeviceGuid(foundAddr);
        }

        // 从扫描响应中提取设备 GUID（如果尚未获取）
        private async Task ExtractDeviceGuid(ulong addr)
        {
            if (_deviceGuid != Guid.Empty) return;
            var tcs = new TaskCompletionSource<Guid?>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.Received += (s, e) =>
            {
                if (e.BluetoothAddress == addr)
                {
                    // 扫描响应中的服务 UUID 列表包含设备 GUID（除了已知的服务 UUID）
                    foreach (var uuid in e.Advertisement.ServiceUuids)
                    {
                        if (uuid != OurServiceUuid)
                        {
                            tcs.TrySetResult(uuid);
                            watcher.Stop();
                            return;
                        }
                    }
                }
            };
            watcher.Stopped += (s, e) => { if (!tcs.Task.IsCompleted) tcs.TrySetResult(null); };
            watcher.Start();
            var result = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            watcher.Stop();
            if (result is Task<Guid?> guidTask && guidTask.Result.HasValue)
            {
                _deviceGuid = guidTask.Result.Value;
                // 保存到配置文件
                ConfigManager.Default.DeviceGuid = _deviceGuid.ToString();
                ConfigManager.Save();
            }
        }

        // 扫描并查找匹配我们服务 UUID 或已知 GUID 的设备
        private async Task<ulong> DiscoverDeviceByUuidOrGuid()
        {
            var tcs = new TaskCompletionSource<ulong>();
            _discoveryWatcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _discoveryWatcher.Received += (s, e) =>
            {
                // 优先匹配设备 GUID（若已知）
                if (_deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid))
                {
                    tcs.TrySetResult(e.BluetoothAddress);
                }
                else if (e.Advertisement.ServiceUuids.Contains(OurServiceUuid))
                {
                    tcs.TrySetResult(e.BluetoothAddress);
                }
            };
            _discoveryWatcher.Stopped += (s, e) => { if (!tcs.Task.IsCompleted) _discoveryWatcher.Start(); };
            _discoveryWatcher.Start();
            ulong addr = await tcs.Task;
            _discoveryWatcher.Stop();
            _discoveryWatcher = null;
            return addr;
        }

        private async Task SetupConnection(BluetoothLEDevice dev, ulong addr)
        {
            _updateDeviceName(dev.Name);
            _session = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId)
                ?? throw new Exception("无法创建GATT会话");
            _session.MaintainConnection = true;
            _session.SessionStatusChanged += OnSessionClosed;
            StartRssiWatcher(addr);
        }

        private void StartRssiWatcher(ulong addr)
        {
            _rssiWatcher?.Stop();
            _rssiWatcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _rssiWatcher.Received += (s, e) =>
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
            _rssiWatcher.Start();
        }

        private void Cleanup()
        {
            _rssiWatcher?.Stop(); _rssiWatcher = null;
            _discoveryWatcher?.Stop(); _discoveryWatcher = null;
            if (_session != null) { _session.SessionStatusChanged -= OnSessionClosed; _session.MaintainConnection = false; _session.Dispose(); _session = null; }
            _device?.Dispose(); _device = null;
        }

        private void StartReconnectTimer() => _reconnectTimer.Start();
        private void StopReconnectTimer() => _reconnectTimer.Stop();

        private async void OnReconnectTimer(object sender, ElapsedEventArgs e)
        {
            if (_isReconnecting || !_isMonitoring) return;
            try
            {
                if (_device?.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    _isReconnecting = true;
                    _updateStatus("重连中...");
                    Cleanup();
                    await ConnectAndExtractGuid();
                    _updateStatus("已重连");
                }
            }
            catch { }
            finally { _isReconnecting = false; }
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
