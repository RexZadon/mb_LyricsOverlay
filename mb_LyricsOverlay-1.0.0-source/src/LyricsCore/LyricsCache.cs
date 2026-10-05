using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace LyricsOverlay.Core
{
    [DataContract]
    public sealed class CacheEntry
    {
        /// <summary>Lyrics text (LRC or plain). Null for a negative ("not found") entry.</summary>
        [DataMember(Name = "lyrics")] public string Lyrics { get; set; }
        [DataMember(Name = "instrumental")] public bool Instrumental { get; set; }
        [DataMember(Name = "fetchedUtcTicks")] public long FetchedUtcTicks { get; set; }
        [DataMember(Name = "artist")] public string Artist { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        /// <summary>Which online provider produced the lyrics (null for entries written by older versions).</summary>
        [DataMember(Name = "provider")] public string Provider { get; set; }

        public bool IsNegative => Lyrics == null && !Instrumental;
    }

    /// <summary>
    /// One small JSON file per track in <c>dir</c>, fronted by a small in-memory map so replays and
    /// prefetched tracks don't touch the disk. Positive entries never expire; negative entries expire
    /// after <c>negativeTtl</c> so a missing track is retried eventually but not on every play.
    /// Thread-safe.
    /// </summary>
    public sealed class LyricsCache
    {
        const int MemoryCapacity = 256;
        readonly object _gate = new object();
        readonly Dictionary<string, CacheEntry> _memory = new Dictionary<string, CacheEntry>();
        readonly string _dir;
        readonly TimeSpan _negativeTtl;
        readonly Func<DateTime> _utcNow;
        volatile bool _closed;

        public LyricsCache(string dir, TimeSpan negativeTtl, Func<DateTime> utcNow = null)
        {
            _dir = dir;
            _negativeTtl = negativeTtl;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public static string MakeKey(string artist, string title, string album, int durationSec)
        {
            string raw = string.Join("|",
                TitleNormalizer.Key(TitleNormalizer.NormalizeArtist(artist)),
                TitleNormalizer.Key(TitleNormalizer.NormalizeTitle(title)),
                TitleNormalizer.Key(album),
                durationSec.ToString());
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>False when there is no entry, or only an expired negative one.</summary>
        public bool TryGet(string key, out CacheEntry entry)
        {
            lock (_gate) _memory.TryGetValue(key, out entry);
            if (entry == null)
            {
                var path = PathFor(key);
                if (!File.Exists(path)) return false;
                try
                {
                    using (var fs = File.OpenRead(path))
                        entry = (CacheEntry)new DataContractJsonSerializer(typeof(CacheEntry)).ReadObject(fs);
                }
                catch (Exception)
                {
                    return false; // corrupt or half-written file: treat as a miss, it gets overwritten
                }
                if (entry == null) return false;
                Remember(key, entry);
            }
            if (entry.IsNegative && _utcNow() - new DateTime(entry.FetchedUtcTicks, DateTimeKind.Utc) > _negativeTtl)
            {
                lock (_gate) _memory.Remove(key);
                entry = null;
                return false;
            }
            return true;
        }

        void Remember(string key, CacheEntry entry)
        {
            lock (_gate)
            {
                if (_memory.Count >= MemoryCapacity && !_memory.ContainsKey(key)) _memory.Clear(); // crude but bounded
                _memory[key] = entry;
            }
        }

        /// <summary>
        /// Stops disk writes (plugin shutdown or uninstall), so a lookup that completes afterwards can't
        /// recreate the cache folder. Reads and the in-memory map keep working.
        /// </summary>
        public void Close() => _closed = true;

        public void Put(string key, CacheEntry entry)
        {
            entry.FetchedUtcTicks = _utcNow().Ticks;
            Remember(key, entry); // even if the disk write below fails, this session won't ask again
            if (_closed) return;
            Directory.CreateDirectory(_dir);
            var path = PathFor(key);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var fs = File.Create(tmp))
                new DataContractJsonSerializer(typeof(CacheEntry)).WriteObject(fs, entry);
            // Atomic-ish replace; another writer (the provider DLL) may race us, last one wins.
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        string PathFor(string key) => Path.Combine(_dir, key + ".json");
    }
}
