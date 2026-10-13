using System.Buffers;
using System.Text;

namespace Codalus.Workbench.Aspire;

/// <summary>
/// The protocol buffers wire format, as much as the Aspire resource service needs (M17b, ADR 0043): varints,
/// length-delimited fields and fixed-width fields skipped. Messages are read field by field; unknown fields are
/// skipped, so newer Aspire versions that add fields still read.
/// </summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly bool AtEnd => _position >= _data.Length;

    /// <summary>The next field's number and wire type (0 varint, 1 fixed64, 2 length-delimited, 5 fixed32); false at the end.</summary>
    public bool Next(out int field, out int wireType)
    {
        if (AtEnd)
        {
            field = 0;
            wireType = 0;
            return false;
        }

        var key = ReadVarint();
        field = (int)(key >> 3);
        wireType = (int)(key & 7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (_position >= _data.Length)
            {
                throw new InvalidDataException("A truncated protocol buffers varint.");
            }

            var b = _data[_position++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("A protocol buffers varint longer than 64 bits.");
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = (int)ReadVarint();
        if (length < 0 || _position + length > _data.Length)
        {
            throw new InvalidDataException("A truncated protocol buffers field.");
        }

        var bytes = _data.Slice(_position, length);
        _position += length;
        return bytes;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

    public ulong ReadFixed64()
    {
        if (_position + 8 > _data.Length)
        {
            throw new InvalidDataException("A truncated protocol buffers fixed64.");
        }

        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_position, 8));
        _position += 8;
        return value;
    }

    public bool ReadBool() => ReadVarint() != 0;

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint();
                break;
            case 1:
                _position += 8;
                break;
            case 2:
                ReadBytes();
                break;
            case 5:
                _position += 4;
                break;
            default:
                throw new InvalidDataException($"Unsupported protocol buffers wire type {wireType}.");
        }
    }
}

/// <summary>Writes the protocol buffers wire format: strings, booleans and embedded messages.</summary>
internal sealed class ProtoWriter
{
    private readonly System.Buffers.ArrayBufferWriter<byte> _stream = new();

    public ProtoWriter String(int field, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Bytes(field, Encoding.UTF8.GetBytes(value));
        }

        return this;
    }

    public ProtoWriter Bool(int field, bool value)
    {
        if (value)
        {
            Varint(((ulong)field << 3) | 0);
            Varint(1);
        }

        return this;
    }

    public ProtoWriter Bytes(int field, ReadOnlySpan<byte> value)
    {
        Varint(((ulong)field << 3) | 2);
        Varint((ulong)value.Length);
        _stream.Write(value);
        return this;
    }

    private void Varint(ulong value)
    {
        while (value >= 0x80)
        {
            _stream.Write([(byte)(value | 0x80)]);
            value >>= 7;
        }

        _stream.Write([(byte)value]);
    }

    public byte[] ToArray() => _stream.WrittenSpan.ToArray();
}

/// <summary>Reads <c>google.protobuf.Value</c> (a JSON-like value) as text, for resource properties.</summary>
internal static class ProtoValue
{
    public static string? Read(ReadOnlySpan<byte> data)
    {
        var reader = new ProtoReader(data);
        string? result = null;
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 0: // null_value
                    reader.ReadVarint();
                    result = null;
                    break;
                case 2 when wire == 1: // number_value (double)
                    result = BitConverter.Int64BitsToDouble((long)reader.ReadFixed64()).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case 3 when wire == 2: // string_value
                    result = reader.ReadString();
                    break;
                case 4 when wire == 0: // bool_value
                    result = reader.ReadBool() ? "true" : "false";
                    break;
                case 6 when wire == 2: // list_value
                    var list = new ProtoReader(reader.ReadBytes());
                    var items = new List<string>();
                    while (list.Next(out var item, out var itemWire))
                    {
                        if (item == 1 && itemWire == 2)
                        {
                            items.Add(Read(list.ReadBytes()) ?? "null");
                        }
                        else
                        {
                            list.Skip(itemWire);
                        }
                    }

                    result = string.Join(", ", items);
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return result;
    }
}
