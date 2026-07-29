using System;
using System.Collections.Generic;
using System.IO;
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
    
    try
    {
        // 方式1: 使用系统配对状态选择器（最直接）
        string pairedSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var pairedDevices = await DeviceInformation.FindAllAsync(pairedSelector);
        
        foreach (var deviceInfo in pairedDevices)
        {
            AddDeviceToList(devices, deviceInfo);
        }
        
        // 如果方式1返回空，尝试方式2: 枚举所有蓝牙设备，手动过滤配对状态
        if (devices.Count == 0)
        {
            string allBluetoothSelector = "System.Devices.Aep.ProtocolId:=\"{E0CBF06C-CD8B-4647-BB8A-263B43F0F974}\"";
            var allDevices = await DeviceInformation.FindAllAsync(allBluetoothSelector);
            
            foreach (var deviceInfo in allDevices)
            {
                if (deviceInfo.Pairing?.IsPaired == true)
                {
                    AddDeviceToList(devices, deviceInfo);
                }
            }
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"获取已配对设备失败: {ex.Message}");
    }
    
    return devices;
}

// 辅助方法：从 DeviceInformation 提取地址并添加到列表
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
    else
    {
        return; // 没有地址则忽略
    }

    // 避免重复
    if (!devices.Exists(d => d.Address == addr))
    {
        devices.Add(new BluetoothDeviceInfo
        {
            Address = addr,
            DisplayName = $"{name} ({address})"
        });
    }
}

        // ---------- 扫描（备用） ----------
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
        // 检查广播包中是否包含我们的服务 UUID
        bool isOurDevice = false;
        foreach (var uuid in e.Advertisement.ServiceUuids)
        {
            if (uuid == targetUuid)
            {
                isOurDevice = true;
                break;
            }
        }

        string displayName;
        if (isOurDevice)
        {
            displayName = "BLE-Anchor";
        }
        else if (!string.IsNullOrEmpty(e.Advertisement.LocalName))
        {
            displayName = e.Advertisement.LocalName;
        }
        else
        {
            displayName = "未知设备";
        }
        displayName += $" ({e.BluetoothAddress:X12})";

        if (!deviceDict.ContainsKey(e.BluetoothAddress))
        {
            deviceDict[e.BluetoothAddress] = new BluetoothDeviceInfo
            {
                Address = e.BluetoothAddress,
                DisplayName = displayName,
                Rssi = e.RawSignalStrengthInDBm
            };
        }
        else
        {
            deviceDict[e.BluetoothAddress].Rssi = e.RawSignalStrengthInDBm;
        }
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
            if (_isMonitoring)
                throw new InvalidOperationException("已经在监控中。");

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

        public void UpdateThreshold(int newThreshold)
        {
            _rssiThreshold = newThreshold;
        }

        // ---------- RSSI 记录 ----------
        public int RecordAndGetRssi()
        {
            int rssi = _currentRssi;
            lock (_rssiLog)
            {
                _rssiLog.Add(rssi);
            }
            AppendRssiToFile(rssi);
            return rssi;
        }

        private void AppendRssiToFile(int rssi)
        {
            EnsureDataFolderExists();
            try
            {
                using (var writer = new StreamWriter(RssiLogPath, true))
                {
                    writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {rssi} dBm");
                }
            }
            catch { }
        }

        public void RecordCurrentRssi()
        {
            RecordAndGetRssi();
        }

        public void SaveRssiLog()
        {
            // 即时写入已处理，此处留空
        }

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
                    var watcher = new BluetoothLEAdvertisementWatcher
                    {
                        ScanningMode = BluetoothLEScanningMode.Active
                    };
                    watcher.Received += (s, e) =>
                    {
                        if (e.BluetoothAddress == address)
                        {
                            tcs.TrySetResult(e.RawSignalStrengthInDBm);
                            watcher.Stop();
                        }
                    };
                    watcher.Stopped += (s, e) => tcs.TrySetResult(null);
                    watcher.Start();
                    var delayTask = Task.Delay(5000);
                    var resultTask = await Task.WhenAny(tcs.Task, delayTask);
                    watcher.Stop();
                    if (resultTask is Task<int?> rssiTask)
                        return await rssiTask;
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        // ---------- 内部连接逻辑 ----------
        private async Task ConnectAndMonitorAsync(ulong address)
        {
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_device == null)
            {
                throw new Exception(
                    "无法找到指定的蓝牙设备。\n\n" +
                    "可能的原因：\n" +
                    "1. 手机未运行 BLE-Anchor APP 或未点击“开始广播”；\n" +
                    "2. 输入的蓝牙地址有误（请核对大小写及冒号）；\n" +
                    "3. 手机蓝牙已关闭或距离过远。\n\n" +
                    "请先在手机上打开 BLE-Anchor 并点击“开始广播”，然后重试。");
            }

            _updateDeviceName(_device.Name);

            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            if (_session == null)
                throw new Exception("无法创建GATT会话。");

            _session.MaintainConnection = true;
            _session.SessionStatusChanged += OnSessionStatusChanged;

            StartRssiWatcher(address);
        }

        private void StartRssiWatcher(ulong targetAddress)
        {
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            _watcher.Received += (s, e) =>
            {
                if (e.BluetoothAddress == targetAddress)
                {
                    int rssi = e.RawSignalStrengthInDBm;
                    _currentRssi = rssi;
                    _updateRssi(rssi);

                    if (rssi < _rssiThreshold && _isMonitoring)
                    {
                        LockWorkStation();
                        _updateStatus("已触发锁屏（RSSI过低）");
                    }
                }
            };

            _watcher.Start();
        }

        private void CleanupConnection()
        {
            if (_watcher != null)
            {
                _watcher.Stop();
                _watcher = null;
            }

            if (_session != null)
            {
                _session.SessionStatusChanged -= OnSessionStatusChanged;
                _session.MaintainConnection = false;
                _session.Dispose();
                _session = null;
            }

            if (_device != null)
            {
                _device.Dispose();
                _device = null;
            }
        }

        // ---------- 保活与自动重连 ----------
        private void StartReconnectTimer()
        {
            _reconnectTimer.Start();
        }

        private void StopReconnectTimer()
        {
            _reconnectTimer.Stop();
        }

        private async void OnReconnectTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (_isReconnecting || !_isMonitoring || string.IsNullOrEmpty(_deviceAddressStr))
                return;

            try
            {
                bool needReconnect = _device == null || _device.ConnectionStatus != BluetoothConnectionStatus.Connected;
                if (needReconnect)
                {
                    _isReconnecting = true;
                    _updateStatus("连接断开，正在重连...");
                    CleanupConnection();

                    ulong address = Convert.ToUInt64(_deviceAddressStr, 16);
                    await ConnectAndMonitorAsync(address);
                    _updateStatus("已重连，监控中...");
                }
            }
            catch
            {
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            if (args.Status == GattSessionStatus.Closed)
            {
                LockWorkStation();
                _updateStatus("已触发锁屏（蓝牙断开）");
            }
        }

        private static void EnsureDataFolderExists()
        {
            if (!Directory.Exists(DataFolder))
            {
                Directory.CreateDirectory(DataFolder);
            }
        }

        public void Dispose()
        {
            StopMonitoring();
            _reconnectTimer?.Dispose();
        }
    }
}
