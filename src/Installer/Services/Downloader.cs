using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace VibelightValley.Installer.Services;

/// <summary>An update check that failed for a reason the user can act on; the message is shown as-is.</summary>
public sealed class UpdateCheckException : Exception
{
    public UpdateCheckException(string message, Exception inner = null) : base(message, inner) { }
}

/// <summary>HTTPS downloads into memory, verified against a SHA-256 before anything is written to disk.</summary>
public static class Downloader
{
    public const string Repo = "TheBacon00/vibelight-valley";
    public const string ManifestUrl = "https://github.com/" + Repo + "/releases/latest/download/manifest.json";
    public const string RepoUrl = "https://github.com/" + Repo;

    private static readonly HttpClient Http;

    static Downloader()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(5) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("VibelightValley-Installer/" + App.Version);
    }

    /// <summary>
    /// The latest release's manifest. VIBELIGHT_MANIFEST (a local file path or URL) overrides it for testing a release
    /// before publishing it.
    /// </summary>
    public static async Task<Manifest> FetchManifestAsync()
    {
        var url = Environment.GetEnvironmentVariable("VIBELIGHT_MANIFEST");
        string json;
        bool local = !string.IsNullOrEmpty(url) && File.Exists(url);
        if (local)
        {
            Log.Warn($"Using local manifest {url} (VIBELIGHT_MANIFEST)");
            json = File.ReadAllText(url);
        }
        else
        {
            url = string.IsNullOrEmpty(url) ? ManifestUrl : url;
            Log.Info($"Fetching {url}");
            // Cache-buster: GitHub's CDN may otherwise hand out an old 'latest' for a few minutes.
            HttpResponseMessage resp;
            try { resp = await Http.GetAsync(url + (url.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                throw new UpdateCheckException("can't reach GitHub - check your internet connection", ex);
            }
            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    throw new UpdateCheckException("the latest GitHub release has no manifest.json - no installer-ready release is published yet");
                if (!resp.IsSuccessStatusCode)
                    throw new UpdateCheckException($"GitHub answered {(int)resp.StatusCode} {resp.ReasonPhrase}");
                json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }
        var m = Manifest.Parse(json, allowLocalFiles: local);
        Log.Info($"Latest release: v{m.Version} (BepInEx {m.BepInEx.Version}, installer {m.Installer?.Version ?? "-"})");
        return m;
    }

    /// <summary>Downloads a manifest package (or a local file when its URL is a file path in a test manifest).</summary>
    public static async Task<byte[]> DownloadAsync(Manifest.Package p, string what, IProgress<double> progress)
    {
        byte[] data;
        if (File.Exists(p.Url)) data = File.ReadAllBytes(p.Url);
        else
        {
            Log.Info($"Downloading {what}: {p.Url}");
            using var resp = await Http.GetAsync(p.Url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? p.Size;
            using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var ms = new MemoryStream(total > 0 ? (int)total : 1 << 20);
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
            {
                ms.Write(buf, 0, n);
                if (total > 0) progress?.Report(Math.Min(1.0, (double)ms.Length / total));
            }
            data = ms.ToArray();
        }

        var hash = Sha256(data);
        if (!hash.Equals(p.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {what} download failed verification (SHA-256 {hash}, expected {p.Sha256}). Try again.");
        Log.Info($"{what}: {data.Length:N0} bytes, SHA-256 verified");
        progress?.Report(1);
        return data;
    }

    public static string Sha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
    }
}
