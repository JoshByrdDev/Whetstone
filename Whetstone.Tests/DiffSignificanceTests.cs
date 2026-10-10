using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Ranking a diff by what is worth reading.
///
/// Every fixture below is shaped like one real change (add one flag to an entity
/// end to end), because that change is the whole problem in one change — twenty-nine files, of which
/// fourteen are the same line pasted into a proto, an enum, four view models and a generated
/// client, and one is a three-line edit to a SQL query that is the only place it could go wrong.
///
/// The two directions of error are not equal, and the tests are written to say so. Promoting a dull
/// file wastes a scroll; demoting a real one hides a bug behind a fold. So the demotion rules are
/// pinned hard: nothing with control flow, a declaration or a query in it may be folded away.
/// </summary>
public class DiffSignificanceTests
{
    private static DiffVerdict Judge(string path, string body, bool generated = false, string[]? noise = null)
    {
        var added = body.Split('\n').Count(l => l.StartsWith("+", StringComparison.Ordinal));
        var removed = body.Split('\n').Count(l => l.StartsWith("-", StringComparison.Ordinal));
        return DiffSignificance.Judge(path, added, removed, false, body, generated, noise);
    }

    // ---- the boilerplate this exists to demote ------------------------------

    [Fact]
    public void An_auto_property_added_to_a_view_model_is_skimmable()
    {
        // The single most common file in these tickets. It must not read as new API surface just
        // because the line contains `public` and a pair of braces.
        var v = Judge("backend/src/Alpha.Web/ViewModels/ServiceProviderDetailsVM.cs", """
            @@ -65,6 +65,7 @@ namespace Alpha.Web.ViewModels
                     public bool? Solicitor { get; set; }
            +        public bool? Enroller { get; set; }
                     public string? LogoUrl { get; set; }
            """);

        Assert.Equal(DiffTier.Minor, v.Tier);
        Assert.Contains("list entries", v.Reasons);
    }

    [Fact]
    public void An_enum_member_with_its_attribute_is_skimmable()
    {
        var v = Judge("backend/src/Alpha.Core.Abstractions/Models/Enums/AdvisorTypeEnum.cs", """
            @@ -15,5 +15,8 @@ namespace Alpha.Core.Abstractions.Models.Enums
                     Solicitor = 40,
            +
            +        [AlphaEnumDisplayName("Enroller")]
            +        Enroller = 50,
            """);

        Assert.Equal(DiffTier.Minor, v.Tier);
    }

    [Fact]
    public void A_new_proto_field_is_skimmable()
    {
        var v = Judge("backend/protos/models.proto", """
            @@ -2924,6 +2924,7 @@ message ServiceProviderDetails {
                 google.protobuf.Int32Value EmployerType = 35;
            +    google.protobuf.BoolValue Enroller = 36;
            """);

        Assert.Equal(DiffTier.Minor, v.Tier);
    }

    [Fact]
    public void A_typescript_interface_property_is_skimmable()
    {
        var v = Judge("frontend/src/models/Advisor.ts", """
            @@ -10,4 +10,5 @@
                 solicitor?: boolean;
            +    enroller?: boolean;
            """);

        Assert.Equal(DiffTier.Minor, v.Tier);
    }

    [Fact]
    public void Reshuffled_imports_are_skimmable()
    {
        var v = Judge("frontend/src/components/Thing.tsx", """
            @@ -1,3 +1,3 @@
            -import { UserRoleTypesEnum } from "@/fetchClient/models/UserRoleTypesEnum";
            +import { advisorTypeByName } from "./types";
            """);

        Assert.Equal(DiffTier.Minor, v.Tier);
        Assert.Contains("imports", v.Reasons);
    }

    // ---- the change this exists to promote ----------------------------------

    [Fact]
    public void Three_lines_inside_an_existing_sql_query_outrank_everything_boilerplate()
    {
        // The case line counts get most wrong. Six changed lines, and the blast radius is every
        // advisor role lookup in the product.
        var v = Judge("backend/src/Alpha.Core.DataServices/Services/UserDataService.cs", """
            @@ -1983,11 +1983,11 @@ namespace Alpha.Core.DataServices.Services
            -                        from (SELECT sp.ServiceProviderId,ISNULL(sp.Solicitor,0) Solicitor
            +                        from (SELECT sp.ServiceProviderId,ISNULL(sp.Solicitor,0) Solicitor,ISNULL(sp.Enroller,0) Enroller
            -                            for UserRoleType in (RIA,Solicitor,Broker_Dealer,Insurance)
            +                            for UserRoleType in (RIA,Solicitor,Broker_Dealer,Insurance,Enroller)
            """);

        Assert.Equal(DiffTier.Major, v.Tier);
        Assert.Contains("query changed", v.Reasons);

        var boilerplate = Judge("backend/src/Alpha.Web/ViewModels/ServiceProviderDetailsVM.cs", """
            @@ -65,6 +65,7 @@
            +        public bool? Enroller { get; set; }
            """);
        Assert.True(v.Score > boilerplate.Score);
    }

    [Fact]
    public void A_migration_is_major_however_small_it_is()
    {
        // The one change in a ticket that a later code edit cannot undo.
        var v = Judge("backend/src/Alpha.Core.Migrations/Migrations/_202608071655_8870_Add_Enroller.cs", """
            @@ -0,0 +1,3 @@
            +            migrationBuilder.Sql(@"ALTER TABLE ServiceProviders ADD Enroller BIT NULL");
            """);

        Assert.Equal(DiffTier.Major, v.Tier);
        Assert.Contains("migration", v.Reasons);
        Assert.Contains("schema change", v.Reasons);
    }

    [Fact]
    public void A_single_line_of_control_flow_is_never_folded_away_for_being_small()
    {
        var v = Judge("backend/src/Alpha.Core.DataServices/Services/Thing.cs", """
            @@ -10,3 +10,3 @@
            -            if (model.Excluded == true)
            +            if (model.Excluded == true && !model.Enroller)
            """);

        Assert.NotEqual(DiffTier.Minor, v.Tier);
        Assert.Contains("control flow", v.Reasons);
    }

    [Fact]
    public void A_new_method_counts_as_new_surface_but_a_property_does_not()
    {
        var method = Judge("src/Service.cs", """
            @@ -1,0 +1,3 @@
            +        public async Task<int> CountEnrollersAsync(int id)
            +        {
            +            return await _db.Query(id);
            """);
        var property = Judge("src/Model.cs", """
            @@ -1,0 +1,1 @@
            +        public bool? Enroller { get; set; }
            """);

        Assert.Contains("new declarations", method.Reasons);
        Assert.DoesNotContain("new declarations", property.Reasons);
    }

    // ---- generated and data --------------------------------------------------

    [Fact]
    public void A_file_a_generator_marked_is_skimmable_whatever_is_in_it()
    {
        var v = Judge("frontend/src/fetchClient/models/ServiceProviderDetailsVM.ts", """
            @@ -1,0 +1,2 @@
            +    if (json['enroller'] !== undefined) { return mapEnroller(json); }
            """, generated: true);

        Assert.Equal(DiffTier.Minor, v.Tier);
        Assert.Contains("generated", v.Reasons);
    }

    [Fact]
    public void Lockfiles_and_build_output_are_skimmable_without_being_told()
    {
        Assert.Equal(DiffTier.Minor, Judge("frontend/package-lock.json", "@@ -1,0 +1,1 @@\n+    \"x\": \"1\",").Tier);
        Assert.Equal(DiffTier.Minor, Judge("src/obj/Debug/App.g.cs", "@@ -1,0 +1,1 @@\n+public class Gen { }").Tier);
    }

    [Fact]
    public void A_large_json_fixture_never_outranks_code_just_for_containing_the_word_select()
    {
        // A 377-line Postman collection. Its "SQL" and "control flow" live inside request bodies as
        // strings, so scoring it as code puts it above the migration it shipped beside.
        var body = "@@ -1,0 +1,400 @@\n" + string.Join('\n',
            Enumerable.Range(0, 400).Select(i => $"+      \"raw\": \"SELECT * FROM Advisors WHERE Id = {i} return\","));

        var v = Judge("backend/tests/Alpha.Tests/PostmanCollections/0023_Service_Providers.postman_collection.json", body);

        Assert.NotEqual(DiffTier.Major, v.Tier);
        Assert.DoesNotContain("query changed", v.Reasons);
    }

    [Fact]
    public void A_project_can_name_its_own_noise()
    {
        var noise = new[] { "frontend/src/apiTypes/**" };
        Assert.Equal(DiffTier.Minor, Judge("frontend/src/apiTypes/deep/Thing.ts", "@@ -1,0 +1,1 @@\n+if (x) return 1;", noise: noise).Tier);
        // ...and the glob does not leak onto its neighbours.
        Assert.NotEqual(DiffTier.Minor, Judge("frontend/src/apiTypesHand/Thing.ts", "@@ -1,0 +1,1 @@\n+if (x) return 1;", noise: noise).Tier);
    }

    // ---- non-changes ---------------------------------------------------------

    [Fact]
    public void A_pure_reindent_is_skimmable_because_nothing_was_written()
    {
        var v = Judge("src/Thing.cs", """
            @@ -1,3 +1,3 @@
            -    if (x) { return 1; }
            +        if (x) { return 1; }
            """);

        Assert.Equal(DiffTier.Minor, v.Tier);
        Assert.Contains("formatting", v.Reasons);
    }

    [Fact]
    public void A_rename_with_no_edit_is_skimmable()
    {
        var v = DiffSignificance.Judge("src/New.cs", 0, 0, false, "");

        Assert.Equal(DiffTier.Minor, v.Tier);
        Assert.Contains("renamed", v.Reasons);
    }

    [Fact]
    public void Every_file_carries_at_least_one_reason()
    {
        // An empty badge row reads as "not assessed" sitting beside files that were.
        var v = Judge("src/Plain.tsx", "@@ -1,2 +1,2 @@\n-const a = 1;\n+const a = 2;");
        Assert.NotEmpty(v.Reasons);
    }

    // ---- glob --------------------------------------------------------------

    [Theory]
    [InlineData("src/**", "src/a/b/c.ts", true)]
    [InlineData("src/*.ts", "src/a.ts", true)]
    [InlineData("src/*.ts", "src/a/b.ts", false)]   // a single star stops at the separator
    [InlineData("**/gen/**", "a/b/gen/c.ts", true)]
    [InlineData("*.lock", "yarn.lock", true)]
    public void Glob_matching(string glob, string path, bool expected)
        => Assert.Equal(expected, DiffSignificance.GlobMatch(glob, path));
}
