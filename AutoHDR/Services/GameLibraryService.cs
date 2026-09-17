using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoHDR.Models;
using Microsoft.Win32;

namespace AutoHDR.Services;

/// <summary>
/// Discovers installed games (Steam / Epic / XboxGames / custom folders),
/// persists library to %AppData%\AutoHDR\games.json, and answers process lookups.
/// </summary>
public sealed class GameLibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] HelperNameFragments =
    {
        "uninstall", "unins00", "setup", "installer", "crash", "crashhandler",
        "unitycrashhandler", "easyanticheat", "eac_launcher", "redist",
        "vcredist", "directx", "dxsetup", "dotnet", "cefsharp", "report",
        "crashpad", "crashreporter", "notification_helper", "steamerrorreporter",
        "vc_redist", "physx", "oalinst", "dotnetfx", "prereq", "bootstrapper",
        "updater", "patcher", "launcher_helper",
    };

    private static readonly string[] PreferSkipIfBetterExists =
    {
        "launcher", "start", "bootstrap", "crash",
    };

    private readonly object _gate = new();
    private GameLibraryStore _store = new();
    private DateTime _lastScanUtc = DateTime.MinValue;

    public string LibraryDirectory { get; }
    public string LibraryPath { get; }

    public GameLibraryService()
    {
        LibraryDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AutoHDR");
        LibraryPath = Path.Combine(LibraryDirectory, "games.json");
    }

    public IReadOnlyList<GameEntry> Games
    {
        get { lock (_gate) return _store.Games.ToList(); }
    }

    public IReadOnlyList<string> CustomFolders
    {
        get { lock (_gate) return _store.CustomFolders.ToList(); }
    }

    public int EnabledCount
    {
        get { lock (_gate) return _store.Games.Count(g => g.Enabled); }
    }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(LibraryDirectory);
                if (!File.Exists(LibraryPath))
                {
                    _store = new GameLibraryStore();
                    SaveUnlocked();
                    return;
                }

                var json = File.ReadAllText(LibraryPath);
                _store = JsonSerializer.Deserialize<GameLibraryStore>(json, JsonOptions)
                         ?? new GameLibraryStore();
                _store.Games ??= new List<GameEntry>();
                _store.CustomFolders ??= new List<string>();
                NormalizeStore(_store);
            }
            catch (Exception ex)
            {
                AppLog.Error("GameLibraryService.Load failed", ex);
                _store = new GameLibraryStore();
            }
        }
    }

    public void Save()
    {
        lock (_gate) SaveUnlocked();
    }

    private void SaveUnlocked()
    {
        try
        {
            NormalizeStore(_store);
            Directory.CreateDirectory(LibraryDirectory);
            var json = JsonSerializer.Serialize(_store, JsonOptions);
            File.WriteAllText(LibraryPath, json);
        }
        catch (Exception ex)
        {
            AppLog.Error("GameLibraryService.Save failed", ex);
        }
    }

    /// <summary>
    /// Scan discovery sources and merge into the library.
    /// New games default to Enabled=true; existing keep On/Off; missing installs stay with Enabled preserved.
    /// </summary>
    public int Scan(bool force = false)
    {
        lock (_gate)
        {
            if (!force && (DateTime.UtcNow - _lastScanUtc).TotalMinutes < 2)
                return 0;

            var discovered = new List<GameEntry>();
            try { discovered.AddRange(ScanSteam()); }
            catch (Exception ex) { AppLog.Warn($"Steam scan failed: {ex.Message}"); }
            try { discovered.AddRange(ScanEpic()); }
            catch (Exception ex) { AppLog.Warn($"Epic scan failed: {ex.Message}"); }
            try { discovered.AddRange(ScanXbox()); }
            catch (Exception ex) { AppLog.Warn($"Xbox scan failed: {ex.Message}"); }
            try { discovered.AddRange(ScanCustomFolders(_store.CustomFolders)); }
            catch (Exception ex) { AppLog.Warn($"Custom folder scan failed: {ex.Message}"); }

            int added = MergeDiscovered(discovered);
            _lastScanUtc = DateTime.UtcNow;
            SaveUnlocked();
            AppLog.Info($"Game library scan: {discovered.Count} found, {added} new, total {_store.Games.Count}.");
            return added;
        }
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            var g = _store.Games.FirstOrDefault(x => x.Id == id);
            if (g == null) return;
            g.Enabled = enabled;
            SaveUnlocked();
        }
    }

    public void SetAllEnabled(IEnumerable<(string Id, bool Enabled)> updates)
    {
        lock (_gate)
        {
            var map = updates.ToDictionary(u => u.Id, u => u.Enabled, StringComparer.Ordinal);
            foreach (var g in _store.Games)
            {
                if (map.TryGetValue(g.Id, out bool en))
                    g.Enabled = en;
            }
            SaveUnlocked();
        }
    }

    public bool AddCustomFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        folder = Path.GetFullPath(folder.Trim());
        lock (_gate)
        {
            if (_store.CustomFolders.Any(f =>
                    string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
                return false;
            _store.CustomFolders.Add(folder);
            SaveUnlocked();
        }
        Scan(force: true);
        return true;
    }

    public bool RemoveCustomFolder(string folder)
    {
        lock (_gate)
        {
            int removed = _store.CustomFolders.RemoveAll(f =>
                string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            SaveUnlocked();
            return true;
        }
    }

    public GameEntry? AddManualExe(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return null;
        exePath = Path.GetFullPath(exePath);
        string exeName = ConfigService.NormalizeExeName(Path.GetFileName(exePath));
        string name = exeName;
        var entry = MakeEntry(name, exeName, exePath, "Manual");
        lock (_gate)
        {
            var existing = FindExisting(entry);
            if (existing != null)
            {
                existing.ExePath = entry.ExePath;
                existing.LastSeenUtc = DateTime.UtcNow.ToString("o");
                SaveUnlocked();
                return existing;
            }
            entry.Enabled = true;
            _store.Games.Add(entry);
            SaveUnlocked();
            return entry;
        }
    }

    /// <summary>
    /// Find an enabled library game whose process is currently running.
    /// Prefers path match when MainModule is accessible.
    /// </summary>
    public (GameEntry Game, int ProcessId)? FindRunningEnabledGame()
    {
        List<GameEntry> enabled;
        lock (_gate)
            enabled = _store.Games.Where(g => g.Enabled && !string.IsNullOrWhiteSpace(g.ExeName)).ToList();

        if (enabled.Count == 0)
            return null;

        // Group by exe name for fewer GetProcessesByName calls
        foreach (var group in enabled.GroupBy(g => g.ExeName, StringComparer.OrdinalIgnoreCase))
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(group.Key); }
            catch { continue; }

            foreach (var proc in procs)
            {
                try
                {
                    if (proc.HasExited) continue;
                    string? path = null;
                    try { path = proc.MainModule?.FileName; } catch { /* access denied OK */ }

                    GameEntry? match = null;
                    if (!string.IsNullOrEmpty(path))
                    {
                        match = group.FirstOrDefault(g =>
                            !string.IsNullOrEmpty(g.ExePath) &&
                            string.Equals(
                                Path.GetFullPath(g.ExePath),
                                Path.GetFullPath(path),
                                StringComparison.OrdinalIgnoreCase));
                    }

                    match ??= group.FirstOrDefault(g =>
                        string.IsNullOrEmpty(g.ExePath) ||
                        string.Equals(
                            ConfigService.NormalizeExeName(Path.GetFileName(g.ExePath)),
                            group.Key,
                            StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                        return (match, proc.Id);
                }
                catch
                {
                    /* skip process */
                }
                finally
                {
                    try { proc.Dispose(); } catch { /* ignore */ }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True if the given process name belongs to an enabled library game
    /// (used to avoid double-enable via fullscreen fallback).
    /// </summary>
    public bool IsEnabledLibraryExe(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        string n = ConfigService.NormalizeExeName(exeName);
        lock (_gate)
            return _store.Games.Any(g =>
                g.Enabled &&
                string.Equals(g.ExeName, n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True if exe is in the library at all (enabled or not) — fullscreen fallback
    /// should skip disabled library entries so Off means Off.
    /// </summary>
    public bool IsLibraryExe(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        string n = ConfigService.NormalizeExeName(exeName);
        lock (_gate)
            return _store.Games.Any(g =>
                string.Equals(g.ExeName, n, StringComparison.OrdinalIgnoreCase));
    }

    // ── Discovery ──────────────────────────────────────────────────────────

    private static List<GameEntry> ScanSteam()
    {
        var result = new List<GameEntry>();
        var steamRoots = FindSteamRoots();
        var libraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in steamRoots)
        {
            libraryPaths.Add(root);
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                vdf = Path.Combine(root, "config", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                continue;

            foreach (var p in ParseLibraryFoldersVdf(vdf))
            {
                if (Directory.Exists(p))
                    libraryPaths.Add(p);
            }
        }

        foreach (var lib in libraryPaths)
        {
            string common = Path.Combine(lib, "steamapps", "common");
            if (!Directory.Exists(common))
                continue;

            // Optional: map installdir → display name from appmanifest
            var namesByInstallDir = ReadSteamManifestNames(Path.Combine(lib, "steamapps"));

            DirectoryInfo[] gameDirs;
            try { gameDirs = new DirectoryInfo(common).GetDirectories(); }
            catch { continue; }

            foreach (var dir in gameDirs)
            {
                try
                {
                    var exe = PickBestExe(dir.FullName, maxDepth: 3);
                    if (exe == null) continue;

                    string exeName = ConfigService.NormalizeExeName(Path.GetFileName(exe));
                    if (IsHelperExe(exeName)) continue;

                    string display = namesByInstallDir.TryGetValue(dir.Name, out var n) && !string.IsNullOrWhiteSpace(n)
                        ? n
                        : HumanizeFolderName(dir.Name);

                    result.Add(MakeEntry(display, exeName, exe, "Steam"));
                }
                catch
                {
                    /* skip folder */
                }
            }
        }

        return Deduplicate(result);
    }

    private static List<GameEntry> ScanEpic()
    {
        var result = new List<GameEntry>();
        string manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests))
            return result;

        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                string? install = GetJsonString(root, "InstallLocation");
                string? launch = GetJsonString(root, "LaunchExecutable");
                string? display = GetJsonString(root, "DisplayName")
                                  ?? GetJsonString(root, "AppName");
                if (string.IsNullOrWhiteSpace(install) || string.IsNullOrWhiteSpace(launch))
                    continue;

                string exePath = Path.Combine(install, launch.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(exePath))
                    continue;

                string exeName = ConfigService.NormalizeExeName(Path.GetFileName(exePath));
                if (IsHelperExe(exeName)) continue;

                result.Add(MakeEntry(
                    string.IsNullOrWhiteSpace(display) ? exeName : display!,
                    exeName,
                    exePath,
                    "Epic"));
            }
            catch
            {
                /* skip item */
            }
        }

        return Deduplicate(result);
    }

    private static List<GameEntry> ScanXbox()
    {
        var result = new List<GameEntry>();
        var roots = new List<string>();

        // Common XboxGames roots on fixed drives
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "XboxGames"));
            }
        }
        catch { /* ignore */ }

        string? userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
            roots.Add(Path.Combine(userProfile, "XboxGames"));

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
                continue;

            DirectoryInfo[] gameDirs;
            try { gameDirs = new DirectoryInfo(root).GetDirectories(); }
            catch { continue; }

            foreach (var gameDir in gameDirs)
            {
                try
                {
                    string content = Path.Combine(gameDir.FullName, "Content");
                    string scanRoot = Directory.Exists(content) ? content : gameDir.FullName;
                    var exe = PickBestExe(scanRoot, maxDepth: 4);
                    if (exe == null) continue;

                    string exeName = ConfigService.NormalizeExeName(Path.GetFileName(exe));
                    if (IsHelperExe(exeName)) continue;

                    result.Add(MakeEntry(HumanizeFolderName(gameDir.Name), exeName, exe, "Xbox"));
                }
                catch
                {
                    /* skip inaccessible */
                }
            }
        }

        return Deduplicate(result);
    }

    private static List<GameEntry> ScanCustomFolders(IEnumerable<string> folders)
    {
        var result = new List<GameEntry>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                continue;

            try
            {
                // Treat each immediate subfolder as a game, plus loose exes at root
                var rootExe = PickBestExe(folder, maxDepth: 1, includeSubdirs: false);
                if (rootExe != null)
                {
                    string exeName = ConfigService.NormalizeExeName(Path.GetFileName(rootExe));
                    if (!IsHelperExe(exeName))
                        result.Add(MakeEntry(exeName, exeName, rootExe, "Custom"));
                }

                DirectoryInfo[] subdirs;
                try { subdirs = new DirectoryInfo(folder).GetDirectories(); }
                catch { continue; }

                foreach (var dir in subdirs)
                {
                    try
                    {
                        var exe = PickBestExe(dir.FullName, maxDepth: 3);
                        if (exe == null) continue;
                        string exeName = ConfigService.NormalizeExeName(Path.GetFileName(exe));
                        if (IsHelperExe(exeName)) continue;
                        result.Add(MakeEntry(HumanizeFolderName(dir.Name), exeName, exe, "Custom"));
                    }
                    catch { /* skip */ }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Custom scan '{folder}' failed: {ex.Message}");
            }
        }

        return Deduplicate(result);
    }

    // ── Merge / helpers ────────────────────────────────────────────────────

    private int MergeDiscovered(List<GameEntry> discovered)
    {
        int added = 0;
        var now = DateTime.UtcNow.ToString("o");
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var d in discovered)
        {
            seenIds.Add(d.Id);
            var existing = FindExisting(d);
            if (existing != null)
            {
                // Keep Enabled; refresh path/name/source/lastSeen
                if (!string.IsNullOrWhiteSpace(d.ExePath))
                    existing.ExePath = d.ExePath;
                if (!string.IsNullOrWhiteSpace(d.Name))
                    existing.Name = d.Name;
                if (!string.IsNullOrWhiteSpace(d.Source))
                    existing.Source = d.Source;
                existing.ExeName = d.ExeName;
                existing.LastSeenUtc = now;
                // Keep stable Id; do not overwrite Enabled
            }
            else
            {
                d.Enabled = true; // default On for newly found games
                d.LastSeenUtc = now;
                _store.Games.Add(d);
                added++;
            }
        }

        // Missing installs: keep entry, do not flip Enabled
        foreach (var g in _store.Games)
        {
            if (!seenIds.Contains(g.Id) &&
                !string.Equals(g.Source, "Manual", StringComparison.OrdinalIgnoreCase))
            {
                // leave LastSeenUtc as-is; UI shows Not found via IsFound
            }
        }

        return added;
    }

    private GameEntry? FindExisting(GameEntry candidate)
    {
        // Prefer exact id
        var byId = _store.Games.FirstOrDefault(g => g.Id == candidate.Id);
        if (byId != null) return byId;

        // Dedup by normalized exe name + path
        string candPath = NormalizePathKey(candidate.ExePath);
        string candExe = candidate.ExeName;

        return _store.Games.FirstOrDefault(g =>
        {
            string gExe = g.ExeName;
            string gPath = NormalizePathKey(g.ExePath);
            if (!string.Equals(gExe, candExe, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrEmpty(candPath) && !string.IsNullOrEmpty(gPath))
                return string.Equals(gPath, candPath, StringComparison.OrdinalIgnoreCase);
            // Same exe name + same source without path
            return string.Equals(g.Source, candidate.Source, StringComparison.OrdinalIgnoreCase)
                   && (string.IsNullOrEmpty(candPath) || string.IsNullOrEmpty(gPath));
        });
    }

    private static void NormalizeStore(GameLibraryStore store)
    {
        store.Games ??= new List<GameEntry>();
        store.CustomFolders ??= new List<string>();
        store.CustomFolders = store.CustomFolders
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var g in store.Games)
        {
            g.ExeName = ConfigService.NormalizeExeName(g.ExeName ?? "");
            g.Name = string.IsNullOrWhiteSpace(g.Name) ? g.ExeName : g.Name.Trim();
            g.Source = string.IsNullOrWhiteSpace(g.Source) ? "Custom" : g.Source.Trim();
            g.ExePath = g.ExePath?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(g.Id))
                g.Id = BuildId(g.Source, g.ExeName, g.ExePath);
        }

        // Dedup by id
        store.Games = store.Games
            .GroupBy(g => g.Id, StringComparer.Ordinal)
            .Select(grp => grp.First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static GameEntry MakeEntry(string name, string exeName, string exePath, string source)
    {
        exeName = ConfigService.NormalizeExeName(exeName);
        exePath = string.IsNullOrWhiteSpace(exePath) ? "" : Path.GetFullPath(exePath);
        return new GameEntry
        {
            Id = BuildId(source, exeName, exePath),
            Name = string.IsNullOrWhiteSpace(name) ? exeName : name.Trim(),
            ExeName = exeName,
            ExePath = exePath,
            Source = source,
            Enabled = true,
            LastSeenUtc = DateTime.UtcNow.ToString("o"),
        };
    }

    private static string BuildId(string source, string exeName, string exePath)
    {
        string key = $"{source}|{exeName}|{NormalizePathKey(exePath)}".ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string NormalizePathKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return path.Trim().ToLowerInvariant(); }
    }

    private static List<GameEntry> Deduplicate(List<GameEntry> list)
    {
        return list
            .GroupBy(g => g.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private static string? GetJsonString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
            return p.GetString();
        return null;
    }

    private static string HumanizeFolderName(string name)
    {
        // "007_First_Light" → "007 First Light"
        name = name.Replace('_', ' ').Replace('-', ' ');
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return name;
    }

    // ── Exe picking ────────────────────────────────────────────────────────

    private static string? PickBestExe(string root, int maxDepth, bool includeSubdirs = true)
    {
        var candidates = new List<FileInfo>();
        CollectExes(root, 0, maxDepth, includeSubdirs, candidates);
        if (candidates.Count == 0)
            return null;

        // Filter obvious helpers
        var filtered = candidates
            .Where(f => !IsHelperExe(ConfigService.NormalizeExeName(f.Name)))
            .ToList();
        if (filtered.Count == 0)
            filtered = candidates; // fall back if only helpers found

        // Prefer non-launcher-ish names when alternatives exist
        var preferred = filtered
            .Where(f => !PreferSkipIfBetterExists.Any(frag =>
                f.Name.Contains(frag, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (preferred.Count > 0)
            filtered = preferred;

        // Prefer exe in root / shallow depth, then largest file
        return filtered
            .OrderByDescending(f =>
            {
                try { return f.Length; } catch { return 0L; }
            })
            .ThenBy(f => f.FullName.Length)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }

    private static void CollectExes(string dir, int depth, int maxDepth, bool includeSubdirs, List<FileInfo> sink)
    {
        if (depth > maxDepth) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.exe"))
            {
                try { sink.Add(new FileInfo(file)); }
                catch { /* skip */ }
            }

            if (!includeSubdirs || depth >= maxDepth)
                return;

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                // Skip heavy / useless trees
                if (name.Equals("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Redist", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Engine", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    continue;
                CollectExes(sub, depth + 1, maxDepth, includeSubdirs, sink);
            }
        }
        catch
        {
            /* inaccessible */
        }
    }

    private static bool IsHelperExe(string exeNameWithoutExt)
    {
        if (string.IsNullOrWhiteSpace(exeNameWithoutExt)) return true;
        string n = exeNameWithoutExt.ToLowerInvariant();
        foreach (var frag in HelperNameFragments)
        {
            if (n.Contains(frag))
                return true;
        }
        // UE shipping helpers when name is exactly like GameName-Win64-Shipping — keep those
        // (they ARE the game). Only skip bare "Win64Shipping" style helpers already covered.
        return false;
    }

    // ── Steam paths / VDF ──────────────────────────────────────────────────

    private static List<string> FindSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = path.Trim().TrimEnd('\\', '/');
                if (Directory.Exists(path))
                    roots.Add(Path.GetFullPath(path));
            }
            catch { /* ignore */ }
        }

        try
        {
            using var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            TryAdd(hkcu?.GetValue("SteamPath") as string);
            TryAdd(hkcu?.GetValue("SteamExe") is string exe ? Path.GetDirectoryName(exe) : null);
        }
        catch { /* ignore */ }

        try
        {
            using var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                             ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            TryAdd(hklm?.GetValue("InstallPath") as string);
        }
        catch { /* ignore */ }

        TryAdd(@"C:\Program Files (x86)\Steam");
        TryAdd(@"C:\Program Files\Steam");
        TryAdd(@"D:\Steam");
        TryAdd(@"D:\SteamLibrary");
        TryAdd(@"E:\Steam");
        TryAdd(@"E:\SteamLibrary");

        string? pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf))
            TryAdd(Path.Combine(pf, "Steam"));

        return roots.ToList();
    }

    private static IEnumerable<string> ParseLibraryFoldersVdf(string vdfPath)
    {
        string text;
        try { text = File.ReadAllText(vdfPath); }
        catch { yield break; }

        // Match "path" "X:\..." entries (libraryfolders.vdf)
        var matches = Regex.Matches(
            text,
            "\"path\"\\s*\"([^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (Match m in matches)
        {
            string p = m.Groups[1].Value.Replace(@"\\", @"\");
            if (!string.IsNullOrWhiteSpace(p))
                yield return p;
        }
    }

    private static Dictionary<string, string> ReadSteamManifestNames(string steamappsDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(steamappsDir))
            return map;

        try
        {
            foreach (var file in Directory.EnumerateFiles(steamappsDir, "appmanifest_*.acf"))
            {
                try
                {
                    string text = File.ReadAllText(file);
                    string? installdir = MatchVdfString(text, "installdir");
                    string? name = MatchVdfString(text, "name");
                    if (!string.IsNullOrWhiteSpace(installdir) && !string.IsNullOrWhiteSpace(name))
                        map[installdir!] = name!;
                }
                catch { /* skip */ }
            }
        }
        catch { /* ignore */ }

        return map;
    }

    private static string? MatchVdfString(string text, string key)
    {
        var m = Regex.Match(
            text,
            $"\"{Regex.Escape(key)}\"\\s*\"([^\"]*)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : null;
    }
}
