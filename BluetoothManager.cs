using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Win32;
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
        private bool _isAttemptingReconnect = false;
        private const int ReconnectIntervalMs = 5000;

        private static readonly string DataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly string RssiLogPath = Path.Combine(DataFolder, "rssi_log.txt");
        private static readonly string AppLogPath = Path.Combine(DataFolder, "app_log.txt");
        private static readonly Guid OurServiceUuid = Guid.Parse("0000ABCD-0000-1000-8000-00805F9B34FB");

        private Guid _deviceGuid = Guid.Empty;
        private bool _isScreenLocked = false;

        public BluetoothManager(Action<string> status, Action<int> rssi, Action<string> name)
        {
            _updateStatus = status; _updateRssi = rssi; _updateDeviceName = name;
            _rssiThreshold = ConfigManager.Default.RssiThreshold;
            _reconnectTimer = new Timer(ReconnectIntervalMs) { AutoReset = true };
            _reconnectTimer.Elapsed += OnReconnectTimer;
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(AppLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 程序启动\n");
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        private void Log(string msg)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
            try { File.AppendAllText(AppLogPath, line + Environment.NewLine); } catch { }
            System.Diagnostics.Debug.WriteLine(line);
        }

        // ---------- 扫描（仅返回 BLE-Anchor 设备） ----------
        public async Task<List<BluetoothDeviceInfo>> ScanDevicesAsync()
        {
            Log("开始扫描 BLE-Anchor 设备...");
            var dict = new Dictionary<ulong, BluetoothDeviceInfo>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            var tcs = new TaskCompletionSource<bool>();
            watcher.Received += (s, e) =>
            {
                if (e.Advertisement.ServiceUuids.Contains(OurServiceUuid))
                {
                    string name = "BLE-Anchor";
                    if (!dict.ContainsKey(e.BluetoothAddress))
                        dict[e.BluetoothAddress] = new BluetoothDeviceInfo
                        {
                            Address = e.BluetoothAddress,
                            DisplayName = $"{name} ({e.BluetoothAddress:X12})",
                            Rssi = e.RawSignalStrengthInDBm
                        };
                    else
                    {
                        dict[e.BluetoothAddress].Rssi = e.RawSignalStrengthInDBm;
                    }
                }
            };
            watcher.Stopped += (s, e) => tcs.TrySetResult(true);
            watcher.Start();
            await Task.Delay(5000);
            watcher.Stop();
            await tcs.Task;
            var devices = dict.Values.OrderByDescending(d => d.Rssi).ToList();
            Log($"扫描完成，找到 {devices.Count} 个 BLE-Anchor 设备");
            return devices;
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

        // ---------- 自动连接（核心） ----------
        /// <summary>
        /// 自动扫描并连接第一个 BLE-Anchor 设备，如果已有 GUID 则优先匹配 GUID
        /// </summary>
        public async Task AutoConnectAndMonitorAsync()
        {
            if (_isMonitoring) return;
            Log("启动自动连接...");
            if (!string.IsNullOrEmpty(ConfigManager.Default.DeviceGuid))
                _deviceGuid = Guid.Parse(ConfigManager.Default.DeviceGuid);

            await ConnectToFirstAnchor();
            StartReconnectTimer();
            _isMonitoring = true;
            _updateStatus("监控中...");
        }

        /// <summary>
        /// 使用传入地址或自动扫描连接
        /// </summary>
        public async Task StartMonitoringAsync(string addressHex)
        {
            if (_isMonitoring) throw new InvalidOperationException("已在监控中");
            _deviceAddressStr = addressHex;
            Log($"手动启动监控，地址: {_deviceAddressStr}");
            if (!string.IsNullOrEmpty(ConfigManager.Default.DeviceGuid))
                _deviceGuid = Guid.Parse(ConfigManager.Default.DeviceGuid);

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
                        StartReconnectTimer();
                        _isMonitoring = true;
                        _updateStatus("监控中...");
                        return;
                    }
                }
                catch { }
            }
            // 地址无效或为空，自动扫描
            Log("已知地址无效，自动扫描连接...");
            await ConnectToFirstAnchor();
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

        // ---------- 自动扫描并连接第一个 BLE-Anchor ----------
        private async Task ConnectToFirstAnchor()
        {
            Cleanup();
            Log("开始自动扫描 BLE-Anchor...");
            var tcs = new TaskCompletionSource<ulong>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.Received += (s, e) =>
            {
                // 优先 GUID 匹配，其次 UUID 匹配
                if (_deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid))
                {
                    tcs.TrySetResult(e.BluetoothAddress);
                }
                else if (e.Advertisement.ServiceUuids.Contains(OurServiceUuid))
                {
                    tcs.TrySetResult(e.BluetoothAddress);
                }
            };
            watcher.Stopped += (s, e) => { if (!tcs.Task.IsCompleted) watcher.Start(); };
            watcher.Start();
            ulong addr = await tcs.Task; // 无限等待直到发现设备
            watcher.Stop();

            Log($"自动发现 BLE-Anchor，地址: {addr:X12}");
            _deviceAddressStr = addr.ToString("X12");
            ConfigManager.Default.DeviceAddress = _deviceAddressStr;
            ConfigManager.Save();

            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr)
                ?? throw new Exception("无法连接自动发现的设备");
            await SetupConnection(_device, addr);
            await ExtractDeviceGuid(addr);
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

        private async Task SetupConnection(BluetoothLEDevice dev, ulong addr)
        {
            _updateDeviceName(dev.Name);
            _session = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId)
                ?? throw new Exception("无法创建GATT会话");
            _session.MaintainConnection = true;
            _session.SessionStatusChanged += OnSessionClosed;
            StartRssiWatcher(addr);
        }

        // ---------- RSSI 监听（快速重连） ----------
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

                    if (_currentRssi < _rssiThreshold && _isMonitoring && !_isAttemptingReconnect && !_isScreenLocked)
                    {
                        Log($"RSSI={_currentRssi} 低于阈值，开始快速重连...");
                        _isAttemptingReconnect = true;
                        _updateStatus("信号丢失，尝试重连...");
                        bool reconnected = await TryQuickReconnect();
                        if (!reconnected)
                        {
                            Log("快速重连失败，执行锁屏");
                            LockWorkStation();
                            _updateStatus("锁屏（信号丢失）");
                        }
                        else
                        {
                            Log("快速重连成功，继续监控");
                            _updateStatus("监控中...");
                        }
                        _isAttemptingReconnect = false;
                    }
                }
            };
            _rssiWatcher.Start();
        }

        private async Task<bool> TryQuickReconnect()
        {
            try
            {
                Cleanup();
                var tcs = new TaskCompletionSource<ulong>();
                var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
                watcher.Received += (s, e) =>
                {
                    if (_deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid))
                        tcs.TrySetResult(e.BluetoothAddress);
                    else if (e.Advertisement.ServiceUuids.Contains(OurServiceUuid))
                        tcs.TrySetResult(e.BluetoothAddress);
                };
                watcher.Start();
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000));
                watcher.Stop();
                if (completed != tcs.Task) return false;

                ulong newAddr = tcs.Task.Result;
                Log($"快速重连发现地址: {newAddr:X12}");
                var newDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(newAddr);
                if (newDevice == null) return false;
                var newSession = await GattSession.FromDeviceIdAsync(newDevice.BluetoothDeviceId);
                if (newSession == null) { newDevice.Dispose(); return false; }

                _deviceAddressStr = newAddr.ToString("X12");
                ConfigManager.Default.DeviceAddress = _deviceAddressStr;
                ConfigManager.Save();
                _device = newDevice;
                _session = newSession;
                _session.MaintainConnection = true;
                _session.SessionStatusChanged += OnSessionClosed;
                _updateDeviceName(newDevice.Name);
                StartRssiWatcher(newAddr);

                await Task.Delay(2000);
                if (_device?.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    Log("快速重连成功且稳定");
                    return true;
                }
                Cleanup();
                return false;
            }
            catch (Exception ex) { Log($"快速重连异常: {ex.Message}"); return false; }
        }

        private void Cleanup()
        {
            _rssiWatcher?.Stop(); _rssiWatcher = null;
            _discoveryWatcher?.Stop(); _discoveryWatcher = null;
            if (_session != null) { _session.SessionStatusChanged -= OnSessionClosed; _session.MaintainConnection = false; _session.Dispose(); _session = null; }
            _device?.Dispose(); _device = null;
        }

        // ---------- 系统锁定/解锁事件 ----------
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                Log("系统锁定，暂停后台重连");
                _isScreenLocked = true;
                StopReconnectTimer();
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                Log("系统解锁，启动自动重连...");
                _isScreenLocked = false;
                if (_isMonitoring)
                {
                    StartReconnectTimer();
                    _ = Task.Run(async () =>
                    {
                        try { await ScanAndReconnectAfterUnlock(); }
                        catch { }
                    });
                }
            }
        }

        private async Task ScanAndReconnectAfterUnlock()
        {
            if (_isReconnecting || !_isMonitoring) return;
            try
            {
                _isReconnecting = true;
                Log("解锁后扫描重连...");
                _updateStatus("重连中...");
                Cleanup();
                await ConnectToFirstAnchor();
                Log("解锁后重连成功");
                _updateStatus("已重连");
            }
            catch (Exception ex)
            {
                Log($"解锁后重连失败: {ex.Message}");
                _updateStatus("重连中...");
            }
            finally { _isReconnecting = false; }
        }

        private void StartReconnectTimer() { if (!_isScreenLocked) _reconnectTimer.Start(); }
        private void StopReconnectTimer() { _reconnectTimer.Stop(); }

        private async void OnReconnectTimer(object sender, ElapsedEventArgs e)
        {
            if (_isReconnecting || !_isMonitoring || _isAttemptingReconnect || _isScreenLocked) return;
            try
            {
                if (_device?.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    _isReconnecting = true;
                    Log("定时器检测到连接丢失，后台重连...");
                    _updateStatus("重连中...");
                    Cleanup();
                    while (_isMonitoring && !_isScreenLocked)
                    {
                        try
                        {
                            await ConnectToFirstAnchor();
                            await Task.Delay(2000);
                            if (_device?.ConnectionStatus == BluetoothConnectionStatus.Connected)
                            {
                                Log("后台重连稳定");
                                _updateStatus("已重连");
                                break;
                            }
                            Cleanup();
                        }
                        catch { }
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
                Log("GATT会话关闭，锁屏");
                if (!_isScreenLocked)
                {
                    LockWorkStation();
                    _updateStatus("锁屏（断开）");
                }
            }
        }

        private void AppendRssi(int rssi)
        {
            try { File.AppendAllText(RssiLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {rssi} dBm\n"); } catch { }
        }

        public void Dispose()
        {
            StopMonitoring();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _reconnectTimer?.Dispose();
        }
    }
}
