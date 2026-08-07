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
        private BluetoothLEAdvertisementWatcher _unlockScanWatcher;
        private int _rssiThreshold;
        private int _currentRssi = int.MinValue;
        private List<int> _rssiLog = new List<int>();

        private bool _isMonitoring = false;
        private string _deviceAddressStr;
        private Timer _reconnectTimer;
        private bool _isReconnecting = false;
        private bool _isQuickScanning = false;        // 正在执行快速扫描（RSSI触发）
        private const int ReconnectIntervalMs = 2000;

        private static readonly string DataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly string RssiLogPath = Path.Combine(DataFolder, "rssi_log.txt");
        private static readonly string AppLogPath = Path.Combine(DataFolder, "app_log.txt");
        private static readonly Guid OurServiceUuid = Guid.Parse("0000ABCD-0000-1000-8000-00805F9B34FB");

        private Guid _deviceGuid = Guid.Empty;
        private bool _isScreenLocked = false;
        private DateTime _lastLockTime = DateTime.MinValue;

        public BluetoothManager(Action<string> status, Action<int> rssi, Action<string> name)
        {
            _updateStatus = status; _updateRssi = rssi; _updateDeviceName = name;
            _rssiThreshold = ConfigManager.Default.RssiThreshold <= 0 ? ConfigManager.Default.RssiThreshold : -100;
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

        // ---------- UI 扫描 ----------
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
                if (e.Advertisement.ServiceUuids.Contains(OurServiceUuid))
                {
                    if (!dict.ContainsKey(e.BluetoothAddress))
                        dict[e.BluetoothAddress] = new BluetoothDeviceInfo { Address = e.BluetoothAddress, DisplayName = $"BLE-Anchor ({e.BluetoothAddress:X12})", Rssi = e.RawSignalStrengthInDBm };
                    else
                        dict[e.BluetoothAddress].Rssi = e.RawSignalStrengthInDBm;
                }
            };
            watcher.Stopped += (s, e) => tcs.TrySetResult(true);
            watcher.Start();
            await Task.Delay(5000);
            watcher.Stop();
            await tcs.Task;
            var devices = dict.Values.OrderByDescending(d => d.Rssi).ToList();
            Log($"UI扫描完成，找到 {devices.Count} 个设备");

            if (_isMonitoring && !string.IsNullOrEmpty(_deviceAddressStr))
            {
                Log("恢复RSSI监听");
                StartRssiWatcher(Convert.ToUInt64(_deviceAddressStr, 16));
            }
            return devices;
        }

        public async Task<List<BluetoothDeviceInfo>> GetPairedDevicesAsync() { return new List<BluetoothDeviceInfo>(); }

        // ---------- 监控启动 ----------
        public async Task AutoConnectAndMonitorAsync()
        {
            if (_isMonitoring) return;
            if (!string.IsNullOrEmpty(ConfigManager.Default.DeviceGuid))
                _deviceGuid = Guid.Parse(ConfigManager.Default.DeviceGuid);
            await ConnectToFirstAnchor();
            _isMonitoring = true;
            _reconnectTimer.Start();
            _updateStatus("监控中...");
        }

        public async Task StartMonitoringAsync(string addressHex)
        {
            if (_isMonitoring) return;
            _deviceAddressStr = addressHex;
            if (!string.IsNullOrEmpty(ConfigManager.Default.DeviceGuid))
                _deviceGuid = Guid.Parse(ConfigManager.Default.DeviceGuid);
            await ConnectToFirstAnchor();
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
        public int RecordAndGetRssi() { int r = _currentRssi; lock (_rssiLog) _rssiLog.Add(r); AppendRssi(r); return r; }
        public async Task<int?> TestConnectionAsync(string addressHex) { return null; }

        // ---------- 核心连接方法 ----------
        private async Task ConnectToFirstAnchor()
        {
            Cleanup();
            Log("扫描 BLE-Anchor...");
            var tcs = new TaskCompletionSource<ulong>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.Received += (s, e) =>
            {
                if (_deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid))
                    tcs.TrySetResult(e.BluetoothAddress);
                else if (e.Advertisement.ServiceUuids.Contains(OurServiceUuid))
                    tcs.TrySetResult(e.BluetoothAddress);
            };
            watcher.Stopped += (s, e) => { if (!tcs.Task.IsCompleted) watcher.Start(); };
            watcher.Start();
            ulong addr = await tcs.Task;
            watcher.Stop();

            Log($"发现设备: {addr:X12}");
            _deviceAddressStr = addr.ToString("X12");
            ConfigManager.Default.DeviceAddress = _deviceAddressStr;
            ConfigManager.Save();
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr)
                ?? throw new Exception("无法连接设备");
            await SetupConnection(_device, addr);
            await ExtractDeviceGuid(addr);
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
                    foreach (var uuid in e.Advertisement.ServiceUuids)
                        if (uuid != OurServiceUuid) { tcs.TrySetResult(uuid); watcher.Stop(); return; }
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
                Log($"设备GUID: {_deviceGuid}");
            }
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

        // ---------- RSSI 监听（新增快速扫描锁屏保护） ----------
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

                    // 低于阈值且未锁屏、未在快速扫描中
                    if (_currentRssi < _rssiThreshold && _isMonitoring && !_isScreenLocked && !_isQuickScanning)
                    {
                        Log($"RSSI={_currentRssi} 低于阈值，执行快速扫描...");
                        _isQuickScanning = true;
                        _updateStatus("信号弱，确认设备...");

                        bool deviceFound = await QuickScanAndReconnect();
                        if (!deviceFound)
                        {
                            Log("快速扫描未发现设备，执行锁屏");
                            LockWorkStation();
                            _updateStatus("锁屏（信号丢失）");
                        }
                        else
                        {
                            Log("快速扫描成功，已更新连接，取消锁屏");
                            _updateStatus("监控中...");
                        }
                        _isQuickScanning = false;
                    }
                }
            };
            _rssiWatcher.Start();
        }

        /// <summary>
        /// 快速扫描 2 秒，尝试找到目标设备并重新建立连接
        /// </summary>
        private async Task<bool> QuickScanAndReconnect()
        {
            Cleanup();  // 停止当前监听和会话，释放扫描器
            var tcs = new TaskCompletionSource<ulong>();
            using (var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active })
            {
                watcher.Received += (s, e) =>
                {
                    if (_deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid))
                        tcs.TrySetResult(e.BluetoothAddress);
                    else if (e.Advertisement.ServiceUuids.Contains(OurServiceUuid))
                        tcs.TrySetResult(e.BluetoothAddress);
                };
                watcher.Start();
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
                watcher.Stop();
                if (completed != tcs.Task) return false;
            }

            ulong addr = tcs.Task.Result;
            Log($"快速扫描发现设备: {addr:X12}");
            try
            {
                var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                if (dev == null) return false;
                var sess = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId);
                if (sess == null) { dev.Dispose(); return false; }

                _deviceAddressStr = addr.ToString("X12");
                ConfigManager.Default.DeviceAddress = _deviceAddressStr;
                ConfigManager.Save();
                _device = dev;
                _session = sess;
                _session.MaintainConnection = true;
                _session.SessionStatusChanged += OnSessionClosed;
                _updateDeviceName(dev.Name);
                StartRssiWatcher(addr);
                return true;
            }
            catch (Exception ex)
            {
                Log($"快速扫描连接失败: {ex.Message}");
                return false;
            }
        }

        private void Cleanup()
        {
            _rssiWatcher?.Stop(); _rssiWatcher = null;
            _unlockScanWatcher?.Stop(); _unlockScanWatcher = null;
            if (_session != null)
            {
                _session.SessionStatusChanged -= OnSessionClosed;
                _session.MaintainConnection = false;
                _session.Dispose();
                _session = null;
            }
            _device?.Dispose(); _device = null;
        }

        // ---------- 系统锁定/解锁事件 ----------
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
                await ConnectToFirstAnchor();
                Log("解锁重连成功");
                _updateStatus("已重连");
                _reconnectTimer.Start();
            }
            catch (Exception ex)
            {
                Log($"解锁重连失败: {ex.Message}");
                _updateStatus("重连中...");
                _reconnectTimer.Start();
            }
            finally { _isReconnecting = false; }
        }

        private async void OnReconnectTimer(object sender, ElapsedEventArgs e)
        {
            if (_isReconnecting || !_isMonitoring || _isScreenLocked || _isQuickScanning) return;
            if (_device?.ConnectionStatus == BluetoothConnectionStatus.Connected) return;

            _isReconnecting = true;
            Log("定时器重连...");
            try
            {
                await ConnectToFirstAnchor();
                Log("定时器重连成功");
                _updateStatus("已重连");
            }
            catch (Exception ex)
            {
                Log($"定时器重连失败: {ex.Message}");
            }
            finally { _isReconnecting = false; }
        }

        private void OnSessionClosed(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status != GattSessionStatus.Closed) return;
            if ((DateTime.Now - _lastLockTime).TotalSeconds < 2)
            {
                Log("GATT关闭事件忽略（重复）");
                return;
            }
            _lastLockTime = DateTime.Now;
            Log("GATT会话关闭，触发锁屏");
            if (!_isScreenLocked)
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
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _reconnectTimer?.Dispose();
        }
    }
}
