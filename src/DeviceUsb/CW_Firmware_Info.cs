using System;

namespace SoncaAudioInspector.DeviceUsb
{
    public class CW_Firmware_Info
    {
        public int Firmware_Type { get; set; }
        public int Firmware_Version { get; set; }
        public int Firmware_Version_1 { get; set; }
        public int Firmware_Version_2 { get; set; }
        public int EffectLib_Version { get; set; }
        public int EffectLib_Version_1 { get; set; }
        public int EffectLib_Version_2 { get; set; }

        public string FwTypeString { get; set; } = "";
        public string FirmwareVersionString { get; set; } = "";
        public string EffectLibVersionString { get; set; } = "";

        public CW_Firmware_Info(byte[] data)
        {
            if (data == null || data.Length < 7) return;

            int i = 0;
            Firmware_Type = (int)data[i++];

            Firmware_Version = (int)data[i++];
            Firmware_Version_1 = (int)data[i++];
            Firmware_Version_2 = (int)data[i++];

            EffectLib_Version = (int)data[i++];
            EffectLib_Version_1 = (int)data[i++];
            EffectLib_Version_2 = (int)data[i++];

            FirmwareVersionString = $"V{Firmware_Version}.{Firmware_Version_1}.{Firmware_Version_2}";
            EffectLibVersionString = $"V{EffectLib_Version}.{EffectLib_Version_1}.{EffectLib_Version_2}";
        }

        public override string ToString()
        {
            return $"[{FwTypeString}, Version: {FirmwareVersionString}, Effect Lib: {EffectLibVersionString}]";
        }
    }
}
