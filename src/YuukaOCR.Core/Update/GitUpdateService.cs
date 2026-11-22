using System.Diagnostics;
using System.Text;
using YuukaOCR.Core.Logging;
using YuukaOCR.Core.Localization;

namespace YuukaOCR.Core.Update;

public sealed record UpdateCheckResult(bool UpdateAvailable, string Message, string LocalHash, string RemoteHash);

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
    public string Combined => (Output + "\n" + Error).Trim();
}

/// <summary>
/// Git-based self update. Unlike the old implementation this never runs `git reset --hard`:
/// local changes are stashed, the pull is fast-forward only, and a failed pull restores the stash.
/// </summary>
public sealed class GitUpdateService
{
    private readonly LogService _log;
    private readonly string _repoRoot;

    /// <summary>
    /// <c>origin</c> of a checkout, or "" when this is not a git working copy. Read straight from the
    /// config file — no <c>git</c> process, so it also works when git is not installed.
    /// </summary>
    public static string RemoteUrl(string repoRoot)
    {
        try
        {
            var config = Path.Combine(repoRoot, ".git", "config");
            if (!File.Exists(config)) return "";

            var lines = File.ReadAllLines(config);
            var inOrigin = false;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inOrigin = line.Equals("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inOrigin) continue;

                var split = line.IndexOf('=');
                if (split <= 0) continue;
                if (!line[..split].Trim().Equals("url", StringComparison.OrdinalIgnoreCase)) continue;

                return line[(split + 1)..].Trim();
            }
        }
        catch (IOException)
        {
            // Unreadable config is the same as no remote: the caller falls back to the built-in name.
        }
        return "";
    }

    /// <summary>True when the folder is a git checkout, i.e. the pull-based update can work at all.</summary>
    public static bool IsCheckout(string repoRoot) => Directory.Exists(Path.Combine(repoRoot, ".git"));

    public GitUpdateService(LogService log, string? repoRoot = null)
    {
        _log = log;
        _repoRoot = repoRoot ?? AppPaths.Root;
    }

    public string RepoRoot => _repoRoot;

    public bool IsRepository => Directory.Exists(Path.Combine(_repoRoot, ".git"));

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRepository)
            return new UpdateCheckResult(false, L.T("update.notRepo"), "", "");

        var fetch = await RunGitAsync("fetch --quiet --prune", cancellationToken).ConfigureAwait(false);
        if (!fetch.Success)
            return new UpdateCheckResult(false, L.F("update.fetchFailed", Short(fetch.Combined)), "", "");

        var local = await RunGitAsync("rev-parse HEAD", cancellationToken).ConfigureAwait(false);
        var remote = await RunGitAsync("rev-parse @{u}", cancellationToken).ConfigureAwait(false);

        if (!local.Success || !remote.Success)
            return new UpdateCheckResult(false, L.T("update.noUpstream"), "", "");

        var localHash = local.Output.Trim();
        var remoteHash = remote.Output.Trim();

        if (localHash == remoteHash)
            return new UpdateCheckResult(false, L.T("update.upToDate"), localHash, remoteHash);

        var behind = await RunGitAsync("rev-list --count HEAD..@{u}", cancellationToken).ConfigureAwait(false);
        var count = int.TryParse(behind.Output.Trim(), out var parsed) ? parsed : 0;

        return count > 0
            ? new UpdateCheckResult(true, L.F("update.behind", count), localHash, remoteHash)
            : new UpdateCheckResult(false, L.T("update.ahead"), localHash, remoteHash);
    }

    /// <summary>Stash → pull --ff-only → (on failure) restore the stash.</summary>
    public async Task<CommandResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRepository)
            return new CommandResult(1, "", L.T("update.notRepoShort"));

        _log.ConsoleLog(L.T("update.applying"));

        var dirty = await RunGitAsync("status --porcelain", cancellationToken).ConfigureAwait(false);
        var hasLocalChanges = dirty.Success && dirty.Output.Trim().Length > 0;
        var stashed = false;

        if (hasLocalChanges)
        {
            var stash = await RunGitAsync("stash push -u -m \"YuukaOCR auto-update\"", cancellationToken)
                .ConfigureAwait(false);
            stashed = stash.Success;
            if (!stashed)
                _log.ConsoleLog(L.F("update.stashFailed", Short(stash.Combined)));
        }

        var pull = await RunGitAsync("pull --ff-only", cancellationToken).ConfigureAwait(false);

        if (!pull.Success && stashed)
        {
            await RunGitAsync("stash pop", cancellationToken).ConfigureAwait(false);
            _log.ConsoleLog(L.T("update.restored"));
            return pull;
        }

        if (pull.Success)
        {
            _log.ConsoleLog(L.T("update.done"));
            if (stashed) await RunGitAsync("stash pop", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _log.ConsoleLog(L.F("update.failed", Short(pull.Combined)));
        }

        return pull;
    }

    private Task<CommandResult> RunGitAsync(string arguments, CancellationToken cancellationToken)
        => RunAsync("git", arguments, _repoRoot, cancellationToken);

    private static async Task<CommandResult> RunAsync(string file, string arguments, string workdir,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = arguments,
                    WorkingDirectory = workdir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            process.Start();

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                return new CommandResult(-1, "", "git timeout");
            }

            return new CommandResult(process.ExitCode, await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, "", ex.Message);
        }
    }

    private static string Short(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 160 ? single : single[..160] + "...";
    }
}
