using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;   // 新增

namespace BluetoothLockScreen
{
    public class BluetoothManager : IDisposable
    {
        // ... 前面的代码保持不变 ...

        /// <summary>
        /// 获取 Windows 中所有已配对的蓝牙设备（经典 + BLE）
        /// </summary>
        public async Task<List<BluetoothDeviceInfo>> GetPairedDevicesAsync()
        {
            var devices = new List<BluetoothDeviceInfo>();

            // 筛选已配对的蓝牙设备
            string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var deviceCollection = await DeviceInformation.FindAllAsync(selector);

            foreach (var deviceInfo in deviceCollection)
            {
                string name = deviceInfo.Name;
                string address = "";
                ulong addr = 0;

                // 尝试从属性中提取蓝牙地址
                if (deviceInfo.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out object prop))
                {
                    address = prop.ToString();
                    addr = Convert.ToUInt64(address.Replace(":", ""), 16);
                }
                else
                {
                    // 没有地址则跳过
                    continue;
                }

                devices.Add(new BluetoothDeviceInfo
                {
                    Address = addr,
                    DisplayName = $"{name} ({address})"
                });
            }

            return devices;
        }

        // ... 后面的代码保持不变 ...
    }
}
