#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BmwDecode.Core.Diagnostics;
using EdiabasLib;

namespace BmwDecode.Ediabas;

public sealed partial class EdiabasEngine
{
    private static EdInterfaceBase CreateInterface(ConnectionSettings settings) => settings.Kind switch
    {
        // INPA-style K+DCAN cable on its virtual COM port. The FTDI latency timer should be 1 ms.
        InterfaceKind.KDcanSerial => new EdInterfaceObd { ComPort = NormalizeComPort(settings.Port) },

        // ENET cable: "auto" lets EdiabasLib discover the vehicle, otherwise an IP address.
        InterfaceKind.Enet => new EdInterfaceEnet { RemoteHost = string.IsNullOrWhiteSpace(settings.Port) ? "auto" : settings.Port.Trim() },

        _ => throw new DiagnosticException($"Interface {settings.Kind} is not handled by EDIABAS.", "BMWDECODE_INTERFACE"),
    };

    /// <summary>"3" or "com3" becomes COM3 on Windows; device paths (/dev/cu.usbserial-…) are kept as typed.</summary>
    internal static string NormalizeComPort(string port)
    {
        var p = (port ?? string.Empty).Trim();
        if (p.StartsWith('/'))
        {
            return p;
        }

        if (!OperatingSystem.IsWindows())
        {
            // A bare device name on macOS / Linux.
            return p.Length == 0 ? p : "/dev/" + p;
        }

        p = p.ToUpperInvariant();
        return int.TryParse(p, out var n) ? $"COM{n}" : p;
    }

    /// <summary>Serial ports plus FTDI devices for the interface picker.</summary>
    public static IReadOnlyList<string> AvailablePorts()
    {
        var ports = new List<string>();
        try
        {
            var names = System.IO.Ports.SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase);
            if (OperatingSystem.IsMacOS())
            {
                // macOS lists each device twice (tty.* and cu.*): cu.* is the one to open for outgoing use.
                // A K+DCAN cable on Apple's built-in FTDI driver shows up as /dev/cu.usbserial-XXXXXXXX.
                names = names.Where(n => n.StartsWith("/dev/cu.", StringComparison.Ordinal)
                                         && !n.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                                         && !n.Contains("debug-console", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(n => n.Contains("usbserial", StringComparison.OrdinalIgnoreCase));
                ports.AddRange(names);
            }
            else
            {
                ports.AddRange(names.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception)
        {
            // No serial subsystem: leave empty.
        }

        return ports;
    }
}
