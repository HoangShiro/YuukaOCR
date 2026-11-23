using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YuukaOCR.Core.Logging;

namespace YuukaOCR.Core.Update;

/// <summary>One downloadable file of a release.</summary>
public sealed record UpdateAsset(string Name, string Url, long Size);

/// <summary>
/// Result of asking GitHub for the latest release. <see cref="MessageKey"/> is a localization key, never
/// a sentence: the service has no business deciding what language the user reads.
/// </summary>
public sealed record UpdateCheck(
    bool Completed,
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    UpdateAsset? Asset,
    string MessageKey,
    string? MessageArg)
{
    /// <summary>True when a release is newer <b>and</b> has something to install.</summary>
    public bool CanInstall => UpdateAvailable && Asset is not null;

    public static UpdateCheck Failed(string current, string key, string? arg = null) =>
        new(false, current, null, false, null, key, arg);
}

/// <summary>Result of downloading the release asset.</summary>
public sealed record UpdateDownload(bool Success, string? Path, string MessageKey, string? MessageArg);

/// <summary>
/// Application updates from GitHub Releases.
///
/// Deliberately separate from <see cref="GitUpdateService"/>: that one pulls commits in a git checkout,
/// this one downloads a published zip. A dev checkout usually has no releases, a published build has no
/// <c>.git</c> — the two mechanisms never fight, and the settings window offers whichever applies.
///
/// Everything is failure-tolerant: no exception escapes, and the repository is private, so the token is
/// optional and a missing one produces an explanatory message instead of an error dialog.
/// </summary>
public sealed class GitHubUpdateService : IDisposable
{
    // Verified against the real repository: origin = https://github.com/HoangShiro/YuukaOCR.git,
    // private, default branch main, no releases published yet.
    public const string DefaultOwner = "HoangShiro";
    public const string DefaultRepository = "YuukaOCR";

    /// <summary>The token is read from user/.env (never committed) or the process environment.</summary>
    public const string TokenKey = "GITHUB_TOKEN";

    public const string AssetSuffix = "win-x64.zip";

    // ---- localization keys -------------------------------------------------
    public const string KeyUpToDate = "upd.upToDate";
    public const string KeyAvailable = "upd.available";
    public const string KeyNoRelease = "upd.noRelease";
    public const string KeyPrivate = "upd.private";
    public const string KeyAuth = "upd.auth";
    public const string KeyRate = "upd.rate";
    public const string KeyOffline = "upd.offline";
    public const string KeyBadTag = "upd.badTag";
    public const string KeyHttp = "upd.http";
    public const string KeyNoAsset = "upd.noAsset";
    public const string KeyDownloading = "upd.downloading";
    public const string KeyDownloaded = "upd.downloaded";
    public const string KeyDownloadFailed = "upd.downloadFailed";
    public const string KeyNotZip = "upd.notZip";
    public const string KeyInstallStarted = "upd.installStarted";
    public const string KeyInstallFailed = "upd.installFailed";

    private readonly LogService _log;
    private readonly string _apiBase;
    private readonly string _token;
    private readonly HttpClient _http;

    public string Owner { get; }
    public string Repository { get; }
    public string CurrentVersion { get; }
    public AppVersion Current { get; }

    /// <summary>
    /// <paramref name="apiBase"/> exists for the tests: a loopback server can stand in for GitHub
    /// without a single line of the parsing logic changing.
    /// </summary>
    public GitHubUpdateService(
        LogService log,
        string currentVersion,
        string? token = null,
        string owner = DefaultOwner,
        string repository = DefaultRepository,
        string apiBase = "https://api.github.com")
    {
        _log = log;
        _apiBase = apiBase.TrimEnd('/');
        _token = (token ?? "").Trim();
        Owner = owner;
        Repository = repository;
        CurrentVersion = AppVersion.TryParse(currentVersion, out var parsed) ? parsed.ToString() : currentVersion;
        Current = parsed;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"YuukaOCR/{CurrentVersion}");
    }

    /// <summary>The tag the release should carry for <paramref name="version"/>, e.g. <c>v2.2.0</c>.</summary>
    public static string TagFor(AppVersion version) => "v" + version;

    // ------------------------------------------------------------------ check

    /// <summary>
    /// Asks for the newest release. Never throws: a network failure, a private repository, a rate limit
    /// and a repository without releases are all ordinary answers here.
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_apiBase}/repos/{Owner}/{Repository}/releases/latest");

        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (_token.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A private repository answers 404 to an anonymous caller, exactly like a repository
                // with no releases — the token decides which of the two explanations to show.
                return UpdateCheck.Failed(CurrentVersion, _token.Length > 0 ? KeyNoRelease : KeyPrivate);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var remaining = Header(response, "x-ratelimit-remaining");
                return UpdateCheck.Failed(CurrentVersion, remaining == "0" ? KeyRate : KeyAuth);
            }

            if (!response.IsSuccessStatusCode)
                return UpdateCheck.Failed(CurrentVersion, KeyHttp, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Interpret(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return UpdateCheck.Failed(CurrentVersion, KeyOffline);   // HttpClient timeout
        }
        catch (HttpRequestException)
        {
            return UpdateCheck.Failed(CurrentVersion, KeyOffline);
        }
        catch (Exception ex)
        {
            // Parsing surprises end here too: a broken answer must never take the app down.
            _log.ConsoleLog($"[update] {ex.GetType().Name}: {ex.Message}");
            return UpdateCheck.Failed(CurrentVersion, KeyHttp, ex.GetType().Name);
        }
    }

    /// <summary>Pure: turns a GitHub release document into a check result. Public for the tests.</summary>
    public UpdateCheck Interpret(string json)
    {
        string? tag = null;
        string? name = null;
        var assets = new List<UpdateAsset>();

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return UpdateCheck.Failed(CurrentVersion, KeyBadTag);

            if (root.TryGetProperty("tag_name", out var tagElement) && tagElement.ValueKind == JsonValueKind.String)
                tag = tagElement.GetString();

            if (root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                name = nameElement.GetString();

            if (root.TryGetProperty("assets", out var assetList) && assetList.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in assetList.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var assetName = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    var url = item.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    var size = item.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0L;

                    if (!string.IsNullOrWhiteSpace(assetName) && !string.IsNullOrWhiteSpace(url))
                        assets.Add(new UpdateAsset(assetName!, url!, size));
                }
            }
        }
        catch (JsonException)
        {
            return UpdateCheck.Failed(CurrentVersion, KeyBadTag);
        }

        if (!TryReadVersion(tag, name, assets, out var latest))
            return UpdateCheck.Failed(CurrentVersion, KeyBadTag, DescribeTag(tag, name, assets));

        var available = latest > Current;
        var asset = PickAsset(assets);

        if (!available)
            return new UpdateCheck(true, CurrentVersion, latest.ToString(), false, null, KeyUpToDate, null);

        if (asset is null)
            return new UpdateCheck(true, CurrentVersion, latest.ToString(), true, null, KeyNoAsset, latest.ToString());

        return new UpdateCheck(true, CurrentVersion, latest.ToString(), true, asset, KeyAvailable, latest.ToString());
    }

    /// <summary>
    /// The version a release claims to be. A release tag is hand-written and is not always a number:
    /// this repository publishes every build under the moving tag <c>Windows</c> and puts the version in
    /// the release name (<c>YuukaOCR v2.2</c>) and in the package name (<c>YuukaOCR.v2.2.zip</c>).
    /// Reading only the tag turned every check into "the release tag could not be read: Windows".
    ///
    /// Order: the tag, then the release name, then the asset names — the first readable version wins.
    /// </summary>
    public static bool TryReadVersion(string? tag, string? name, IReadOnlyList<UpdateAsset>? assets, out AppVersion version)
    {
        if (AppVersion.TryParse(tag, out version)) return true;
        if (AppVersion.TryParse(name, out version)) return true;

        if (TryExtractVersion(tag, out version)) return true;
        if (TryExtractVersion(name, out version)) return true;

        if (assets is not null)
        {
            foreach (var asset in assets)
                if (TryExtractVersion(asset.Name, out version))
                    return true;
        }

        version = AppVersion.Zero;
        return false;
    }

    /// <summary>
    /// Finds a dotted version anywhere in a label: <c>"YuukaOCR v2.2"</c> → 2.2,
    /// <c>"YuukaOCR-2.2.0-win-x64.zip"</c> → 2.2.0, <c>"YuukaOCR.v2.2.zip"</c> → 2.2.
    /// A token must contain a dot and must not be part of a longer word, so a label like
    /// <c>"Windows 11 build 5"</c> is not mistaken for a version.
    /// </summary>
    public static bool TryExtractVersion(string? text, out AppVersion version)
    {
        version = AppVersion.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.AsSpan().Trim();

        for (var i = 0; i < span.Length; i++)
        {
            var start = i;

            if (span[i] is 'v' or 'V')
            {
                // "v2.2" — only when the v starts a word and a digit follows it.
                if (i > 0 && char.IsLetterOrDigit(span[i - 1])) continue;
                start = i + 1;
                if (start >= span.Length || !char.IsAsciiDigit(span[start])) continue;
            }
            else if (!char.IsAsciiDigit(span[i]))
            {
                continue;
            }
            else if (i > 0 && (char.IsLetterOrDigit(span[i - 1]) || span[i - 1] == '.'))
            {
                continue;
            }

            var end = start;
            while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] == '.')) end++;

            var token = span[start..end].TrimEnd('.');
            i = end;

            if (!token.Contains('.') || !AppVersion.TryParse(token.ToString(), out var parsed)) continue;

            version = parsed;
            return true;
        }

        return false;
    }

    /// <summary>The label to quote when nothing could be read, so the message names what it saw.</summary>
    private static string DescribeTag(string? tag, string? name, IReadOnlyList<UpdateAsset> assets)
    {
        if (!string.IsNullOrWhiteSpace(tag)) return tag!;
        if (!string.IsNullOrWhiteSpace(name)) return name!;
        return assets.Count > 0 ? assets[0].Name : "";
    }

    /// <summary>Prefers the published win-x64 package, falls back to any zip, ignores everything else.</summary>
    public static UpdateAsset? PickAsset(IReadOnlyList<UpdateAsset> assets)
    {
        foreach (var asset in assets)
            if (asset.Name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase))
                return asset;

        foreach (var asset in assets)
            if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return asset;

        return null;
    }

    // --------------------------------------------------------------- download

    /// <summary>
    /// Downloads the release asset to <paramref name="targetPath"/>. Progress is 0..1 and may be null.
    /// A truncated download is rejected: installing half a package is worse than not installing one.
    /// </summary>
    public async Task<UpdateDownload> DownloadAsync(
        UpdateAsset asset,
        string targetPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
            if (_token.Length > 0)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new UpdateDownload(false, null, KeyDownloadFailed,
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));

            var total = response.Content.Headers.ContentLength ?? asset.Size;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(targetPath))
            {
                var buffer = new byte[128 * 1024];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    if (total > 0) progress?.Report(Math.Clamp((double)copied / total, 0, 1));
                }

                if (total > 0 && copied != total)
                {
                    target.Close();
                    TryDelete(targetPath);
                    return new UpdateDownload(false, null, KeyDownloadFailed, $"{copied}/{total}");
                }
            }

            // A zip starts with "PK". GitHub redirects to a CDN, and a captive portal would happily
            // return HTML with status 200.
            if (!LooksLikeZip(targetPath))
            {
                TryDelete(targetPath);
                return new UpdateDownload(false, null, KeyNotZip, null);
            }

            progress?.Report(1);
            _log.ConsoleLog($"[update] downloaded {Path.GetFileName(targetPath)} ({new FileInfo(targetPath).Length} bytes)");
            return new UpdateDownload(true, targetPath, KeyDownloaded, Path.GetFileName(targetPath));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(targetPath);
            _log.ConsoleLog($"[update] download failed: {ex.GetType().Name}: {ex.Message}");
            return new UpdateDownload(false, null, KeyDownloadFailed, ex.Message);
        }
    }

    // ---------------------------------------------------------------- install

    /// <summary>
    /// Unpacks the package over the install folder from a detached PowerShell script and restarts the
    /// app. The script waits for this process to exit first, so no file is replaced while it is in use.
    ///
    /// <c>user/</c> is removed from the staged copy before anything is written, so an update can never
    /// touch settings, API keys, action presets or history — even if a package accidentally contains them.
    /// </summary>
    public UpdateDownload Install(string zipPath, string installDir, string exeName, int processId)
    {
        try
        {
            var folder = Path.GetDirectoryName(zipPath)!;
            var script = Path.Combine(folder, "install-update.ps1");
            var log = Path.Combine(folder, "install.log");

            File.WriteAllText(script, InstallerScript(zipPath, installDir, exeName, processId, log), Encoding.UTF8);

            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File {Quote(script)}"
            };

            Process.Start(start);
            _log.ConsoleLog($"[update] installer started for {exeName} in {installDir}");
            return new UpdateDownload(true, zipPath, KeyInstallStarted, exeName);
        }
        catch (Exception ex)
        {
            _log.ConsoleLog($"[update] could not start the installer: {ex.Message}");
            return new UpdateDownload(false, null, KeyInstallFailed, ex.Message);
        }
    }

    /// <summary>The PowerShell script that replaces the files. Public so the tests can inspect it.</summary>
    public static string InstallerScript(string zipPath, string installDir, string exeName, int processId, string logPath) =>
        string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $"$appPid = {processId.ToString(CultureInfo.InvariantCulture)}",
            $"$zip = {Quote(zipPath)}",
            $"$dest = {Quote(installDir)}",
            $"$exe = {Quote(exeName)}",
            $"$log = {Quote(logPath)}",
            "function Note([string]$text) {",
            "  try { Add-Content -LiteralPath $log -Value ((Get-Date -Format s) + ' ' + $text) } catch { }",
            "}",
            "try {",
            "  Wait-Process -Id $appPid -Timeout 60 -ErrorAction SilentlyContinue",
            "  $stage = Join-Path ([System.IO.Path]::GetTempPath()) ('yuukaocr-stage-' + [guid]::NewGuid().ToString('n'))",
            "  Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force",
            "  if (-not (Test-Path -LiteralPath (Join-Path $stage $exe))) { throw \"the package has no $exe\" }",
            "  # User data is never part of an update: drop it from the staged copy before touching $dest.",
            "  $userDir = Join-Path $stage 'user'",
            "  if (Test-Path -LiteralPath $userDir) { Remove-Item -LiteralPath $userDir -Recurse -Force }",
            "  Get-ChildItem -LiteralPath $stage -Force | ForEach-Object {",
            "    Copy-Item -LiteralPath $_.FullName -Destination $dest -Recurse -Force",
            "  }",
            "  Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue",
            "  Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue",
            "  Note 'installed'",
            "  Start-Process -FilePath (Join-Path $dest $exe) -WorkingDirectory $dest",
            "} catch {",
            "  Note ('failed: ' + $_.Exception.Message)",
            "}");

    public void Dispose() => _http.Dispose();

    // ---------------------------------------------------------------- helpers

    /// <summary>Reads <c>owner/repo</c> out of an https or ssh git remote. False for anything else.</summary>
    public static bool TryParseRepository(string? remoteUrl, out string owner, out string repository)
    {
        owner = "";
        repository = "";

        if (string.IsNullOrWhiteSpace(remoteUrl)) return false;

        var marker = remoteUrl.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;

        var rest = remoteUrl[(marker + "github.com".Length)..].TrimStart(':', '/').TrimEnd('/');
        if (rest.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) rest = rest[..^4];

        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        owner = parts[0];
        repository = parts[1];
        return owner.Length > 0 && repository.Length > 0;
    }

    /// <summary>Downloads land in %TEMP%\yuukaocr-update, next to the installer script and its log.</summary>
    public static string DownloadFolder =>
        Path.Combine(Path.GetTempPath(), "yuukaocr-update");

    public static string PackagePath(string version) =>
        Path.Combine(DownloadFolder, $"YuukaOCR-{version}-{AssetSuffix}");

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static bool LooksLikeZip(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[4];
            return stream.Read(header, 0, 4) == 4 && header[0] == 'P' && header[1] == 'K';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Still locked by the failed write; the temp folder is cleaned by Windows eventually.
        }
    }

    /// <summary>PowerShell single-quoted literal: only the quote itself needs escaping (doubled).</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
