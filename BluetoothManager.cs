using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;   // 新增：支持 OrderByDescending
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

        public BluetoothManager(Action<string> updateStatus, Action<int> updateRssi, Action<string> updateDeviceName)
        {
            _updateStatus = updateStatus;
            _updateRssi = updateRssi;
            _updateDeviceName = updateDeviceName;
            _rssiThreshold = ConfigManager.Default.RssiThreshold;

            _reconnectTimer = new Timer(ReconnectIntervalMs);
            _reconnectTimer.AutoReset = true;
            _reconnectTimer.Elapsed += OnReconnectTimerElapsed;

            EnsureDataFolderExists();
        }

        // ---------- 获取已配对的蓝牙设备（改进版） ----------
        public async Task<List<BluetoothDeviceInfo>> GetPairedDevicesAsync()
        {
            var devices = new List<BluetoothDeviceInfo>();
            // 方式1: 使用系统配对状态选择器
            string pairedSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var pairedDevices = await DeviceInformation.FindAllAsync(pairedSelector);
            foreach (var deviceInfo in pairedDevices)
                AddDeviceToList(devices, deviceInfo);

            // 如果方式1返回空，尝试方式2: 枚举所有蓝牙设备，手动过滤配对状态
            if (devices.Count == 0)
            {
                string allBluetoothSelector = "System.Devices.Aep.ProtocolId:=\"{E0CBF06C-CD8B-4647-BB8A-263B43F0F974}\"";
                var allDevices = await DeviceInformation.FindAllAsync(allBluetoothSelector);
                foreach (var deviceInfo in allDevices)
                {
                    if (deviceInfo.Pairing?.IsPaired == true)
                        AddDeviceToList(devices, deviceInfo);
                }
            }
            return devices;
        }

        private void AddDeviceToList(List<BluetoothDeviceInfo> devices, DeviceInformation deviceInfo)
        {
            string name = deviceInfo.Name;
            string address = "";
            ulong addr = 0;
            if (deviceInfo.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out object prop))
            {
                address = prop.ToString();
                addr = Convert.ToUInt64(address.Replace(":", ""), 16);
            }
            else return;

            if (!devices.Exists(d => d.Address == addr))
                devices.Add(new BluetoothDeviceInfo { Address = addr, DisplayName = $"{name} ({address})" });
        }

        // ---------- 扫描（通过 UUID 识别 BLE-Anchor） ----------
        public async Task<List<BluetoothDeviceInfo>> ScanDevicesAsync()
        {
            var deviceDict = new Dictionary<ulong, BluetoothDeviceInfo>();
            var targetUuid = Guid.Parse("0000ABCD-0000-1000-8000-00805F9B34FB");
            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            var tcs = new TaskCompletionSource<bool>();
            watcher.Received += (s, e) =>
            {
                bool isOurDevice = false;
                foreach (var uuid in e.Advertisement.ServiceUuids)
                    if (uuid == targetUuid) { isOurDevice = true; break; }

                string displayName;
                if (isOurDevice)
                    displayName = "BLE-Anchor";
                else if (!string.IsNullOrEmpty(e.Advertisement.LocalName))
                    displayName = e.Advertisement.LocalName;
                else
                    displayName = "未知设备";

                displayName += $" ({e.BluetoothAddress:X12})";

                if (!deviceDict.ContainsKey(e.BluetoothAddress))
                    deviceDict[e.BluetoothAddress] = new BluetoothDeviceInfo { Address = e.BluetoothAddress, DisplayName = displayName, Rssi = e.RawSignalStrengthInDBm };
                else
                    deviceDict[e.BluetoothAddress].Rssi = e.RawSignalStrengthInDBm;
            };
            watcher.Stopped += (s, e) => tcs.TrySetResult(true);

            watcher.Start();
            await Task.Delay(5000);
            watcher.Stop();
            await tcs.Task;

            return deviceDict.Values.OrderByDescending(d => d.Rssi).ToList();
        }

        // ---------- 监控控制 ----------
        public async Task StartMonitoringAsync(string addressHexString)
        {
            if (_isMonitoring) throw new InvalidOperationException("已经在监控中。");
            _deviceAddressStr = addressHexString;
            await ConnectAndMonitorAsync(Convert.ToUInt64(addressHexString, 16));
            StartReconnectTimer();
            _isMonitoring = true;
            _updateStatus("监控中...");
        }

        public void StopMonitoring()
        {
            _isMonitoring = false;
            _updateStatus("已停止监控");
            StopReconnectTimer();
            CleanupConnection();
        }

        public void UpdateThreshold(int newThreshold) => _rssiThreshold = newThreshold;

        // ---------- RSSI 记录 ----------
        public int RecordAndGetRssi()
        {
            int rssi = _currentRssi;
            lock (_rssiLog) { _rssiLog.Add(rssi); }
            AppendRssiToFile(rssi);
            return rssi;
        }

        private void AppendRssiToFile(int rssi)
        {
            EnsureDataFolderExists();
            try { File.AppendAllText(RssiLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {rssi} dBm\n"); } catch { }
        }

        public void RecordCurrentRssi() => RecordAndGetRssi();
        public void SaveRssiLog() { }

        // ---------- 测试连接 ----------
        public async Task<int?> TestConnectionAsync(string addressHexString)
        {
            try
            {
                ulong address = Convert.ToUInt64(addressHexString, 16);
                using (var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address))
                {
                    if (device == null) return null;
                    var tcs = new TaskCompletionSource<int?>();
                    var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
                    w.Received += (s, e) => { if (e.BluetoothAddress == address) { tcs.TrySetResult(e.RawSignalStrengthInDBm); w.Stop(); } };
                    w.Stopped += (s, e) => tcs.TrySetResult(null);
                    w.Start();
                    await Task.WhenAny(tcs.Task, Task.Delay(5000));
                    w.Stop();
                    return await tcs.Task;
                }
            }
            catch { return null; }
        }

        // ---------- 内部连接逻辑 ----------
        private async Task ConnectAndMonitorAsync(ulong address)
        {
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_device == null) throw new Exception("无法找到指定的蓝牙设备。\n请确保手机已运行 BLE-Anchor 并点击“开始广播”。");
            _updateDeviceName(_device.Name);
            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            if (_session == null) throw new Exception("无法创建GATT会话。");
            _session.MaintainConnection = true;
            _session.SessionStatusChanged += OnSessionStatusChanged;
            StartRssiWatcher(address);
        }

        private void StartRssiWatcher(ulong targetAddress)
        {
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _watcher.Received += (s, e) =>
            {
                if (e.BluetoothAddress == targetAddress)
                {
                    _currentRssi = e.RawSignalStrengthInDBm;
                    _updateRssi(_currentRssi);
                    if (_currentRssi < _rssiThreshold && _isMonitoring) { LockWorkStation(); _updateStatus("已触发锁屏（RSSI过低）"); }
                }
            };
            _watcher.Start();
        }

        private void CleanupConnection()
        {
            _watcher?.Stop(); _watcher = null;
            if (_session != null) { _session.SessionStatusChanged -= OnSessionStatusChanged; _session.MaintainConnection = false; _session.Dispose(); _session = null; }
            _device?.Dispose(); _device = null;
        }

        private void StartReconnectTimer() => _reconnectTimer.Start();
        private void StopReconnectTimer() => _reconnectTimer.Stop();

        private async void OnReconnectTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (_isReconnecting || !_isMonitoring || string.IsNullOrEmpty(_deviceAddressStr)) return;
            try
            {
                if (_device?.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    _isReconnecting = true;
                    _updateStatus("连接断开，正在重连...");
                    CleanupConnection();
                    await ConnectAndMonitorAsync(Convert.ToUInt64(_deviceAddressStr, 16));
                    _updateStatus("已重连，监控中...");
                }
            }
            catch { }
            finally { _isReconnecting = false; }
        }

        private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status == GattSessionStatus.Closed) { LockWorkStation(); _updateStatus("已触发锁屏（蓝牙断开）"); }
        }

        private static void EnsureDataFolderExists() { if (!Directory.Exists(DataFolder)) Directory.CreateDirectory(DataFolder); }

        public void Dispose()
        {
            StopMonitoring();
            _reconnectTimer?.Dispose();
        }
    }
}
