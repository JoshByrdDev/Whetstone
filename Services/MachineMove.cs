using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Whetstone.Services;

/// <summary>One folder on the old machine and where the same thing lives on the new one.</summary>
public sealed record PathPair(string From, string To)
{
    /// <summary><c>C:\Projects=/Users/me/Projects</c>. Split on the first '=', which no path here holds.</summary>
    public static PathPair? Parse(string spec)
    {
        var at = spec.IndexOf('=');
        if (at <= 0 || at == spec.Length - 1) return null;
        var from = spec[..at].Trim();
        var to = spec[(at + 1)..].Trim();
        return from.Length == 0 || to.Length == 0 ? null : new PathPair(from, to);
    }
}

/// <summary>One stored path, where it is kept, and what it becomes.</summary>
public sealed record PathChange(string Where, string Before, string After);

/// <summary>
/// Moving a board's data to another machine - Windows to a Mac, a Mac to Windows, or one drive to
/// another - leaves absolute paths behind that name the old machine: a ticket's worktree in the
/// database, a project's <c>projectPath</c>, a Claude login's <c>configDir</c>. This rewrites them
/// from a map of folder prefixes, in the copied data folder, before the board is started over it.
/// <para>
/// Only a value that <i>is</i> a path is rewritten: a config string that starts with a mapped folder,
/// or the same in a database column named for a path (<see cref="IsPathColumn"/>). Prose that
/// mentions a path (a comment, a QA note, a JSON blob) is history, and stays as it was written. A
/// value is matched folder by folder, so <c>C:\Projects</c> never matches <c>C:\Projects2</c>; on a
/// Windows-style prefix case is ignored, as Windows does, and on any other it is not.
/// </para>
/// <para>
/// Run as <c>dotnet run -c Release -- move-paths --map "C:\Projects=/Users/me/Projects" ...</c>.
/// Without <c>--apply</c> it only lists what it would change; with it, it refuses while a board
/// answers on this data folder's own address, and keeps a copy of everything it is about to write.
/// </para>
/// </summary>
public static class MachineMove
{
    /// <summary>The files under the data root that hold machine paths, beside the database.</summary>
    public static IEnumerable<string> ConfigFiles(string dataRoot)
    {
        foreach (var name in new[] { "config.json", "host.json" })
        {
            var f = Path.Combine(dataRoot, name);
            if (File.Exists(f)) yield return f;
        }
        var projects = Path.Combine(dataRoot, "projects");
        if (!Directory.Exists(projects)) yield break;
        foreach (var dir in Directory.GetDirectories(projects).OrderBy(d => d, StringComparer.Ordinal))
            foreach (var name in new[] { "project.json", "project.defaults.json" })
            {
                var f = Path.Combine(dir, name);
                if (File.Exists(f)) yield return f;
            }
    }

    /// <summary>
    /// <paramref name="value"/> moved by the longest prefix of <paramref name="map"/> that holds it,
    /// or null when none does. The rest of the path takes the separator the new prefix is written in.
    /// </summary>
    public static string? Map(string? value, IReadOnlyList<PathPair> map)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var v = Slash(value);
        foreach (var pair in map.OrderByDescending(p => Slash(p.From).TrimEnd('/').Length))
        {
            var from = Slash(pair.From).TrimEnd('/');
            if (from.Length == 0) continue;
            var cmp = IsWindowsStyle(pair.From) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            string rest;
            if (v.Equals(from, cmp)) rest = "";
            else if (v.StartsWith(from + "/", cmp)) rest = v[(from.Length + 1)..];
            else continue;

            var sep = pair.To.Contains('\\') ? '\\' : pair.To.Contains('/') ? '/' : IsWindowsStyle(pair.To) ? '\\' : '/';
            var head = pair.To.TrimEnd('/', '\\');
            if (rest.Length == 0) return head.Length == 0 ? pair.To : head;
            return head + sep + rest.Replace('/', sep);
        }
        return null;
    }

    /// <summary>A drive letter, a UNC share, or a backslash anywhere: a path written for Windows.</summary>
    public static bool IsWindowsStyle(string path) =>
        (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') || path.Contains('\\');

    private static string Slash(string path) => path.Replace('\\', '/');

    /// <summary>Every string in <paramref name="node"/> that is a mapped path, rewritten in place.</summary>
    public static List<PathChange> RewriteJson(JsonNode? node, IReadOnlyList<PathPair> map, string where)
    {
        var changes = new List<PathChange>();
        Walk(node, where);
        return changes;

        void Walk(JsonNode? n, string at)
        {
            switch (n)
            {
                case JsonObject obj:
                    foreach (var (key, child) in obj.ToList())
                    {
                        if (key.StartsWith("_comment", StringComparison.Ordinal)) continue;
                        if (Rewritten(child) is { } after) { changes.Add(new($"{at}.{key}", Str(child), after)); obj[key] = after; }
                        else Walk(child, $"{at}.{key}");
                    }
                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        if (Rewritten(arr[i]) is { } after) { changes.Add(new($"{at}[{i}]", Str(arr[i]), after)); arr[i] = after; }
                        else Walk(arr[i], $"{at}[{i}]");
                    }
                    break;
            }
        }

        string? Rewritten(JsonNode? n) =>
            n is JsonValue jv && jv.TryGetValue<string>(out var s) ? Map(s, map) : null;
        static string Str(JsonNode? n) => n!.GetValue<string>();
    }

    /// <summary>
    /// A column that holds a path says so in its name (<c>WorktreePath</c>). Any other text column is
    /// prose or JSON, where a cell can start with a path and go on in words - and a path can have a
    /// space in it, so the value alone cannot tell the two apart.
    /// </summary>
    public static bool IsPathColumn(string column) =>
        new[] { "Path", "Dir", "Directory", "Root", "Folder" }.Any(s => column.EndsWith(s, StringComparison.Ordinal));

    /// <summary>
    /// Every mapped path in the database's path columns (<see cref="IsPathColumn"/>), in any table, so
    /// a path column added after this was written is moved too. Written only when
    /// <paramref name="apply"/>, in one transaction.
    /// </summary>
    public static List<PathChange> RewriteDatabase(SqliteConnection conn, IReadOnlyList<PathPair> map, bool apply)
    {
        var changes = new List<PathChange>();
        var found = new List<(string Table, string Column, long RowId, string After)>();

        foreach (var table in Strings(conn, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '\\_\\_EF%' ESCAPE '\\' ORDER BY name"))
        {
            var columns = new List<string>();
            using (var info = conn.CreateCommand())
            {
                info.CommandText = $"SELECT name, type FROM pragma_table_info({Quote(table)})";
                using var r = info.ExecuteReader();
                while (r.Read())
                    if ((r.GetString(1).Contains("TEXT", StringComparison.OrdinalIgnoreCase) || r.GetString(1).Length == 0)
                        && IsPathColumn(r.GetString(0)))
                        columns.Add(r.GetString(0));
            }

            foreach (var column in columns)
                foreach (var pair in map)
                {
                    // A cheap first cut in SQL - the prefix, either slash, any case - and the exact
                    // folder-by-folder test in Map. A row two pairs both match is read twice and
                    // moved once, by the longer.
                    var prefix = Slash(pair.From).TrimEnd('/').ToLowerInvariant();
                    if (prefix.Length == 0) continue;
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT rowid, {Ident(column)} FROM {Ident(table)} " +
                        $"WHERE typeof({Ident(column)}) = 'text' AND lower(replace(substr({Ident(column)}, 1, $n), '\\', '/')) = $p";
                    cmd.Parameters.AddWithValue("$n", prefix.Length);
                    cmd.Parameters.AddWithValue("$p", prefix);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var rowId = r.GetInt64(0);
                        var before = r.GetString(1);
                        if (found.Any(f => f.Table == table && f.Column == column && f.RowId == rowId)) continue;
                        if (Map(before, map) is not { } after || after == before) continue;
                        found.Add((table, column, rowId, after));
                        changes.Add(new($"{table}.{column} #{rowId}", before, after));
                    }
                }
        }

        if (apply && found.Count > 0)
        {
            using var tx = conn.BeginTransaction();
            foreach (var f in found)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"UPDATE {Ident(f.Table)} SET {Ident(f.Column)} = $v WHERE rowid = $r";
                cmd.Parameters.AddWithValue("$v", f.After);
                cmd.Parameters.AddWithValue("$r", f.RowId);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return changes;
    }

    /// <summary>The command-line verb. Returns the process exit code.</summary>
    public static int Run(string[] args, TextWriter output)
    {
        var map = new List<PathPair>();
        string? dataRoot = null;
        var apply = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--map" when i + 1 < args.Length:
                    if (PathPair.Parse(args[++i]) is { } pair) map.Add(pair);
                    else { output.WriteLine($"Not a map: '{args[i]}'. Write it as OLD=NEW, e.g. \"C:\\Projects=/Users/me/Projects\"."); return 2; }
                    break;
                case "--data" when i + 1 < args.Length: dataRoot = args[++i]; break;
                case "--apply": apply = true; break;
                default: output.WriteLine($"Unknown argument '{args[i]}'."); return Usage(output);
            }
        }
        if (map.Count == 0) return Usage(output);

        dataRoot ??= Environment.GetEnvironmentVariable("DASHBOARD_DATA_ROOT") is { Length: > 0 } env ? env
            : DashboardPaths.FindRepoData(Directory.GetCurrentDirectory(), AppContext.BaseDirectory);
        if (dataRoot is null || !Directory.Exists(dataRoot))
        {
            output.WriteLine("No data folder found. Pass --data <folder>.");
            return 2;
        }
        dataRoot = Path.GetFullPath(dataRoot);
        output.WriteLine($"Data folder: {dataRoot}");
        foreach (var p in map) output.WriteLine($"  {p.From}  ->  {p.To}");

        if (apply && BoardAddress(dataRoot) is { } live && Answers(live))
        {
            output.WriteLine($"A board is answering on {live.Host}:{live.Port}, which is this data folder's own address. " +
                             "Stop it first: it holds the database open and would write the old paths back.");
            return 1;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var total = 0;

        foreach (var file in ConfigFiles(dataRoot))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(File.ReadAllText(file), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
            catch (JsonException e) { output.WriteLine($"{Rel(file)}: not valid JSON, left alone ({e.Message})"); continue; }

            var changes = RewriteJson(node, map, "");
            if (changes.Count == 0) continue;
            Print(output, Rel(file), changes);
            total += changes.Count;
            if (!apply) continue;

            File.Copy(file, $"{file}.premove-{stamp}.bak", overwrite: false);
            File.WriteAllText(file, node!.ToJsonString(WriteOptions) + Environment.NewLine);
        }

        var db = Path.Combine(dataRoot, "dashboard.db");
        if (File.Exists(db))
        {
            if (apply)
            {
                var backups = Path.Combine(dataRoot, "backups");
                Directory.CreateDirectory(backups);
                var copy = Path.Combine(backups, $"dashboard-premove-{stamp}.db");
                using var conn = Open(db, readOnly: false);
                using (var vac = conn.CreateCommand()) { vac.CommandText = "VACUUM INTO $f"; vac.Parameters.AddWithValue("$f", copy); vac.ExecuteNonQuery(); }
                output.WriteLine($"Database copied to {Rel(copy)} before writing.");
                var changes = RewriteDatabase(conn, map, apply: true);
                Print(output, "dashboard.db", changes);
                total += changes.Count;
            }
            else
            {
                using var conn = Open(db, readOnly: true);
                var changes = RewriteDatabase(conn, map, apply: false);
                Print(output, "dashboard.db", changes);
                total += changes.Count;
            }
        }
        else output.WriteLine("No dashboard.db in this data folder.");

        output.WriteLine(total == 0 ? "Nothing names a mapped folder."
            : apply ? $"Rewrote {total} path(s). The copies taken first end in .premove-{stamp}.bak, and the database's is in backups/."
            : $"{total} path(s) would change. Nothing was written: run again with --apply.");
        return 0;

        string Rel(string f) => Path.GetRelativePath(dataRoot!, f);
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Usage(TextWriter output)
    {
        output.WriteLine("""
            Rewrites the absolute paths a board's data folder holds, after moving it to another machine.

              move-paths --map OLD=NEW [--map OLD=NEW ...] [--data <folder>] [--apply]

              --map    a folder on the old machine and where it is now; the longest that fits wins
              --data   the data folder to rewrite (default: this checkout's data/)
              --apply  write the changes; without it, only list them

            Example, a Windows board moved to a Mac:
              move-paths --map "C:\Projects=/Users/me/Projects" --map "C:\Users\me=/Users/me"
            """);
        return 2;
    }

    private static void Print(TextWriter output, string file, List<PathChange> changes)
    {
        if (changes.Count == 0) return;
        output.WriteLine($"{file}: {changes.Count}");
        const int shown = 12;
        foreach (var c in changes.Take(shown))
            output.WriteLine($"  {(c.Where.Length == 0 ? "" : c.Where.TrimStart('.') + ": ")}{c.Before}  ->  {c.After}");
        if (changes.Count > shown) output.WriteLine($"  ... and {changes.Count - shown} more");
    }

    private static SqliteConnection Open(string db, bool readOnly)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>The board's own address from this data folder's config, when it is on this machine.</summary>
    private static Uri? BoardAddress(string dataRoot)
    {
        var url = "http://localhost:5338";
        try
        {
            var f = Path.Combine(dataRoot, "config.json");
            if (File.Exists(f) && JsonNode.Parse(File.ReadAllText(f))?["dashboardUrl"]?.GetValue<string>() is { Length: > 0 } u) url = u;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback ? uri : null;
    }

    private static bool Answers(Uri uri)
    {
        try
        {
            using var tcp = new TcpClient();
            return tcp.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromMilliseconds(800)) && tcp.Connected;
        }
        catch (Exception e) when (e is SocketException or AggregateException) { return false; }
    }

    private static IEnumerable<string> Strings(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private static string Ident(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
}
