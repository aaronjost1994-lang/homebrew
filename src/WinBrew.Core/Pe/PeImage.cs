using System.Buffers.Binary;

namespace WinBrew.Core.Pe;

/// <summary>PE subsystem class that decides shim behavior (D4): console waits, GUI does not.</summary>
public enum PeSubsystemKind
{
    Console,
    Gui,
    Other,
}

/// <summary>Parsed PE header facts WinBrew needs (A10, A11 step 6, D4).</summary>
public sealed record PeInfo(ushort Machine, ushort Subsystem)
{
    public PeSubsystemKind SubsystemKind => Subsystem switch
    {
        PeImage.SubsystemWindowsCui => PeSubsystemKind.Console,
        PeImage.SubsystemWindowsGui => PeSubsystemKind.Gui,
        _ => PeSubsystemKind.Other,
    };
}

/// <summary>
/// Minimal, bounds-checked PE header parser (no I/O: callers pass the first
/// bytes of the file). Reads the DOS header, the PE signature, the COFF
/// machine type and the optional-header subsystem (PE32 and PE32+).
/// </summary>
public static class PeImage
{
    public const ushort MachineI386 = 0x014C;
    public const ushort MachineAmd64 = 0x8664;
    public const ushort MachineArm64 = 0xAA64;
    public const ushort MachineArmNt = 0x01C4;

    public const ushort SubsystemWindowsGui = 2;
    public const ushort SubsystemWindowsCui = 3;

    private const ushort Pe32Magic = 0x10B;
    private const ushort Pe32PlusMagic = 0x20B;

    /// <summary>Bytes the caller should read from the start of the file (covers any realistic e_lfanew).</summary>
    public const int HeaderProbeLength = 4096;

    /// <summary>Parses the header bytes; null when the data is not a valid PE image.</summary>
    public static PeInfo? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x40 || data[0] != (byte)'M' || data[1] != (byte)'Z')
        {
            return null;
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(data[0x3C..]);
        // signature(4) + COFF header(20) + optional header magic(2) ... subsystem at +68 of optional header.
        if (peOffset < 0x40 || peOffset > data.Length - 24)
        {
            return null;
        }

        if (data[peOffset] != (byte)'P' || data[peOffset + 1] != (byte)'E' || data[peOffset + 2] != 0 || data[peOffset + 3] != 0)
        {
            return null;
        }

        var coff = peOffset + 4;
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(data[coff..]);
        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data[(coff + 16)..]);
        var optional = coff + 20;

        if (optionalHeaderSize < 70 || optional + 70 > data.Length)
        {
            return null;
        }

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(data[optional..]);
        if (magic is not (Pe32Magic or Pe32PlusMagic))
        {
            return null;
        }

        // Subsystem sits at offset 68 in both PE32 and PE32+ optional headers.
        var subsystem = BinaryPrimitives.ReadUInt16LittleEndian(data[(optional + 68)..]);
        return new PeInfo(machine, subsystem);
    }
}
