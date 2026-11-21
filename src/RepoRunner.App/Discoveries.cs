using RepoRunner.Core;

namespace RepoRunner.App;

/// <summary>
/// Keeps each repo's discovered processes. A scan is saved beside the repo's recordings and loaded the next time,
/// so a repo is read again only when someone asks or its code has changed (any source file added, removed or edited).
/// </summary>
public sealed class Discoveries(VaultService vaults)
{
    public sealed class Entry
    {
        public ProcessMap? Map { get; internal set; }
        public bool Scanning { get; internal set; }
        /// <summary>Why the last scan ran, in words, or what went wrong.</summary>
        public string? Note { get; internal set; }
        public bool Failed { get; internal set; }
        internal DateTime CheckedAt { get; set; }
    }

    private readonly Dictionary<long, Entry> _entries = [];
    private long _version;

    /// <summary>Goes up whenever a scan starts or ends; the page polls it.</summary>
    public long Version => Interlocked.Read(ref _version);

    private string FileOf(RepoInfo repo) => Path.Combine(vaults.Use(v => v.RecordingsFolder(repo)), "processes-found.json");

    /// <summary>The repo's processes as last found. Loads the saved scan, and starts a scan if there is none or the code changed.</summary>
    public Entry For(RepoInfo repo)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(repo.Id, out entry!))
            {
                _entries[repo.Id] = entry = new Entry { Map = Discovery.Load(FileOf(repo)) };
            }
        }
        // Looking for changes costs a directory walk, so at most every half minute.
        if (!entry.Scanning && DateTime.UtcNow - entry.CheckedAt > TimeSpan.FromSeconds(30))
        {
            entry.CheckedAt = DateTime.UtcNow;
            _ = Task.Run(() => CheckAndScan(repo, entry, force: false));
        }
        return entry;
    }

    public void Rescan(RepoInfo repo)
    {
        var entry = For(repo);
        entry.Scanning = true; // shown at once; the scan itself may wait for a check in progress
        Interlocked.Increment(ref _version);
        _ = Task.Run(() => CheckAndScan(repo, entry, force: true));
    }

    /// <summary>Every repo's saved scan that is loaded, for following calls from one repo into another.</summary>
    public Dictionary<string, ProcessMap> Loaded(IEnumerable<RepoInfo> repos)
    {
        var maps = new Dictionary<string, ProcessMap>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in repos)
            if (For(repo).Map is { } map) maps[repo.Name] = map;
        return maps;
    }

    private void CheckAndScan(RepoInfo repo, Entry entry, bool force)
    {
        // One scan of a repo at a time. A check that finds another under way leaves it be; a scan someone asked for waits its turn.
        if (force) Monitor.Enter(entry);
        else if (!Monitor.TryEnter(entry)) return;
        entry.Scanning = force;
        if (force) Interlocked.Increment(ref _version);
        try
        {
            string why;
            if (force) why = "Scanned again on request.";
            else if (entry.Map is null) why = "First scan of this repo.";
            else
            {
                var now = Discovery.Fingerprint(repo.Path, out _);
                if (now == entry.Map.Fingerprint) return;
                why = "The code changed since the last scan, so it was scanned again.";
            }
            entry.Scanning = true;
            Interlocked.Increment(ref _version);
            var map = Discovery.Scan(repo.Path);
            Discovery.Save(map, FileOf(repo));
            entry.Map = map;
            entry.Note = why;
            entry.Failed = false;
        }
        catch (Exception ex)
        {
            entry.Note = $"The scan stopped: {ex.Message}";
            entry.Failed = true;
        }
        finally
        {
            var was = entry.Scanning;
            entry.Scanning = false;
            entry.CheckedAt = DateTime.UtcNow;
            Monitor.Exit(entry);
            if (was) Interlocked.Increment(ref _version);
        }
    }
}
