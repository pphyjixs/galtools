// Copyright (c) galtools contributors. MIT.
// Maintainer utility: make reproducible additions to the bundled GARbro-Mod Formats.dat.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GameRes;
using GameRes.Formats.Musica;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "apply-soreyori")
        {
            Console.Error.WriteLine("Usage: Galtools.FormatData.exe apply-soreyori");
            return 2;
        }
        string path = Path.Combine(FormatCatalog.Instance.DataDirectory, "Formats.dat");
        using (var input = File.OpenRead(path)) FormatCatalog.Instance.DeserializeScheme(input);
        var paz = FormatCatalog.Instance.ArcFormats.OfType<PazOpener>().First();
        PazScheme scheme;
        if (!paz.KnownTitles.TryGetValue("Soreyori no Prologue", out scheme))
            throw new InvalidDataException("Formats.dat lacks the existing Soreyori no Prologue PAZ scheme.");
        if (scheme.Version != 2 || !scheme.ArcKeys.ContainsKey("scr") || !scheme.ArcKeys.ContainsKey("sys"))
            throw new InvalidDataException("Unexpected Soreyori PAZ scheme layout.");

        // The Chinese patch uses the same PAZ keys for its script and UI archives.
        scheme.ArcKeys["cnscr"] = scheme.ArcKeys["scr"];
        scheme.ArcKeys["cnsys"] = scheme.ArcKeys["sys"];
        // 'yori' XOR 0x8B, as stored in the first four bytes of these archives.
        paz.KnownSchemes[0xE2F9E4F2] = scheme;

        var field = typeof(FormatCatalog).GetField("m_game_map", BindingFlags.Instance | BindingFlags.NonPublic);
        var games = (Dictionary<string, string>)field.GetValue(FormatCatalog.Instance);
        foreach (string exe in new[] { "yorino.exe", "_yorino.exe", "yorino_chs.exe", "yorino_chs（win10启动）.exe" })
            games[exe] = "Soreyori no Prologue";

        string temporary = path + ".galtools-partial";
        try
        {
            using (var output = File.Create(temporary)) FormatCatalog.Instance.SerializeScheme(output);
            File.Replace(temporary, path, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Console.WriteLine("Updated " + path);
        return 0;
    }
}
