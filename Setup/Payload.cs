using System;
using System.IO;
using System.Text;

namespace PdfReaderSetup;

/// <summary>
/// The installer is this exe with the zipped app appended: <c>[exe][zip][zip length: 8 bytes][magic: 16 bytes]</c>.
/// The uninstaller is a copy of just the exe part, so it does not carry the app again.
/// </summary>
internal static class Payload
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("XTPDFRSETUPV1ZIP");

    /// <summary>Length of the exe part and the zip bytes; null when nothing is appended (the uninstaller copy).</summary>
    internal static (long ExeLength, byte[] Zip)? Read(string exePath)
    {
        using var file = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        const int tail = 8 + 16;
        if (file.Length < tail) return null;
        file.Seek(-tail, SeekOrigin.End);
        var footer = new byte[tail];
        file.ReadExactly(footer);
        if (!footer.AsSpan(8).SequenceEqual(Magic)) return null;
        long zipLength = BitConverter.ToInt64(footer, 0);
        long exeLength = file.Length - tail - zipLength;
        if (zipLength <= 0 || exeLength <= 0) return null;
        file.Seek(exeLength, SeekOrigin.Begin);
        var zip = new byte[zipLength];
        file.ReadExactly(zip);
        return (exeLength, zip);
    }

    /// <summary>The exe part of this installer (what the uninstaller copy consists of).</summary>
    internal static void CopyStub(string exePath, string destination, long exeLength)
    {
        using var source = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var target = new FileStream(destination, FileMode.Create, FileAccess.Write);
        var buffer = new byte[1 << 16];
        long left = exeLength;
        while (left > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read <= 0) break;
            target.Write(buffer, 0, read);
            left -= read;
        }
    }

    /// <summary>Exe length of a file with no payload appended (the whole file).</summary>
    internal static long StubLength(string exePath) => Read(exePath) is { } p ? p.ExeLength : new FileInfo(exePath).Length;
}
