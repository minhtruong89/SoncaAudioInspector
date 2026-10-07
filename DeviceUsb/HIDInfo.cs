namespace SoncaAudioInspector.DeviceUsb
{
    public class HIDInfo
    {
        public string Path { get; set; } = "";
        public short Vid { get; set; }
        public short Pid { get; set; }
        public string Product { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string SerialNumber { get; set; } = "";

        public int ReportLength { get; set; }
        public int InputReportLength { get; set; }
        public int OutputReportLength { get; set; }

        public bool EncryptProcessDone { get; set; }
        public int FirmwareType { get; set; }
        public CW_Firmware_Info? Firmware_Info { get; set; }

        public HIDInfo(string product, string serial, string manufacturer,
            string path, short vid, short pid)
        {
            Product = product;
            SerialNumber = serial;
            Manufacturer = manufacturer;
            Path = path;
            Vid = vid;
            Pid = pid;
            EncryptProcessDone = false;
        }

        public HIDInfo()
        {
        }

        public override string ToString()
        {
            string name = !string.IsNullOrWhiteSpace(Product) ? Product : "Acnos Board";
            return $"{name} [VID: {Vid:X4}, PID: {Pid:X4}] - {SerialNumber}";
        }
    }
}
