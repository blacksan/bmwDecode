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

    private static string NormalizeComPort(string port)
    {
        var p = (port ?? string.Empty).Trim().ToUpperInvariant();
        return int.TryParse(p, out var n) ? $"COM{n}" : p;
    }

    /// <summary>Serial ports plus FTDI devices for the interface picker.</summary>
    public static IReadOnlyList<string> AvailablePorts()
    {
        var ports = new List<string>();
        try
        {
            ports.AddRange(System.IO.Ports.SerialPort.GetPortNames()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // No serial subsystem: leave empty.
        }

        return ports;
    }
}
