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
    private const byte BITMODE_SYNCBB = 0x04;  // Synchronous bitbang (required by Denkovi)

    // Pin mapping for Denkovi DAE-CB/Ro4-USB (4-channel board uses odd pins: D1, D3, D5, D7)
    // The 4-relay board connects relays to FT245RL IO pins 1, 3, 5, 7 (not 0, 1, 2, 3)
    private static readonly int[] Denkovi4ChPinMap = { 1, 3, 5, 7 };

    /// <summary>
    /// Static constructor to register custom native library resolver for cross-platform support.
    /// </summary>
    static FtdiRelayBoard()
    {
        NativeLibrary.SetDllImportResolver(typeof(FtdiRelayBoard).Assembly, ResolveFtdiLibrary);
    }

    private static IntPtr ResolveFtdiLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Handle both libftdi1 and libusb-1.0 resolution
        if (libraryName != "libftdi1" && libraryName != "libusb-1.0")
            return IntPtr.Zero; // Let default resolver handle it

        IntPtr handle;

        // Try standard resolution first
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out handle))
            return handle;

        // Platform-specific paths
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string[] macPaths;
            if (libraryName == "libftdi1")
            {
                macPaths = new[]
                {
                    "/opt/homebrew/lib/libftdi1.dylib",      // Apple Silicon Homebrew
                    "/opt/homebrew/lib/libftdi1.1.dylib",    // Apple Silicon versioned
                    "/usr/local/lib/libftdi1.dylib",         // Intel Homebrew
                    "/usr/local/lib/libftdi1.1.dylib",       // Intel versioned
                    "libftdi1.dylib",                         // In PATH
                    "libftdi1.1.dylib"
                };
            }
            else // libusb-1.0
            {
                macPaths = new[]
                {
                    "/opt/homebrew/lib/libusb-1.0.dylib",    // Apple Silicon Homebrew
                    "/opt/homebrew/lib/libusb-1.0.0.dylib",  // Apple Silicon versioned
                    "/usr/local/lib/libusb-1.0.dylib",       // Intel Homebrew
                    "/usr/local/lib/libusb-1.0.0.dylib",     // Intel versioned
                    "libusb-1.0.dylib"                        // In PATH
                };
            }

            foreach (var path in macPaths)
            {
                if (NativeLibrary.TryLoad(path, out handle))
                    return handle;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string[] linuxNames;
            if (libraryName == "libftdi1")
            {
                linuxNames = new[]
                {
                    "libftdi1.so.2",
                    "libftdi1.so.1",
                    "libftdi1.so",
                    "/usr/lib/x86_64-linux-gnu/libftdi1.so.2",
                    "/usr/lib/aarch64-linux-gnu/libftdi1.so.2"
                };
            }
            else // libusb-1.0
            {
                linuxNames = new[]
                {
                    "libusb-1.0.so.0",
                    "libusb-1.0.so",
                    "/usr/lib/x86_64-linux-gnu/libusb-1.0.so.0",
                    "/usr/lib/aarch64-linux-gnu/libusb-1.0.so.0"
                };
            }

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

                // Get stable USB path from libusb (bus-port.port.port format)
                var usbPath = GetUsbPath(node.dev);

                devices.Add(new FtdiDeviceInfo(
                    Index: i,
                    SerialNumber: serialStr,
                    Description: descStr ?? $"FTDI Device {i}",
                    IsOpen: false,
                    UsbPath: usbPath
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

    /// <summary>
    /// Open connection to a specific FTDI device by USB path hash.
    /// Used for boards identified by USB port path when serial numbers are not unique.
    /// </summary>
    /// <param name="pathHash">The hash portion of the board ID (e.g., "CA88BCAC" from "FTDI:CA88BCAC")</param>
    /// <returns>True if connection was successful.</returns>
    public bool OpenByPathHash(string pathHash)
    {
        lock (_lock)
        {
            if (_context != IntPtr.Zero)
            {
                _logger?.LogWarning("FTDI device already open");
                return true;
            }

            IntPtr devList = IntPtr.Zero;

            try
            {
                // Use a single context for both enumeration and opening
                // (device pointers from enumeration are only valid for the same context)
                _context = LibFtdi.ftdi_new();
                if (_context == IntPtr.Zero)
                {
                    _logger?.LogError("Failed to create FTDI context");
                    return false;
                }

                // Enumerate all FTDI devices to find the one with matching path hash
                int count = LibFtdi.ftdi_usb_find_all(_context, ref devList, FTDI_VENDOR_ID, FT245RL_PRODUCT_ID);
                if (count <= 0)
                {
                    _logger?.LogWarning("No FTDI devices found when looking for path hash {Hash}", pathHash);
                    LibFtdi.ftdi_free(_context);
                    _context = IntPtr.Zero;
                    return false;
                }

                IntPtr current = devList;
                IntPtr matchingDev = IntPtr.Zero;

                for (int i = 0; i < count && current != IntPtr.Zero; i++)
                {
                    var node = Marshal.PtrToStructure<LibFtdi.ftdi_device_list>(current);

                    // Get USB path for this device
                    var usbPath = GetUsbPath(node.dev);
                    if (!string.IsNullOrEmpty(usbPath))
                    {
                        var deviceHash = FtdiDeviceInfo.StableHash(usbPath);
                        if (deviceHash.Equals(pathHash, StringComparison.OrdinalIgnoreCase))
                        {
                            matchingDev = node.dev;
                            _logger?.LogDebug("Found FTDI device by path hash: {Hash} (path: {Path})", pathHash, usbPath);
                            break;
                        }
                    }

                    current = node.next;
                }

                if (matchingDev == IntPtr.Zero)
                {
                    _logger?.LogWarning("No FTDI device found with path hash {Hash}", pathHash);
                    LibFtdi.ftdi_list_free(ref devList);
                    LibFtdi.ftdi_free(_context);
                    _context = IntPtr.Zero;
                    return false;
                }

                // Open the device by its libusb device pointer (same context used for enumeration)
                int result = LibFtdi.ftdi_usb_open_dev(_context, matchingDev);
                LibFtdi.ftdi_list_free(ref devList);
                devList = IntPtr.Zero;

                if (result < 0)
                {
                    _logger?.LogError("Failed to open FTDI device by path hash {Hash}: error {Result} - {Error}",
                        pathHash, result, GetErrorString());
                    LibFtdi.ftdi_free(_context);
                    _context = IntPtr.Zero;
                    return false;
                }

                if (!Initialize())
                {
                    Close();
                    return false;
                }

                _logger?.LogInformation("FTDI relay board opened (path hash: {Hash})", pathHash);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Exception opening FTDI device by path hash '{Hash}'", pathHash);
                if (devList != IntPtr.Zero)
                {
                    LibFtdi.ftdi_list_free(ref devList);
                }
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
    /// Initialize the device for relay control (synchronous bit-bang mode).
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

            // Set latency timer (important for sync mode)
            result = LibFtdi.ftdi_set_latency_timer(_context, 2);
            if (result < 0)
            {
                _logger?.LogWarning("Failed to set latency timer: {Result}", result);
            }

            // Purge buffers
            result = LibFtdi.ftdi_tcioflush(_context);
            if (result < 0)
            {
                _logger?.LogWarning("Failed to flush FTDI buffers: {Result}", result);
            }

            // Set baud rate (affects bit-bang timing)
            result = LibFtdi.ftdi_set_baudrate(_context, 9600);
            if (result < 0)
            {
                _logger?.LogWarning("Failed to set baud rate: {Result}", result);
            }

            // Enable synchronous bit-bang mode with all pins as outputs
            // Denkovi DAE-CB/Ro8-USB requires sync mode (0x04), not async (0x01)
            result = LibFtdi.ftdi_set_bitmode(_context, PIN_MASK_ALL_OUTPUT, BITMODE_SYNCBB);
            if (result < 0)
            {
                _logger?.LogError("Failed to set sync bit-bang mode: {Result} - {Error}", result, GetErrorString());
                return false;
            }

            _logger?.LogDebug("FTDI sync bit-bang mode enabled");

            // Initialize all relays to OFF
            _currentState = 0x00;
            return WriteState(_currentState);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize FTDI device for sync bit-bang mode");
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
    /// Get the bit mask for a relay channel, accounting for Denkovi board pin mapping.
    /// </summary>
    /// <param name="channel">Channel number (1-based).</param>
    /// <returns>Bit mask for the specified channel.</returns>
    /// <remarks>
    /// Denkovi DAE-CB/Ro8-USB (8-channel): Sequential mapping, relay N uses bit N-1.
    /// Denkovi DAE-CB/Ro4-USB (4-channel): Odd pin mapping, relay N uses bit (2*N - 1).
    /// </remarks>
    private ushort GetBitMaskForChannel(int channel)
    {
        if (_channelCount == 4 && channel >= 1 && channel <= 4)
        {
            // 4-channel Denkovi board uses odd pins: D1, D3, D5, D7
            return (ushort)(1 << Denkovi4ChPinMap[channel - 1]);
        }
        else
        {
            // 8-channel Denkovi board uses sequential pins: D0-D7
            return (ushort)(1 << (channel - 1));
        }
    }

    /// <summary>
    /// Set the state of a specific relay channel.
    /// </summary>
    /// <param name="channel">Channel number (1-8 for 8-ch board, 1-4 for 4-ch board).</param>
    /// <param name="on">True to turn on, false to turn off.</param>
    public bool SetRelay(int channel, bool on)
    {
        if (channel < 1 || channel > _channelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), $"Channel must be 1-{_channelCount}");

        lock (_lock)
        {
            if (_context == IntPtr.Zero)
            {
                _logger?.LogWarning("Cannot set relay - device not open");
                return false;
            }

            // Get the correct bit mask for this channel (handles 4-ch vs 8-ch pin mapping)
            ushort bit = GetBitMaskForChannel(channel);

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
                _logger?.LogDebug("Relay {Channel} set to {State} (bit mask 0x{Bit:X2})", channel, on ? "ON" : "OFF", bit);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Get the state of a specific relay channel.
    /// </summary>
    /// <param name="channel">Channel number (1-8 for 8-ch board, 1-4 for 4-ch board).</param>
    public RelayState GetRelay(int channel)
    {
        if (channel < 1 || channel > _channelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), $"Channel must be 1-{_channelCount}");

        lock (_lock)
        {
            if (_context == IntPtr.Zero)
                return RelayState.Unknown;

            // Use the correct bit mask for this channel (handles 4-ch vs 8-ch pin mapping)
            ushort bit = GetBitMaskForChannel(channel);
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
    /// Read the actual hardware state of all relay pins.
    /// This queries the FTDI chip directly to get the current pin states.
    /// </summary>
    /// <returns>Relay state bitmask from hardware (bit 0 = relay 1), or null if read failed.</returns>
    public byte? ReadHardwareState()
    {
        lock (_lock)
        {
            if (_context == IntPtr.Zero)
            {
                _logger?.LogDebug("Cannot read hardware state - device not open");
                return null;
            }

            try
            {
                int result = LibFtdi.ftdi_read_pins(_context, out byte pins);
                if (result < 0)
                {
                    _logger?.LogWarning("Failed to read FTDI pins: error {Result} - {Error}",
                        result, GetErrorString());
                    return null;
                }

                return pins;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Exception reading FTDI pins");
                return null;
            }
        }
    }

    /// <summary>
    /// Write relay state to the board and verify the write succeeded.
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

            // Verify the write succeeded by reading back hardware state
            int readResult = LibFtdi.ftdi_read_pins(_context, out byte actualState);
            if (readResult >= 0)
            {
                byte expectedLowByte = (byte)(state & 0xFF);
                if (actualState != expectedLowByte)
                {
                    _logger?.LogWarning(
                        "FTDI relay state mismatch: wrote 0x{Expected:X2}, hardware reports 0x{Actual:X2}",
                        expectedLowByte, actualState);
                    return false;
                }
                _logger?.LogDebug("FTDI write verified: 0x{State:X2}", actualState);
            }
            else
            {
                // Verification failed but write may have succeeded - log but don't fail
                _logger?.LogDebug("Could not verify FTDI write (ftdi_read_pins returned {Result})", readResult);
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
        public static extern int ftdi_tcioflush(IntPtr ftdi);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_set_latency_timer(IntPtr ftdi, byte latency);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_set_baudrate(IntPtr ftdi, int baudrate);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_set_bitmode(IntPtr ftdi, byte bitmask, byte mode);

        // Data transfer
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_write_data(IntPtr ftdi, byte[] buf, int size);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_read_data(IntPtr ftdi, [Out] byte[] buf, int size);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftdi_read_pins(IntPtr ftdi, out byte pins);

        // Error handling
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ftdi_get_error_string(IntPtr ftdi);
    }

    /// <summary>
    /// P/Invoke declarations for libusb-1.0.
    /// Used to get USB bus/port path for stable device identification.
    /// libftdi links against libusb, so this library is available wherever FTDI works.
    /// </summary>
    private static class LibUsb
    {
        // Library name varies by platform:
        // - Linux: libusb-1.0.so
        // - macOS: libusb-1.0.dylib (usually via Homebrew)
        private const string LibraryName = "libusb-1.0";

        /// <summary>Get the bus number of a device.</summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern byte libusb_get_bus_number(IntPtr dev);

        /// <summary>
        /// Get the list of port numbers from root for a device.
        /// Returns the number of elements filled, or LIBUSB_ERROR_OVERFLOW on failure.
        /// </summary>
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int libusb_get_port_numbers(IntPtr dev, byte[] port_numbers, int port_numbers_len);
    }

    /// <summary>
    /// Get the USB path (bus-port.port.port) for a libusb device.
    /// This path is stable across reboots as long as the device stays in the same physical port.
    /// </summary>
    /// <param name="libusb_device">Pointer to libusb_device (from ftdi_device_list.dev)</param>
    /// <returns>USB path like "1-3.2" (bus 1, port 3, hub port 2), or null if unavailable.</returns>
    private static string? GetUsbPath(IntPtr libusb_device)
    {
        if (libusb_device == IntPtr.Zero)
            return null;

        try
        {
            byte bus = LibUsb.libusb_get_bus_number(libusb_device);
            byte[] ports = new byte[7]; // USB spec allows max 7 levels of hubs
            int portCount = LibUsb.libusb_get_port_numbers(libusb_device, ports, ports.Length);

            if (portCount <= 0)
                return null;

            // Format: "1-3.2" (bus 1, port 3, hub port 2) - matches Linux sysfs format
            var portPath = string.Join(".", ports.Take(portCount).Select(p => p.ToString()));
            return $"{bus}-{portPath}";
        }
        catch
        {
            // libusb not available or call failed - graceful degradation
            return null;
        }
    }
}
