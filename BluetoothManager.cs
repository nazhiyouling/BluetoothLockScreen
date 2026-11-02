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

        // 必须与手机端 BleAdvertiserService.DEVICE_NAME 完全一致
        private const string ExpectedDeviceName = "Ghost-BLE-Anchor";

        private readonly Action<string> _updateStatus;
        private readonly Action<int> _updateRssi;
        private readonly Action<string> _updateDeviceName;

        private BluetoothLEDevice _device;
        private GattSession _session;
        private BluetoothLEAdvertisementWatcher _rssiWatcher;
        private BluetoothLEAdvertisementWatcher _unlockScanWatcher;
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

        public BluetoothManager(Action<string> status, Action<int> rssi, Action<string> name)
        {
            _updateStatus = status;
            _updateRssi = rssi;
            _updateDeviceName = name;
            _rssiThreshold = ConfigManager.Default.RssiThreshold <= 0 ? ConfigManager.Default.RssiThreshold : -83;
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

        private bool IsExpectedDeviceName(string name)
        {
            return string.Equals(name, ExpectedDeviceName, StringComparison.Ordinal);
        }

        // ---------- UI 扫描（只列出 UUID/GUID 匹配的候选） ----------
        public async Task<List<BluetoothDeviceInfo>> ScanDevicesAsync()
        {
            Log("UI扫描：暂停后台监听");
            _rssiWatcher?.Stop();
            _unlockScanWatcher?.Stop();

            var dict = new Dictionary<ulong, BluetoothDeviceInfo>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            var tcs = new TaskCompletionSource<bool>();
            watcher.Received += (s, e) =>
            {
                bool isCandidate = (_deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid))
                                   || e.Advertisement.ServiceUuids.Contains(OurServiceUuid);
                if (!isCandidate) return;

                string name = string.IsNullOrEmpty(e.Advertisement.LocalName)
                    ? "候选设备"
                    : e.Advertisement.LocalName;
                string displayName = $"{name} ({e.BluetoothAddress:X12})";

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
                }
            };
            watcher.Stopped += (s, e) => tcs.TrySetResult(true);
            watcher.Start();
            await Task.Delay(5000);
            watcher.Stop();
            await tcs.Task;

            var devices = dict.Values.OrderByDescending(d => d.Rssi).ToList();
            Log($"UI扫描完成，找到 {devices.Count} 个候选设备。连接后会校验名称是否为 {ExpectedDeviceName}");

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

        // ---------- 监控启动 ----------
        public async Task AutoConnectAndMonitorAsync()
        {
            if (_isMonitoring) return;
            if (!string.IsNullOrEmpty(ConfigManager.Default.DeviceGuid))
                _deviceGuid = Guid.Parse(ConfigManager.Default.DeviceGuid);

            await ConnectToExpectedAnchor();
            _isMonitoring = true;
            _reconnectTimer.Start();
            _updateStatus("监控中...");
        }

        public async Task StartMonitoringAsync(string addressHex)
        {
            if (_isMonitoring) return;
            if (!string.IsNullOrEmpty(ConfigManager.Default.DeviceGuid))
                _deviceGuid = Guid.Parse(ConfigManager.Default.DeviceGuid);

            await ConnectToExpectedAnchor();
            _isMonitoring = true;
            _reconnectTimer.Start();
            _updateStatus("监控中...");
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

        public async Task<int?> TestConnectionAsync(string addressHex)
        {
            return null;
        }

        // ---------- 扫描候选地址 ----------
        private async Task<List<ulong>> DiscoverCandidatesAsync(int timeoutMs)
        {
            var candidates = new HashSet<ulong>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.Received += (s, e) =>
            {
                bool isCandidate = (_deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid))
                                   || e.Advertisement.ServiceUuids.Contains(OurServiceUuid);
                if (isCandidate)
                    candidates.Add(e.BluetoothAddress);
            };
            watcher.Start();
            await Task.Delay(timeoutMs);
            watcher.Stop();
            return candidates.ToList();
        }

        // ---------- 连接候选并校验设备名称 ----------
        private async Task<bool> TryConnectAndValidateAsync(ulong addr)
        {
            BluetoothLEDevice dev = null;
            GattSession sess = null;
            try
            {
                Log($"尝试连接候选: {addr:X12}");
                dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                if (dev == null)
                {
                    Log("无法创建设备对象");
                    return false;
                }

                string actualName = dev.Name ?? "";
                Log($"候选设备名称: '{actualName}'");

                if (!IsExpectedDeviceName(actualName))
                {
                    Log($"名称不匹配，拒绝连接（期望 {ExpectedDeviceName}）");
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

                // 校验通过，正式接管
                Cleanup();
                _device = dev;
                _session = sess;
                _session.MaintainConnection = true;
                _session.SessionStatusChanged += OnSessionClosed;
                _deviceAddressStr = addr.ToString("X12");
                ConfigManager.Default.DeviceAddress = _deviceAddressStr;
                ConfigManager.Save();
                _updateDeviceName(ExpectedDeviceName);
                StartRssiWatcher(addr);
                _lastReconnectTime = DateTime.Now;
                _lastPacketTime = DateTime.Now;

                await ExtractDeviceGuid(addr);
                Log($"连接并校验成功: {ExpectedDeviceName} ({addr:X12})");
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

        // ---------- 持续扫描直到找到正确名称的设备 ----------
        private async Task ConnectToExpectedAnchor()
        {
            Cleanup();
            _updateStatus("正在查找 Ghost-BLE-Anchor...");

            while (true)
            {
                var candidates = await DiscoverCandidatesAsync(3000);
                if (candidates.Count == 0)
                {
                    Log("本轮未发现候选设备，继续等待...");
                    await Task.Delay(1000);
                    continue;
                }

                foreach (ulong addr in candidates)
                {
                    if (await TryConnectAndValidateAsync(addr))
                        return;
                }

                Log("本轮候选设备名称均不匹配，继续扫描...");
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
                    {
                        _lowRssiCount++;
                        Log($"RSSI={_currentRssi} 低于阈值 (连续 {_lowRssiCount} 次)");
                    }
                    else
                    {
                        _lowRssiCount = 0;
                    }

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
                            Log("扫描2秒内未发现 Ghost-BLE-Anchor，执行锁屏");
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

        // ---------- 快速扫描并校验名称 ----------
        private async Task<bool> QuickScanForExpectedDevice()
        {
            Cleanup();

            var candidates = await DiscoverCandidatesAsync(2000);
            if (candidates.Count == 0)
            {
                Log("快速扫描未发现任何候选设备");
                return false;
            }

            foreach (ulong addr in candidates)
            {
                if (await TryConnectAndValidateAsync(addr))
                    return true;
            }

            Log("快速扫描候选设备均名称不匹配");
            return false;
        }

        private void Cleanup()
        {
            _rssiWatcher?.Stop();
            _rssiWatcher = null;
            _unlockScanWatcher?.Stop();
            _unlockScanWatcher = null;

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
                {
                    _ = Task.Run(async () => await UnlockReconnect());
                }
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

        // ---------- 定时器：连续断开 + 看门狗 ----------
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

        // ---------- GATT 关闭 ----------
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

        // ---------- 锁屏冷却 ----------
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
