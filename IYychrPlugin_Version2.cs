using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using Yychr;

/// <summary>
/// YY-CHR plugin for extracting / reinserting NES 2bpp CHR data from/to an iNES ROM (typical NES .nes files).
/// - Read(...) extracts the CHR-ROM region from a .nes file and writes the raw 2bpp CHR bytes to args.DestinationStream.
/// - Write(...) reinserts provided CHR data into the ROM and writes the updated ROM to args.DestinationStream.
/// 
/// Notes:
/// - This implementation understands the iNES header (16 bytes) and optional 512-byte trainer.
/// - CHR-ROM is in 8 KB banks. Each tile is 16 bytes (8 bytes plane 0, then 8 bytes plane 1).
/// - Some cartridges have CHR-RAM (header indicates 0 CHR banks) — in that case Read will throw.
/// - For Write(...), you must supply the new CHR bytes to be written back into the ROM. This plugin looks for that stream/bytes
///   in args.Parameters["NewChr"] (either Stream or byte[]). If you can't pass it that way, tell me how your PluginArgs is shaped
///   and I can adapt the code.
/// </summary>
public class Smb3Format : IYychrPlugin
{
    public string Name => "SMB3 / NES iNES CHR extractor";

    // The public API expected by YY-CHR: Read should load data to DestinationStream (the editor).
    public void Read(PluginArgs args)
    {
        if (args == null) throw new ArgumentNullException(nameof(args));

        Stream rom = args.SourceStream ?? throw new ArgumentException("SourceStream is required for Read.");
        Stream outChr = args.DestinationStream ?? throw new ArgumentException("DestinationStream is required for Read.");

        ExtractChrFromRom(rom, outChr);
        outChr.Flush();
    }

    // Write should take modified CHR data and write it back into the ROM image.
    // This implementation expects the new CHR bytes to be passed in args.Parameters["NewChr"] as either a Stream or byte[].
    public void Write(PluginArgs args)
    {
        if (args == null) throw new ArgumentNullException(nameof(args));

        Stream rom = args.SourceStream ?? throw new ArgumentException("SourceStream is required for Write (original ROM).");
        Stream outRom = args.DestinationStream ?? throw new ArgumentException("DestinationStream is required for Write (output ROM).");

        // Try to obtain new CHR data from args.Parameters["NewChr"]
        Stream newChrStream = null;
        byte[] newChrBytes = null;

        // Try a few ways to get the new CHR from PluginArgs.Parameters if available
        object paramObj = null;
        try
        {
            // If PluginArgs has a Parameters dictionary-like object, try to read "NewChr"
            var parametersProp = args.GetType().GetProperty("Parameters");
            if (parametersProp != null)
            {
                var parameters = parametersProp.GetValue(args) as IDictionary;
                if (parameters != null && parameters.Contains("NewChr"))
                {
                    paramObj = parameters["NewChr"];
                }
                else if (parameters != null && parameters.Contains("newChr"))
                {
                    paramObj = parameters["newChr"];
                }
            }
        }
        catch
        {
            // ignore reflection errors - we'll try other approaches below
        }

        // Fallback: maybe PluginArgs exposes NewChr / NewData directly
        if (paramObj == null)
        {
            var newChrProp = args.GetType().GetProperty("NewChr") ?? args.GetType().GetProperty("NewData");
            if (newChrProp != null)
            {
                paramObj = newChrProp.GetValue(args);
            }
        }

        // Inspect paramObj type
        if (paramObj is Stream s) newChrStream = s;
        else if (paramObj is byte[] b) newChrBytes = b;
        else if (paramObj != null)
        {
            // maybe it's MemoryStream-like; try to convert via ToArray if possible
            var toArrayMethod = paramObj.GetType().GetMethod("ToArray");
            if (toArrayMethod != null)
            {
                try
                {
                    var arr = toArrayMethod.Invoke(paramObj, null) as byte[];
                    if (arr != null) newChrBytes = arr;
                }
                catch { }
            }
        }

        if (newChrStream == null && newChrBytes == null)
        {
            throw new ArgumentException("Write requires the new CHR data to be provided in args.Parameters[\"NewChr\"] as a Stream or byte[]. If you can't provide it that way, tell me how your PluginArgs is structured and I'll adapt the plugin.");
        }

        ReplaceChrInRom(rom, newChrStream ?? new MemoryStream(newChrBytes), outRom);
        outRom.Flush();
    }

    // Helper: extract CHR region from an iNES ROM stream and copy it to destination
    public static void ExtractChrFromRom(Stream rom, Stream destination)
    {
        if (!rom.CanSeek) throw new ArgumentException("Source ROM stream must support seeking.");
        rom.Position = 0;

        byte[] header = new byte[16];
        int read = rom.Read(header, 0, 16);
        if (read < 16) throw new InvalidDataException("Not a valid iNES file (too small).");

        // Validate iNES signature "NES\x1A"
        if (header[0] != 'N' || header[1] != 'E' || header[2] != 'S' || header[3] != 0x1A)
        {
            // Not an iNES header. Assume the entire file is raw CHR data and copy it.
            rom.Position = 0;
            rom.CopyTo(destination);
            return;
        }

        bool hasTrainer = (header[6] & 0x04) != 0;
        int prgBanks = header[4]; // 16 KB units
        int chrBanks = header[5]; // 8 KB units

        if (chrBanks == 0)
            throw new InvalidOperationException("This ROM indicates CHR-RAM (no CHR-ROM present in the file).");

        long trainerSize = hasTrainer ? 512 : 0;
        long prgSize = (long)prgBanks * 16 * 1024;
        long chrSize = (long)chrBanks * 8 * 1024;
        long chrOffset = 16 + trainerSize + prgSize;

        if (rom.Length < chrOffset + chrSize)
            throw new InvalidDataException("ROM too small for claimed PRG/CHR sizes from header.");

        rom.Seek(chrOffset, SeekOrigin.Begin);

        // Copy CHR bytes
        const int bufferSize = 8192;
        byte[] buffer = new byte[bufferSize];
        long remaining = chrSize;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(bufferSize, remaining);
            int actuallyRead = rom.Read(buffer, 0, toRead);
            if (actuallyRead == 0) throw new EndOfStreamException("Unexpected end of ROM while reading CHR.");
            destination.Write(buffer, 0, actuallyRead);
            remaining -= actuallyRead;
        }
    }

    // Helper: replace CHR region in rom with newChr stream and write the resulting ROM to outRom
    public static void ReplaceChrInRom(Stream rom, Stream newChr, Stream outRom)
    {
        if (!rom.CanSeek) throw new ArgumentException("Source ROM stream must support seeking.");
        if (!newChr.CanSeek) newChr = CopyToMemoryStream(newChr); // make seekable for length checks
        rom.Position = 0;
        newChr.Position = 0;

        byte[] header = new byte[16];
        int read = rom.Read(header, 0, 16);
        if (read < 16) throw new InvalidDataException("Not a valid iNES file (too small).");

        if (header[0] != 'N' || header[1] != 'E' || header[2] != 'S' || header[3] != 0x1A)
        {
            throw new InvalidOperationException("ReplaceChrInRom expects an iNES ROM with header. For raw CHR files use a raw write path instead.");
        }

        bool hasTrainer = (header[6] & 0x04) != 0;
        int prgBanks = header[4]; // 16 KB units
        int chrBanks = header[5]; // 8 KB units

        long trainerSize = hasTrainer ? 512 : 0;
        long prgSize = (long)prgBanks * 16 * 1024;
        long chrSize = (long)chrBanks * 8 * 1024;
        long chrOffset = 16 + trainerSize + prgSize;

        if (newChr.Length != chrSize)
            throw new InvalidOperationException($"New CHR data size ({newChr.Length}) does not match CHR-ROM size in the ROM ({chrSize}).");

        // Copy ROM up to CHR region
        rom.Position = 0;
        CopyBytes(rom, outRom, chrOffset);

        // Write new CHR
        newChr.Position = 0;
        newChr.CopyTo(outRom);

        // Skip old CHR in input ROM and copy the rest (if any)
        rom.Position = chrOffset + chrSize;
        rom.CopyTo(outRom);
    }

    // Utility: copy exact count
    private static void CopyBytes(Stream src, Stream dst, long count)
    {
        const int bufSize = 8192;
        byte[] buffer = new byte[bufSize];
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(bufSize, remaining);
            int r = src.Read(buffer, 0, toRead);
            if (r == 0) throw new EndOfStreamException("Unexpected end of stream while copying.");
            dst.Write(buffer, 0, r);
            remaining -= r;
        }
    }

    // Make a seekable MemoryStream copy of a non-seekable stream
    private static MemoryStream CopyToMemoryStream(Stream s)
    {
        var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }
}