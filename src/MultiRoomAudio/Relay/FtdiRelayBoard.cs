using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Relay;

/// <summary>
/// libftdi1 wrapper for controlling 8-channel relay boards using bit-bang mode.
/// Supports the Denkovi USB 8 Relay Board with FT245RL chip.
/// </summary>
/// <remarks>
/// The FT245RL uses FTDI's Bit-Bang Mode where each of the 8 data pins
/// directly controls a relay. Writing a byte value immediately sets
/// all 8 relay states (bit 0 = relay 1, bit 7 = relay 8).
///
/// Uses libftdi1 (open source) which can automatically detach the ftdi_sio
/// kernel driver without requiring manual host configuration.
/// On Linux, requires libftdi1 and libusb-1.0 to be installed.
/// </remarks>
public sealed class FtdiRelayBoard : IRelayBoard
{
    private IntPtr _context = IntPtr.Zero;
    private ushort _currentState;  // 16-bit state for up to 16 relays
    private bool _disposed;
    private readonly ILogger<FtdiRelayBoard>? _logger;
    private readonly object _lock = new();
    private string? _serialNumber;
    private int _channelCount = 8; // Default to 8 channels

    // FTDI USB IDs
    private const int FTDI_VENDOR_ID = 0x0403;
    private const int FT245RL_PRODUCT_ID = 0x6001; // FT232/FT245 series

    // All pins as outputs (0xFF = all 8 bits)
    private const byte PIN_MASK_ALL_OUTPUT = 0xFF;

    // Bit mode constants
    private const byte BITMODE_RESET = 0x00;
    private const byte BITMODE_BITBANG = 0x01; // Async bitbang

    /// <summary>
    /// Static constructor to register custom native library resolver for cross-platform support.
    /// </summary>
    static FtdiRelayBoard()
    {
        NativeLibrary.SetDllImportResolver(typeof(FtdiRelayBoard).Assembly, ResolveFtdiLibrary);
    }

    private static IntPtr ResolveFtdiLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "libftdi1")
            return IntPtr.Zero; // Let default resolver handle it

        IntPtr handle;

        // Try standard resolution first
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out handle))
            return handle;

        // Platform-specific paths
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS: Try Homebrew paths
            string[] macPaths = new[]
            {
                "/opt/homebrew/lib/libftdi1.dylib",      // Apple Silicon Homebrew
                "/opt/homebrew/lib/libftdi1.1.dylib",    // Apple Silicon versioned
                "/usr/local/lib/libftdi1.dylib",         // Intel Homebrew
                "/usr/local/lib/libftdi1.1.dylib",       // Intel versioned
                "libftdi1.dylib",                         // In PATH
                "libftdi1.1.dylib"
            };

            foreach (var path in macPaths)
            {
                if (NativeLibrary.TryLoad(path, out handle))
                    return handle;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Linux: Try common library names
            string[] linuxNames = new[]
            {
                "libftdi1.so.2",
                "libftdi1.so.1",
                "libftdi1.so",
                "/usr/lib/x86_64-linux-gnu/libftdi1.so.2",
                "/usr/lib/aarch64-linux-gnu/libftdi1.so.2"
            };

            foreach (var name in linuxNames)
            {
                if (NativeLibrary.TryLoad(name, out handle))
                    return handle;
            }
        }

        return IntPtr.Zero; // Library not found
    }

    public FtdiRelayBoard(ILogger<FtdiRelayBoard>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Check if libftdi1 library is available on this system.
    /// </summary>
    public static bool IsLibraryAvailable()
    {
        try
        {
            IntPtr ctx = LibFtdi.ftdi_new();
            if (ctx != IntPtr.Zero)
            {
                LibFtdi.ftdi_free(ctx);
                return true;
            }
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Enumerate all connected FTDI devices.
    /// </summary>
    public static List<FtdiDeviceInfo> EnumerateDevices()
    {
        var devices = new List<FtdiDeviceInfo>();
        IntPtr ctx = IntPtr.Zero;
        IntPtr devList = IntPtr.Zero;

        try
        {
            ctx = LibFtdi.ftdi_new();
            if (ctx == IntPtr.Zero)
                return devices;

            // Get device list
            int count = LibFtdi.ftdi_usb_find_all(ctx, ref devList, FTDI_VENDOR_ID, FT245RL_PRODUCT_ID);
            if (count <= 0)
                return devices;

            IntPtr current = devList;
            for (int i = 0; i < count && current != IntPtr.Zero; i++)
            {
                var node = Marshal.PtrToStructure<LibFtdi.ftdi_device_list>(current);

                // Try to get device info
                var manufacturer = new byte[256];
                var description = new byte[256];
                var serial = new byte[256];

                int result = LibFtdi.ftdi_usb_get_strings(ctx, node.dev,
                    manufacturer, manufacturer.Length,
                    description, description.Length,
                    serial, serial.Length);

                string? serialStr = null;
                string? descStr = null;

                if (result >= 0)
                {
                    serialStr = GetNullTerminatedString(serial);
                    descStr = GetNullTerminatedString(description);
                }

                devices.Add(new FtdiDeviceInfo(
                    Index: i,
                    SerialNumber: serialStr,
                    Description: descStr ?? $"FTDI Device {i}",
                    IsOpen: false
                ));

                current = node.next;
            }
        }
        catch (Exception)
        {
            // Library not available or error - return empty list
        }
        finally
        {
            if (devList != IntPtr.Zero && ctx != IntPtr.Zero)
            {
                LibFtdi.ftdi_list_free(ref devList);
            }
            if (ctx != IntPtr.Zero)
            {
                LibFtdi.ftdi_free(ctx);
            }
        }

        return devices;
    }

    private static string? GetNullTerminatedString(byte[] buffer)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0)
            len = buffer.Length;
        if (len == 0)
            return null;
        return Encoding.ASCII.GetString(buffer, 0, len);
    }

    /// <summary>
    /// Whether the relay board is currently connected.
    /// </summary>
    public bool IsConnected => _context != IntPtr.Zero;

    /// <summary>
    /// The board's serial number (if available).
    /// </summary>
    public string? SerialNumber => _serialNumber;

    /// <summary>
    /// Number of relay channels on this board.
    /// FTDI boards typically have 8 channels, but 16-channel boards exist.
    /// </summary>
    public int ChannelCount => _channelCount;

    /// <summary>
    /// Current relay states as an int (bit 0 = relay 1, supports up to 16 relays).
    /// </summary>
    public int CurrentState
    {
        get
        {
            lock (_lock)
            {
                return _currentState;
            }
        }
    }

    /// <summary>
    /// Open connection to the first available FTDI device.
    /// </summary>
    public bool Open()
    {
        return Open(0);
    }

    /// <summary>
    /// Open connection to a specific FTDI device by index.
    /// </summary>
    public bool Open(int deviceIndex)
    {
        lock (_lock)
        {
            if (_context != IntPtr.Zero)
            {
                _logger?.LogWarning("FTDI device already open");
                return true;
            }

            try
            {
                _context = LibFtdi.ftdi_new();
                if (_context == IntPtr.Zero)
                {
                    _logger?.LogError("Failed to create FTDI context");
                    return false;
                }

                // Find all devices
                IntPtr devList = IntPtr.Zero;
                int count = LibFtdi.ftdi_usb_find_all(_context, ref devList, FTDI_VENDOR_ID, FT245RL_PRODUCT_ID);

                if (count <= 0)
                {
                    _logger?.LogError("No FTDI devices found (count: {Count})", count);
                    LibFtdi.ftdi_free(_context);
                    _context = IntPtr.Zero;
                    return false;
                }

                if (deviceIndex >= count)
                {
                    _logger?.LogError("Device index {Index} out of range (found {Count} devices)",
                        deviceIndex, count);
                    LibFtdi.ftdi_list_free(ref devList);
                    LibFtdi.ftdi_free(_context);
                    _context = IntPtr.Zero;
                    return false;
                }

                // Navigate to the requested device
                IntPtr current = devList;
                for (int i = 0; i < deviceIndex && current != IntPtr.Zero; i++)
                {
                    var node = Marshal.PtrToStructure<LibFtdi.ftdi_device_list>(current);
                    current = node.next;
                }

                if (current == IntPtr.Zero)
                {
                    _logger?.LogError("Failed to find device at index {Index}", deviceIndex);
                    LibFtdi.ftdi_list_free(ref devList);
                    LibFtdi.ftdi_free(_context);
                    _context = IntPtr.Zero;
                    return false;
                }

                var targetNode = Marshal.PtrToStructure<LibFtdi.ftdi_device_list>(current);

                // Open the device
                int result = LibFtdi.ftdi_usb_open_dev(_context, targetNode.dev);
                LibFtdi.ftdi_list_free(ref devList);

                if (result < 0)
                {
                    _logger?.LogError("Failed to open FTDI device {Index}: error {Result} - {Error}",
                        deviceIndex, result, GetErrorString());
                    LibFtdi.ftdi_free(_context);
                    _context = IntPtr.Zero;
                    return false;
                }

                if (!Initialize())
                {
                    Close();
                    return false;
                }

                _logger?.LogInformation("FTDI relay board opened (device {Index})", deviceIndex);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Exception opening FTDI device {Index}", deviceIndex);
                if (_context != IntPtr.Zero)
                {
                    LibFtdi.ftdi_free(_context);
                    _context = IntPtr.Zero;
                }
                return false;
            }
        }
    }

    /// <summary>
    /// Open connection to a specific FTDI device by serial number.
    /// </summary>
    public bool OpenBySerial(string serialNumber)
    {
        lock (_lock)
        {
            if (_context != IntPtr.Zero)
            {
                _logger?.LogWarning("FTDI device already open");
                return true;
            }

            try
            {
                _context = LibFtdi.ftdi_new();
                if (_context == IntPtr.Zero)
                {
                    _logger?.LogError("Failed to create FTDI context");
                    return false;
                }

                // Open by description with serial
                int result = LibFtdi.ftdi_usb_open_desc(
                    _context, FTDI_VENDOR_ID, FT245RL_PRODUCT_ID, null, serialNumber);

                if (result < 0)
                {
                    _logger?.LogError("Failed to open FTDI device with serial '{Serial}': error {Result} - {Error}",
                        serialNumber, result, GetErrorString());
                    LibFtdi.ftdi_free(_context);
                    _context = IntPtr.Zero;
                    return false;
                }

                if (!Initialize())
                {
                    Close();
                    return false;
                }

                // Store the serial number
                _serialNumber = serialNumber;

                _logger?.LogInformation("FTDI relay board opened (serial: {Serial})", serialNumber);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Exception opening FTDI device with serial '{Serial}'", serialNumber);
                if (_context != IntPtr.Zero)
                {
                    LibFtdi.ftdi_free(_context);
                    _context = IntPtr.Zero;
                }
                return false;
            }
        }
    }

    private string GetErrorString()
    {
        if (_context == IntPtr.Zero)
            return "No context";

        IntPtr errorPtr = LibFtdi.ftdi_get_error_string(_context);
        if (errorPtr == IntPtr.Zero)
            return "Unknown error";

        return Marshal.PtrToStringAnsi(errorPtr) ?? "Unknown error";
    }

    /// <summary>
    /// Initialize the device for relay control (bit-bang mode).
    /// </summary>
    private bool Initialize()
    {
        if (_context == IntPtr.Zero)
            return false;

        try
        {
            // Reset USB device
            int result = LibFtdi.ftdi_usb_reset(_context);
            if (result < 0)
            {
                _logger?.LogWarning("Failed to reset FTDI device: {Result} - {Error}", result, GetErrorString());
            }

            // Purge buffers
            result = LibFtdi.ftdi_usb_purge_buffers(_context);
            if (result < 0)
            {
                _logger?.LogWarning("Failed to purge FTDI buffers: {Result}", result);
            }

            // Set baud rate (affects bit-bang timing)
            result = LibFtdi.ftdi_set_baudrate(_context, 9600);
            if (result < 0)
            {
                _logger?.LogWarning("Failed to set baud rate: {Result}", result);
            }

            // Enable async bit-bang mode with all pins as outputs
            result = LibFtdi.ftdi_set_bitmode(_context, PIN_MASK_ALL_OUTPUT, BITMODE_BITBANG);
            if (result < 0)
            {
                _logger?.LogError("Failed to set bit-bang mode: {Result} - {Error}", result, GetErrorString());
                return false;
            }

            // Initialize all relays to OFF
            _currentState = 0x00;
            return WriteState(_currentState);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize FTDI device for bit-bang mode");
            return false;
        }
    }

    /// <summary>
    /// Close connection to the relay board.
    /// </summary>
    public void Close()
    {
        lock (_lock)
        {
            if (_context == IntPtr.Zero)
                return;

            try
            {
                // Turn off all relays before closing
                WriteState(0x00);

                // Reset bit mode
                LibFtdi.ftdi_set_bitmode(_context, 0x00, BITMODE_RESET);

                // Close USB
                LibFtdi.ftdi_usb_close(_context);
                _logger?.LogInformation("FTDI relay board closed");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error closing FTDI device");
            }
            finally
            {
                // Free context
                LibFtdi.ftdi_free(_context);
                _context = IntPtr.Zero;
                _currentState = 0x00;
            }
        }
    }

    /// <summary>
    /// Set the state of a specific relay channel.
    /// </summary>
    /// <param name="channel">Channel number (1-16).</param>
    /// <param name="on">True to turn on, false to turn off.</param>
    public bool SetRelay(int channel, bool on)
    {
        if (channel < 1 || channel > 16)
            throw new ArgumentOutOfRangeException(nameof(channel), "Channel must be 1-16");

        lock (_lock)
        {
            if (_context == IntPtr.Zero)
            {
                _logger?.LogWarning("Cannot set relay - device not open");
                return false;
            }

            // Convert channel (1-16) to bit position (0-15)
            ushort bit = (ushort)(1 << (channel - 1));

            ushort newState;
            if (on)
                newState = (ushort)(_currentState | bit);
            else
                newState = (ushort)(_currentState & ~bit);

            if (newState == _currentState)
                return true; // No change needed

            if (WriteState(newState))
            {
                _currentState = newState;
                _logger?.LogDebug("Relay {Channel} set to {State}", channel, on ? "ON" : "OFF");
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Get the state of a specific relay channel.
    /// </summary>
    /// <param name="channel">Channel number (1-16).</param>
    public RelayState GetRelay(int channel)
    {
        if (channel < 1 || channel > 16)
            throw new ArgumentOutOfRangeException(nameof(channel), "Channel must be 1-16");

        lock (_lock)
        {
            if (_context == IntPtr.Zero)
                return RelayState.Unknown;

            ushort bit = (ushort)(1 << (channel - 1));
            return (_currentState & bit) != 0 ? RelayState.On : RelayState.Off;
        }
    }

    /// <summary>
    /// Set all relay states at once.
    /// </summary>
    /// <param name="states">Value where each bit represents a relay (bit 0 = relay 1, supports up to 16 bits).</param>
    public bool SetAllRelays(ushort states)
    {
        lock (_lock)
        {
            if (_context == IntPtr.Zero)
            {
                _logger?.LogWarning("Cannot set relays - device not open");
                return false;
            }

            if (states == _currentState)
                return true;

            if (WriteState(states))
            {
                _currentState = states;
                _logger?.LogDebug("All relays set to 0x{State:X2}", states);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Turn off all relays.
    /// </summary>
    public bool AllOff()
    {
        return SetAllRelays(0x0000);
    }

    /// <summary>
    /// Write relay state to the board.
    /// For 8-channel boards, writes 1 byte. For 16-channel, writes 2 bytes.
    /// </summary>
    private bool WriteState(ushort state)
    {
        if (_context == IntPtr.Zero)
            return false;

        try
        {
            // For 16-channel boards, we need to write 2 bytes
            // For 8-channel boards, the high byte will be ignored
            // The FT245RL in bitbang mode treats each byte as immediate output
            // For 16-relay boards, typically two FT245 chips are used, or the board
            // expects sequential bytes for the two 8-bit ports
            var buffer = new byte[] { (byte)(state & 0xFF), (byte)((state >> 8) & 0xFF) };

            // For 8-channel boards, only write 1 byte; for 16-channel, write 2
            // Since we can't detect this, we write based on whether high byte is used
            int bytesToWrite = (state > 0xFF) ? 2 : 1;
            int bytesWritten = LibFtdi.ftdi_write_data(_context, buffer, bytesToWrite);

            if (bytesWritten < 0)
            {
                _logger?.LogError("Failed to write to FTDI device: error {Result} - {Error}",
                    bytesWritten, GetErrorString());
                return false;
            }

            if (bytesWritten != bytesToWrite)
            {
                _logger?.LogWarning("FTDI write returned {BytesWritten} bytes (expected {Expected})",
                    bytesWritten, bytesToWrite);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception writing to FTDI device");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Close();
    }

    /// <summary>
    /// P/Invoke declarations for libftdi1.
    /// </summary>
    private static class LibFtdi
    {
        // Library name varies by platform:
        // - Linux: libftdi1.so (or libftdi1.so.2)
        // - macOS (Homebrew): libftdi1.dylib (symlink) or libftdi1.1.dylib
        // - macOS (ARM/Apple Silicon): /opt/homebrew/lib/libftdi1.dylib
        // - macOS (Intel): /usr/local/lib/libftdi1.dylib
        // .NET's NativeLibrary resolver will try platform-appropriate names
        private const string LibraryName = "libftdi1";

        [StructLayout(LayoutKind.Sequential)]
        public struct ftdi_device_list
        {
            public IntPtr next;
            public IntPtr dev; // libusb_device*
        }

        // Context management
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ftdi_new();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ftdi_free(IntPtr ftdi);

        // Device enumeration
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_usb_find_all(IntPtr ftdi, ref IntPtr devlist, int vendor, int product);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ftdi_list_free(ref IntPtr devlist);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_usb_get_strings(
            IntPtr ftdi,
            IntPtr dev,
            [Out] byte[] manufacturer, int manufacturer_len,
            [Out] byte[] description, int description_len,
            [Out] byte[] serial, int serial_len);

        // Device opening
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_usb_open(IntPtr ftdi, int vendor, int product);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_usb_open_dev(IntPtr ftdi, IntPtr dev);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int ftdi_usb_open_desc(
            IntPtr ftdi, int vendor, int product,
            [MarshalAs(UnmanagedType.LPStr)] string? description,
            [MarshalAs(UnmanagedType.LPStr)] string? serial);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_usb_close(IntPtr ftdi);

        // Device control
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_usb_reset(IntPtr ftdi);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_usb_purge_buffers(IntPtr ftdi);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_set_baudrate(IntPtr ftdi, int baudrate);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_set_bitmode(IntPtr ftdi, byte bitmask, byte mode);

        // Data transfer
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_write_data(IntPtr ftdi, byte[] buf, int size);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_read_data(IntPtr ftdi, [Out] byte[] buf, int size);

        // Error handling
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ftdi_get_error_string(IntPtr ftdi);
    }
}
