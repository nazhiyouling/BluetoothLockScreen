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
        private bool _isAttemptingReconnect = false;
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

        // ---------- UI 扫描（略） ----------
        public async Task<List<BluetoothDeviceInfo>> ScanDevicesAsync() { /* 保持不变 */ return new List<BluetoothDeviceInfo>(); }
        public async Task<List<BluetoothDeviceInfo>> GetPairedDevicesAsync() { return new List<BluetoothDeviceInfo>(); }

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
            _isMonitoring = true;   // 连接成功后才设为 true
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
        public int RecordAndGetRssi() { return 0; } // 略
        public async Task<int?> TestConnectionAsync(string addressHex) { return null; }

        // ---------- 核心连接 ----------
        private async Task ConnectAndExtractGuid()
        {
            _discoveryWatcher?.Stop(); _discoveryWatcher = null;

            // 尝试已知地址
            if (!string.IsNullOrEmpty(_deviceAddressStr))
            {
                try
                {
                    ulong addr = Convert.ToUInt64(_deviceAddressStr, 16);
                    _device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                    if (_device != null)
                    {
                        Log("已知地址连接成功");
                        await SetupConnection(_device, addr);
                        await ExtractDeviceGuid(addr);
                        return;
                    }
                }
                catch { }
            }

            Log("已知地址失效，启动扫描...");
            var foundAddr = await DiscoverDeviceByUuidOrGuid();
            _deviceAddressStr = foundAddr.ToString("X12");
            ConfigManager.Default.DeviceAddress = _deviceAddressStr;
            ConfigManager.Save();
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(foundAddr)
                ?? throw new Exception("无法连接设备");
            await SetupConnection(_device, foundAddr);
            await ExtractDeviceGuid(foundAddr);
        }

        private async Task ExtractDeviceGuid(ulong addr) { /* 保持不变 */ }

        private async Task<ulong> DiscoverDeviceByUuidOrGuid()
        {
            var tcs = new TaskCompletionSource<ulong>();
            _discoveryWatcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _discoveryWatcher.Received += (s, e) =>
            {
                if (_deviceGuid != Guid.Empty && e.Advertisement.ServiceUuids.Contains(_deviceGuid))
                {
                    Log($"GUID匹配: {e.BluetoothAddress:X12}");
                    tcs.TrySetResult(e.BluetoothAddress);
                }
                else if (e.Advertisement.ServiceUuids.Contains(OurServiceUuid))
                {
                    Log($"UUID匹配: {e.BluetoothAddress:X12}");
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

        // ---------- RSSI 监听（快速重连机制） ----------
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

                    if (_currentRssi < _rssiThreshold && _isMonitoring && !_isAttemptingReconnect)
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
                            // 注意：即使锁屏，_isMonitoring 仍然为 true，定时器会继续尝试后台重连
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

        /// <summary>
        /// 快速重连：扫描 3 秒，若获取新地址并完整建立连接（含GATT、RSSI监听）则返回 true
        /// </summary>
        private async Task<bool> TryQuickReconnect()
        {
            try
            {
                Cleanup(); // 断开旧连接

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

                // 关键：验证完整连接
                var newDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(newAddr);
                if (newDevice == null) { Log("新地址无法连接设备"); return false; }

                // 尝试建立GATT会话
                var newSession = await GattSession.FromDeviceIdAsync(newDevice.BluetoothDeviceId);
                if (newSession == null) { newDevice.Dispose(); Log("无法创建GATT会话"); return false; }

                // 成功，保存并替换
                _deviceAddressStr = newAddr.ToString("X12");
                ConfigManager.Default.DeviceAddress = _deviceAddressStr;
                ConfigManager.Save();
                _device = newDevice;
                _session = newSession;
                _session.MaintainConnection = true;
                _session.SessionStatusChanged += OnSessionClosed;
                _updateDeviceName(newDevice.Name);
                StartRssiWatcher(newAddr);   // 重启RSSI监听
                Log("快速重连完整建立，监控恢复");
                return true;
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
            if (_isReconnecting || !_isMonitoring || _isAttemptingReconnect) return;
            try
            {
                if (_device?.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    _isReconnecting = true;
                    Log("定时器检测到断连，后台重连...");
                    _updateStatus("重连中...");
                    Cleanup();
                    while (_isMonitoring)
                    {
                        try
                        {
                            await ConnectAndExtractGuid();
                            Log("后台重连成功");
                            _updateStatus("已重连");
                            break;
                        }
                        catch { await Task.Delay(5000); }
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
                LockWorkStation();
                _updateStatus("锁屏（断开）");
            }
        }

        private void AppendRssi(int rssi) { /* 略 */ }
        public void Dispose() { StopMonitoring(); _reconnectTimer?.Dispose(); }
    }
}
