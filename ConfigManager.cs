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
                if (_config == null)
                    _config = Load();
                return _config;
            }
        }

        public static void Save()
        {
            EnsureDataFolderExists();
            // 如果存在旧版本根目录的 config.json，保存后删除它（避免混淆）
            string oldConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (File.Exists(oldConfigPath))
            {
                try { File.Delete(oldConfigPath); } catch { }
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_config, options);
            File.WriteAllText(ConfigPath, json);
        }

        private static ConfigData Load()
        {
            EnsureDataFolderExists();

            // 优先从 data 目录加载
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<ConfigData>(json) ?? new ConfigData();
            }

            // 兼容旧版本：如果根目录存在 config.json，迁移到 data 目录
            string oldConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (File.Exists(oldConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(oldConfigPath);
                    var config = JsonSerializer.Deserialize<ConfigData>(json) ?? new ConfigData();
                    // 保存到新位置
                    File.WriteAllText(ConfigPath, json);
                    // 删除旧文件
                    File.Delete(oldConfigPath);
                    return config;
                }
                catch
                {
                    // 迁移失败，继续使用默认值
                }
            }

            return new ConfigData();
        }

        private static void EnsureDataFolderExists()
        {
            if (!Directory.Exists(DataFolder))
            {
                Directory.CreateDirectory(DataFolder);
            }
        }
    }

    public class ConfigData
    {
        public string DeviceAddress { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public int RssiThreshold { get; set; } = -70;
    }
}
