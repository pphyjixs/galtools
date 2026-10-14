using System;
using System.IO;
using Galtools;

internal static class SmokeFuyin
{
    private static void Main(string[] args)
    {
        using (var archive = new FuyinPakBackend())
        {
            archive.Open(args[0], null);
            if (archive.Entries.Count != 40) throw new Exception("Expected 40 BMP images.");
            Console.WriteLine(archive.Description + ": " + archive.Entries.Count);
            Console.WriteLine(archive.Entries[0].Name);
            byte[] raw = archive.ReadImagePng(0);
            byte[] preview = archive.ReadImagePreview(0);
            if (raw[0] != 0x89 || raw[1] != 0x50 || preview[0] != 0x89 || preview[1] != 0x50)
                throw new Exception("PNG conversion failed.");
            Console.WriteLine("PNG " + raw.Length + " bytes; preview " + preview.Length + " bytes");
            string output = Path.Combine(Path.GetTempPath(), "galtools-fuyin-smoke.bmp");
            try
            {
                archive.Save("R", 0, output);
                using (var file = File.OpenRead(output))
                    if (file.ReadByte() != 'B' || file.ReadByte() != 'M') throw new Exception("BMP extraction failed.");
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }
    }
}
