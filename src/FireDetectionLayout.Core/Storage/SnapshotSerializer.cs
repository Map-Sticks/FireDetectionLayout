using System.IO;
using FireDetectionLayout.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace FireDetectionLayout.Core.Storage
{
    public static class SnapshotSerializer
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,

            // Lists that carry defaults (rule keywords) must be replaced on load, not appended to.
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            Converters = { new StringEnumConverter() }
        };

        public static string Serialize<T>(T value) => JsonConvert.SerializeObject(value, Settings);

        public static T Deserialize<T>(string json) =>
            JsonConvert.DeserializeObject<T>(json, Settings)
            ?? throw new InvalidDataException($"Could not deserialize {typeof(T).Name} from JSON.");

        public static void SaveToFile<T>(T value, string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, Serialize(value));
        }

        public static T LoadFromFile<T>(string filePath) =>
            Deserialize<T>(File.ReadAllText(filePath));
    }
}
