using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XivMcp.Connect;

/// <summary>
/// Finds the icons of AI apps installed on this PC, so the Connect tab can show the real ones without XIV MCP shipping any logos.
/// Microsoft Store apps declare theirs in AppxManifest.xml (in several sizes); for other apps the plugin asks Windows for the
/// program's icon, found through the link scheme it registered.
/// </summary>
public static class AppLogos
{
    /// <summary>The app's small logo (Square44x44Logo of the first application), relative to the package folder.</summary>
    public static string? LogoFromManifest(string manifestXml)
    {
        try
        {
            var visual = XDocument.Parse(manifestXml).Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
            return visual?.Attribute("Square44x44Logo")?.Value;
        }
        catch { return null; }
    }

    private static readonly Regex TargetSize = new(@"\.targetsize-(\d+)(_altform-unplated)?\.png$", RegexOptions.IgnoreCase);
    private static readonly Regex Scale = new(@"\.scale-(\d+)\.png$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Picks the logo file to load for <paramref name="size"/> pixels from the files next to the declared logo. Windows stores variants
    /// beside it (Logo.targetsize-64_altform-unplated.png, Logo.scale-200.png, …): prefer the unplated ones (no coloured plate behind
    /// the mark) at the smallest size that is large enough, never the light-theme variants, then the largest scale, then the file itself.
    /// </summary>
    public static string? BestLogo(string logoPath, IEnumerable<string> fileNames, int size)
    {
        var stem = Path.GetFileNameWithoutExtension(logoPath.Replace('\\', '/').Split('/')[^1]);
        var mine = fileNames.Select(f => Path.GetFileName(f)).Where(f => f.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)).ToList();

        var sized = mine.Select(f => (File: f, Match: TargetSize.Match(f))).Where(x => x.Match.Success)
                        .Select(x => (x.File, Size: int.Parse(x.Match.Groups[1].Value), Unplated: x.Match.Groups[2].Success)).ToList();
        foreach (var group in new[] { sized.Where(s => s.Unplated).ToList(), sized.Where(s => !s.Unplated).ToList() })
        {
            if (group.Count == 0) continue;
            var fits = group.Where(s => s.Size >= size).OrderBy(s => s.Size).ToList();
            return (fits.Count > 0 ? fits[0] : group.OrderByDescending(s => s.Size).First()).File;
        }
        var scaled = mine.Select(f => (File: f, Match: Scale.Match(f))).Where(x => x.Match.Success)
                         .OrderByDescending(x => int.Parse(x.Match.Groups[1].Value)).Select(x => x.File).FirstOrDefault();
        return scaled ?? mine.FirstOrDefault(f => f.Equals(stem + ".png", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The program in a registered open command, e.g. <c>"C:\…\Code.exe" --open-url -- "%1"</c> → <c>C:\…\Code.exe</c>.</summary>
    public static string? ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
    }
}
