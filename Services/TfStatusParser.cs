using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>One row of <c>tf status /format:brief</c>: a file TFVC holds a change for.</summary>
/// <param name="Change">tf's own words - <c>edit</c>, <c>add</c>, <c>delete</c>, <c>rename</c>, or a
/// combination such as <c>rename, edit</c>.</param>
/// <param name="ServerFolder">The <c>$/...</c> header the row sat under, or empty.</param>
/// <param name="Detected">True when the row sat below <c>Detected Changes:</c> - a change the local
/// workspace has noticed on disk but that is NOT yet a pending change, so a check-in will not carry
/// it until a human promotes it in Team Explorer.</param>
public record TfStatusEntry(string FileName, string Change, string LocalPath, string ServerFolder, bool Detected);

/// <summary>
/// What one <c>tf status</c> said, read as far as it can be.
/// </summary>
/// <param name="NoPendingChanges">tf printed <c>There are no pending changes.</c></param>
/// <param name="PendingCount">From the closing <c>N change(s), M detected change(s)</c> line, when
/// there was one.</param>
/// <param name="Problems">Every line that was not recognised, and every error tf printed. Anything
/// here means the reading is incomplete - see <see cref="TfStatusParser"/> for why that has to stop
/// a check-in rather than be skipped over.</param>
public record TfStatusReport(
    bool NoPendingChanges,
    IReadOnlyList<TfStatusEntry> Entries,
    int? PendingCount,
    int? DetectedCount,
    IReadOnlyList<string> Problems)
{
    /// <summary>
    /// The entries add up to the summary line. False means rows were lost in the reading - a line
    /// that did not parse, or a column tf truncated - and the missing ones could be exactly the
    /// file that was never promoted.
    /// </summary>
    public bool CountsAgree =>
        PendingCount is null && DetectedCount is null
            // No summary line: tf prints one after every list of rows, so rows without it are a
            // listing that stopped early.
            ? Entries.Count == 0
            : Entries.Count(e => !e.Detected) == (PendingCount ?? 0)
              && Entries.Count(e => e.Detected) == (DetectedCount ?? 0);
}

/// <summary>
/// Reads the text <c>tf status &lt;files&gt; /format:brief</c> prints.
///
/// <para>
/// The shape: server-folder headers (<c>$/External/...</c>), then one row per file as
/// <c>&lt;file name&gt;  &lt;change&gt;  &lt;local path&gt;</c>; a <c>Detected Changes:</c> line
/// separating pending changes above from detected ones below; and a closing
/// <c>N change(s), M detected change(s)</c>. A clean set is the single line
/// <c>There are no pending changes.</c>
/// </para>
///
/// <para>
/// Pure, because there is no TFVC workspace in the tests and there never will be - the only way to
/// pin this down is against captured text. And deliberately suspicious: the caller is deciding
/// whether a human's check-in is complete, and a row this failed to read looks exactly like a file
/// with nothing pending. So nothing is skipped quietly. An unrecognised line lands in
/// <see cref="TfStatusReport.Problems"/>, and a summary line the rows do not add up to fails
/// <see cref="TfStatusReport.CountsAgree"/>; either one is the caller's cue to refuse rather than
/// pass.
/// </para>
/// </summary>
public static class TfStatusParser
{
    public const string NoPendingChangesLine = "There are no pending changes.";

    // A local path is the one column that is always last and always rooted, which makes it the one
    // safe anchor on a line whose other two columns can both contain spaces ("rename, edit", or a
    // file called "Copy of X.cs").
    private static readonly Regex LocalPath = new(@"\s(?<path>[A-Za-z]:\\.*|\\\\.*)$", RegexOptions.Compiled);

    private static readonly Regex Summary = new(
        @"^(?:(?<pending>\d+) change\(s\))?(?:,\s*)?(?:(?<detected>\d+) detected change\(s\))?\.?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Rule = new(@"^-+(\s+-+)*$", RegexOptions.Compiled);

    public static TfStatusReport Parse(string? output)
    {
        var entries = new List<TfStatusEntry>();
        var problems = new List<string>();
        var clean = false;
        var detected = false;
        var folder = "";
        int? pending = null, detectedCount = null;

        foreach (var raw in (output ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.Equals(NoPendingChangesLine, StringComparison.OrdinalIgnoreCase)) { clean = true; continue; }
            if (trimmed.StartsWith("Detected Changes", StringComparison.OrdinalIgnoreCase)) { detected = true; folder = ""; continue; }
            if (Rule.IsMatch(trimmed)) continue;
            if (trimmed.StartsWith("File name", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("$/", StringComparison.Ordinal)) { folder = trimmed; continue; }

            var summary = Summary.Match(trimmed);
            if (summary.Success && (summary.Groups["pending"].Success || summary.Groups["detected"].Success))
            {
                if (summary.Groups["pending"].Success) pending = int.Parse(summary.Groups["pending"].Value);
                if (summary.Groups["detected"].Success) detectedCount = int.Parse(summary.Groups["detected"].Value);
                continue;
            }

            var entry = ParseRow(line, folder, detected);
            if (entry is null) problems.Add(trimmed);
            else entries.Add(entry);
        }

        return new TfStatusReport(clean, entries, pending, detectedCount, problems);
    }

    /// <summary>
    /// One file row, or null when it is not one. The local path is taken off the end; what is left
    /// is the file name then the change, and the file name is normally the path's own leaf - which
    /// is what separates the two when either contains a space.
    /// </summary>
    private static TfStatusEntry? ParseRow(string line, string folder, bool detected)
    {
        var m = LocalPath.Match(line);
        if (!m.Success) return null;

        var path = m.Groups["path"].Value.Trim();
        var rest = line[..m.Index].Trim();
        if (rest.Length == 0) return null;

        string name, change;
        var leaf = Path.GetFileName(path);
        if (leaf.Length > 0 && rest.StartsWith(leaf, StringComparison.OrdinalIgnoreCase) && rest.Length > leaf.Length)
        {
            name = leaf;
            change = rest[leaf.Length..].Trim();
        }
        else
        {
            // Not the leaf (a rename lists its new name against the old path, or tf shortened the
            // column): fall back to the column gap, which is at least two spaces wide.
            var gap = Regex.Match(rest, @"\s{2,}");
            if (!gap.Success) return null;
            name = rest[..gap.Index].Trim();
            change = rest[(gap.Index + gap.Length)..].Trim();
        }

        return change.Length == 0 ? null : new TfStatusEntry(name, change, path, folder, detected);
    }
}
