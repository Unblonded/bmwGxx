using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace GDiag.Net
{
    // an installed J2534 pass-through driver, e.g. the Tactrix OpenPort 2.0
    public class J2534Driver
    {
        public string Name;
        public string Library;
        // size of C 'unsigned long' in the driver: 4 on Windows and for u32 drivers, 8 for C drivers on 64-bit macOS/Linux
        public int LongSize;

        public override string ToString() => $"{Name}  ({Path.GetFileName(Library)})";

        static int DefaultLongSize => OperatingSystem.IsWindows() ? 4 : IntPtr.Size;

        // Lists drivers the way Diogenes-ImGui does: the PassThruSupport registry keys on Windows; on macOS and Linux
        // ~/.passthru/*.json (Macchina / OpenVehicleDiag convention) and the openport-j2534 Homebrew library.
        public static List<J2534Driver> Find(IEnumerable<string> extraLibraries)
        {
            var found = OperatingSystem.IsWindows() ? FromRegistry() : FromUnix();
            foreach (string library in extraLibraries ?? Enumerable.Empty<string>())
            {
                if (File.Exists(library) && !found.Any(d => d.Library == library))
                {
                    found.Add(new J2534Driver { Name = Path.GetFileNameWithoutExtension(library), Library = library, LongSize = DefaultLongSize });
                }
            }
            return found;
        }

        static readonly string[] OpenPortUnixLibraries =
        {
            "/opt/homebrew/lib/libj2534.dylib",
            "/usr/local/lib/libj2534.dylib",
            "/usr/local/lib/libj2534.so",
            "/usr/lib/libj2534.so",
        };

        static List<J2534Driver> FromUnix()
        {
            var result = new List<J2534Driver>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string folder = Path.Combine(home, ".passthru");
            if (Directory.Exists(folder))
            {
                foreach (string jsonPath in Directory.GetFiles(folder, "*.json").OrderBy(x => x))
                {
                    try
                    {
                        using var json = JsonDocument.Parse(File.ReadAllText(jsonPath));
                        var root = json.RootElement;
                        string library = root.TryGetProperty("FUNCTION_LIB", out var lib) ? lib.GetString() : null;
                        if (string.IsNullOrWhiteSpace(library))
                        {
                            continue;
                        }
                        if (library.StartsWith("~/"))
                        {
                            library = Path.Combine(home, library.Substring(2));
                        }
                        string name = root.TryGetProperty("NAME", out var n) ? n.GetString() : Path.GetFileNameWithoutExtension(jsonPath);
                        // these drivers use 32-bit integers unless the file says otherwise
                        int longSize = root.TryGetProperty("LONG_SIZE", out var size) && size.TryGetInt32(out int s) && (s == 4 || s == 8) ? s : 4;
                        result.Add(new J2534Driver { Name = name, Library = library, LongSize = longSize });
                    }
                    catch (Exception ex) when (ex is JsonException || ex is IOException || ex is InvalidOperationException)
                    {
                        Console.WriteLine($"Skipped driver file {jsonPath}: {ex.Message}");
                    }
                }
            }
            string openPort = OpenPortUnixLibraries.FirstOrDefault(File.Exists);
            if (openPort != null && !result.Any(d => d.Library == openPort))
            {
                result.Add(new J2534Driver { Name = "Tactrix OpenPort 2.0", Library = openPort, LongSize = DefaultLongSize });
            }
            return result;
        }

        static List<J2534Driver> FromRegistry()
        {
            var result = new List<J2534Driver>();
            if (!OperatingSystem.IsWindows())
            {
                return result;
            }
            foreach (string path in new[] { @"Software\PassThruSupport.04.04", @"Software\WOW6432Node\PassThruSupport.04.04" })
            {
                using var root = Registry.LocalMachine.OpenSubKey(path, false);
                if (root is null)
                {
                    continue;
                }
                foreach (string entry in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(entry);
                    string library = key?.GetValue("FunctionLibrary", "") as string;
                    if (string.IsNullOrWhiteSpace(library) || result.Any(d => d.Library == library))
                    {
                        continue;
                    }
                    result.Add(new J2534Driver { Name = key.GetValue("Name", entry) as string, Library = library, LongSize = 4 });
                }
            }
            return result;
        }
    }

    public class J2534Exception : Exception
    {
        public readonly int Code;

        public J2534Exception(string call, int code, string detail)
            : base($"{call} failed: {Describe(code)}{(string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail.Trim()})")}")
        {
            Code = code;
        }

        static string Describe(int code)
        {
            switch (code)
            {
                case 0x01: return "not supported by this driver";
                case 0x02: return "invalid channel";
                case 0x03: return "invalid protocol";
                case 0x05: return "unsupported flag";
                case 0x07: return "failed";
                case 0x08: return "no adapter connected (is the OpenPort plugged in?)";
                case 0x09: return "timeout";
                case 0x0A: return "invalid message";
                case 0x0C: return "too many filters";
                case 0x0D: return "invalid device";
                case 0x0E: return "adapter already in use by another program";
                case 0x10: return "receive buffer empty";
                case 0x11: return "transmit buffer full";
                case 0x12: return "receive buffer overflow";
                case 0x13: return "pin invalid";
                case 0x14: return "channel in use";
                case 0x18: return "invalid baud rate";
                case 0x1A: return "device not connected";
                default: return $"error 0x{code:X2}";
            }
        }
    }

    // Minimal J2534 (SAE J2534-1 v04.04) binding: just what a raw CAN channel needs.
    // All PASSTHRU_MSG and SCONFIG fields are C 'unsigned long', whose size depends on the driver (LongSize).
    public sealed class J2534Api : IDisposable
    {
        public const int ProtocolCan = 0x05;
        public const int PassFilter = 0x01;
        public const int IoctlSetConfig = 0x02;
        public const int IoctlClearRxBuffer = 0x08;
        public const int ConfigDataRate = 0x01;
        public const int ConfigLoopback = 0x03;
        const int ErrBufferEmpty = 0x10;
        const int ErrTimeout = 0x09;
        const int RxTxMsgType = 0x01;      // RxStatus bit: our own frame echoed back
        const int MaxData = 4128;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int OpenFn(IntPtr name, IntPtr deviceId);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int CloseFn(nint deviceId);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int ConnectFn(nint deviceId, nint protocol, nint flags, nint baud, IntPtr channelId);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int DisconnectFn(nint channelId);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int ReadMsgsFn(nint channelId, IntPtr msgs, IntPtr numMsgs, nint timeout);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int WriteMsgsFn(nint channelId, IntPtr msgs, IntPtr numMsgs, nint timeout);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int StartFilterFn(nint channelId, nint type, IntPtr mask, IntPtr pattern, IntPtr flowControl, IntPtr filterId);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int IoctlFn(nint channelId, nint ioctl, IntPtr input, IntPtr output);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int GetLastErrorFn(IntPtr text);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int ReadVersionFn(nint deviceId, IntPtr firmware, IntPtr dll, IntPtr api);

        readonly IntPtr Handle;
        readonly int L;
        readonly OpenFn POpen;
        readonly CloseFn PClose;
        readonly ConnectFn PConnect;
        readonly DisconnectFn PDisconnect;
        readonly ReadMsgsFn PRead;
        readonly WriteMsgsFn PWrite;
        readonly StartFilterFn PStartFilter;
        readonly IoctlFn PIoctl;
        readonly GetLastErrorFn PGetLastError;
        readonly ReadVersionFn PReadVersion;

        // one call at a time into the driver; the reader thread and the sender share it
        readonly object CallLock = new object();
        readonly IntPtr MsgBuffer;
        const int MsgBufferCount = 16;
        readonly IntPtr Scratch;

        int DeviceId = -1;
        int ChannelId = -1;

        public J2534Api(J2534Driver driver)
        {
            L = driver.LongSize;
            if (IntPtr.Size == 4 && L == 8)
            {
                throw new NotSupportedException("A driver with 8-byte longs can't be used from a 32-bit program");
            }
            if (!File.Exists(driver.Library))
            {
                throw new FileNotFoundException($"Driver library not found: {driver.Library}");
            }
            try
            {
                Handle = NativeLibrary.Load(driver.Library);
            }
            catch (BadImageFormatException)
            {
                throw new BadImageFormatException(IntPtr.Size == 8
                    ? $"{Path.GetFileName(driver.Library)} is a 32-bit driver. Use the 32-bit (x86) Windows build of G-Diag."
                    : $"{Path.GetFileName(driver.Library)} does not match this program's CPU type.");
            }
            POpen = Fn<OpenFn>("PassThruOpen");
            PClose = Fn<CloseFn>("PassThruClose");
            PConnect = Fn<ConnectFn>("PassThruConnect");
            PDisconnect = Fn<DisconnectFn>("PassThruDisconnect");
            PRead = Fn<ReadMsgsFn>("PassThruReadMsgs");
            PWrite = Fn<WriteMsgsFn>("PassThruWriteMsgs");
            PStartFilter = Fn<StartFilterFn>("PassThruStartMsgFilter");
            PIoctl = Fn<IoctlFn>("PassThruIoctl");
            PGetLastError = Fn<GetLastErrorFn>("PassThruGetLastError");
            PReadVersion = Fn<ReadVersionFn>("PassThruReadVersion");

            MsgBuffer = Marshal.AllocHGlobal(MessageSize * MsgBufferCount);
            Scratch = Marshal.AllocHGlobal(4096);
        }

        T Fn<T>(string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(Handle, name, out IntPtr address))
            {
                throw new EntryPointNotFoundException($"The driver has no {name}, it is not a J2534 v04.04 driver");
            }
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        int MessageSize => L * 6 + MaxData;

        int ReadLong(IntPtr p, int offset) => L == 8 ? (int)Marshal.ReadInt64(p, offset) : Marshal.ReadInt32(p, offset);

        void WriteLong(IntPtr p, int offset, int value)
        {
            if (L == 8)
            {
                Marshal.WriteInt64(p, offset, (uint)value);
            }
            else
            {
                Marshal.WriteInt32(p, offset, value);
            }
        }

        void Check(string call, int result)
        {
            if (result != 0)
            {
                throw new J2534Exception(call, result, LastError());
            }
        }

        string LastError()
        {
            var text = Marshal.AllocHGlobal(256);
            try
            {
                Marshal.WriteByte(text, 0);
                PGetLastError(text);
                return Marshal.PtrToStringAnsi(text);
            }
            catch
            {
                return "";
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }

        // opens the adapter and a raw CAN channel at the given bit rate
        public void OpenCan(int baud)
        {
            lock (CallLock)
            {
                var id = Marshal.AllocHGlobal(8);
                try
                {
                    Marshal.WriteInt64(id, 0);
                    Check("PassThruOpen", POpen(IntPtr.Zero, id));
                    DeviceId = ReadLong(id, 0);
                    Marshal.WriteInt64(id, 0);
                    Check("PassThruConnect", PConnect(DeviceId, ProtocolCan, 0, baud, id));
                    ChannelId = ReadLong(id, 0);
                }
                finally
                {
                    Marshal.FreeHGlobal(id);
                }
                SetConfig(ConfigLoopback, 0);
            }
        }

        public string Versions()
        {
            lock (CallLock)
            {
                var buffers = new[] { Marshal.AllocHGlobal(80), Marshal.AllocHGlobal(80), Marshal.AllocHGlobal(80) };
                try
                {
                    foreach (var b in buffers)
                    {
                        Marshal.WriteByte(b, 0);
                    }
                    if (PReadVersion(DeviceId, buffers[0], buffers[1], buffers[2]) != 0)
                    {
                        return "";
                    }
                    return $"firmware {Marshal.PtrToStringAnsi(buffers[0])}, driver {Marshal.PtrToStringAnsi(buffers[1])}, API {Marshal.PtrToStringAnsi(buffers[2])}";
                }
                finally
                {
                    foreach (var b in buffers)
                    {
                        Marshal.FreeHGlobal(b);
                    }
                }
            }
        }

        void SetConfig(int parameter, int value)
        {
            // SCONFIG_LIST { unsigned long NumOfParams; SCONFIG* ConfigPtr; }, SCONFIG { unsigned long Parameter; unsigned long Value; }
            IntPtr config = Scratch + 256;
            WriteLong(config, 0, parameter);
            WriteLong(config, L, value);
            WriteLong(Scratch, 0, 1);
            Marshal.WriteIntPtr(Scratch, Math.Max(L, IntPtr.Size), config);
            Check("PassThruIoctl(SET_CONFIG)", PIoctl(ChannelId, IoctlSetConfig, Scratch, IntPtr.Zero));
        }

        // lets through every 11-bit frame whose id matches pattern under mask
        public void PassFilter11(uint mask, uint pattern)
        {
            lock (CallLock)
            {
                IntPtr maskMsg = Marshal.AllocHGlobal(MessageSize);
                IntPtr patternMsg = Marshal.AllocHGlobal(MessageSize);
                IntPtr filterId = Marshal.AllocHGlobal(8);
                try
                {
                    FillMessage(maskMsg, new[] { (byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask });
                    FillMessage(patternMsg, new[] { (byte)(pattern >> 24), (byte)(pattern >> 16), (byte)(pattern >> 8), (byte)pattern });
                    Check("PassThruStartMsgFilter", PStartFilter(ChannelId, PassFilter, maskMsg, patternMsg, IntPtr.Zero, filterId));
                }
                finally
                {
                    Marshal.FreeHGlobal(maskMsg);
                    Marshal.FreeHGlobal(patternMsg);
                    Marshal.FreeHGlobal(filterId);
                }
                PIoctl(ChannelId, IoctlClearRxBuffer, IntPtr.Zero, IntPtr.Zero);
            }
        }

        // PASSTHRU_MSG: ProtocolID, RxStatus, TxFlags, Timestamp, DataSize, ExtraDataIndex, Data[4128]
        void FillMessage(IntPtr msg, byte[] data)
        {
            WriteLong(msg, 0, ProtocolCan);
            WriteLong(msg, L, 0);
            WriteLong(msg, L * 2, 0);
            WriteLong(msg, L * 3, 0);
            WriteLong(msg, L * 4, data.Length);
            WriteLong(msg, L * 5, data.Length);
            Marshal.Copy(data, 0, msg + L * 6, data.Length);
        }

        // one CAN frame: 4 id bytes then up to 8 data bytes
        public void Write(uint canId, byte[] data, int timeoutMs = 100)
        {
            var frame = new byte[4 + data.Length];
            frame[0] = (byte)(canId >> 24);
            frame[1] = (byte)(canId >> 16);
            frame[2] = (byte)(canId >> 8);
            frame[3] = (byte)canId;
            Buffer.BlockCopy(data, 0, frame, 4, data.Length);
            lock (CallLock)
            {
                IntPtr msg = Marshal.AllocHGlobal(MessageSize);
                try
                {
                    FillMessage(msg, frame);
                    WriteLong(Scratch, 0, 1);
                    Check("PassThruWriteMsgs", PWrite(ChannelId, msg, Scratch, timeoutMs));
                }
                finally
                {
                    Marshal.FreeHGlobal(msg);
                }
            }
        }

        // frames received within timeoutMs, as (id, data); empty when none
        public List<(uint id, byte[] data)> Read(int timeoutMs)
        {
            var result = new List<(uint, byte[])>();
            lock (CallLock)
            {
                WriteLong(Scratch, 0, MsgBufferCount);
                int status = PRead(ChannelId, MsgBuffer, Scratch, timeoutMs);
                if (status != 0 && status != ErrBufferEmpty && status != ErrTimeout)
                {
                    throw new J2534Exception("PassThruReadMsgs", status, LastError());
                }
                int count = ReadLong(Scratch, 0);
                for (int i = 0; i < count && i < MsgBufferCount; i++)
                {
                    IntPtr msg = MsgBuffer + i * MessageSize;
                    int rxStatus = ReadLong(msg, L);
                    int size = ReadLong(msg, L * 4);
                    if ((rxStatus & RxTxMsgType) != 0 || size < 4 || size > 12)
                    {
                        continue;
                    }
                    var bytes = new byte[size];
                    Marshal.Copy(msg + L * 6, bytes, 0, size);
                    uint id = Hex.BigEndian(bytes, 0, 4);
                    result.Add((id, bytes.Skip(4).ToArray()));
                }
            }
            return result;
        }

        public void Dispose()
        {
            lock (CallLock)
            {
                try
                {
                    if (ChannelId >= 0)
                    {
                        PDisconnect(ChannelId);
                    }
                    if (DeviceId >= 0)
                    {
                        PClose(DeviceId);
                    }
                }
                catch
                {
                    // the adapter may already be unplugged
                }
                ChannelId = -1;
                DeviceId = -1;
                Marshal.FreeHGlobal(MsgBuffer);
                Marshal.FreeHGlobal(Scratch);
                // the library stays loaded: some drivers crash when unloaded while their USB thread still runs
            }
        }
    }
}
