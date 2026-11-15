using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>
/// A Recon suggestion's key (docs/recon-tab-plan.md, "Scout this, and where a ticket came from"):
/// what <see cref="Models.Ticket.ReconFrom"/> and a set-aside event carry. Stable strings, built by
/// the marts (<c>mart_recon_suggestions.suggestion_key</c>) and only checked for shape here:
/// <list type="table">
/// <item><term>gap</term><description><c>gap:&lt;area&gt;:&lt;role&gt;:&lt;width&gt;</c>, or <c>gap:&lt;area&gt;</c> for a collapsed row</description></item>
/// <item><term>changed</term><description><c>changed:&lt;area&gt;</c></description></item>
/// <item><term>rerun</term><description><c>rerun:&lt;discovery ticket id&gt;</c></description></item>
/// </list>
/// </summary>
public static partial class ReconKey
{
    public const int MaxLength = 256;

    [GeneratedRegex(@"^(gap:[^:]+(:[^:]+:[^:]+)?|changed:[^:]+|rerun:\d+)$")]
    private static partial Regex Shape();

    [GeneratedRegex(@"^rerun:(\d+)$")]
    private static partial Regex Rerun();

    /// <summary>The key trimmed, or null when it is blank.</summary>
    public static string? Clean(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim();

    /// <summary>Why a key cannot be stored, or null when it can.</summary>
    public static string? Problem(string key) =>
        key.Length > MaxLength ? $"A Recon key is at most {MaxLength} characters."
        : Shape().IsMatch(key) ? null
        : $"'{key}' is not a Recon suggestion's key (gap:<area>[:<role>:<width>], changed:<area> or rerun:<discovery id>).";

    /// <summary>The discovery a rerun key reruns, or null for any other key.</summary>
    public static int? RerunOf(string? key) =>
        key is not null && Rerun().Match(key) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
}
