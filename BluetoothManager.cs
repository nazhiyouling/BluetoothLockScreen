public async Task<List<BluetoothDeviceInfo>> GetPairedDevicesAsync()
{
    var devices = new List<BluetoothDeviceInfo>();

    // 获取所有蓝牙设备（包括经典和 BLE）
    string selector = "System.Devices.Aep.ProtocolId:=\"{E0CBF06C-CD8B-4647-BB8A-263B43F0F974}\"";
    var deviceCollection = await DeviceInformation.FindAllAsync(selector);

    foreach (var deviceInfo in deviceCollection)
    {
        // 检查是否已配对
        if (deviceInfo.Pairing?.IsPaired == true)
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
                continue;
            }

            devices.Add(new BluetoothDeviceInfo
            {
                Address = addr,
                DisplayName = $"{name} ({address})"
            });
        }
    }

    return devices;
}
