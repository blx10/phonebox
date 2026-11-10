using System.Globalization;

namespace PhoneBox.Agent;

internal static class DeckMidi
{
    /// <summary>Wire-order bytes, optionally substituting one data byte. System messages and SysEx are excluded.</summary>
    internal static int Parse(string message, int value)
    {
        if (value is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(value));
        var pieces = message.Split([' ', ',', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length is < 2 or > 3) throw new ArgumentException("Use MIDI wire-order bytes, e.g. B0 07 {value}");
        var bytes = pieces.Select(p => p == "{value}" ? (byte)value : byte.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        if (bytes[0] is < 0x80 or >= 0xF0 || bytes.Skip(1).Any(b => b > 127)) throw new ArgumentException("Invalid MIDI channel message");
        var length = (bytes[0] & 0xF0) is 0xC0 or 0xD0 ? 2 : 3;
        if (bytes.Length != length) throw new ArgumentException("Wrong MIDI message length");
        return bytes[0] | bytes[1] << 8 | (bytes.Length == 3 ? bytes[2] << 16 : 0);
    }
}
