using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace XTPdfMergeApp.Licensing;

/// <summary>A stable id of this Windows installation: SHA-256 of the machine GUID and the CPU name, as 25 upper-case hex digits.</summary>
internal static class MachineId
{
    private static string? _cached;

    internal static string Get() => _cached ??= Compute();

    internal static string DeviceName() => Environment.MachineName;

    private static string Compute()
    {
        string guid = Read(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid");
        string cpu = Read(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{guid}|{cpu}|xtreader"));
        return Convert.ToHexString(hash)[..25];
    }

    private static string Read(string subKey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(name)?.ToString() ?? "";
        }
        catch { return ""; }
    }
}
