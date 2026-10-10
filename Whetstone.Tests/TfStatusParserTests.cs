using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Reading <c>tf status /format:brief</c>. There is no TFVC workspace here, so this is pinned against
/// text: two samples captured from Bravo's hub on 2026-09-11 (the clean answer, and the error for a
/// path outside the mapping), and rows in the shape that workspace printed when the check-in flow
/// was worked out by hand on ticket 58 - server-folder headers, file / change / local path, a
/// Detected Changes section, and the closing count line.
///
/// The property that matters most is not the happy path. The caller is deciding whether a human's
/// check-in is complete, and a row this failed to read looks exactly like a file with nothing
/// pending - so every way of losing a row has to surface as a problem rather than as silence.
/// </summary>
public class TfStatusParserTests
{
    // Captured (CRLF and all) from `tf status <files> /format:brief` in a real workspace, names changed.
    private const string CapturedClean = "There are no pending changes.\r\n";
    private const string CapturedNoMapping = "There is no working folder mapping for C:\\Projects\\Bravo\\CLAUDE.md.\r\n";

    private const string PendingAndDetected =
        "File name                          Change       Local path\r\n" +
        "---------------------------------- ------------ -------------------------------------------------------------\r\n" +
        "$/External/BravoSoftware/Core/Operations/ReportingControls/Extras\r\n" +
        "ReportingRegistry.cs               edit         C:\\Projects\\Bravo\\External\\BravoSoftware\\Core\\Operations\\ReportingControls\\Extras\\ReportingRegistry.cs\r\n" +
        "\r\n" +
        "$/Internal/BravoSoftware/TestBravo\r\n" +
        "UnitTestsV2.csproj                 edit         C:\\Projects\\Bravo\\Internal\\BravoSoftware\\TestBravo\\UnitTestsV2.csproj\r\n" +
        "\r\n" +
        "Detected Changes:\r\n" +
        "-------------------------------------------------------------------------------------------------------------\r\n" +
        "File name                          Change       Local path\r\n" +
        "---------------------------------- ------------ -------------------------------------------------------------\r\n" +
        "$/Internal/BravoSoftware/TestBravo/ReportingTests\r\n" +
        "MoveItemToCustomerSearchTests.cs   add          C:\\Projects\\Bravo\\Internal\\BravoSoftware\\TestBravo\\ReportingTests\\MoveItemToCustomerSearchTests.cs\r\n" +
        "\r\n" +
        "2 change(s), 1 detected change(s)\r\n";

    [Fact]
    public void The_captured_clean_answer_reads_as_clean()
    {
        var r = TfStatusParser.Parse(CapturedClean);

        Assert.True(r.NoPendingChanges);
        Assert.Empty(r.Entries);
        Assert.Empty(r.Problems);
        Assert.True(r.CountsAgree);
    }

    [Fact]
    public void Pending_rows_and_detected_rows_are_told_apart()
    {
        var r = TfStatusParser.Parse(PendingAndDetected);

        Assert.False(r.NoPendingChanges);
        Assert.Empty(r.Problems);
        Assert.Equal(2, r.PendingCount);
        Assert.Equal(1, r.DetectedCount);
        Assert.True(r.CountsAgree);

        var edit = Assert.Single(r.Entries, e => e.FileName == "ReportingRegistry.cs");
        Assert.Equal("edit", edit.Change);
        Assert.False(edit.Detected);
        Assert.Equal("$/External/BravoSoftware/Core/Operations/ReportingControls/Extras", edit.ServerFolder);
        Assert.EndsWith(@"Extras\ReportingRegistry.cs", edit.LocalPath);

        // The one that nearly shipped missing: a new file only TFVC's scanner has noticed.
        var add = Assert.Single(r.Entries, e => e.Detected);
        Assert.Equal("MoveItemToCustomerSearchTests.cs", add.FileName);
        Assert.Equal("add", add.Change);
        Assert.Equal("$/Internal/BravoSoftware/TestBravo/ReportingTests", add.ServerFolder);
    }

    [Fact]
    public void A_path_outside_the_mapping_is_a_problem_not_a_row()
    {
        var r = TfStatusParser.Parse(CapturedNoMapping);

        Assert.False(r.NoPendingChanges);
        Assert.Empty(r.Entries);
        Assert.Equal(new[] { @"There is no working folder mapping for C:\Projects\Bravo\CLAUDE.md." }, r.Problems);
    }

    /// <summary>File names and change descriptions can both contain spaces; the local path anchors the row.</summary>
    [Fact]
    public void Spaces_in_the_name_and_a_compound_change_both_survive()
    {
        var r = TfStatusParser.Parse(
            "$/External/Docs\r\n" +
            "Copy of Spec.docx  rename, edit  C:\\Projects\\Bravo\\External\\Docs\\Copy of Spec.docx\r\n" +
            "\r\n" +
            "1 change(s), 0 detected change(s)\r\n");

        var e = Assert.Single(r.Entries);
        Assert.Equal("Copy of Spec.docx", e.FileName);
        Assert.Equal("rename, edit", e.Change);
        Assert.Equal(@"C:\Projects\Bravo\External\Docs\Copy of Spec.docx", e.LocalPath);
        Assert.True(r.CountsAgree);
    }

    [Fact]
    public void A_summary_the_rows_do_not_add_up_to_fails_the_count_check()
    {
        // tf says there are two, and only one row reads - the other could be the missing file.
        var r = TfStatusParser.Parse(
            "$/External\r\n" +
            "A.cs  edit  C:\\Projects\\Bravo\\External\\A.cs\r\n" +
            "\r\n" +
            "2 change(s), 0 detected change(s)\r\n");

        Assert.Single(r.Entries);
        Assert.False(r.CountsAgree);
    }

    [Fact]
    public void Rows_with_no_summary_line_fail_the_count_check()
    {
        var r = TfStatusParser.Parse("A.cs  edit  C:\\Projects\\Bravo\\External\\A.cs\r\n");

        Assert.Single(r.Entries);
        Assert.False(r.CountsAgree);
    }

    [Fact]
    public void An_unrecognised_line_is_kept_as_a_problem()
    {
        var r = TfStatusParser.Parse("TF30063: You are not authorized to access example.visualstudio.com\\example.\r\n");

        Assert.Empty(r.Entries);
        Assert.Single(r.Problems);
        Assert.StartsWith("TF30063", r.Problems[0]);
    }

    [Fact]
    public void A_detected_only_listing_counts_without_a_pending_half()
    {
        var r = TfStatusParser.Parse(
            "Detected Changes:\r\n" +
            "------------------------------------------\r\n" +
            "$/Internal\r\n" +
            "Gone.cs   delete   C:\\Projects\\Bravo\\Internal\\Gone.cs\r\n" +
            "\r\n" +
            "0 change(s), 1 detected change(s)\r\n");

        var e = Assert.Single(r.Entries);
        Assert.True(e.Detected);
        Assert.Equal("delete", e.Change);
        Assert.True(r.CountsAgree);
    }

    [Fact]
    public void Nothing_at_all_is_neither_clean_nor_a_listing()
    {
        var r = TfStatusParser.Parse("");

        Assert.False(r.NoPendingChanges);
        Assert.Empty(r.Entries);
        Assert.Empty(r.Problems);   // the caller refuses on "printed nothing" - see QueryTfvcAsync
    }
}

/// <summary><c>git diff --name-status -M -z</c>, which is what tells an add from an edit.</summary>
public class NameStatusParseTests
{
    [Fact]
    public void Every_kind_of_change_is_named_and_a_rename_keeps_both_halves()
    {
        var z = "M\0External/A.cs\0A\0External/New.cs\0D\0Internal/Gone.cs\0R087\0External/Old.cs\0External/Renamed.cs\0T\0tool.sh\0C100\0a.txt\0b.txt\0";

        var s = TicketGitService.ParseNameStatus(z);

        Assert.Equal(6, s.Count);
        Assert.Equal(new NameStatus("External/A.cs", "modified", null), s[0]);
        Assert.Equal(new NameStatus("External/New.cs", "added", null), s[1]);
        Assert.Equal(new NameStatus("Internal/Gone.cs", "deleted", null), s[2]);
        Assert.Equal(new NameStatus("External/Renamed.cs", "renamed", "External/Old.cs"), s[3]);
        Assert.Equal(new NameStatus("tool.sh", "modified", null), s[4]);   // a type change is an edit
        Assert.Equal(new NameStatus("b.txt", "added", null), s[5]);        // a copy is a new file
    }

    [Fact]
    public void Nothing_parses_to_nothing()
    {
        Assert.Empty(TicketGitService.ParseNameStatus(""));
        Assert.Empty(TicketGitService.ParseNameStatus("\0"));
    }
}
