using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace SoncaAudioInspector.DeviceUsb
{
    public class HIDDev : IDisposable
    {
        private IntPtr _handle = NativeHid.INVALID_HANDLE_VALUE;
        private SafeFileHandle? _shandle;
        private FileStream? _fileStream;
        private bool _isConnected;

        public bool IsConnected => _isConnected;

        public bool CheckValid(HIDInfo dev)
        {
            if (string.IsNullOrEmpty(dev.Path)) return false;

            IntPtr testHandle = NativeHid.CreateFile(dev.Path,
                NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
                NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeHid.OPEN_EXISTING, NativeHid.FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (testHandle == NativeHid.INVALID_HANDLE_VALUE)
            {
                return false;
            }

            using SafeFileHandle safeHandle = new SafeFileHandle(testHandle, true);
            IntPtr preparsedData = IntPtr.Zero;
            try
            {
                if (!NativeHid.HidD_GetPreparsedData(safeHandle, ref preparsedData))
                {
                    return false;
                }

                NativeHid.HIDP_CAPS caps = new NativeHid.HIDP_CAPS();
                int result = NativeHid.HidP_GetCaps(preparsedData, ref caps);
                if (result == 0)
                {
                    return false;
                }

                string usageHex = Convert.ToString(caps.Usage, 16).ToUpperInvariant();
                if (!usageHex.Equals("55AA", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                dev.InputReportLength = caps.InputReportByteLength;
                dev.OutputReportLength = caps.OutputReportByteLength;

                if (dev.InputReportLength != dev.OutputReportLength || dev.InputReportLength < 4)
                {
                    return false;
                }

                dev.ReportLength = dev.InputReportLength;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (preparsedData != IntPtr.Zero)
                {
                    NativeHid.HidD_FreePreparsedData(preparsedData);
                }
            }
        }

        public bool Open(HIDInfo dev)
        {
            Close();

            _handle = NativeHid.CreateFile(dev.Path,
                NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
                NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeHid.OPEN_EXISTING, NativeHid.FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (_handle == NativeHid.INVALID_HANDLE_VALUE)
            {
                _isConnected = false;
                return false;
            }

            _isConnected = true;
            _shandle = new SafeFileHandle(_handle, false);

            try
            {
                _fileStream = new FileStream(_shandle, FileAccess.ReadWrite, dev.ReportLength, true);
            }
            catch
            {
                // FileStream can be optional if SetOutputReport / GetInputReport are used via driver handle
            }

            return true;
        }

        public bool SetOutputReport(byte[] buffer, int length)
        {
            if (_shandle == null || _shandle.IsInvalid || _shandle.IsClosed) return false;
            return NativeHid.HidD_SetOutputReport(_shandle, buffer, length);
        }

        public bool GetInputReport(byte[] buffer, int length)
        {
            if (_shandle == null || _shandle.IsInvalid || _shandle.IsClosed) return false;
            return NativeHid.HidD_GetInputReport(_shandle, buffer, length);
        }

        public void Close()
        {
            try
            {
                if (_fileStream != null)
                {
                    _fileStream.Close();
                    _fileStream = null;
                }
            }
            catch { }

            try
            {
                if (_shandle != null && !_shandle.IsClosed)
                {
                    _shandle.Close();
                    _shandle = null;
                }
            }
            catch { }

            if (_handle != NativeHid.INVALID_HANDLE_VALUE)
            {
                NativeHid.CloseHandle(_handle);
                _handle = NativeHid.INVALID_HANDLE_VALUE;
            }

            _isConnected = false;
        }

        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }
    }
}
