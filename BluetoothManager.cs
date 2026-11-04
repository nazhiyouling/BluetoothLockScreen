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
        private int _rssiThreshold;
        private int _currentRssi = int.MinValue;

        private bool _isMonitoring = false;
        private string _deviceAddressStr;
        private Timer _reconnectTimer;
        private bool _isReconnecting = false;
        private bool _isQuickScanning = false;

        // 可配置时间参数
        private int _lowRssiCountThreshold;
        private TimeSpan _quickScanCooldown;
        private TimeSpan _quickScanTimeout;
        private TimeSpan _reconnectInterval;
        private int _disconnectCountThreshold;
        private TimeSpan _packetTimeout;
        private TimeSpan _gattGrace;
        private TimeSpan _lockCooldown;

        private static readonly string DataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly string AppLogPath = Path.Combine(DataFolder, "app_log.txt");
        private static readonly Guid OurServiceUuid = Guid.Parse("0000ABCD-0000-1000-8000-00805F9B34FB");

        private Guid _deviceGuid = Guid.Empty;
        private bool _isScreenLocked = false;
        private DateTime _lastLockTime = DateTime.MinValue;
        private DateTime _lastReconnectTime = DateTime.MinValue;
        private DateTime _lastQuickScanTime = DateTime.MinValue;
        private int _lowRssiCount = 0;
        private int _disconnectCount = 0;
        private DateTime _lastPacketTime = DateTime.MinValue;

        private string _expectedDeviceName = "";

        public BluetoothManager(Action<string> status, Action<int> rssi, Action<string> name)
        {
            _updateStatus = status;
            _updateRssi = rssi;
            _updateDeviceName = name;

            LoadSettingsFromConfig();

            _reconnectTimer = new Timer(_reconnectInterval.TotalMilliseconds) { AutoReset = true };
            _reconnectTimer.Elapsed += OnReconnectTimer;

            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(AppLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 程序启动\n");
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        private void LoadSettingsFromConfig()
        {
            var cfg = ConfigManager.Default;

            _rssiThreshold = cfg.RssiThreshold <= 0 ? cfg.RssiThreshold : -83;
            _expectedDeviceName = cfg.ExpectedDeviceName?.Trim() ?? "";

            _lowRssiCountThreshold = Clamp(cfg.LowRssiCount, 1, 10, 3);
            _quickScanCooldown = TimeSpan.FromSeconds(Clamp(cfg.QuickScanCooldownSec, 1, 300, 30));
            _quickScanTimeout = TimeSpan.FromSeconds(Clamp(cfg.QuickScanTimeoutSec, 1, 300, 2));
            _reconnectInterval = TimeSpan.FromSeconds(Clamp(cfg.ReconnectIntervalSec, 1, 300, 2));
            _disconnectCountThreshold = Clamp(cfg.DisconnectCount, 1, 10, 2);
            _packetTimeout = TimeSpan.FromSeconds(Clamp(cfg.PacketTimeoutSec, 1, 300, 5));
            _gattGrace = TimeSpan.FromSeconds(Clamp(cfg.GattGraceSec, 1, 300, 5));
            _lockCooldown = TimeSpan.FromSeconds(Clamp(cfg.LockCooldownSec, 1, 300, 5));

            if (!string.IsNullOrEmpty(cfg.DeviceGuid))
            {
                try { _deviceGuid = Guid.Parse(cfg.DeviceGuid); }
                catch { _deviceGuid = Guid.Empty; }
            }
        }

        private static int Clamp(int value, int min, int max, int defaultValue)
        {
            if (value < min || value > max) return defaultValue;
            return value;
        }

        /// <summary>
        /// 设置界面保存后调用，热更新所有参数
        /// </summary>
        public void UpdateSettings(
            int rssiThreshold,
            string expectedName,
            int lowRssiCount,
            int quickScanCooldownSec,
            int quickScanTimeoutSec,
            int reconnectIntervalSec,
            int disconnectCount,
            int packetTimeoutSec,
            int gattGraceSec,
            int lockCooldownSec)
        {
            _rssiThreshold = rssiThreshold;
            _expectedDeviceName = expectedName?.Trim() ?? "";
            _lowRssiCountThreshold = Clamp(lowRssiCount, 1, 10, 3);
            _quickScanCooldown = TimeSpan.FromSeconds(Clamp(quickScanCooldownSec, 1, 300, 30));
            _quickScanTimeout = TimeSpan.FromSeconds(Clamp(quickScanTimeoutSec, 1, 300, 2));
            _reconnectInterval = TimeSpan.FromSeconds(Clamp(reconnectIntervalSec, 1, 300, 2));
            _disconnectCountThreshold = Clamp(disconnectCount, 1, 10, 2);
            _packetTimeout = TimeSpan.FromSeconds(Clamp(packetTimeoutSec, 1, 300, 5));
            _gattGrace = TimeSpan.FromSeconds(Clamp(gattGraceSec, 1, 300, 5));
            _lockCooldown = TimeSpan.FromSeconds(Clamp(lockCooldownSec, 1, 300, 5));

            if (_reconnectTimer != null)
                _reconnectTimer.Interval = _reconnectInterval.TotalMilliseconds;

            Log($"设置已更新：RSSI={_rssiThreshold}, 名称='{_expectedDeviceName}', " +
                $"低RSSI次数={_lowRssiCountThreshold}, 扫描冷却={_quickScanCooldown.TotalSeconds}s, " +
                $"扫描超时={_quickScanTimeout.TotalSeconds}s, 重连间隔={_reconnectInterval.TotalSeconds}s, " +
                $"断开次数={_disconnectCountThreshold}, 看门狗={_packetTimeout.TotalSeconds}s, " +
                $"GATT宽限={_gattGrace.TotalSeconds}s, 锁屏冷却={_lockCooldown.TotalSeconds}s");
        }

        public void UpdateExpectedDeviceName(string name)
        {
            _expectedDeviceName = name?.Trim() ?? "";
            Log($"期望设备名称已更新为: '{(string.IsNullOrEmpty(_expectedDeviceName) ? "(空)" : _expectedDeviceName)}'");
        }

        public bool HasExpectedDeviceName => !string.IsNullOrEmpty(_expectedDeviceName);

        private void Log(string msg)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
            try { File.AppendAllText(AppLogPath, line + Environment.NewLine); } catch { }
            System.Diagnostics.Debug.WriteLine(line);
        }

        // ---------- UI 扫描 ----------
        public async Task<List<BluetoothDeviceInfo>> ScanDevicesAsync()
        {
            Log("UI扫描：暂停后台监听");
            _rssiWatcher?.Stop();

            var dict = new Dictionary<ulong, BluetoothDeviceInfo>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            var tcs = new TaskCompletionSource<bool>();
            watcher.Received += (s, e) =>
            {
                if (!IsOurService(e.Advertisement)) return;

                string scanName = e.Advertisement.LocalName ?? "";
                string displayName = string.IsNullOrEmpty(scanName)
                    ? $"未命名 ({e.BluetoothAddress:X12})"
                    : $"{scanName} ({e.BluetoothAddress:X12})";

                if (!dict.ContainsKey(e.BluetoothAddress))
                {
                    dict[e.BluetoothAddress] = new BluetoothDeviceInfo
                    {
                        Address = e.BluetoothAddress,
                        DisplayName = displayName,
                        Rssi = e.RawSignalStrengthInDBm
                    };
                }
                else
                {
                    dict[e.BluetoothAddress].Rssi = e.RawSignalStrengthInDBm;
                    if (!string.IsNullOrEmpty(scanName))
                        dict[e.BluetoothAddress].DisplayName = displayName;
                }
            };
            watcher.Stopped += (s, e) => tcs.TrySetResult(true);
            watcher.Start();
            await Task.Delay(5000);
            watcher.Stop();
            await tcs.Task;

            var devices = dict.Values.OrderByDescending(d => d.Rssi).ToList();
            Log($"UI扫描完成，找到 {devices.Count} 个候选设备（期望名称: '{(string.IsNullOrEmpty(_expectedDeviceName) ? "(空)" : _expectedDeviceName)}'）");

            if (_isMonitoring && !string.IsNullOrEmpty(_deviceAddressStr))
            {
                Log("恢复RSSI监听");
                StartRssiWatcher(Convert.ToUInt64(_deviceAddressStr, 16));
            }
            return devices;
        }

        public async Task<List<BluetoothDeviceInfo>> GetPairedDevicesAsync() => new List<BluetoothDeviceInfo>();

        private bool IsOurService(BluetoothLEAdvertisement adv)
        {
            if (_deviceGuid != Guid.Empty && adv.ServiceUuids.Contains(_deviceGuid))
                return true;
            return adv.ServiceUuids.Contains(OurServiceUuid);
        }

        // ---------- 自动连接 ----------
        public async Task<bool> AutoConnectAndMonitorAsync()
        {
            if (_isMonitoring) return true;

            if (string.IsNullOrEmpty(_expectedDeviceName) && _deviceGuid == Guid.Empty)
            {
                Log("期望名称为空且无GUID，跳过自动连接");
                _updateStatus("未配置设备，请先在设置中填写名称");
                return false;
            }

            await ConnectToExpectedAnchor();
            _isMonitoring = true;
            _reconnectTimer.Start();
            _updateStatus("监控中...");
            return true;
        }

        public async Task<bool> StartMonitoringAsync(string addressHex)
        {
            if (_isMonitoring) return true;

            if (string.IsNullOrEmpty(_expectedDeviceName) && _deviceGuid == Guid.Empty)
            {
                Log("手动启动失败：未配置设备名称且无GUID");
                return false;
            }

            await ConnectToExpectedAnchor();
            _isMonitoring = true;
            _reconnectTimer.Start();
            _updateStatus("监控中...");
            return true;
        }

        public void StopMonitoring()
        {
            _isMonitoring = false;
            _updateStatus("已停止");
            Log("监控停止");
            _reconnectTimer.Stop();
            Cleanup();
        }

        public void UpdateThreshold(int t) => _rssiThreshold = t;

        public async Task<int?> TestConnectionAsync(string addressHex) => null;

        // ---------- 扫描候选 ----------
        private async Task<List<(ulong Address, string Name, bool HasGuid)>> DiscoverCandidatesAsync(int timeoutMs)
        {
            var candidates = new Dictionary<ulong, (string Name, bool HasGuid)>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.Received += (s, e) =>
            {
                bool hasGuid = _deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid);
                bool hasUuid = e.Advertisement.ServiceUuids.Contains(OurServiceUuid);
                if (!hasGuid && !hasUuid) return;

                string name = e.Advertisement.LocalName ?? "";
                if (!candidates.ContainsKey(e.BluetoothAddress))
                    candidates[e.BluetoothAddress] = (name, hasGuid);
                else
                {
                    var old = candidates[e.BluetoothAddress];
                    candidates[e.BluetoothAddress] = (string.IsNullOrEmpty(old.Name) ? name : old.Name, old.HasGuid || hasGuid);
                }
            };
            watcher.Start();
            await Task.Delay(timeoutMs);
            watcher.Stop();
            return candidates.Select(kv => (kv.Key, kv.Value.Name, kv.Value.HasGuid)).ToList();
        }

        // ---------- 连接候选 ----------
        private async Task<bool> TryConnectAsync(ulong addr, string scannedName, bool hasGuid)
        {
            if (_deviceGuid != Guid.Empty)
            {
                if (!hasGuid)
                {
                    Log($"地址 {addr:X12} 未携带已保存的 GUID，跳过");
                    return false;
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(scannedName) &&
                    !string.IsNullOrEmpty(_expectedDeviceName) &&
                    !string.Equals(scannedName, _expectedDeviceName, StringComparison.Ordinal))
                {
                    Log($"扫描名称不匹配: '{scannedName}'，跳过 {addr:X12}");
                    return false;
                }
            }

            BluetoothLEDevice dev = null;
            GattSession sess = null;
            try
            {
                Log($"尝试连接: {addr:X12}，扫描名称: '{(string.IsNullOrEmpty(scannedName) ? "无" : scannedName)}'，hasGuid: {hasGuid}");
                dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                if (dev == null)
                {
                    Log("无法创建设备对象");
                    return false;
                }

                string actualName = scannedName;
                if (string.IsNullOrEmpty(actualName))
                {
                    actualName = await ReadGattDeviceNameAsync(dev);
                    if (string.IsNullOrEmpty(actualName))
                        actualName = dev.Name ?? "";
                    Log($"GATT/系统读取名称: '{actualName}'");
                }

                if (_deviceGuid == Guid.Empty &&
                    !string.IsNullOrEmpty(_expectedDeviceName) &&
                    !string.Equals(actualName, _expectedDeviceName, StringComparison.Ordinal))
                {
                    Log($"名称不匹配（期望 '{_expectedDeviceName}'，实际 '{actualName}'），拒绝");
                    dev.Dispose();
                    return false;
                }

                sess = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId);
                if (sess == null)
                {
                    Log("无法创建GATT会话");
                    dev.Dispose();
                    return false;
                }

                Cleanup();
                _device = dev;
                _session = sess;
                _session.MaintainConnection = true;
                _session.SessionStatusChanged += OnSessionClosed;
                _deviceAddressStr = addr.ToString("X12");
                ConfigManager.Default.DeviceAddress = _deviceAddressStr;
                ConfigManager.Save();
                _updateDeviceName(string.IsNullOrEmpty(actualName) ? "未知设备" : actualName);
                StartRssiWatcher(addr);
                _lastReconnectTime = DateTime.Now;
                _lastPacketTime = DateTime.Now;

                if (_deviceGuid == Guid.Empty)
                    await ExtractDeviceGuid(addr);

                Log($"连接成功: {(string.IsNullOrEmpty(actualName) ? addr.ToString("X12") : actualName)} ({addr:X12})");
                return true;
            }
            catch (Exception ex)
            {
                Log($"连接异常: {ex.Message}");
                try { sess?.Dispose(); } catch { }
                try { dev?.Dispose(); } catch { }
                return false;
            }
        }

        private async Task<string> ReadGattDeviceNameAsync(BluetoothLEDevice device)
        {
            try
            {
                var servicesResult = await device.GetGattServicesAsync();
                if (servicesResult.Status != GattCommunicationStatus.Success) return null;

                var genericAccess = servicesResult.Services
                    .FirstOrDefault(s => s.Uuid == Guid.Parse("00001800-0000-1000-8000-00805F9B34FB"));
                if (genericAccess == null) return null;

                var charsResult = await genericAccess.GetCharacteristicsAsync();
                if (charsResult.Status != GattCommunicationStatus.Success) return null;

                var nameChar = charsResult.Characteristics
                    .FirstOrDefault(c => c.Uuid == Guid.Parse("00002A00-0000-1000-8000-00805F9B34FB"));
                if (nameChar == null) return null;

                var readResult = await nameChar.ReadValueAsync();
                if (readResult.Status != GattCommunicationStatus.Success) return null;

                var reader = Windows.Storage.Streams.DataReader.FromBuffer(readResult.Value);
                return reader.ReadString(readResult.Value.Length);
            }
            catch { return null; }
        }

        private async Task ConnectToExpectedAnchor()
        {
            Cleanup();
            _updateStatus(string.IsNullOrEmpty(_expectedDeviceName)
                ? "正在查找已保存的 GUID 设备..."
                : $"正在查找 {_expectedDeviceName}...");

            while (true)
            {
                var candidates = await DiscoverCandidatesAsync(3000);
                if (candidates.Count == 0)
                {
                    Log("本轮未发现候选，继续等待...");
                    await Task.Delay(1000);
                    continue;
                }

                foreach (var c in candidates)
                {
                    if (await TryConnectAsync(c.Address, c.Name, c.HasGuid))
                        return;
                }

                Log("本轮候选均未通过，继续扫描...");
                await Task.Delay(500);
            }
        }

        private async Task ExtractDeviceGuid(ulong addr)
        {
            Log("提取设备GUID...");
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
            if (result is Task<Guid?> gt && gt.Result.HasValue)
            {
                _deviceGuid = gt.Result.Value;
                ConfigManager.Default.DeviceGuid = _deviceGuid.ToString();
                ConfigManager.Save();
                Log($"已保存设备GUID: {_deviceGuid}");
            }
        }

        // ---------- RSSI 监听 ----------
        private void StartRssiWatcher(ulong addr)
        {
            _rssiWatcher?.Stop();
            _rssiWatcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _rssiWatcher.Received += async (s, e) =>
            {
                if (e.BluetoothAddress == addr)
                {
                    _lastPacketTime = DateTime.Now;
                    _currentRssi = e.RawSignalStrengthInDBm;
                    _updateRssi(_currentRssi);

                    if (_currentRssi < _rssiThreshold) _lowRssiCount++;
                    else _lowRssiCount = 0;

                    if (_lowRssiCount >= _lowRssiCountThreshold &&
                        _isMonitoring &&
                        !_isScreenLocked &&
                        !_isQuickScanning &&
                        (DateTime.Now - _lastQuickScanTime) >= _quickScanCooldown)
                    {
                        _lowRssiCount = 0;
                        _isQuickScanning = true;
                        _lastQuickScanTime = DateTime.Now;
                        Log($"连续{_lowRssiCountThreshold}次低RSSI，扫描确认设备...");
                        _updateStatus("信号弱，确认设备...");

                        bool found = await QuickScanForExpectedDevice();
                        if (!found)
                        {
                            Log("扫描未发现目标，执行锁屏");
                            TryLockWorkStation();
                            _updateStatus("锁屏（设备离开）");
                        }
                        else
                        {
                            Log("已重新找到目标，继续监控");
                            _updateStatus("监控中...");
                        }
                        _isQuickScanning = false;
                    }
                }
            };
            _rssiWatcher.Start();
        }

        private async Task<bool> QuickScanForExpectedDevice()
        {
            Cleanup();
            var candidates = await DiscoverCandidatesAsync((int)_quickScanTimeout.TotalMilliseconds);
            if (candidates.Count == 0)
            {
                Log("快速扫描无候选");
                return false;
            }

            foreach (var c in candidates)
            {
                if (await TryConnectAsync(c.Address, c.Name, c.HasGuid))
                    return true;
            }
            Log("快速扫描候选均未通过");
            return false;
        }

        private void Cleanup()
        {
            _rssiWatcher?.Stop(); _rssiWatcher = null;
            if (_session != null)
            {
                _session.SessionStatusChanged -= OnSessionClosed;
                _session.MaintainConnection = false;
                _session.Dispose();
                _session = null;
            }
            _device?.Dispose(); _device = null;
        }

        // ---------- 系统锁定/解锁 ----------
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                Log("系统锁定，暂停重连");
                _isScreenLocked = true;
                _reconnectTimer.Stop();
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                Log("系统解锁，启动立即重连...");
                _isScreenLocked = false;
                if (_isMonitoring)
                    _ = Task.Run(async () => await UnlockReconnect());
            }
        }

        private async Task UnlockReconnect()
        {
            if (_isReconnecting) return;
            _isReconnecting = true;
            _updateStatus("重连中...");
            try
            {
                await ConnectToExpectedAnchor();
                Log("解锁重连成功");
                _updateStatus("已重连");
                _reconnectTimer.Start();
                _disconnectCount = 0;
                _lastPacketTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                Log($"解锁重连失败: {ex.Message}");
                _updateStatus("重连中...");
                _reconnectTimer.Start();
            }
            finally { _isReconnecting = false; }
        }

        // ---------- 定时器 ----------
        private async void OnReconnectTimer(object sender, ElapsedEventArgs e)
        {
            if (_isReconnecting || !_isMonitoring || _isScreenLocked || _isQuickScanning) return;

            bool connected = _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
            if (!connected)
            {
                _disconnectCount++;
                Log($"定时器检测到断开 (连续 {_disconnectCount} 次)");
                if (_disconnectCount >= _disconnectCountThreshold)
                {
                    _disconnectCount = 0;
                    _isReconnecting = true;
                    Log($"连续{_disconnectCountThreshold}次断开，启动重连...");
                    _updateStatus("重连中...");
                    try
                    {
                        await ConnectToExpectedAnchor();
                        Log("定时器重连成功");
                        _updateStatus("已重连");
                        _lastPacketTime = DateTime.Now;
                    }
                    catch (Exception ex) { Log($"定时器重连失败: {ex.Message}"); }
                    finally { _isReconnecting = false; }
                }
            }
            else
            {
                if (_disconnectCount != 0)
                {
                    Log("连接恢复，重置断开计数");
                    _disconnectCount = 0;
                }
            }

            if (!_isScreenLocked && (DateTime.Now - _lastPacketTime) > _packetTimeout)
            {
                Log($"看门狗触发：{_packetTimeout.TotalSeconds}秒无RSSI包，扫描确认");
                _isQuickScanning = true;
                _lastQuickScanTime = DateTime.Now;
                bool found = await QuickScanForExpectedDevice();
                if (!found)
                {
                    Log("看门狗未发现目标，锁屏");
                    TryLockWorkStation();
                    _updateStatus("锁屏（信号丢失）");
                }
                else
                {
                    Log("看门狗已找到目标，继续监控");
                    _updateStatus("监控中...");
                }
                _isQuickScanning = false;
            }
        }

        private void OnSessionClosed(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status != GattSessionStatus.Closed) return;

            if ((DateTime.Now - _lastReconnectTime) < _gattGrace)
            {
                Log("GATT关闭忽略（宽限期内）");
                return;
            }

            Log("GATT关闭，触发锁屏（带冷却）");
            TryLockWorkStation();
        }

        private void TryLockWorkStation()
        {
            if ((DateTime.Now - _lastLockTime) < _lockCooldown)
            {
                Log("锁屏冷却中，跳过");
                return;
            }
            _lastLockTime = DateTime.Now;
            LockWorkStation();
        }

        public void Dispose()
        {
            StopMonitoring();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _reconnectTimer?.Dispose();
        }
    }
}
