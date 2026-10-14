using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Galtools
{
    public sealed class ArchiveEntry
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public long Size { get; set; }
        public long PackedSize { get; set; }
    }

    public interface IArchiveBackend : IDisposable
    {
        string Id { get; }
        string Description { get; }
        List<ArchiveEntry> Entries { get; }
        void Open(string archive, string scheme);
        byte[] ReadImagePng(int index);
        byte[] ReadImagePreview(int index);
        byte[] ReadAudioWav(int index);
        void Save(string command, int index, string target);
    }

    public static class BackendRegistry
    {
        private static readonly List<Func<IArchiveBackend>> Factories = new List<Func<IArchiveBackend>> {
            delegate { return new FuyinPakBackend(); },
            delegate { return new GarbroModClient(); }
        };

        public static void Register(Func<IArchiveBackend> factory)
        {
            if (factory == null) throw new ArgumentNullException("factory");
            lock (Factories) Factories.Add(factory);
        }

        public static IArchiveBackend Open(string path, string scheme)
        {
            Exception last = null;
            Func<IArchiveBackend>[] factories;
            lock (Factories) factories = Factories.ToArray();
            foreach (var factory in factories)
            {
                IArchiveBackend backend = factory();
                try { backend.Open(path, scheme); return backend; }
                catch (Exception ex) { last = ex; backend.Dispose(); }
            }
            throw new InvalidDataException(last == null ? "没有可用的解包后端" : last.Message, last);
        }
    }

    public sealed class GarbroModClient : IArchiveBackend
    {
        private readonly object gate = new object();
        private Process process;
        private BinaryReader reader;
        private StreamWriter writer;
        public string Description { get; private set; }
        public List<ArchiveEntry> Entries { get; private set; }
        public string Id { get { return "garbro-mod"; } }

        public static string ExecutablePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "garbro-mod", "Galtools.GarbroModBridge.exe"); }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static ProcessStartInfo StartInfo(string arguments)
        {
            if (!File.Exists(ExecutablePath)) throw new FileNotFoundException("找不到 GARbro-Mod 后端", ExecutablePath);
            return new ProcessStartInfo(ExecutablePath, arguments) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath)
            };
        }

        public static List<string> ListSchemes()
        {
            using (var p = Process.Start(StartInfo("--schemes")))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                var result = new List<string>();
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    result.Add(line);
                return result;
            }
        }

        public void Open(string archive, string scheme)
        {
            Dispose();
            string arguments = Quote(archive);
            if (!string.IsNullOrWhiteSpace(scheme)) arguments += " " + Quote(scheme);
            process = Process.Start(StartInfo(arguments));
            reader = new BinaryReader(process.StandardOutput.BaseStream, new UTF8Encoding(false));
            writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false));
            writer.AutoFlush = true;
            try
            {
                byte[] magic = ReadExact(reader, 4);
                if (Encoding.ASCII.GetString(magic) != "GTL1") throw new InvalidDataException("后端协议版本不匹配");
                Description = ReadString(reader);
                uint count = reader.ReadUInt32();
                if (count > 1000000) throw new InvalidDataException("封包条目数量异常");
                Entries = new List<ArchiveEntry>((int)count);
                for (uint i = 0; i < count; ++i)
                {
                    var item = new ArchiveEntry();
                    item.Index = (int)reader.ReadUInt32();
                    item.Name = ReadString(reader);
                    item.Type = ReadString(reader);
                    item.Size = checked((long)reader.ReadUInt64());
                    item.PackedSize = checked((long)reader.ReadUInt64());
                    Entries.Add(item);
                }
            }
            catch (Exception ex)
            {
                string detail = "";
                if (process.WaitForExit(3000)) detail = process.StandardError.ReadToEnd();
                Dispose();
                throw new InvalidDataException(string.IsNullOrWhiteSpace(detail) ? ex.Message : detail.Trim(), ex);
            }
        }

        private static byte[] ReadExact(BinaryReader input, int length)
        {
            byte[] bytes = new byte[length];
            int position = 0;
            while (position < length)
            {
                int read = input.BaseStream.Read(bytes, position, length - position);
                if (read <= 0) throw new EndOfStreamException("解包后端意外退出");
                position += read;
            }
            return bytes;
        }

        private static string ReadString(BinaryReader input)
        {
            uint length = input.ReadUInt32();
            if (length > 1048576) throw new InvalidDataException("后端返回的文字过长");
            return Encoding.UTF8.GetString(ReadExact(input, (int)length));
        }

        public byte[] ReadImagePng(int index) { return ReadBytes("I", index); }
        public byte[] ReadImagePreview(int index) { return ReadBytes("P", index); }
        public byte[] ReadAudioWav(int index) { return ReadBytes("A", index); }

        public byte[] ReadBytes(string command, int index)
        {
            lock (gate)
            {
                ulong length = BeginRequest(command, index);
                if (length > 268435456)
                {
                    byte[] buffer = new byte[65536];
                    while (length > 0)
                    {
                        int read = reader.BaseStream.Read(buffer, 0, (int)Math.Min((ulong)buffer.Length, length));
                        if (read <= 0) throw new EndOfStreamException("解包后端意外退出");
                        length -= (ulong)read;
                    }
                    throw new InvalidDataException("预览文件超过 256 MB，请导出原文件");
                }
                return ReadExact(reader, (int)length);
            }
        }

        public void Save(string command, int index, string target)
        {
            lock (gate)
            {
                string temporary = target + ".galtools-partial";
                try
                {
                    ulong length = BeginRequest(command, index);
                    using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[65536];
                        while (length > 0)
                        {
                            int take = (int)Math.Min((ulong)buffer.Length, length);
                            int count = reader.BaseStream.Read(buffer, 0, take);
                            if (count <= 0) throw new EndOfStreamException("导出时后端意外退出");
                            output.Write(buffer, 0, count);
                            length -= (ulong)count;
                        }
                    }
                    if (File.Exists(target)) File.Replace(temporary, target, null);
                    else File.Move(temporary, target);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        private ulong BeginRequest(string command, int index)
        {
            if (process == null || process.HasExited) throw new InvalidOperationException("封包后端已退出");
            writer.WriteLine(command + " " + index);
            byte status = reader.ReadByte();
            ulong length = reader.ReadUInt64();
            if (status != 0)
            {
                if (length > 65536) throw new InvalidDataException("后端错误信息过长");
                throw new InvalidDataException(Encoding.UTF8.GetString(ReadExact(reader, (int)length)));
            }
            return length;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (process == null) return;
                try { if (!process.HasExited) { writer.WriteLine("Q"); process.WaitForExit(1500); } }
                catch { }
                if (!process.HasExited) process.Kill();
                try { reader.Close(); } catch { }
                try { writer.Close(); } catch { }
                process.Dispose(); process = null; reader = null; writer = null; Entries = null;
            }
        }
    }
}
