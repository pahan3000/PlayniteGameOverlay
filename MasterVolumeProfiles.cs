using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace PlayniteGameOverlay
{
    // Persists per-game master-endpoint and app-session volume preferences. Profiles written
    // by older versions (with neither mode flag) are migrated as legacy app-session levels.
    public static class MasterVolumeProfiles
    {
        public sealed class Entry
        {
            public Guid GameId { get; set; }
            public float Level { get; set; }
            // Missing in older XML files, so legacy app-session values deserialize as false.
            public bool IsMasterVolume { get; set; }
            public bool IsGameVolume { get; set; }
        }
        public sealed class Store { public List<Entry> Games { get; set; } = new List<Entry>(); }
        private static readonly object Gate = new object();
        private static Store cache;
        private static string PathToStore => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlayniteGameOverlay", "master_volume_profiles.xml");
        private static void EnsureLoaded()
        {
            if (cache != null) return;
            try
            {
                using (var f = File.OpenRead(PathToStore))
                    cache = (Store)new XmlSerializer(typeof(Store)).Deserialize(f);
            }
            catch { cache = new Store(); }
        }
        public static bool TryGet(Guid id, out float level)
        {
            lock (Gate)
            {
                EnsureLoaded();
                foreach (var e in cache.Games)
                    if (id != Guid.Empty && e.GameId == id && e.IsMasterVolume) { level = e.Level; return true; }
                level = 1f;
                return false;
            }
        }
        public static bool TryGetGameVolume(Guid id, out float level)
        {
            lock (Gate)
            {
                EnsureLoaded();
                foreach (var e in cache.Games)
                    if (id != Guid.Empty && e.GameId == id && e.IsGameVolume) { level = e.Level; return true; }
                level = 1f;
                return false;
            }
        }
        public static bool TryGetLegacy(Guid id, out float level)
        {
            lock (Gate)
            {
                EnsureLoaded();
                foreach (var e in cache.Games)
                    if (id != Guid.Empty && e.GameId == id && !e.IsMasterVolume && !e.IsGameVolume)
                    {
                        level = e.Level;
                        return true;
                    }
                level = 1f; return false;
            }
        }
        public static void Save(Guid id, float level)
        {
            if (id == Guid.Empty) return;
            lock (Gate)
            {
                EnsureLoaded();
                var entry = cache.Games.Find(e => e.GameId == id && e.IsMasterVolume);
                if (entry == null)
                {
                    entry = new Entry { GameId = id, IsMasterVolume = true };
                    cache.Games.Add(entry);
                }
                entry.Level = Math.Max(0f, Math.Min(1f, level));
                Persist();
            }
        }
        public static void SaveGameVolume(Guid id, float level)
        {
            if (id == Guid.Empty) return;
            lock (Gate)
            {
                EnsureLoaded();
                var entry = cache.Games.Find(e => e.GameId == id && e.IsGameVolume);
                if (entry == null)
                {
                    entry = new Entry { GameId = id, IsGameVolume = true };
                    cache.Games.Add(entry);
                }
                entry.Level = Math.Max(0f, Math.Min(1f, level));
                Persist();
            }
        }

        public static bool MigrateLegacyToGameVolume(Guid id)
        {
            if (id == Guid.Empty) return false;
            lock (Gate)
            {
                EnsureLoaded();
                var legacy = cache.Games.Find(e =>
                    e.GameId == id && !e.IsMasterVolume && !e.IsGameVolume);
                if (legacy == null) return false;

                var current = cache.Games.Find(e => e.GameId == id && e.IsGameVolume);
                if (current == null)
                    cache.Games.Add(new Entry { GameId = id, Level = legacy.Level, IsGameVolume = true });
                cache.Games.RemoveAll(e =>
                    e.GameId == id && !e.IsMasterVolume && !e.IsGameVolume);
                Persist();
                return true;
            }
        }

        public static void RemoveLegacy(Guid id)
        {
            if (id == Guid.Empty) return;
            lock (Gate)
            {
                EnsureLoaded();
                if (cache.Games.RemoveAll(e => e.GameId == id && !e.IsMasterVolume && !e.IsGameVolume) == 0)
                    return;
                Persist();
            }
        }

        private static void Persist()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathToStore));
                string temp = PathToStore + ".tmp";
                using (var f = File.Create(temp))
                    new XmlSerializer(typeof(Store)).Serialize(f, cache);
                File.Copy(temp, PathToStore, true);
                File.Delete(temp);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Could not save volume profile store: " + ex.Message);
            }
        }
    }
}
