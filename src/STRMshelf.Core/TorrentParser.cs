using System.Security.Cryptography;
using System.Text;

namespace STRMshelf.Core;

public sealed class TorrentParser
{
    static TorrentParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public TorrentMetadata Parse(string path) => Parse(File.ReadAllBytes(path));

    public TorrentMetadata Parse(ReadOnlyMemory<byte> metainfo)
    {
        var parser = new BencodeReader(metainfo);
        var root = parser.ReadDictionary(captureInfo: true);
        var info = (Dictionary<string, object>)root["info"];
        var name = ReadText(info, "name.utf-8", "name");
        var files = ReadFiles(info);
        // BitTorrent v1 hashes the original bencoded info slice, not a re-encoded dictionary.
        var infoHash = Convert.ToHexString(
            SHA1.HashData(metainfo.Span[parser.InfoStart..parser.InfoEnd])).ToLowerInvariant();
        return new TorrentMetadata(name, infoHash, files);
    }

    private static IReadOnlyList<TorrentFile> ReadFiles(Dictionary<string, object> info)
    {
        if (!info.TryGetValue("files", out var rawFiles))
            return [new TorrentFile(1, ReadText(info, "name.utf-8", "name"), (long)info["length"])];

        return ((List<object>)rawFiles).Select((raw, index) =>
        {
            var file = (Dictionary<string, object>)raw;
            var path = ReadList(file, "path.utf-8", "path").Cast<byte[]>().Select(DecodeText);
            // TorrServer addresses files with one-based indexes.
            return new TorrentFile(index + 1, string.Join("/", path), (long)file["length"]);
        }).ToArray();
    }

    private static List<object> ReadList(Dictionary<string, object> value, params string[] keys)
    {
        foreach (var key in keys)
            if (value.TryGetValue(key, out var result))
                return (List<object>)result;
        throw new InvalidDataException($"Torrent metadata does not contain '{string.Join("' or '", keys)}'.");
    }

    private static string ReadText(Dictionary<string, object> value, params string[] keys)
    {
        foreach (var key in keys)
            if (value.TryGetValue(key, out var result))
                return DecodeText((byte[])result);
        throw new InvalidDataException($"Torrent metadata does not contain '{string.Join("' or '", keys)}'.");
    }

    private static string DecodeText(byte[] value)
    {
        try { return new UTF8Encoding(false, true).GetString(value); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1251).GetString(value); }
    }

    private sealed class BencodeReader(ReadOnlyMemory<byte> data)
    {
        private int _position;
        public int InfoStart { get; private set; }
        public int InfoEnd { get; private set; }

        public Dictionary<string, object> ReadDictionary(bool captureInfo = false)
        {
            Expect((byte)'d');
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            while (Peek() != 'e')
            {
                var key = Encoding.UTF8.GetString(ReadBytes());
                var start = _position;
                result[key] = ReadValue();
                if (captureInfo && key == "info")
                    (InfoStart, InfoEnd) = (start, _position);
            }
            _position++;
            return result;
        }

        private object ReadValue() => Peek() switch
        {
            (byte)'d' => ReadDictionary(),
            (byte)'l' => ReadList(),
            (byte)'i' => ReadInteger(),
            >= (byte)'0' and <= (byte)'9' => ReadBytes(),
            _ => throw new InvalidDataException($"Invalid bencode value at offset {_position}.")
        };

        private List<object> ReadList()
        {
            Expect((byte)'l');
            var result = new List<object>();
            while (Peek() != 'e')
                result.Add(ReadValue());
            _position++;
            return result;
        }

        private long ReadInteger()
        {
            Expect((byte)'i');
            var end = data.Span[_position..].IndexOf((byte)'e');
            if (end < 0) throw new InvalidDataException("Unterminated bencode integer.");
            var value = long.Parse(Encoding.ASCII.GetString(data.Span.Slice(_position, end)));
            _position += end + 1;
            return value;
        }

        private byte[] ReadBytes()
        {
            var separator = data.Span[_position..].IndexOf((byte)':');
            if (separator < 0) throw new InvalidDataException("Invalid bencode string.");
            var length = int.Parse(Encoding.ASCII.GetString(data.Span.Slice(_position, separator)));
            _position += separator + 1;
            if (_position + length > data.Length) throw new InvalidDataException("Bencode string exceeds source.");
            var result = data.Span.Slice(_position, length).ToArray();
            _position += length;
            return result;
        }

        private byte Peek() => _position < data.Length ? data.Span[_position] : throw new EndOfStreamException();
        private void Expect(byte value)
        {
            if (Peek() != value) throw new InvalidDataException($"Expected '{(char)value}' at offset {_position}.");
            _position++;
        }
    }
}
