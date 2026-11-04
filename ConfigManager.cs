using System;
using System.IO;
using System.Text.Json;

namespace BluetoothLockScreen
{
    public static class ConfigManager
    {
        private static readonly string DataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly string ConfigPath = Path.Combine(DataFolder, "config.json");
        private static ConfigData _config;

        public static ConfigData Default
        {
            get
            {
                if (_config == null) _config = Load();
                return _config;
            }
        }

        public static void Save()
        {
            EnsureFolder();
            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static ConfigData Load()
        {
            EnsureFolder();
            if (File.Exists(ConfigPath))
            {
                try
                {
                    return JsonSerializer.Deserialize<ConfigData>(File.ReadAllText(ConfigPath))
                           ?? new ConfigData();
                }
                catch { }
            }
            return new ConfigData();
        }

        private static void EnsureFolder() => Directory.CreateDirectory(DataFolder);
    }

    public class ConfigData
    {
        // 基础
        public string DeviceAddress { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public int RssiThreshold { get; set; } = -83;
        public string DeviceGuid { get; set; } = "";
        public string ExpectedDeviceName { get; set; } = "";

        // 高级时间参数（单位：秒，次数单位：次）
        public int LowRssiCount { get; set; } = 3;              // 连续低 RSSI 次数
        public int QuickScanCooldownSec { get; set; } = 30;     // 快速扫描冷却
        public int QuickScanTimeoutSec { get; set; } = 2;       // 快速扫描超时
        public int ReconnectIntervalSec { get; set; } = 2;      // 重连定时器间隔
        public int DisconnectCount { get; set; } = 2;           // 连续断开次数
        public int PacketTimeoutSec { get; set; } = 5;          // 看门狗超时
        public int GattGraceSec { get; set; } = 5;              // GATT 关闭宽限期
        public int LockCooldownSec { get; set; } = 5;           // 锁屏冷却
    }
}
