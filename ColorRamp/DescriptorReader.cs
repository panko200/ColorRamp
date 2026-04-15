using System.IO;
using System.Text;

namespace ColorRamp;

internal static class DescriptorReader
{
    internal static Dictionary<string, object?>? ReadDescriptor(BinaryReader reader)
    {
        try
        {
            return ReadDescriptorCore(reader);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?> ReadDescriptorCore(BinaryReader reader)
    {
        var name = ReadUnicodeString(reader);
        var classId = ReadId(reader);
        var count = ReadBigEndianInt32(reader);

        var result = new Dictionary<string, object?>
        {
            ["_name"] = name,
            ["_class"] = classId
        };

        for (var i = 0; i < count; i++)
        {
            var key = ReadId(reader);
            var typeTag = Encoding.ASCII.GetString(reader.ReadBytes(4));
            result[key] = ReadValue(reader, typeTag);
        }

        return result;
    }

    private static object? ReadValue(BinaryReader reader, string typeTag)
    {
        return typeTag switch
        {
            "Objc" or "GlbO" => ReadDescriptorCore(reader),
            "VlLs" => ReadValueList(reader),
            "doub" => ReadBigEndianDouble(reader),
            "UntF" => ReadUnitFloat(reader),
            "TEXT" => ReadUnicodeString(reader),
            "enum" => ReadEnumerated(reader),
            "long" => ReadBigEndianInt32(reader),
            "bool" => reader.ReadByte() != 0,
            "tdta" => ReadRawData(reader),
            "type" or "GlbC" => ReadClass(reader),
            "alis" => ReadRawData(reader),
            "obj " => ReadReference(reader),
            _ => null
        };
    }

    private static List<object?> ReadValueList(BinaryReader reader)
    {
        var count = ReadBigEndianInt32(reader);
        var list = new List<object?>(count);
        for (var i = 0; i < count; i++)
        {
            var typeTag = Encoding.ASCII.GetString(reader.ReadBytes(4));
            list.Add(ReadValue(reader, typeTag));
        }
        return list;
    }

    private static (string Type, string Value) ReadEnumerated(BinaryReader reader)
    {
        var type = ReadId(reader);
        var value = ReadId(reader);
        return (type, value);
    }

    private static (string Unit, double Value) ReadUnitFloat(BinaryReader reader)
    {
        var unit = Encoding.ASCII.GetString(reader.ReadBytes(4));
        var value = ReadBigEndianDouble(reader);
        return (unit, value);
    }

    private static (string Name, string ClassId) ReadClass(BinaryReader reader)
    {
        var name = ReadUnicodeString(reader);
        var classId = ReadId(reader);
        return (name, classId);
    }

    private static object? ReadReference(BinaryReader reader)
    {
        var count = ReadBigEndianInt32(reader);
        for (var i = 0; i < count; i++)
        {
            var refType = Encoding.ASCII.GetString(reader.ReadBytes(4));
            switch (refType)
            {
                case "prop":
                    ReadUnicodeString(reader);
                    ReadId(reader);
                    ReadId(reader);
                    break;
                case "Clss":
                    ReadUnicodeString(reader);
                    ReadId(reader);
                    break;
                case "Enmr":
                    ReadUnicodeString(reader);
                    ReadId(reader);
                    ReadId(reader);
                    ReadId(reader);
                    break;
                case "rele":
                    ReadUnicodeString(reader);
                    ReadId(reader);
                    ReadBigEndianInt32(reader);
                    break;
                case "Idnt":
                    ReadBigEndianInt32(reader);
                    break;
                case "indx":
                    ReadBigEndianInt32(reader);
                    break;
                case "name":
                    ReadUnicodeString(reader);
                    ReadId(reader);
                    ReadUnicodeString(reader);
                    break;
            }
        }
        return null;
    }

    private static byte[]? ReadRawData(BinaryReader reader)
    {
        var length = ReadBigEndianInt32(reader);
        return length > 0 ? reader.ReadBytes(length) : null;
    }

    private static string ReadUnicodeString(BinaryReader reader)
    {
        var charCount = ReadBigEndianInt32(reader);
        if (charCount <= 0) return string.Empty;

        var sb = new StringBuilder(charCount);
        for (var i = 0; i < charCount; i++)
        {
            var c = (char)ReadBigEndianUInt16(reader);
            if (c != '\0') sb.Append(c);
        }
        return sb.ToString();
    }

    private static string ReadId(BinaryReader reader)
    {
        var length = ReadBigEndianInt32(reader);
        if (length == 0) length = 4;
        return Encoding.ASCII.GetString(reader.ReadBytes(length));
    }

    private static int ReadBigEndianInt32(BinaryReader reader)
    {
        var buf = reader.ReadBytes(4);
        return (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
    }

    private static ushort ReadBigEndianUInt16(BinaryReader reader)
    {
        var buf = reader.ReadBytes(2);
        return (ushort)((buf[0] << 8) | buf[1]);
    }

    private static double ReadBigEndianDouble(BinaryReader reader)
    {
        var buf = reader.ReadBytes(8);
        if (BitConverter.IsLittleEndian) Array.Reverse(buf);
        return BitConverter.ToDouble(buf, 0);
    }
}
