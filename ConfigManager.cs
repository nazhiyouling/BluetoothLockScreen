using System;
using System.IO;
using System.Text.Json;

namespace BluetoothLockScreen
{
    public static class ConfigManager
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private static ConfigData _config;

        public static ConfigData Default
        {
            get
            {
                if (_config == null)
                    _config = Load();
                return _config;
            }
        }

        public static void Save()
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_config, options);
            File.WriteAllText(ConfigPath, json);
        }

        private static ConfigData Load()
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<ConfigData>(json) ?? new ConfigData();
            }
            return new ConfigData();
        }
    }

    public class ConfigData
    {
        public string DeviceAddress { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public int RssiThreshold { get; set; } = -70;
    }
}
