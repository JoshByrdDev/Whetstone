using System.IO.Compression;

namespace Whetstone.Services;

/// <summary>
/// What a worker on a worker host reads from a project's folder, served from the board host on
/// every run so a skill edited here takes effect there at once (docs/worker-hosts-plan.md, piece 2).
/// <para>
/// An allowlist, not a denylist: the project's skills, its <c>guard.ps1</c>, and a read-only copy of
/// its notes under <c>_notes/</c>. Never <c>project.json</c> or <c>project.defaults.json</c>, which
/// hold the project's credentials - the worker gets the config it needs from the claim - and never
/// <c>reflect-prompt.md</c>, whose absence is what switches reflection off on a worker host: its
/// writes belong to this folder and this notes store, not to a copy of them.
/// </para>
/// </summary>
public static class ProjectKit
{
    public const string NotesFolder = "_notes";

    public static byte[] Build(string projectDir, string? memoryDir)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var skills = Path.Combine(projectDir, "skills");
            if (Directory.Exists(skills))
                foreach (var f in Directory.EnumerateFiles(skills, "*", SearchOption.AllDirectories))
                    Add(zip, f, "skills/" + Path.GetRelativePath(skills, f));

            var guard = Path.Combine(projectDir, "guard.ps1");
            if (File.Exists(guard)) Add(zip, guard, "guard.ps1");

            if (!string.IsNullOrWhiteSpace(memoryDir) && Directory.Exists(memoryDir))
                foreach (var f in Directory.EnumerateFiles(memoryDir, "*.md", SearchOption.TopDirectoryOnly))
                    Add(zip, f, $"{NotesFolder}/{Path.GetFileName(f)}");
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string file, string entryName)
    {
        var entry = zip.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Fastest);
        using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = entry.Open();
        src.CopyTo(dst);
    }
}
