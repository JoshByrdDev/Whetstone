using System.ComponentModel;
using ModelContextProtocol.Server;
using Whetstone.Models;
using Whetstone.Services;

namespace Whetstone.Mcp;

/// <summary>
/// The owner's to-do list, as an agent reaches it (docs/owner-todo-plan.md). Shown on both doors -
/// a worker's <c>/mcp</c> and a steward's <c>/mcp/steward</c> - because both find things only the
/// owner can do. What is here can fill the list and say an item looks finished; nothing here can
/// close one. Done, dropped and waiting are the owner's, on REST routes with no tool behind them.
/// </summary>
[McpServerToolType]
public class OwnerTodoTools
{
    /// <summary>Every tool here carries this prefix; the endpoint filter shows it on both doors.</summary>
    public const string Prefix = "owner_todo_";

    /// <summary>Which door the call came in by: the steward's, or a worker's.</summary>
    private static string By(IHttpContextAccessor http) =>
        http.HttpContext?.Request.Path.StartsWithSegments("/mcp/steward") == true ? "steward" : "worker";

    /// <summary>A steward in propose mode says what it would add, and adds nothing.</summary>
    private static object? Proposing(IHttpContextAccessor http, ProjectRegistry registry, string? project, string what) =>
        By(http) == "steward" && project is not null && registry.IsEnabled(project)
            && registry.GetProjectConfig(project).StewardMode == StewardMode.Propose
            ? new { answer = "proposed", why = $"In propose mode: would {what}. Nothing was written." } : null;

    private static object Out(OwnerTaskReply r) => new { answer = r.Answer, why = r.Why, data = r.Data };

    [McpServerTool(Name = "owner_todo_find"), Description(
        "Search the owner's to-do list - the one list, across every project, of what only the owner can do: a secret or API key to get, " +
        "an account to open, infrastructure to stand up, a production deploy, a step by hand. Call it BEFORE owner_todo_add, with the " +
        "thing's own name (the env var, the service, the bucket). Returns open and waiting items, and those done in the last month, best match first.")]
    public static async Task<object> Find(OwnerTaskService s,
        [Description("What to look for: the env var or secret's name, the service, the resource.")] string query,
        [Description("The project key, to search that project and the owner's own items. Leave out to search everything.")] string? project = null)
    {
        var hits = await s.FindAsync(project, query);
        return new
        {
            answer = "found",
            items = hits.Select(h => new
            {
                @ref = h.Task.Ref, h.Task.Id, h.Task.Title, kind = h.Task.Kind.ToString().ToLowerInvariant(),
                status = h.Task.Status.ToString().ToLowerInvariant(), project = h.Task.ProjectKey, ticket = h.Task.TicketId,
                likeness = Math.Round(h.Score, 2),
            }),
        };
    }

    [McpServerTool(Name = "owner_todo_get"), Description(
        "Read one item on the owner's to-do list in full: its body, who asked for it, its log, and whether it is done.")]
    public static async Task<object> Get(OwnerTaskService s,
        [Description("The item: 14 or OT-14.")] string id) =>
        OwnerTaskService.ParseRef(id) is { } n && await s.GetAsync(n) is { } t
            ? OwnerTaskService.Detail(t) : new { answer = "not-found", why = $"There is no {id}." };

    [McpServerTool(Name = "owner_todo_add"), Description(
        "Put something on the owner's to-do list: a thing ONLY the owner can do, that the work needs. A secret or API key from a vendor's " +
        "console; an account or subscription; infrastructure outside the local stack; a setting in a shared environment; a production or " +
        "shared-environment deploy (write the runbook to the branch first, and point at it); wording only they can approve. Not for anything " +
        "you can do yourself, and not for a question - a decision is a Blocked ticket's question, not a to-do. Not for wording to read " +
        "(a 'words' or 'terms' note for release) and not for a line for a document (the worker writes it, or a 'docs' note for release): " +
        "both are refused as not-a-job. The TITLE is one plain line in everyday words, at most 110 characters, saying what to do and why " +
        "it matters - 'Open the analytics account so the warehouse has somewhere to load', not file paths or code. " +
        "Write the body so a fresh Claude session can walk the owner through it with nothing else in front of it, under these headings: " +
        "**Why** (what it unblocks), **Where** (the console URL, the account, the region), **Steps**, **Done when** (something checkable: a " +
        "command, a URL, a value present in a named file), **Hand back** (where the result goes: the file and the key name). " +
        "NEVER put a secret's value in an item. " +
        "Give a key for anything with a name (secret:STRIPE_API_KEY, infra:uploads-bucket, account:sendgrid): the same key on the same project " +
        "lands on the existing item instead of making another. Without a key match, an add that looks like an existing item is refused with " +
        "the candidates: read each, then owner_todo_append to the one it is, or add again with notDuplicateOf naming every candidate. " +
        "Answers 'added' or 'merged' with the item's ref (OT-14) - put the ref in your progress note, so the ticket says where its to-do is.")]
    public static async Task<object> Add(OwnerTaskService s, IHttpContextAccessor http, ProjectRegistry registry,
        [Description("One line: what the owner has to do.")] string title,
        [Description("Markdown, under Why / Where / Steps / Done when / Hand back. Never a secret's value.")] string body,
        [Description("secret, config, infra, account, deploy or action.")] string kind,
        [Description("The project key. Leave out only for something that is no project's.")] string? project = null,
        [Description("A dedupe slug, kind:name - secret:STRIPE_API_KEY, infra:uploads-bucket.")] string? key = null,
        [Description("The ticket that needs it.")] int? ticketId = null,
        [Description("True when the ticket's slice must not be released until this is done: something the release or the push needs " +
            "in place first (a secret the deployed code reads, an account it calls). Not for something that needs what the deploy itself " +
            "puts up - that is afterRelease.")] bool holdsRelease = false,
        [Description("Items a likely-duplicate refusal named, that you read and found to be something else.")] int[]? notDuplicateOf = null,
        [Description("True when it can only be done after the ticket's slice is released and deployed, because it needs what the deploy " +
            "puts up (a role on a bucket the deploy creates, a step that reads from a table it makes). Linked to the slice, shown beside " +
            "its release, and never holds it. Needs ticketId. Never with holdsRelease.")] bool afterRelease = false)
    {
        if (Proposing(http, registry, project, $"add to the owner's to-do list: {title}") is { } p) return p;
        return Out(await s.AddAsync(new OwnerTaskDraft(project, title, body, kind, key, ticketId, null, holdsRelease, notDuplicateOf, afterRelease), By(http)));
    }

    [McpServerTool(Name = "owner_todo_append"), Description(
        "Add to an item already on the owner's to-do list: another ticket that needs the same thing (give its ticketId), a correction, a link, " +
        "a finding that changes the steps. This is what to do instead of adding a duplicate.")]
    public static async Task<object> Append(OwnerTaskService s, IHttpContextAccessor http, ProjectRegistry registry,
        [Description("The item: 14 or OT-14.")] string id,
        [Description("What to add, in a few lines. Never a secret's value.")] string text,
        [Description("The ticket that also needs it, when that is why you are here.")] int? ticketId = null)
    {
        if (OwnerTaskService.ParseRef(id) is not { } n) return new { answer = "not-found", why = $"There is no {id}." };
        var item = await s.GetAsync(n);
        if (Proposing(http, registry, item?.ProjectKey, $"add to {id}: {text}") is { } p) return p;
        return Out(await s.AppendAsync(n, text, ticketId, By(http)));
    }

    [McpServerTool(Name = "owner_todo_suggest"), Description(
        "Say an item on the owner's to-do list looks done ('done': the env var is now set, the bucket exists) or is no longer needed " +
        "('obsolete': the feature that needed it was dropped), with what you saw. Only a suggestion: the owner closes it, and it stays on the list until they do.")]
    public static async Task<object> Suggest(OwnerTaskService s, IHttpContextAccessor http, ProjectRegistry registry,
        [Description("The item: 14 or OT-14.")] string id,
        [Description("'done' or 'obsolete'.")] string suggestion,
        [Description("What you saw: the file, the command and what it printed.")] string because)
    {
        if (OwnerTaskService.ParseRef(id) is not { } n) return new { answer = "not-found", why = $"There is no {id}." };
        var item = await s.GetAsync(n);
        if (Proposing(http, registry, item?.ProjectKey, $"suggest {id} is {suggestion}") is { } p) return p;
        return Out(await s.SuggestAsync(n, suggestion, because, By(http)));
    }
}
