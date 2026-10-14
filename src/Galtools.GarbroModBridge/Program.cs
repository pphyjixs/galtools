// Copyright (c) galtools contributors. MIT.
// A small process boundary around the vendored GARbro-Mod runtime.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameRes;
using GameRes.Formats.KiriKiri;
using GameRes.Formats.Musica;
using NAudio.Wave;

internal static class Program
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    [STAThread]
    private static int Main(string[] args)
    {
        bool listSchemes = args.Length == 1 && args[0] == "--schemes";
        bool listPazSchemes = args.Length == 1 && args[0] == "--paz-schemes";
        if (!listSchemes && !listPazSchemes && (args.Length < 1 || args.Length > 2 || !File.Exists(args[0])))
        {
            Console.Error.WriteLine("Archive path is missing.");
            return 2;
        }
        try
        {
            string path = listSchemes || listPazSchemes ? null : Path.GetFullPath(args[0]);
            if (path != null) Directory.SetCurrentDirectory(Path.GetDirectoryName(path));
            string schemes = Path.Combine(FormatCatalog.Instance.DataDirectory, "Formats.dat");
            if (File.Exists(schemes))
            {
                using (var file = File.OpenRead(schemes))
                    FormatCatalog.Instance.DeserializeScheme(file);
            }
            if (listSchemes)
            {
                using (var writer = new StreamWriter(Console.OpenStandardOutput(), Utf8))
                    foreach (string title in Xp3Opener.KnownSchemes.Keys.OrderBy(x => x))
                        writer.WriteLine(title);
                return 0;
            }
            var paz = FormatCatalog.Instance.ArcFormats.OfType<PazOpener>().First();
            if (listPazSchemes)
            {
                using (var writer = new StreamWriter(Console.OpenStandardOutput(), Utf8))
                    foreach (var title in paz.KnownTitles.Keys.OrderBy(x => x))
                        writer.WriteLine(title);
                return 0;
            }
            string chosenScheme = args.Length == 2 ? args[1] : null;
            if (Path.GetExtension(path).Equals(".paz", StringComparison.OrdinalIgnoreCase)
                && (File.Exists(Path.Combine(Path.GetDirectoryName(path), "yorino.exe"))
                    || File.Exists(Path.Combine(Path.GetDirectoryName(path), "yorino_chs.exe")))
                && string.IsNullOrEmpty(chosenScheme))
                chosenScheme = "Soreyori no Prologue";
            FormatCatalog.Instance.ParametersRequest += delegate(object sender, ParametersRequestEventArgs request)
            {
                if (sender is Xp3Opener && !string.IsNullOrEmpty(chosenScheme)
                    && Xp3Opener.KnownSchemes.ContainsKey(chosenScheme))
                {
                    request.Options = new Xp3Options { Scheme = Xp3Opener.GetScheme(chosenScheme) };
                    request.InputResult = true;
                }
                else if (sender is PazOpener && !string.IsNullOrEmpty(chosenScheme)
                    && paz.KnownTitles.ContainsKey(chosenScheme))
                {
                    request.Options = new PazOptions { Scheme = paz.KnownTitles[chosenScheme] };
                    request.InputResult = true;
                }
            };
            using (var archive = ArcFile.TryOpen(path))
            {
                if (archive == null)
                {
                    var error = FormatCatalog.Instance.LastError;
                    throw new InvalidDataException(error is OperationCanceledException
                        ? "This archive needs a game decryption scheme."
                        : error == null
                        ? "GARbro-Mod does not recognize this archive."
                        : "GARbro-Mod could not open this archive: " + error.Message);
                }
                var entries = EntryInspector.Inspect(archive);
                using (var output = new BinaryWriter(Console.OpenStandardOutput(), Utf8))
                {
                    output.Write(new byte[] { (byte)'G', (byte)'T', (byte)'L', (byte)'1' });
                    WriteString(output, archive.Description ?? archive.Tag);
                    output.Write((uint)entries.Length);
                    for (int i = 0; i < entries.Length; ++i)
                    {
                        var info = entries[i];
                        var entry = info.Source;
                        var packed = entry as PackedEntry;
                        output.Write((uint)i);
                        WriteString(output, info.DisplayName);
                        WriteString(output, info.Type);
                        output.Write((ulong)(packed != null && packed.IsPacked && packed.UnpackedSize != 0
                            ? packed.UnpackedSize : entry.Size));
                        output.Write((ulong)entry.Size);
                    }
                    output.Flush();
                    string command;
                    while ((command = Console.ReadLine()) != null)
                    {
                        if (command == "Q") break;
                        try
                        {
                            var parts = command.Split(' ');
                            if (parts.Length != 2) throw new ArgumentException("Invalid command.");
                            int index = int.Parse(parts[1]);
                            if (index < 0 || index >= entries.Length) throw new IndexOutOfRangeException();
                            string temporary = Path.GetTempFileName();
                            try
                            {
                                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                                {
                                    if (parts[0] == "R")
                                    {
                                        using (var input = archive.OpenEntry(entries[index].Source)) input.CopyTo(file);
                                    }
                                    else if (parts[0] == "I" || parts[0] == "P")
                                    {
                                        BitmapSource bitmap = ReadImage(archive, entries, index);
                                        if (parts[0] == "P" && bitmap.PixelHeight > 480)
                                        {
                                            double scale = 480.0 / bitmap.PixelHeight;
                                            bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
                                        }
                                        var encoder = new PngBitmapEncoder();
                                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                        encoder.Save(file);
                                    }
                                    else if (parts[0] == "A" || parts[0] == "M")
                                    {
                                        using (var input = archive.OpenBinaryEntry(entries[index].Source))
                                        {
                                            if (string.IsNullOrEmpty(Path.GetExtension(input.Name)))
                                            {
                                                if (input.Signature == 0x5367674F) input.Name += ".ogg";
                                                else if (input.Signature == 0x46464952) input.Name += ".wav";
                                                else if (input.Signature == 0x43614C66) input.Name += ".flac";
                                            }
                                            using (var sound = AudioFormat.Read(input))
                                            {
                                                if (sound == null)
                                                {
                                                    var error = FormatCatalog.Instance.LastError;
                                                    throw new InvalidDataException("Audio format is not recognized." +
                                                        (error == null ? "" : " " + error.Message));
                                                }
                                                if (parts[0] == "A") AudioFormat.Wav.Write(sound, file);
                                                else
                                                {
                                                    string wavPath = Path.GetTempFileName();
                                                    try
                                                    {
                                                        using (var wavFile = File.Create(wavPath))
                                                            AudioFormat.Wav.Write(sound, wavFile);
                                                        using (var wave = new WaveFileReader(wavPath))
                                                            MediaFoundationEncoder.EncodeToMp3(wave, file, 192000);
                                                    }
                                                    finally { File.Delete(wavPath); }
                                                }
                                            }
                                        }
                                    }
                                    else throw new ArgumentException("Unknown command.");
                                    file.Position = 0;
                                    output.Write((byte)0);
                                    output.Write((ulong)file.Length);
                                    output.Flush();
                                    file.CopyTo(output.BaseStream);
                                }
                            }
                            finally { File.Delete(temporary); }
                        }
                        catch (Exception ex)
                        {
                            byte[] message = Utf8.GetBytes(ex.Message);
                            output.Write((byte)1);
                            output.Write((ulong)message.Length);
                            output.Write(message);
                        }
                        output.Flush();
                    }
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void WriteString(BinaryWriter output, string value)
    {
        byte[] bytes = Utf8.GetBytes(value);
        output.Write((uint)bytes.Length);
        output.Write(bytes);
    }

    private static BitmapSource ReadImage(ArcFile archive, InspectedEntry[] entries, int index)
    {
        var info = entries[index];
        if (info.ReferenceName == null)
        {
            using (var decoder = archive.OpenImage(info.Source)) return decoder.Image.Bitmap;
        }
        if (info.ReferenceBaseIndex < 0)
            throw new InvalidDataException("TLG 引用图像的基础文件尚未定位：" + info.ReferenceName);
        using (var input = archive.OpenBinaryEntry(entries[info.ReferenceBaseIndex].Source))
        {
            var format = new TlgFormat();
            var metadata = format.ReadMetaData(input);
            if (metadata == null || metadata.GetType().GetField("LayerIndex", BindingFlags.Instance | BindingFlags.Public) == null)
                throw new InvalidDataException("TLG 引用目标格式不匹配：" + info.ReferenceName);
            metadata.FileName = entries[info.ReferenceBaseIndex].DisplayName;
            metadata.GetType().GetField("LayerIndex", BindingFlags.Instance | BindingFlags.Public).SetValue(metadata, info.ReferenceLayer);
            input.Position = 0;
            return format.Read(input, metadata).Bitmap;
        }
    }
}
