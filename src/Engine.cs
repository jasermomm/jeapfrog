using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

sealed class Game
{
    public string Id, Name, Version, Region, SourceName;
    public Uri Download;
}

sealed class Engine
{
    public const string Release = "tadpole-04092026-0001";
    public const string ReleasesUrl = "https://github.com/BubbletopTag/tadpole/releases";
    public static readonly Uri CatalogUrl = new Uri("https://lolroms.com/leapfrog/explorer");
    const string SetupHash = "79deeb5ee5bddd347055e7f459e9c300dd1d1ab277d6734e37411e1b814f158c";
    const string MusicHash = "16157d6f1f20dd3f4e074385f1997c59a9eb68dc";
    const string CatalogPath = "/LeapFrog/Explorer/";
    const int DownloadLimit = 768 * 1024 * 1024;
    static readonly HttpClient Http = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = TimeSpan.FromMinutes(15) };

    public readonly string Data, Root, Log, Sysroot;
    readonly string bridge;
    readonly string alternate;
    readonly Dictionary<string, string> linked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    readonly object writeLock = new object();

    public Engine(string data, string existing)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Data = Path.GetFullPath(data);
        Root = Path.Combine(Data, "Engine");
        Sysroot = Path.Combine(Root, "runtime", "sysroot");
        Log = Path.Combine(Data, "jeapfrog.log");
        bridge = Path.Combine(Data, "bridge.py");
        alternate = existing;
        Directory.CreateDirectory(Data);
        var links = Path.Combine(Data, "library-links.tsv");
        if (File.Exists(links))
            foreach (var row in File.ReadLines(links))
            {
                var columns = row.Split('\t');
                if (columns.Length == 2) linked[columns[0]] = columns[1];
            }
    }

    public bool Ready { get { return IsReady(Root); } }
    public string Programs { get { return Path.Combine(Sysroot, "LF", "Bulk", "ProgramFiles"); } }
    public string GameLog { get { return Path.Combine(Root, "run", "game.log"); } }

    static bool IsReady(string root)
    {
        var rootfs = Path.Combine(root, "rootfs");
        return Directory.Exists(rootfs)
            && Directory.EnumerateDirectories(rootfs).Any(p => Directory.Exists(Path.Combine(p, "ubi_rfs")))
            && File.Exists(Path.Combine(root, "runtime", "sysroot", "LF", "Base", "bin", "AppManager"))
            && File.Exists(Path.Combine(root, "runtime", "libs", "libc.so.0"))
            && File.Exists(Path.Combine(root, "build", "deps", "python", "python.exe"))
            && File.Exists(Path.Combine(root, "glasspole", "build", "glasspole.exe"));
    }

    public static string Resource(string name)
    {
        using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (var reader = new StreamReader(input, Encoding.UTF8))
            return reader.ReadToEnd();
    }

    static bool IsCatalogUrl(Uri uri)
    {
        if (uri == null || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, CatalogUrl.Host, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !uri.AbsolutePath.StartsWith(CatalogPath, StringComparison.OrdinalIgnoreCase))
            return false;
        var name = Uri.UnescapeDataString(uri.AbsolutePath.Substring(CatalogPath.Length));
        return name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    static Game CatalogGame(string fileName, Uri link)
    {
        var raw = Path.GetFileNameWithoutExtension(fileName);
        var match = Regex.Match(raw, @"^(.*?)\s+\((USA|Canada|Europe|Japan|France|UK|Germany|Australia)\)(.*)$");
        var version = Regex.Match(raw, @"\((v?\d+(?:\.\d+)+)\)$");
        var region = match.Success ? match.Groups[2].Value : "";
        var edition = match.Success ? Regex.Replace(match.Groups[3].Value.Trim(), @"\s*\(v?\d+(?:\.\d+)+\)$", "") : "";
        return new Game
        {
            Name = match.Success ? match.Groups[1].Value : raw,
            Region = edition.Length == 0 ? region : region + " " + edition,
            Version = version.Success ? version.Groups[1].Value.TrimStart('v') : "",
            SourceName = raw,
            Download = link
        };
    }

    public static List<Game> ReadCatalog(string html)
    {
        var games = new List<Game>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(html, "href\\s*=\\s*(?:\"([^\"]+)\"|'([^']+)'|([^\\s>]+))", RegexOptions.IgnoreCase))
        {
            var encoded = m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;
            Uri link;
            if (!Uri.TryCreate(CatalogUrl, WebUtility.HtmlDecode(encoded), out link) ||
                !IsCatalogUrl(link) || !known.Add(link.AbsolutePath)) continue;
            games.Add(CatalogGame(Uri.UnescapeDataString(link.Segments.Last()), link));
        }
        return games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(g => g.Version, StringComparer.Ordinal).ToList();
    }

    public List<Game> Catalog()
    {
        var cache = Path.Combine(Data, "catalog.html");
        if (File.Exists(cache))
        {
            var fromCache = ReadCatalog(File.ReadAllText(cache));
            if (fromCache.Count > 0) return fromCache;
        }
        return Resource("catalog.txt").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(n => CatalogGame(n + ".7z",
                new Uri(CatalogUrl, CatalogPath + Uri.EscapeDataString(n + ".7z")))).ToList();
    }

    public async Task<List<Game>> RefreshCatalog()
    {
        var html = await Http.GetStringAsync(CatalogUrl).ConfigureAwait(false);
        var games = ReadCatalog(html);
        if (games.Count == 0) throw new InvalidDataException("Game catalog is unavailable.");
        File.WriteAllText(Path.Combine(Data, "catalog.html"), html, Encoding.UTF8);
        return games;
    }

    static string Field(string meta, string key)
    {
        var match = Regex.Match(meta, "^" + Regex.Escape(key) + "\\s*=\\s*\"?([^\"\\r\\n]+)", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    static string Normal(string text)
    {
        return Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    }

    public List<Game> Installed()
    {
        var games = new List<Game>();
        if (!Directory.Exists(Programs)) return games;
        foreach (var path in Directory.EnumerateDirectories(Programs))
        {
            var meta = Path.Combine(path, "meta.inf");
            if (!File.Exists(meta)) continue;
            var contents = File.ReadAllText(meta);
            var id = Path.GetFileName(path);
            var title = Field(contents, "Name");
            var entry = Field(contents, "AppSo");
            var cartridge = id.StartsWith("LST3-", StringComparison.OrdinalIgnoreCase) ||
                Field(contents, "Category").Equals("Game", StringComparison.OrdinalIgnoreCase) ||
                linked.ContainsValue(id);
            if (!cartridge || title.Length == 0 || entry.Length == 0 ||
                Path.IsPathRooted(entry) || entry.Split('/', '\\').Contains("..") ||
                !File.Exists(Path.Combine(path, entry))) continue;
            games.Add(new Game { Id = id, Name = title, Version = Field(contents, "Version"),
                Region = Field(contents, "Locale") });
        }
        return games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Game Match(Game game, IEnumerable<Game> installed)
    {
        string known;
        linked.TryGetValue(game.SourceName, out known);
        return installed.FirstOrDefault(g =>
            (g.Id == known || Normal(g.Name) == Normal(game.Name)) &&
            (game.Version.Length == 0 || game.Version == g.Version));
    }

    public void WriteLog(string text)
    {
        lock (writeLock) File.AppendAllText(Log, text + Environment.NewLine);
    }

    static string Hash(byte[] bytes, HashAlgorithm algorithm)
    {
        using (algorithm)
            return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    static string FileHash(string path)
    {
        using (var input = File.OpenRead(path))
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }

    async Task Download(Uri url, string path, Action<string, int?> progress, string label)
    {
        var temp = path + ".partial";
        try
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var length = response.Content.Headers.ContentLength;
                if (length > DownloadLimit) throw new InvalidDataException("Download is too large.");
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = File.Create(temp))
                {
                    var buffer = new byte[65536];
                    long total = 0;
                    int read;
                    int shown = -1;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > DownloadLimit) throw new InvalidDataException("Download is too large.");
                        await output.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                        if (length.HasValue && length.Value > 0)
                        {
                            int percent = (int)Math.Min(100, 100 * total / length.Value);
                            if (percent != shown) { shown = percent; progress(label, percent); }
                        }
                    }
                    if (length.HasValue && total != length.Value) throw new IOException("Download was interrupted.");
                }
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    static public string Arg(string value)
    {
        if (value == null || value.Length == 0) return "\"\"";
        if (!value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        return "\"" + Regex.Replace(value, "(\\\\*)\"|(\\\\+)$",
            m => m.Groups[2].Success ? m.Value + m.Value : m.Groups[1].Value + m.Value) + "\"";
    }

    List<string> Run(string file, IEnumerable<string> args, Action<string> output, string commandLine = null)
    {
        var lines = new List<string>();
        using (var p = new Process())
        {
            p.StartInfo = new ProcessStartInfo(file, commandLine ?? string.Join(" ", args.Select(Arg)))
            {
                WorkingDirectory = Directory.Exists(Root) ? Root : Data,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            p.StartInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            p.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            DataReceivedEventHandler collect = (sender, e) =>
            {
                if (e.Data == null) return;
                lock (lines) lines.Add(e.Data);
                if (output != null) output(e.Data);
                if (!e.Data.StartsWith("@@PROGRESS ", StringComparison.Ordinal)) WriteLog(e.Data);
            };
            p.OutputDataReceived += collect;
            p.ErrorDataReceived += collect;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException("Operation failed. See Tools → Open Log.");
        }
        return lines;
    }

    List<string> Python(Action<string> output, params string[] args)
    {
        return Run(Path.Combine(Root, "build", "deps", "python", "python.exe"), args, output);
    }

    string Tool(string name) { return Path.Combine(Root, "tools", name); }

    string FindExisting()
    {
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(alternate)) candidates.Add(alternate);
        for (int i = 0; i < 4 && directory != null; i++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, "work", "glasspole"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "Programs", "Glasspole"));
        var codex = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex");
        if (Directory.Exists(codex))
            foreach (var day in Directory.EnumerateDirectories(codex).OrderByDescending(p => p, StringComparer.Ordinal).Take(14))
                candidates.Add(Path.Combine(day, "new-chat", "work", "glasspole"));
        return candidates.FirstOrDefault(p => IsReady(p) && !string.Equals(p, Root, StringComparison.OrdinalIgnoreCase));
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var item in Directory.EnumerateFiles(from))
            File.Copy(item, Path.Combine(to, Path.GetFileName(item)), true);
        foreach (var item in Directory.EnumerateDirectories(from))
        {
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) continue;
            CopyTree(item, Path.Combine(to, Path.GetFileName(item)));
        }
    }

    void CopyExisting(string from)
    {
        Directory.CreateDirectory(Root);
        foreach (var name in new[] { "build", "glasspole", "rootfs", "runtime", "tadpole", "tools" })
            CopyTree(Path.Combine(from, name), Path.Combine(Root, name));
        foreach (var name in new[] { ".tadpole-version", "tadpole.exe", "tadpole.sh", "tadpole.png", "glasspole.png" })
            File.Copy(Path.Combine(from, name), Path.Combine(Root, name), true);
    }

    async Task GetEngine(Action<string, int?> status)
    {
        var installer = Path.Combine(Data, "Glasspole-Setup.exe");
        if (!File.Exists(installer) || FileHash(installer) != SetupHash)
        {
            var saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                     "Downloads", "Glasspole-Setup.exe");
            if (File.Exists(saved) && FileHash(saved) == SetupHash) File.Copy(saved, installer, true);
            else await Download(new Uri(ReleasesUrl + "/download/" + Release + "/Glasspole-Setup.exe"),
                                installer, status, "Downloading engine…").ConfigureAwait(false);
        }
        if (FileHash(installer) != SetupHash) throw new InvalidDataException("Engine checksum failed.");
        status("Installing engine…", null);
        var zip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
        if (File.Exists(zip))
            Run(zip, new[] { "x", "-y", "-o" + Root, installer }, null);
        else
            Run(installer, new string[0], null, "/S /D=" + Root);
        if (!File.Exists(Tool("online-update.py"))) throw new IOException("Engine installation failed.");
    }

    async Task FixEngine()
    {
        var path = Tool("musicdb.py");
        if (File.Exists(path)) return;
        var url = "https://raw.githubusercontent.com/BubbletopTag/tadpole/" + Release + "/tools/musicdb.py";
        var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
        byte[] header = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
        if (Hash(header.Concat(bytes).ToArray(), SHA1.Create()) != MusicHash)
            throw new InvalidDataException("Engine setup patch checksum failed.");
        File.WriteAllBytes(path, bytes);
    }

    public async Task Setup(string cache, bool force, Action<string, int?> status)
    {
        if (!Ready)
        {
            string existing = FindExisting();
            if (existing != null)
            {
                status("Importing existing setup…", null);
                await Task.Run(() => CopyExisting(existing)).ConfigureAwait(false);
            }
            else if (!File.Exists(Tool("online-update.py")))
                await GetEngine(status).ConfigureAwait(false);
        }
        if (!Ready || force)
        {
            status("Installing firmware…", null);
            await FixEngine().ConfigureAwait(false);
            await Task.Run(() =>
            {
                Action<string> progress = line =>
                {
                    var parts = line.Split(' ');
                    long now, max;
                    if (parts.Length == 3 && parts[0] == "@@PROGRESS" &&
                        long.TryParse(parts[1], out now) && long.TryParse(parts[2], out max) && max > 0)
                        status("Downloading firmware…", (int)Math.Min(100, 100 * now / max));
                    else if (line == "==> installing" || line.StartsWith("==> Firmware-Base"))
                        status("Installing firmware…", null);
                };
                if (string.IsNullOrEmpty(cache)) Python(progress, Tool("online-update.py"));
                else Python(progress, Tool("install-firmware.py"), cache);
            }).ConfigureAwait(false);
        }
        if (!Ready) throw new InvalidOperationException("Firmware installation failed.");
        File.WriteAllText(bridge, Resource("bridge.py"), new UTF8Encoding(false));
    }

    public async Task Import(string path, string name)
    {
        if (!File.Exists(bridge)) File.WriteAllText(bridge, Resource("bridge.py"), new UTF8Encoding(false));
        var lines = await Task.Run(() => Python(null, bridge, Root, "import", path)).ConfigureAwait(false);
        var result = lines.FirstOrDefault(line => line.StartsWith("installed ", StringComparison.Ordinal));
        if (result != null && name.IndexOfAny(new[] { '\r', '\n', '\t' }) < 0)
        {
            linked[name] = result.Substring("installed ".Length);
            File.WriteAllLines(Path.Combine(Data, "library-links.tsv"), linked.Select(p => p.Key + "\t" + p.Value));
        }
    }

    public async Task DownloadGame(Game game, Action<string, int?> status)
    {
        if (!IsCatalogUrl(game.Download)) throw new InvalidOperationException("Invalid game source.");
        var folder = Path.Combine(Data, "Downloads");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Uri.UnescapeDataString(game.Download.Segments.Last()));
        if (!File.Exists(path)) await Download(game.Download, path, status, "Downloading game…").ConfigureAwait(false);
        status("Importing game…", null);
        await Import(path, game.SourceName).ConfigureAwait(false);
    }

    public Task Play(Game game, bool software)
    {
        File.WriteAllText(bridge, Resource("bridge.py"), new UTF8Encoding(false));
        return Task.Run(() => Python(null, software ? new[] { bridge, Root, "play", game.Id, "--software" } :
                                                     new[] { bridge, Root, "play", game.Id }));
    }

    public void Stop()
    {
        var dir = Path.Combine(Root, "run");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stop.req"), "stop");
    }

    public Task Didj(Action<string, int?> status)
    {
        return Task.Run(() =>
        {
            status("Downloading Didj support…", null);
            Python(null, Tool("install-didj.py"), "--fetch-compat");
            Python(null, Tool("install-didj.py"), "--fetch-overlay");
        });
    }
}
