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
        private bool _isAttemptingReconnect = false;   // 防止重入快速重连
        private const int ReconnectIntervalMs = 5000;

        private static readonly string DataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly string RssiLogPath = Path.Combine(DataFolder, "rssi_log.txt");
        private static readonly string AppLogPath = Path.Combine(DataFolder, "app_log.txt");
        private static readonly Guid OurServiceUuid = Guid.Parse("0000ABCD-0000-1000-8000-00805F9B34FB");

        private Guid _deviceGuid = Guid.Empty;

        public BluetoothManager(Action<string> status, Action<int> rssi, Action<string> name)
        {
            _updateStatus = status; _updateRssi = rssi; _updateDeviceName = name;
            _rssiThreshold = ConfigManager.Default.RssiThreshold;
            _reconnectTimer = new Timer(ReconnectIntervalMs) { AutoReset = true };
            _reconnectTimer.Elapsed += OnReconnectTimer;
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(AppLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 程序启动\n");
        }

        private void Log(string msg)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
            try { File.AppendAllText(AppLogPath, line + Environment.NewLine); } catch { }
            System.Diagnostics.Debug.WriteLine(line);
        }

        // ---------- UI 扫描 ----------
        public async Task<List<BluetoothDeviceInfo>> ScanDevicesAsync()
        {
            Log("开始UI扫描");
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
            Log($"UI扫描完成，找到 {dict.Count} 个设备");
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
            Log($"启动监控，初始地址: {_deviceAddressStr}");
            if (!string.IsNullOrEmpty(ConfigManager.Default.DeviceGuid))
                _deviceGuid = Guid.Parse(ConfigManager.Default.DeviceGuid);
            await ConnectAndExtractGuid();
            StartReconnectTimer();
            _isMonitoring = true;
            _updateStatus("监控中...");
        }

        public void StopMonitoring()
        {
            _isMonitoring = false;
            _updateStatus("已停止");
            Log("监控停止");
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
            catch (Exception ex) { Log($"测试连接异常: {ex.Message}"); return null; }
        }

        // ---------- 核心连接与 GUID 提取 ----------
        private async Task ConnectAndExtractGuid()
        {
            _discoveryWatcher?.Stop(); _discoveryWatcher = null;

            if (!string.IsNullOrEmpty(_deviceAddressStr))
            {
                try
                {
                    ulong addr = Convert.ToUInt64(_deviceAddressStr, 16);
                    Log($"尝试连接地址: {_deviceAddressStr}");
                    _device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                    if (_device != null)
                    {
                        Log("连接成功，建立GATT会话");
                        await SetupConnection(_device, addr);
                        await ExtractDeviceGuid(addr);
                        return;
                    }
                    Log("已知地址连接失败，启动扫描重连...");
                }
                catch (Exception ex) { Log($"连接异常: {ex.Message}"); }
            }

            var foundAddr = await DiscoverDeviceByUuidOrGuid();
            Log($"扫描发现设备，新地址: {foundAddr:X12}");
            _deviceAddressStr = foundAddr.ToString("X12");
            ConfigManager.Default.DeviceAddress = _deviceAddressStr;
            ConfigManager.Save();
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(foundAddr)
                ?? throw new Exception("无法连接设备");
            await SetupConnection(_device, foundAddr);
            await ExtractDeviceGuid(foundAddr);
        }

        private async Task ExtractDeviceGuid(ulong addr)
        {
            if (_deviceGuid != Guid.Empty) return;
            Log("尝试提取设备GUID...");
            var tcs = new TaskCompletionSource<Guid?>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.Received += (s, e) =>
            {
                if (e.BluetoothAddress == addr)
                {
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
                Log($"提取到设备GUID: {_deviceGuid}");
                ConfigManager.Default.DeviceGuid = _deviceGuid.ToString();
                ConfigManager.Save();
            }
            else Log("未能提取设备GUID");
        }

        private async Task<ulong> DiscoverDeviceByUuidOrGuid()
        {
            var tcs = new TaskCompletionSource<ulong>();
            _discoveryWatcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _discoveryWatcher.Received += (s, e) =>
            {
                if (_deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid))
                {
                    Log($"GUID匹配成功，地址: {e.BluetoothAddress:X12}");
                    tcs.TrySetResult(e.BluetoothAddress);
                }
                else if (e.Advertisement.ServiceUuids.Contains(OurServiceUuid))
                {
                    Log($"服务UUID匹配，地址: {e.BluetoothAddress:X12}");
                    tcs.TrySetResult(e.BluetoothAddress);
                }
            };
            _discoveryWatcher.Stopped += (s, e) =>
            {
                if (!tcs.Task.IsCompleted)
                {
                    Log("扫描意外停止，自动重启...");
                    _discoveryWatcher.Start();
                }
            };
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

        // ---------- RSSI 监听 (核心修改) ----------
        private void StartRssiWatcher(ulong addr)
        {
            _rssiWatcher?.Stop();
            _rssiWatcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _rssiWatcher.Received += async (s, e) =>
            {
                if (e.BluetoothAddress == addr)
                {
                    _currentRssi = e.RawSignalStrengthInDBm;
                    _updateRssi(_currentRssi);

                    // 当 RSSI 降到阈值以下，并且当前没有正在重连时
                    if (_currentRssi < _rssiThreshold && _isMonitoring && !_isAttemptingReconnect)
                    {
                        Log($"RSSI={_currentRssi} 低于阈值，开始快速重连尝试...");
                        _isAttemptingReconnect = true;
                        _updateStatus("信号丢失，尝试重连...");

                        // 尝试快速扫描并重连
                        bool reconnected = await TryQuickReconnect();
                        if (!reconnected)
                        {
                            Log("快速重连失败，执行锁屏");
                            LockWorkStation();
                            _updateStatus("锁屏（信号丢失）");
                            // 锁屏后，定时器会继续尝试自动重连（后台重连）
                        }
                        else
                        {
                            Log("快速重连成功，取消锁屏");
                            _updateStatus("监控中...");
                        }
                        _isAttemptingReconnect = false;
                    }
                }
            };
            _rssiWatcher.Start();
        }

        /// <summary>
        /// 快速扫描并重连（扫描最多 3 秒），返回是否成功
        /// </summary>
        private async Task<bool> TryQuickReconnect()
        {
            try
            {
                // 清理当前连接，准备重新连接
                Cleanup();
                // 快速扫描：使用独立的发现扫描器，超时 3 秒
                var tcs = new TaskCompletionSource<ulong>();
                var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
                watcher.Received += (s, e) =>
                {
                    if (_deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid))
                    {
                        Log($"快速扫描 GUID 匹配: {e.BluetoothAddress:X12}");
                        tcs.TrySetResult(e.BluetoothAddress);
                    }
                    else if (e.Advertisement.ServiceUuids.Contains(OurServiceUuid))
                    {
                        Log($"快速扫描 UUID 匹配: {e.BluetoothAddress:X12}");
                        tcs.TrySetResult(e.BluetoothAddress);
                    }
                };
                watcher.Start();
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
                watcher.Stop();
                if (completedTask == tcs.Task && tcs.Task.IsCompleted)
                {
                    ulong newAddr = tcs.Task.Result;
                    Log($"快速重连获取新地址: {newAddr:X12}");
                    _deviceAddressStr = newAddr.ToString("X12");
                    ConfigManager.Default.DeviceAddress = _deviceAddressStr;
                    ConfigManager.Save();
                    _device = await BluetoothLEDevice.FromBluetoothAddressAsync(newAddr);
                    if (_device != null)
                    {
                        await SetupConnection(_device, newAddr);
                        await ExtractDeviceGuid(newAddr);
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Log($"快速重连异常: {ex.Message}");
                return false;
            }
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
                // 如果当前处于快速重连中，跳过定时器重连
                if (_isAttemptingReconnect) return;

                bool needReconnect = _device == null || _device.ConnectionStatus != BluetoothConnectionStatus.Connected;
                if (!needReconnect) return;

                _isReconnecting = true;
                Log("定时器检测到连接丢失，开始后台重连...");
                _updateStatus("重连中...");
                Cleanup();

                while (_isMonitoring)
                {
                    try
                    {
                        await ConnectAndExtractGuid();
                        Log("后台重连成功！");
                        _updateStatus("已重连");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"后台重连失败: {ex.Message}，5秒后重试...");
                        await Task.Delay(5000);
                    }
                }
            }
            finally { _isReconnecting = false; }
        }

        private void OnSessionClosed(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status == GattSessionStatus.Closed)
            {
                Log("GATT会话关闭，触发锁屏");
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
