// Copyright (c) galtools contributors. MIT.
// FUYINPAK is the plain BMP archive used by the Soreyori Chinese image patch.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Galtools
{
    public sealed class FuyinPakBackend : IArchiveBackend
    {
        private sealed class Location { public long Offset; public int Length; }
        private readonly object gate = new object();
        private FileStream stream;
        private List<Location> locations;
        public string Id { get { return "fuyin-pak"; } }
        public string Description { get { return "FUYINPAK 图像封包"; } }
        public List<ArchiveEntry> Entries { get; private set; }

        public void Open(string path, string scheme)
        {
            Dispose();
            var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                using (var reader = new BinaryReader(input, Encoding.ASCII, true))
                {
                    if (input.Length < 24 || Encoding.ASCII.GetString(reader.ReadBytes(8)) != "FUYINPAK")
                        throw new InvalidDataException("不是 FUYINPAK 封包");
                    uint dataStart = reader.ReadUInt32();
                    uint reserved = reader.ReadUInt32();
                    uint indexOffset = reader.ReadUInt32();
                    uint indexLength = reader.ReadUInt32();
                    if (dataStart != 24 || reserved != 0 || indexLength == 0 || indexLength % 20 != 0
                        || (long)indexOffset + indexLength != input.Length || indexLength / 20 > 1000000)
                        throw new InvalidDataException("FUYINPAK 索引无效");
                    var entries = new List<ArchiveEntry>((int)indexLength / 20);
                    var positions = new List<Location>(entries.Capacity);
                    input.Position = indexOffset;
                    for (int i = 0; i < entries.Capacity; ++i)
                    {
                        uint nameOffset = reader.ReadUInt32();
                        uint nameLength = reader.ReadUInt32();
                        uint offset = reader.ReadUInt32();
                        uint size = reader.ReadUInt32();
                        uint unpacked = reader.ReadUInt32();
                        if (nameLength < 2 || nameLength > 4096 || (long)nameOffset + nameLength > indexOffset
                            || offset < dataStart || (long)offset + size > indexOffset || size != unpacked
                            || size > int.MaxValue)
                            throw new InvalidDataException("FUYINPAK 条目边界无效");
                        long resume = input.Position;
                        input.Position = nameOffset;
                        byte[] nameBytes = reader.ReadBytes((int)nameLength);
                        if (nameBytes.Length != nameLength || nameBytes[nameBytes.Length - 1] != 0)
                            throw new InvalidDataException("FUYINPAK 文件名无效");
                        string name = Encoding.GetEncoding(932).GetString(nameBytes, 0, nameBytes.Length - 1);
                        input.Position = resume;
                        entries.Add(new ArchiveEntry { Index = i, Name = name,
                            Type = Path.GetExtension(name).Equals(".bmp", StringComparison.OrdinalIgnoreCase) ? "image" : "",
                            Size = size, PackedSize = size });
                        positions.Add(new Location { Offset = offset, Length = (int)size });
                    }
                    Entries = entries;
                    locations = positions;
                    stream = input;
                }
            }
            catch { input.Dispose(); throw; }
        }

        private byte[] ReadRaw(int index)
        {
            lock (gate)
            {
                if (stream == null || index < 0 || index >= locations.Count) throw new InvalidOperationException("封包未打开或条目无效");
                var position = locations[index];
                byte[] data = new byte[position.Length];
                stream.Position = position.Offset;
                int done = 0;
                while (done < data.Length)
                {
                    int n = stream.Read(data, done, data.Length - done);
                    if (n == 0) throw new EndOfStreamException();
                    done += n;
                }
                return data;
            }
        }

        private byte[] ReadPng(int index, bool preview)
        {
            using (var source = new MemoryStream(ReadRaw(index)))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (preview) bitmap.DecodePixelHeight = 480;
                bitmap.StreamSource = source; bitmap.EndInit();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = new MemoryStream()) { encoder.Save(output); return output.ToArray(); }
            }
        }

        public byte[] ReadImagePng(int index) { return ReadPng(index, false); }
        public byte[] ReadImagePreview(int index) { return ReadPng(index, true); }
        public byte[] ReadAudioWav(int index) { throw new NotSupportedException("此封包没有音频解码器"); }

        public void Save(string command, int index, string target)
        {
            if (command != "R" && command != "I" && command != "P") throw new NotSupportedException("此封包不支持该转换");
            string temporary = target + ".galtools-partial";
            try
            {
                byte[] data = command == "R" ? ReadRaw(index) : ReadPng(index, command == "P");
                File.WriteAllBytes(temporary, data);
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (stream != null) stream.Dispose();
                stream = null; locations = null; Entries = null;
            }
        }
    }
}
