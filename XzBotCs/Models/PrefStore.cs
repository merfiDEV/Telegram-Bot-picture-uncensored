using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace XzBotCs.Models
{
    public class PrefStore
    {
        public Dictionary<long, string> Prefixes { get; set; } = new Dictionary<long, string>();

        [JsonIgnore]
        public object SyncRoot { get; } = new object();

        private static string FilePath = "pref.json";

        public static PrefStore Load()
        {
            if (File.Exists(FilePath))
            {
                try
                {
                    string json = File.ReadAllText(FilePath);
                    var store = JsonConvert.DeserializeObject<PrefStore>(json);
                    if (store != null)
                    {
                        return store;
                    }
                }
                catch { }
            }
            return new PrefStore();
        }

        public void Save()
        {
            lock (SyncRoot)
            {
                try
                {
                    string tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, FilePath, overwrite: true);
                }
                catch { }
            }
        }
    }
}