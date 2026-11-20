using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SoncaAudioInspector.DeviceUsb
{
    public static class HIDBrowse
    {
        public static List<HIDInfo> Browse()
        {
            List<HIDInfo> info = new List<HIDInfo>();

            NativeHid.HidD_GetHidGuid(out Guid gHid);
            IntPtr hInfoSet = NativeHid.SetupDiGetClassDevs(ref gHid, null, IntPtr.Zero,
                NativeHid.DIGCF_DEVICEINTERFACE | NativeHid.DIGCF_PRESENT);

            if (hInfoSet == NativeHid.INVALID_HANDLE_VALUE)
            {
                return info;
            }

            var iface = new NativeHid.DeviceInterfaceData();
            iface.Size = Marshal.SizeOf(iface);
            uint index = 0;

            try
            {
                while (NativeHid.SetupDiEnumDeviceInterfaces(hInfoSet, 0, ref gHid, index, ref iface))
                {
                    string path = GetPath(hInfoSet, ref iface);
                    if (!string.IsNullOrEmpty(path))
                    {
                        IntPtr handle = NativeHid.CreateFile(path,
                            NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
                            NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
                            IntPtr.Zero, NativeHid.OPEN_EXISTING, NativeHid.FILE_FLAG_OVERLAPPED,
                            IntPtr.Zero);

                        if (handle != NativeHid.INVALID_HANDLE_VALUE)
                        {
                            try
                            {
                                string man = GetManufacturer(handle);
                                string prod = GetProduct(handle);
                                string serial = GetSerialNumber(handle);
                                GetVidPid(handle, out short vid, out short pid);

                                HIDInfo i = new HIDInfo(prod, serial, man, path, vid, pid);
                                info.Add(i);
                            }
                            catch { }
                            finally
                            {
                                NativeHid.CloseHandle(handle);
                            }
                        }
                    }

                    index++;
                }
            }
            finally
            {
                NativeHid.SetupDiDestroyDeviceInfoList(hInfoSet);
            }

            return info;
        }

        private static string GetPath(IntPtr hInfoSet, ref NativeHid.DeviceInterfaceData iface)
        {
            var detIface = new NativeHid.DeviceInterfaceDetailData();
            detIface.Size = Marshal.SizeOf(typeof(IntPtr)) == 8 ? 8 : 5;
            uint reqSize = (uint)Marshal.SizeOf(detIface);

            bool status = NativeHid.SetupDiGetDeviceInterfaceDetail(hInfoSet,
                ref iface, ref detIface, reqSize, ref reqSize, IntPtr.Zero);

            if (!status)
            {
                return string.Empty;
            }

            return detIface.DevicePath ?? string.Empty;
        }

        private static string GetManufacturer(IntPtr handle)
        {
            var s = new StringBuilder(256);
            if (NativeHid.HidD_GetManufacturerString(handle, s, s.Capacity))
            {
                return s.ToString();
            }
            return string.Empty;
        }

        private static string GetProduct(IntPtr handle)
        {
            var s = new StringBuilder(256);
            if (NativeHid.HidD_GetProductString(handle, s, s.Capacity))
            {
                return s.ToString();
            }
            return string.Empty;
        }

        private static string GetSerialNumber(IntPtr handle)
        {
            var s = new StringBuilder(256);
            if (NativeHid.HidD_GetSerialNumberString(handle, s, s.Capacity))
            {
                return s.ToString();
            }
            return string.Empty;
        }

        private static void GetVidPid(IntPtr handle, out short vid, out short pid)
        {
            var attr = new NativeHid.HiddAttributes();
            attr.Size = Marshal.SizeOf(attr);

            if (NativeHid.HidD_GetAttributes(handle, ref attr))
            {
                vid = attr.VendorID;
                pid = attr.ProductID;
            }
            else
            {
                vid = 0;
                pid = 0;
            }
        }
    }
}
