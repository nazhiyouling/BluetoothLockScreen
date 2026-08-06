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
        private bool _isAttemptingReconnect = false;
        private const int ReconnectIntervalMs = 2000;

        private static readonly string DataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly string RssiLogPath = Path.Combine(DataFolder, "rssi_log.txt");
        private static readonly string AppLogPath = Path.Combine(DataFolder, "app_log.txt");
        private static readonly Guid OurServiceUuid = Guid.Parse("0000ABCD-0000-1000-8000-00805F9B34FB");

        private Guid _deviceGuid = Guid.Empty;
        private bool _isScreenLocked = false;
        private DateTime _lastLockTime = DateTime.MinValue;    // 上次锁屏时间，用于过滤重复事件

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

        // ---------- UI 扫描（仅 BLE-Anchor） ----------
        public async Task<List<BluetoothDeviceInfo>> ScanDevicesAsync() { /* 同前，省略 */ return new List<BluetoothDeviceInfo>(); }
        public async Task<List<BluetoothDeviceInfo>> GetPairedDevicesAsync() { /* 同前，省略 */ return new List<BluetoothDeviceInfo>(); }

        // ---------- 自动连接 ----------
        public async Task AutoConnectAndMonitorAsync()
        {
            if (_isMonitoring) return;
            if (!string.IsNullOrEmpty(ConfigManager.Default.DeviceGuid))
                _deviceGuid = Guid.Parse(ConfigManager.Default.DeviceGuid);
            await ConnectToFirstAnchor();
            StartReconnectTimer();
            _isMonitoring = true;
            _updateStatus("监控中...");
        }

        public async Task StartMonitoringAsync(string addressHex)
        {
            if (_isMonitoring) throw new InvalidOperationException("已在监控中");
            _deviceAddressStr = addressHex;
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
        public int RecordAndGetRssi() { int r = _currentRssi; lock (_rssiLog) _rssiLog.Add(r); AppendRssi(r); return r; }
        public async Task<int?> TestConnectionAsync(string addressHex) { return null; }

        // ---------- 核心连接方法 ----------
        private async Task ConnectToFirstAnchor()
        {
            Cleanup();
            Log("开始扫描 BLE-Anchor...");
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

            Log($"发现 BLE-Anchor，地址: {addr:X12}");
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
            // 同前，略
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

        // ---------- RSSI 监听（快速重连，最多 3 次尝试） ----------
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
                        Log($"RSSI={_currentRssi} 低于阈值，快速重连（最多3次）...");
                        _isAttemptingReconnect = true;
                        _updateStatus("信号丢失，尝试重连...");
                        if (!await TryQuickReconnectWithRetry(3))
                        {
                            Log("快速重连失败，执行锁屏");
                            LockWorkStation();
                            _updateStatus("锁屏（信号丢失）");
                        }
                        else
                        {
                            Log("快速重连成功");
                            _updateStatus("监控中...");
                        }
                        _isAttemptingReconnect = false;
                    }
                }
            };
            _rssiWatcher.Start();
        }

        private async Task<bool> TryQuickReconnectWithRetry(int maxRetries)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                if (await TryQuickReconnectOnce())
                    return true;
                Log($"快速重连第 {i + 1} 次失败");
            }
            return false;
        }

        private async Task<bool> TryQuickReconnectOnce()
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
                var done = await Task.WhenAny(tcs.Task, Task.Delay(3000));
                watcher.Stop();
                if (done != tcs.Task) return false;

                ulong addr = tcs.Task.Result;
                Log($"快速重连发现地址: {addr:X12}");
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

                // 增加稳定等待时间到 3 秒
                await Task.Delay(3000);
                if (_device?.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    Log("快速重连连接稳定");
                    return true;
                }
                else
                {
                    Log("快速重连后连接不稳定，清理");
                    Cleanup();
                    return false;
                }
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
            _unlockScanWatcher?.Stop(); _unlockScanWatcher = null;
            if (_session != null) { _session.SessionStatusChanged -= OnSessionClosed; _session.MaintainConnection = false; _session.Dispose(); _session = null; }
            _device?.Dispose(); _device = null;
        }

        // ---------- 系统锁定/解锁事件 ----------
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                Log("系统锁定，暂停重连");
                _isScreenLocked = true;
                StopReconnectTimer();
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                Log("系统解锁，启动立即扫描...");
                _isScreenLocked = false;
                if (_isMonitoring)
                {
                    StartReconnectTimer();
                    _ = Task.Run(async () => await UnlockImmediateScan());
                }
            }
        }

        private async Task UnlockImmediateScan()
        {
            if (_isReconnecting || !_isMonitoring)
            {
                Log("解锁扫描被阻塞: _isReconnecting=" + _isReconnecting);
                return;
            }
            _isReconnecting = true;
            Log("解锁扫描：高优先级扫描器（3秒超时）");
            _updateStatus("重连中...");

            _unlockScanWatcher?.Stop();
            _unlockScanWatcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            var tcs = new TaskCompletionSource<ulong>();
            _unlockScanWatcher.Received += (s, e) =>
            {
                if (_deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid))
                {
                    Log("解锁扫描：GUID匹配 " + e.BluetoothAddress.ToString("X12"));
                    tcs.TrySetResult(e.BluetoothAddress);
                }
                else if (e.Advertisement.ServiceUuids.Contains(OurServiceUuid))
                {
                    Log("解锁扫描：UUID匹配 " + e.BluetoothAddress.ToString("X12"));
                    tcs.TrySetResult(e.BluetoothAddress);
                }
            };
            _unlockScanWatcher.Stopped += (s, e) => { if (!tcs.Task.IsCompleted) _unlockScanWatcher.Start(); };
            _unlockScanWatcher.Start();
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000));
            _unlockScanWatcher.Stop();
            _unlockScanWatcher = null;

            if (completed == tcs.Task)
            {
                ulong addr = tcs.Task.Result;
                Log("解锁扫描成功，连接: " + addr.ToString("X12"));
                try
                {
                    Cleanup();
                    var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                    if (dev != null)
                    {
                        await SetupConnection(dev, addr);
                        await ExtractDeviceGuid(addr);
                        _deviceAddressStr = addr.ToString("X12");
                        ConfigManager.Default.DeviceAddress = _deviceAddressStr;
                        ConfigManager.Save();
                        Log("解锁重连完成");
                        _updateStatus("已重连");
                    }
                }
                catch (Exception ex) { Log("解锁连接异常: " + ex.Message); }
            }
            else
            {
                Log("解锁扫描超时，交由定时器处理");
                _updateStatus("重连中...");
            }
            _isReconnecting = false;
        }

        private void StartReconnectTimer() { if (!_isScreenLocked) _reconnectTimer.Start(); }
        private void StopReconnectTimer() { _reconnectTimer.Stop(); }

        private async void OnReconnectTimer(object sender, ElapsedEventArgs e)
        {
            if (_isReconnecting || !_isMonitoring || _isAttemptingReconnect)
            {
                Log("定时器跳过: " + (_isReconnecting ? "重连中" : "尝试重连中"));
                return;
            }
            if (_device?.ConnectionStatus == BluetoothConnectionStatus.Connected) return;

            _isReconnecting = true;
            Log("定时器重连...");
            _updateStatus("重连中...");
            Cleanup();
            try
            {
                await ConnectToFirstAnchor();
                await Task.Delay(3000);
                if (_device?.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    Log("定时器重连稳定");
                    _updateStatus("已重连");
                }
                else
                {
                    Log("定时器重连后不稳定");
                    Cleanup();
                }
            }
            catch (Exception ex)
            {
                Log("定时器重连失败: " + ex.Message);
                _updateStatus("重连中...");
            }
            finally { _isReconnecting = false; }
        }

        // 过滤重复的 GATT 关闭事件（2 秒内只触发一次锁屏）
        private void OnSessionClosed(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status == GattSessionStatus.Closed)
            {
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
