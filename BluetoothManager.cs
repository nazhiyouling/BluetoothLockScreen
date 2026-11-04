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
        private List<int> _rssiLog = new List<int>();

        private bool _isMonitoring = false;
        private string _deviceAddressStr;
        private Timer _reconnectTimer;
        private bool _isReconnecting = false;
        private bool _isQuickScanning = false;
        private const int ReconnectIntervalMs = 2000;

        private DateTime _lastQuickScanTime = DateTime.MinValue;
        private const int QuickScanCooldownSeconds = 30;

        private static readonly string DataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly string RssiLogPath = Path.Combine(DataFolder, "rssi_log.txt");
        private static readonly string AppLogPath = Path.Combine(DataFolder, "app_log.txt");
        private static readonly Guid OurServiceUuid = Guid.Parse("0000ABCD-0000-1000-8000-00805F9B34FB");

        private Guid _deviceGuid = Guid.Empty;
        private bool _isScreenLocked = false;
        private DateTime _lastLockTime = DateTime.MinValue;
        private DateTime _lastReconnectTime = DateTime.MinValue;
        private int _lowRssiCount = 0;
        private int _disconnectCount = 0;

        private DateTime _lastPacketTime = DateTime.MinValue;
        private const int PacketTimeoutSeconds = 5;

        private string _expectedDeviceName = "";

        public BluetoothManager(Action<string> status, Action<int> rssi, Action<string> name)
        {
            _updateStatus = status;
            _updateRssi = rssi;
            _updateDeviceName = name;
            _rssiThreshold = ConfigManager.Default.RssiThreshold <= 0 ? ConfigManager.Default.RssiThreshold : -83;
            _expectedDeviceName = ConfigManager.Default.ExpectedDeviceName?.Trim() ?? "";

            _reconnectTimer = new Timer(ReconnectIntervalMs) { AutoReset = true };
            _reconnectTimer.Elapsed += OnReconnectTimer;
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(AppLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 程序启动\n");
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        /// <summary>
        /// 设置界面保存后调用，更新期望名称（下次启动生效）
        /// </summary>
        public void UpdateExpectedDeviceName(string name)
        {
            _expectedDeviceName = name?.Trim() ?? "";
            Log($"期望设备名称已更新为: '{(string.IsNullOrEmpty(_expectedDeviceName) ? "(空)" : _expectedDeviceName)}'");
        }

        /// <summary>
        /// 当前是否已配置期望设备名称
        /// </summary>
        public bool HasExpectedDeviceName => !string.IsNullOrEmpty(_expectedDeviceName);

        private void Log(string msg)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
            try { File.AppendAllText(AppLogPath, line + Environment.NewLine); } catch { }
            System.Diagnostics.Debug.WriteLine(line);
        }

        private bool IsExpectedDeviceName(string name)
        {
            if (string.IsNullOrEmpty(_expectedDeviceName))
                return true;
            return string.Equals(name, _expectedDeviceName, StringComparison.Ordinal);
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
                bool isCandidate = (_deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid))
                                   || e.Advertisement.ServiceUuids.Contains(OurServiceUuid);
                if (!isCandidate) return;

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

        public async Task<List<BluetoothDeviceInfo>> GetPairedDevicesAsync()
        {
            return new List<BluetoothDeviceInfo>();
        }

        // ---------- 自动连接（名称为空则跳过） ----------
        public async Task<bool> AutoConnectAndMonitorAsync()
        {
            if (_isMonitoring) return true;

            if (string.IsNullOrEmpty(_expectedDeviceName))
            {
                Log("期望设备名称为空，跳过自动连接。请先在设置中填写设备名称。");
                _updateStatus("未配置设备名称，请先在设置中填写");
                return false;
            }

            if (!string.IsNullOrEmpty(ConfigManager.Default.DeviceGuid))
                _deviceGuid = Guid.Parse(ConfigManager.Default.DeviceGuid);

            await ConnectToExpectedAnchor();
            _isMonitoring = true;
            _reconnectTimer.Start();
            _updateStatus("监控中...");
            return true;
        }

        // ---------- 手动启动监控（名称为空则拒绝） ----------
        public async Task<bool> StartMonitoringAsync(string addressHex)
        {
            if (_isMonitoring) return true;

            if (string.IsNullOrEmpty(_expectedDeviceName))
            {
                Log("手动启动失败：期望设备名称为空");
                return false;
            }

            if (!string.IsNullOrEmpty(ConfigManager.Default.DeviceGuid))
                _deviceGuid = Guid.Parse(ConfigManager.Default.DeviceGuid);

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

        public int RecordAndGetRssi()
        {
            int r = _currentRssi;
            lock (_rssiLog) _rssiLog.Add(r);
            AppendRssi(r);
            return r;
        }

        public async Task<int?> TestConnectionAsync(string addressHex) => null;

        // ---------- 扫描候选设备 ----------
        private async Task<List<(ulong Address, string Name)>> DiscoverCandidatesAsync(int timeoutMs)
        {
            var candidates = new Dictionary<ulong, string>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.Received += (s, e) =>
            {
                bool isCandidate = (_deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid))
                                   || e.Advertisement.ServiceUuids.Contains(OurServiceUuid);
                if (!isCandidate) return;

                string name = e.Advertisement.LocalName ?? "";
                if (!candidates.ContainsKey(e.BluetoothAddress) || string.IsNullOrEmpty(candidates[e.BluetoothAddress]))
                    candidates[e.BluetoothAddress] = name;
            };
            watcher.Start();
            await Task.Delay(timeoutMs);
            watcher.Stop();
            return candidates.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        // ---------- 连接候选并校验名称 ----------
        private async Task<bool> TryConnectAndValidateAsync(ulong addr, string scannedName)
        {
            if (!string.IsNullOrEmpty(scannedName) && !IsExpectedDeviceName(scannedName))
            {
                Log($"扫描名称不匹配: '{scannedName}'，跳过地址 {addr:X12}");
                return false;
            }

            BluetoothLEDevice dev = null;
            GattSession sess = null;
            try
            {
                Log($"尝试连接候选: {addr:X12}，扫描名称: '{(string.IsNullOrEmpty(scannedName) ? "无" : scannedName)}'");
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
                    {
                        actualName = dev.Name ?? "";
                        Log($"GATT名称读取失败，回退到 dev.Name: '{actualName}'");
                    }
                    else
                    {
                        Log($"GATT读取名称: '{actualName}'");
                    }
                }

                if (!IsExpectedDeviceName(actualName))
                {
                    Log($"名称不匹配（期望 '{_expectedDeviceName}'，实际 '{actualName}'），拒绝连接");
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

                await ExtractDeviceGuid(addr);
                Log($"连接并校验成功: {(string.IsNullOrEmpty(actualName) ? addr.ToString("X12") : actualName)} ({addr:X12})");
                return true;
            }
            catch (Exception ex)
            {
                Log($"连接候选异常: {ex.Message}");
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
                if (servicesResult.Status != GattCommunicationStatus.Success)
                    return null;

                var genericAccess = servicesResult.Services
                    .FirstOrDefault(s => s.Uuid == Guid.Parse("00001800-0000-1000-8000-00805F9B34FB"));
                if (genericAccess == null) return null;

                var charsResult = await genericAccess.GetCharacteristicsAsync();
                if (charsResult.Status != GattCommunicationStatus.Success)
                    return null;

                var nameChar = charsResult.Characteristics
                    .FirstOrDefault(c => c.Uuid == Guid.Parse("00002A00-0000-1000-8000-00805F9B34FB"));
                if (nameChar == null) return null;

                var readResult = await nameChar.ReadValueAsync();
                if (readResult.Status != GattCommunicationStatus.Success)
                    return null;

                var reader = Windows.Storage.Streams.DataReader.FromBuffer(readResult.Value);
                return reader.ReadString(readResult.Value.Length);
            }
            catch
            {
                return null;
            }
        }

        // ---------- 持续扫描直到找到目标设备 ----------
        private async Task ConnectToExpectedAnchor()
        {
            Cleanup();
            _updateStatus($"正在查找 {_expectedDeviceName}...");

            while (true)
            {
                var candidates = await DiscoverCandidatesAsync(3000);
                if (candidates.Count == 0)
                {
                    Log("本轮未发现候选设备，继续等待...");
                    await Task.Delay(1000);
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    if (await TryConnectAndValidateAsync(candidate.Address, candidate.Name))
                        return;
                }

                Log("本轮候选设备均未通过校验，继续扫描...");
                await Task.Delay(500);
            }
        }

        private async Task ExtractDeviceGuid(ulong addr)
        {
            if (_deviceGuid != Guid.Empty) return;

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
            watcher.Stopped += (s, e) =>
            {
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(null);
            };
            watcher.Start();
            var result = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            watcher.Stop();
            if (result is Task<Guid?> gt && gt.Result.HasValue)
            {
                _deviceGuid = gt.Result.Value;
                ConfigManager.Default.DeviceGuid = _deviceGuid.ToString();
                ConfigManager.Save();
                Log($"设备GUID: {_deviceGuid}");
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

                    if (_currentRssi < _rssiThreshold)
                        _lowRssiCount++;
                    else
                        _lowRssiCount = 0;

                    if (_lowRssiCount >= 3 &&
                        _isMonitoring &&
                        !_isScreenLocked &&
                        !_isQuickScanning &&
                        (DateTime.Now - _lastQuickScanTime).TotalSeconds >= QuickScanCooldownSeconds)
                    {
                        _lowRssiCount = 0;
                        _isQuickScanning = true;
                        _lastQuickScanTime = DateTime.Now;
                        Log("连续3次低RSSI，扫描确认目标设备...");
                        _updateStatus("信号弱，确认设备...");

                        bool found = await QuickScanForExpectedDevice();
                        if (!found)
                        {
                            Log("扫描2秒内未发现目标设备，执行锁屏");
                            TryLockWorkStation();
                            _updateStatus("锁屏（设备离开）");
                        }
                        else
                        {
                            Log("已找到并校验目标设备，继续监控");
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
            var candidates = await DiscoverCandidatesAsync(2000);
            if (candidates.Count == 0)
            {
                Log("快速扫描未发现任何候选设备");
                return false;
            }

            foreach (var candidate in candidates)
            {
                if (await TryConnectAndValidateAsync(candidate.Address, candidate.Name))
                    return true;
            }

            Log("快速扫描候选设备均未通过校验");
            return false;
        }

        private void Cleanup()
        {
            _rssiWatcher?.Stop();
            _rssiWatcher = null;

            if (_session != null)
            {
                _session.SessionStatusChanged -= OnSessionClosed;
                _session.MaintainConnection = false;
                _session.Dispose();
                _session = null;
            }

            _device?.Dispose();
            _device = null;
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
            finally
            {
                _isReconnecting = false;
            }
        }

        // ---------- 定时器 ----------
        private async void OnReconnectTimer(object sender, ElapsedEventArgs e)
        {
            if (_isReconnecting || !_isMonitoring || _isScreenLocked || _isQuickScanning)
                return;

            bool connected = _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

            if (!connected)
            {
                _disconnectCount++;
                Log($"定时器检测到断开 (连续 {_disconnectCount} 次)");

                if (_disconnectCount >= 2)
                {
                    _disconnectCount = 0;
                    _isReconnecting = true;
                    Log("连续2次断开，启动重连...");
                    _updateStatus("重连中...");
                    try
                    {
                        await ConnectToExpectedAnchor();
                        Log("定时器重连成功");
                        _updateStatus("已重连");
                        _lastPacketTime = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        Log($"定时器重连失败: {ex.Message}");
                    }
                    finally
                    {
                        _isReconnecting = false;
                    }
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

            if (!_isScreenLocked &&
                (DateTime.Now - _lastPacketTime).TotalSeconds > PacketTimeoutSeconds)
            {
                Log($"看门狗触发：{PacketTimeoutSeconds}秒未收到RSSI包，扫描确认设备");
                _isQuickScanning = true;
                _lastQuickScanTime = DateTime.Now;

                bool found = await QuickScanForExpectedDevice();
                if (!found)
                {
                    Log("看门狗未发现目标设备，执行锁屏");
                    TryLockWorkStation();
                    _updateStatus("锁屏（信号丢失）");
                }
                else
                {
                    Log("看门狗已找到目标设备，继续监控");
                    _updateStatus("监控中...");
                }

                _isQuickScanning = false;
            }
        }

        private void OnSessionClosed(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status != GattSessionStatus.Closed)
                return;

            if ((DateTime.Now - _lastReconnectTime).TotalSeconds < 3)
            {
                Log("GATT关闭事件忽略（重连宽限期内）");
                return;
            }

            Log("GATT会话关闭，触发锁屏（带冷却）");
            TryLockWorkStation();
        }

        private void TryLockWorkStation()
        {
            if ((DateTime.Now - _lastLockTime).TotalSeconds < 5)
            {
                Log("锁屏冷却期内，跳过本次锁屏");
                return;
            }

            _lastLockTime = DateTime.Now;
            LockWorkStation();
        }

        private void AppendRssi(int rssi)
        {
            try
            {
                File.AppendAllText(RssiLogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {rssi} dBm\n");
            }
            catch { }
        }

        public void Dispose()
        {
            StopMonitoring();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _reconnectTimer?.Dispose();
        }
    }
}
