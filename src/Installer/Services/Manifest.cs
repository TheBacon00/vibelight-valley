using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace VibelightValley.Installer.Services;

/// <summary>
/// manifest.json, published with every GitHub release (tools/package.ps1 writes it). It pins the URL and SHA-256 of
/// everything the installer downloads, so the installer never has to trust a download it cannot verify.
/// </summary>
[DataContract]
public sealed class Manifest
{
    public const int SupportedSchema = 1;

    [DataMember(Name = "schema")] public int Schema { get; set; }
    [DataMember(Name = "version")] public string Version { get; set; }
    [DataMember(Name = "releaseUrl")] public string ReleaseUrl { get; set; }
    [DataMember(Name = "mod")] public Package Mod { get; set; }
    [DataMember(Name = "bepinex")] public Package BepInEx { get; set; }
    [DataMember(Name = "installer")] public Package Installer { get; set; }

    [DataContract]
    public sealed class Package
    {
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "url")] public string Url { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
    }

    /// <param name="allowLocalFiles">Test manifests (VIBELIGHT_MANIFEST) may point at local files instead of HTTPS URLs.</param>
    public static Manifest Parse(string json, bool allowLocalFiles = false)
    {
        _allowLocal = allowLocalFiles;
        Manifest m;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            m = (Manifest)new DataContractJsonSerializer(typeof(Manifest)).ReadObject(ms);

        if (m.Schema != SupportedSchema)
            throw new InvalidDataException($"The release manifest has format {m.Schema}; this installer understands {SupportedSchema}. Download the latest installer from GitHub.");
        Check(m.Mod, "mod");
        Check(m.BepInEx, "bepinex");
        if (m.Installer != null) Check(m.Installer, "installer");
        return m;
    }

    [ThreadStatic] private static bool _allowLocal;

    private static void Check(Package p, string what)
    {
        if (p == null) throw new InvalidDataException($"The release manifest has no '{what}' entry.");
        if (_allowLocal && p.Url != null && File.Exists(p.Url)) { }
        else if (p.Url == null || !p.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The release manifest's '{what}' URL is not HTTPS.");
        if (p.Sha256 == null || !Regex.IsMatch(p.Sha256, "^[0-9a-fA-F]{64}$"))
            throw new InvalidDataException($"The release manifest's '{what}' SHA-256 is missing or malformed.");
    }
}

/// <summary>Semantic-version comparison: "v1.2.3", "1.2.3", "1.2.3-beta.1" (a release beats its prereleases).</summary>
public static class SemVer
{
    public static int Compare(string a, string b)
    {
        Split(a, out var va, out var pa);
        Split(b, out var vb, out var pb);
        int c = va.CompareTo(vb);
        if (c != 0) return c;
        if (pa == pb) return 0;
        if (pa == null) return 1;
        if (pb == null) return -1;
        return string.CompareOrdinal(pa, pb);
    }

    public static bool IsNewer(string candidate, string current) =>
        candidate != null && (current == null || Compare(candidate, current) > 0);

    private static void Split(string s, out Version v, out string pre)
    {
        s = (s ?? "0").Trim().TrimStart('v', 'V');
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s.Substring(0, plus);
        int dash = s.IndexOf('-');
        pre = dash >= 0 ? s.Substring(dash + 1) : null;
        var core = dash >= 0 ? s.Substring(0, dash) : s;
        if (!System.Version.TryParse(core.Contains(".") ? core : core + ".0", out v)) v = new Version(0, 0);
        // Normalise "1.2" vs "1.2.0" so they compare equal.
        v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }
}
