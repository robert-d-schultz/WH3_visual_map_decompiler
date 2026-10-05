using System.Text;

namespace TwMapDecompiler.Core.IO;

public static class BinaryReaderExtensions
{
    /// <summary>Reads a UInt16 length prefix followed by that many UTF-8 bytes.</summary>
    public static string ReadU16String(this BinaryReader reader)
    {
        int length = reader.ReadUInt16();
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    public static string ReadNullTerminatedString(this BinaryReader reader)
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = reader.ReadByte()) != 0)
            bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public static void Skip(this BinaryReader reader, int count)
    {
        reader.BaseStream.Seek(count, SeekOrigin.Current);
    }

    public static bool ReadBool8(this BinaryReader reader) => reader.ReadByte() == 1;
}
