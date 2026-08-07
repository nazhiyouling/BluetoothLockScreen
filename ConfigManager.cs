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
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static ConfigData Load()
        {
            EnsureFolder();
            if (File.Exists(ConfigPath))
            {
                try { return JsonSerializer.Deserialize<ConfigData>(File.ReadAllText(ConfigPath)) ?? new ConfigData(); }
                catch { }
            }
            return new ConfigData();
        }

        private static void EnsureFolder() => Directory.CreateDirectory(DataFolder);
    }

    public class ConfigData
    {
        public string DeviceAddress { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public int RssiThreshold { get; set; } = -100;
        public string DeviceGuid { get; set; } = "";
    }
}
