using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GameRes;
using GameRes.Formats.KiriKiri;

internal sealed class InspectedEntry
{
    public Entry Source;
    public string DisplayName;
    public string Type;
    public string Extension;
    public bool SyntheticHxName;
    public bool IsQoi;
    public string ReferenceName;
    public int ReferenceLayer;
    public int ReferenceBaseIndex = -1;
    public int Width;
    public int Height;
    public int LayerCount;
}

internal static class EntryInspector
{
    private sealed class RefGroup
    {
        public string Name;
        public int Width;
        public int Height;
        public readonly HashSet<int> Layers = new HashSet<int>();
    }

    public static InspectedEntry[] Inspect(ArcFile archive)
    {
        var source = archive.Dir.ToArray();
        var result = new InspectedEntry[source.Length];
        var qoi = new List<int>();
        var groups = new List<RefGroup>();
        var groupByName = new Dictionary<string, RefGroup>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < source.Length; ++i)
        {
            Entry entry = source[i];
            var info = new InspectedEntry {
                Source = entry,
                DisplayName = entry.Name ?? "",
                Type = entry.Type ?? "",
                SyntheticHxName = IsSyntheticHxName(entry)
            };
            result[i] = info;
            if (!byName.ContainsKey(info.DisplayName)) byName.Add(info.DisplayName, i);
            string originalExtension = Path.GetExtension(info.DisplayName).ToLowerInvariant();
            if (!string.IsNullOrEmpty(info.Type) && !info.SyntheticHxName &&
                originalExtension != ".tlg" && originalExtension != ".tlg5" && originalExtension != ".tlg6") continue;
            try
            {
                byte[] header = new byte[256];
                int count = 0;
                using (var input = archive.OpenEntry(entry))
                {
                    while (count < header.Length)
                    {
                        int read = input.Read(header, count, header.Length - count);
                        if (read <= 0) break;
                        count += read;
                    }
                }
                InspectHeader(info, header, count);
                if (info.IsQoi) qoi.Add(i);
                if (info.ReferenceName != null)
                {
                    RefGroup group;
                    if (!groupByName.TryGetValue(info.ReferenceName, out group))
                    {
                        group = new RefGroup { Name = info.ReferenceName, Width = info.Width, Height = info.Height };
                        groupByName.Add(group.Name, group);
                        groups.Add(group);
                    }
                    group.Layers.Add(info.ReferenceLayer);
                }
            }
            catch { /* A damaged entry must not prevent browsing the archive. */ }
            if (string.IsNullOrEmpty(info.Type))
            {
                string ext = Path.GetExtension(info.DisplayName).ToLowerInvariant();
                if (ext == ".tlg" || ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp") info.Type = "image";
                else if (ext == ".ogg" || ext == ".wav" || ext == ".mp3" || ext == ".flac") info.Type = "audio";
            }
        }

        foreach (var info in result)
        {
            int index;
            if (info.ReferenceName != null && byName.TryGetValue(info.ReferenceName, out index))
                info.ReferenceBaseIndex = index;
        }

        if (qoi.Count > 0 && qoi.Count == groups.Count)
        {
            var assignments = new List<KeyValuePair<int, RefGroup>>();
            int cursor = 0;
            while (cursor < qoi.Count)
            {
                if (Matches(result[qoi[cursor]], groups[cursor]))
                {
                    assignments.Add(new KeyValuePair<int, RefGroup>(qoi[cursor], groups[cursor]));
                    cursor++;
                }
                else if (cursor + 1 < qoi.Count &&
                    Matches(result[qoi[cursor]], groups[cursor + 1]) &&
                    Matches(result[qoi[cursor + 1]], groups[cursor]))
                {
                    assignments.Add(new KeyValuePair<int, RefGroup>(qoi[cursor], groups[cursor + 1]));
                    assignments.Add(new KeyValuePair<int, RefGroup>(qoi[cursor + 1], groups[cursor]));
                    cursor += 2;
                }
                else break;
            }
            if (cursor == qoi.Count)
            {
                var inferred = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in assignments)
                {
                    result[pair.Key].DisplayName = pair.Value.Name;
                    inferred.Add(pair.Value.Name, pair.Key);
                }
                foreach (var info in result)
                {
                    int index;
                    if (info.ReferenceName != null && inferred.TryGetValue(info.ReferenceName, out index))
                        info.ReferenceBaseIndex = index;
                }
            }
        }

        for (int i = 0; i < result.Length; ++i)
        {
            var info = result[i];
            if (!info.SyntheticHxName) continue;
            if (info.ReferenceName != null)
            {
                string target = Path.GetFileNameWithoutExtension(info.ReferenceName.Replace('/', '\\'));
                info.DisplayName = "#" + i.ToString("D5") + "_" + target + "_layer" + info.ReferenceLayer.ToString("D2") + ".tlg";
            }
            else if (info.DisplayName == info.Source.Name)
            {
                string code = string.Join("-", info.Source.Name.Select(c => ((int)c).ToString("X4")));
                info.DisplayName = "#" + i.ToString("D5") + "_" + code + (info.Extension ?? ".bin");
            }
        }
        return result;
    }

    private static bool Matches(InspectedEntry qoi, RefGroup group)
    {
        if (!qoi.IsQoi || qoi.LayerCount < 1 || qoi.LayerCount > 1024 ||
            qoi.Width != group.Width || qoi.Height != group.Height || qoi.LayerCount != group.Layers.Count)
            return false;
        for (int i = 0; i < qoi.LayerCount; ++i)
            if (!group.Layers.Contains(i)) return false;
        return true;
    }

    private static bool IsSyntheticHxName(Entry entry)
    {
        var xp3 = entry as Xp3Entry;
        if (xp3 == null || !(xp3.Cipher is HxCrypt) || xp3.Extra == null || string.IsNullOrEmpty(entry.Name)) return false;
        return entry.Name.All(c => c >= 0x5000 && c <= 0x8FFF);
    }

    private static void InspectHeader(InspectedEntry info, byte[] b, int n)
    {
        if (n >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
            SetFormat(info, "image", ".png");
        else if (n >= 20 && b[0] == 'T' && b[1] == 'L' && b[2] == 'G')
        {
            SetFormat(info, "image", ".tlg");
            int offset = Ascii(b, 0, "TLG0.0") ? 15 : 0;
            if (n < offset + 20) return;
            info.Width = BitConverter.ToInt32(b, offset + 12);
            info.Height = BitConverter.ToInt32(b, offset + 16);
            if (Ascii(b, offset, "TLGqoi"))
            {
                info.IsQoi = true;
                info.LayerCount = n >= offset + 36 && Ascii(b, offset + 20, "QHDR") ? BitConverter.ToInt32(b, offset + 32) : 1;
            }
            else if (Ascii(b, offset, "TLGref") && n >= offset + 44 && Ascii(b, offset + 20, "QREF"))
            {
                int length = BitConverter.ToInt32(b, offset + 40);
                if (length >= 2 && length <= n - offset - 44 && length % 2 == 0)
                {
                    info.ReferenceName = Encoding.Unicode.GetString(b, offset + 44, length).TrimEnd('\0');
                    info.ReferenceLayer = BitConverter.ToInt32(b, offset + 32);
                }
            }
        }
        else if (n >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) SetFormat(info, "image", ".jpg");
        else if (Ascii(b, 0, "BM")) SetFormat(info, "image", ".bmp");
        else if (Ascii(b, 0, "GIF8")) SetFormat(info, "image", ".gif");
        else if (n >= 12 && Ascii(b, 0, "RIFF") && Ascii(b, 8, "WEBP")) SetFormat(info, "image", ".webp");
        else if (Ascii(b, 0, "OggS")) SetFormat(info, "audio", ".ogg");
        else if (n >= 12 && Ascii(b, 0, "RIFF") && Ascii(b, 8, "WAVE")) SetFormat(info, "audio", ".wav");
        else if (Ascii(b, 0, "fLaC")) SetFormat(info, "audio", ".flac");
        else if (Ascii(b, 0, "ID3")) SetFormat(info, "audio", ".mp3");
    }

    private static void SetFormat(InspectedEntry info, string type, string extension)
    {
        info.Type = type;
        info.Extension = extension;
    }

    private static bool Ascii(byte[] data, int offset, string expected)
    {
        if (offset + expected.Length > data.Length) return false;
        for (int i = 0; i < expected.Length; ++i)
            if (data[offset + i] != expected[i]) return false;
        return true;
    }
}
