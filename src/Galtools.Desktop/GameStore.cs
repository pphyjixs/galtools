using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace Galtools
{
    [Serializable]
    public sealed class GameRecord
    {
        public string Title { get; set; }
        public string Folder { get; set; }
        public string Executable { get; set; }
        public string Scheme { get; set; }
        public string CoverPath { get; set; }
        public string Tags { get; set; }
        public string Notes { get; set; }
        public override string ToString() { return Title; }
    }

    public static class GameStore
    {
        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "games.xml"); }
        }

        public static List<GameRecord> Load()
        {
            if (!File.Exists(FilePath)) return new List<GameRecord>();
            using (var input = File.OpenRead(FilePath))
                return (List<GameRecord>)new XmlSerializer(typeof(List<GameRecord>)).Deserialize(input);
        }

        public static void Save(List<GameRecord> games)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temporary = FilePath + ".partial";
            using (var output = File.Create(temporary))
                new XmlSerializer(typeof(List<GameRecord>)).Serialize(output, games);
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
    }
}
