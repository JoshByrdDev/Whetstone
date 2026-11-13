using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

public class MachineMoveTests : IDisposable
{
    private static readonly PathPair[] ToMac =
    [
        new(@"C:\Projects", "/Users/me/Projects"),
        new(@"C:\Users\me", "/Users/me"),
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "machine-move-" + Guid.NewGuid().ToString("N"));

    public MachineMoveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(@"C:\Projects\alpha", "/Users/me/Projects/alpha")]
    [InlineData("C:/Projects/.worktrees/alpha/ticket-22", "/Users/me/Projects/.worktrees/alpha/ticket-22")]
    [InlineData(@"c:\projects\alpha", "/Users/me/Projects/alpha")]
    [InlineData(@"C:\Projects", "/Users/me/Projects")]
    [InlineData(@"C:\Projects\", "/Users/me/Projects")]
    [InlineData(@"C:\Users\me\.claude-alpha", "/Users/me/.claude-alpha")]
    public void A_windows_path_under_a_mapped_folder_moves_to_the_mac(string before, string after) =>
        Assert.Equal(after, MachineMove.Map(before, ToMac));

    [Theory]
    [InlineData(@"C:\Projects2\alpha")]
    [InlineData(@"D:\Projects\alpha")]
    [InlineData("see C:/Projects/alpha for the repo")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_left_alone(string? value) => Assert.Null(MachineMove.Map(value, ToMac));

    [Fact]
    public void The_longest_mapped_folder_wins()
    {
        PathPair[] map = [new(@"C:\Projects", "/a"), new(@"C:\Projects\alpha", "/b")];
        Assert.Equal("/b/src", MachineMove.Map(@"C:\Projects\alpha\src", map));
        Assert.Equal("/a/bravo", MachineMove.Map(@"C:\Projects\bravo", map));
    }

    [Fact]
    public void A_mac_folder_moves_to_windows_and_only_in_its_own_case()
    {
        PathPair[] map = [new("/Users/me/Projects", @"C:\Projects")];
        Assert.Equal(@"C:\Projects\alpha\src", MachineMove.Map("/Users/me/Projects/alpha/src", map));
        Assert.Null(MachineMove.Map("/users/me/projects/alpha", map));
    }

    [Fact]
    public void A_new_prefix_written_with_forward_slashes_keeps_them() =>
        Assert.Equal("D:/src/alpha", MachineMove.Map(@"C:\Projects\alpha", [new(@"C:\Projects", "D:/src")]));

    [Fact]
    public void Json_paths_are_rewritten_and_prose_is_not()
    {
        var node = JsonNode.Parse("""
            {
              "projectPath": "C:\\Projects\\alpha",
              "_comment": "measured in C:\\Projects\\alpha",
              "stack": { "workingDir": "api" },
              "claudeAccounts": [ { "name": "A", "configDir": "C:\\Users\\me\\.claude-a" } ],
              "port": 5338
            }
            """);

        var changes = MachineMove.RewriteJson(node, ToMac, "");

        Assert.Equal(2, changes.Count);
        Assert.Equal("/Users/me/Projects/alpha", node!["projectPath"]!.GetValue<string>());
        Assert.Equal("/Users/me/.claude-a", node["claudeAccounts"]![0]!["configDir"]!.GetValue<string>());
        Assert.Equal(@"measured in C:\Projects\alpha", node["_comment"]!.GetValue<string>());
        Assert.Equal("api", node["stack"]!["workingDir"]!.GetValue<string>());
    }

    [Fact]
    public void Database_paths_are_rewritten_in_any_table_and_prose_is_not()
    {
        using var conn = new SqliteConnection($"Data Source={Path.Combine(_root, "t.db")}");
        conn.Open();
        Exec(conn, """
            CREATE TABLE Tickets (Id INTEGER PRIMARY KEY, WorktreePath TEXT, Description TEXT);
            CREATE TABLE Notes (Id INTEGER PRIMARY KEY, Body TEXT);
            INSERT INTO Notes VALUES (1, 'C:\Projects\alpha');
            CREATE TABLE Later (Id INTEGER PRIMARY KEY, Folder TEXT);
            INSERT INTO Tickets VALUES (1, 'C:/Projects/.worktrees/alpha/ticket-1', 'built in C:/Projects/alpha');
            INSERT INTO Tickets VALUES (2, NULL, 'C:\Projects\alpha is the repo');
            INSERT INTO Tickets VALUES (3, 'C:\Projects2\x', NULL);
            INSERT INTO Later VALUES (1, 'C:\Users\me\notes');
            """);

        var dry = MachineMove.RewriteDatabase(conn, ToMac, apply: false);
        Assert.Equal(2, dry.Count);
        Assert.Equal("C:/Projects/.worktrees/alpha/ticket-1", Scalar(conn, "SELECT WorktreePath FROM Tickets WHERE Id = 1"));

        MachineMove.RewriteDatabase(conn, ToMac, apply: true);
        Assert.Equal("/Users/me/Projects/.worktrees/alpha/ticket-1", Scalar(conn, "SELECT WorktreePath FROM Tickets WHERE Id = 1"));
        Assert.Equal("/Users/me/notes", Scalar(conn, "SELECT Folder FROM Later WHERE Id = 1"));
        Assert.Equal("built in C:/Projects/alpha", Scalar(conn, "SELECT Description FROM Tickets WHERE Id = 1"));
        Assert.Equal(@"C:\Projects2\x", Scalar(conn, "SELECT WorktreePath FROM Tickets WHERE Id = 3"));
        Assert.Equal(@"C:\Projects\alpha is the repo", Scalar(conn, "SELECT Description FROM Tickets WHERE Id = 2"));
        Assert.Equal(@"C:\Projects\alpha", Scalar(conn, "SELECT Body FROM Notes WHERE Id = 1"));
    }

    [Fact]
    public void The_verb_lists_without_apply_and_writes_with_it_keeping_copies()
    {
        Directory.CreateDirectory(Path.Combine(_root, "projects", "alpha"));
        // A loopback port nothing listens on, so the live-board check passes.
        File.WriteAllText(Path.Combine(_root, "config.json"), """{ "dashboardUrl": "http://localhost:1", "claudeAccounts": [ { "name": "A", "configDir": "C:\\Users\\me\\.claude-a" } ] }""");
        var project = Path.Combine(_root, "projects", "alpha", "project.json");
        File.WriteAllText(project, """{ "projectPath": "C:\\Projects\\alpha" }""");
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(_root, "dashboard.db")};Pooling=False"))
        {
            conn.Open();
            Exec(conn, "CREATE TABLE Tickets (Id INTEGER PRIMARY KEY, WorktreePath TEXT); INSERT INTO Tickets VALUES (1, 'C:/Projects/.worktrees/alpha/ticket-1');");
        }
        string[] args = ["--data", _root, "--map", @"C:\Projects=/Users/me/Projects", "--map", @"C:\Users\me=/Users/me"];

        var dry = new StringWriter();
        Assert.Equal(0, MachineMove.Run(args, dry));
        Assert.Contains("3 path(s) would change", dry.ToString());
        Assert.Contains(@"C:\\Projects\\alpha", File.ReadAllText(project));

        var real = new StringWriter();
        Assert.Equal(0, MachineMove.Run([.. args, "--apply"], real));
        Assert.Equal("/Users/me/Projects/alpha", JsonNode.Parse(File.ReadAllText(project))!["projectPath"]!.GetValue<string>());
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "projects", "alpha"), "project.json.premove-*.bak"));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups"), "dashboard-premove-*.db"));
        using var check = new SqliteConnection($"Data Source={Path.Combine(_root, "dashboard.db")};Pooling=False");
        check.Open();
        Assert.Equal("/Users/me/Projects/.worktrees/alpha/ticket-1", Scalar(check, "SELECT WorktreePath FROM Tickets"));
    }

    [Fact]
    public void A_map_that_is_not_old_equals_new_is_refused() =>
        Assert.Equal(2, MachineMove.Run(["--data", _root, "--map", "C:/Projects"], new StringWriter()));

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }
}
