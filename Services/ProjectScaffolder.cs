using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// Sets up the operational files a project needs before the heartbeat can work its tickets:
/// the required per-phase skills (<see cref="PhaseMachine.RequiredSkills"/>) plus the reflect-prompt
/// file. Everything is copied from the committed, project-agnostic templates under
/// <c>data/skill-templates/</c>, with light token substitution.
///
/// There is deliberately no knowledge file here. What a project knows about its own repo lives in
/// that repo (<see cref="ProjectConfig.ResolvedMemoryDir"/>), where humans and interactive sessions
/// read and write it too; the dashboard scaffolds procedure, not facts.
///
/// Scaffolding is always additive — an existing file is never overwritten, so a project's
/// customised skills survive re-running it (e.g. after a template is added later).
/// </summary>
public static class ProjectScaffolder
{
    /// <summary>Presence of one required skill for a project.</summary>
    public record SkillStatus(string Name, bool Present);

    /// <summary>Required-skill presence for a project, in canonical order.</summary>
    public static List<SkillStatus> SkillStatuses(DashboardPaths paths, string project) =>
        PhaseMachine.RequiredSkills
            .Select(s => new SkillStatus(s, File.Exists(paths.SkillFile(project, s))))
            .ToList();

    /// <summary>True when every required skill file exists (the heartbeat can work this project).</summary>
    public static bool SkillsReady(DashboardPaths paths, string project) =>
        PhaseMachine.RequiredSkills.All(s => File.Exists(paths.SkillFile(project, s)));

    /// <summary>
    /// Copy any missing required skills + operational files from the templates into the project.
    /// Returns the project-relative paths that were created (empty if nothing was missing or no
    /// templates are available).
    /// </summary>
    public static List<string> ScaffoldMissing(DashboardPaths paths, string project, string displayName)
    {
        var created = new List<string>();

        string Fill(string content) => content
            .Replace("{{PROJECT_NAME}}", string.IsNullOrWhiteSpace(displayName) ? project : displayName)
            .Replace("{{PROJECT_KEY}}", project);

        void CopyIfMissing(string templatePath, string destPath, string relLabel)
        {
            if (File.Exists(destPath) || !File.Exists(templatePath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.WriteAllText(destPath, Fill(File.ReadAllText(templatePath)));
            created.Add(relLabel);
        }

        // AllSkills, not RequiredSkills: scaffolding lays down everything a project COULD run, while
        // SkillsReady above still asks only for what it MUST have. The two lists differ by the
        // optional peer-review skill, and that asymmetry is the point - a project gets the file so
        // it can take review tickets, and is not marked unready for never having wanted one.
        foreach (var skill in PhaseMachine.AllSkills)
            CopyIfMissing(paths.SkillTemplate(skill), paths.SkillFile(project, skill), $"skills/{skill}/skill.md");

        // The steward's skill is not a phase and sits in no list of them, but a project gets the
        // file for the same reason it gets the optional review skill: so that switching its steward
        // on is a setting, not a hunt for a template.
        CopyIfMissing(paths.SkillTemplate("steward"), paths.SkillFile(project, "steward"), "skills/steward/skill.md");

        CopyIfMissing(paths.ReflectPromptTemplate, paths.ReflectPromptFile(project), "reflect-prompt.md");

        return created;
    }
}
