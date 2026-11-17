# The owner's to-do list

## Why

The fleet keeps finding things only the owner can do: a secret to get from a vendor's console, an
account to open, infrastructure to stand up, a production deploy to run from a runbook, a step by
hand. Until now each landed where the agent that found it happened to be standing - a release
note under one slice, a runbook comment on one ticket, a line in an implementation summary - and
the owner had to remember which project, which slice and which ticket to go back to.

This is one list, across every project, of what is the owner's to do. Agents add to it and read
it; the owner works through it, usually by opening Claude Code and saying "do OT-14", and the
session reads the item off the board and walks them through it in the browser.

## What an item is

`OwnerTask` (`Models/OwnerTasks.cs`), one row per thing to do, named `OT-<id>` everywhere.

| Field | What it is for |
|---|---|
| `ProjectKey` | The project it is for, or null for one of the owner's own ("renew the domain") |
| `Kind` | `secret`, `config`, `infra`, `account`, `deploy`, `action` - how the list is grouped |
| `Title` | One line, what to do |
| `Body` | Markdown, written so a fresh session can carry it out with nothing else: **Why** (what it unblocks), **Where** (the console, the account, the region), **Steps**, **Done when** (something checkable), **Hand back** (where the result goes - the file and the key, never the value) |
| `Key` | An optional dedupe slug: `secret:STRIPE_API_KEY`, `infra:uploads-bucket`. Same key, same project, still open: the same item |
| `Status` | `Open`, `Waiting` (on someone else, with `WaitingOn` and a check-back `WaitingUntil`), `Done`, `Dropped` |
| `TicketId`, `Unit` | The ticket that asked for it, and the slice it belongs to |
| `HoldsRelease` | The slice's release is refused while this is open - it is what a config or action release note was |
| `AfterRelease` | For after the slice is released and deployed: it needs what the deploy puts up. Linked to the slice, shown beside its release, and never holds it. Never set with `HoldsRelease` |
| `DoneNote` | Required to mark it done: what was done, and where the result lives |
| `SuggestedAt` / `Suggestion` | An agent's word that it looks done or no longer needed, with its evidence. The owner decides |

Each item keeps a log (`OwnerTaskNote`): who added it, every later ticket that asked for the same
thing, context an agent appended, suggestions, and the owner's own marks.

**A secret's value never goes in an item.** The body says where it goes; the owner pastes it there.

## It replaces the config and action release notes

A release note of kind `config` or `action` was always a to-do with a gate on it. Now
`steward_note_for_release` with either kind files an owner task, and the release gate counts open
tasks that hold that slice beside the unticked migrations. Migrations and notes-to-know stay
release notes: they are reading for the release, not jobs.

**A job says which side of the deploy it is on** (`when` on the note, `holdsRelease` or
`afterRelease` on `owner_todo_add`), and the note is refused until it does. `before` - a secret or
setting the deployed code reads, an account it calls - sets `HoldsRelease`. `after` - a role on a
bucket the deploy creates, a step that reads from it - sets `AfterRelease`: the item is linked to
the slice (its own `Unit`, else its ticket's, never "the next one") and counted against nothing.
Without the second side, a job that needed the deployed infrastructure held the release that would
deploy it, and the slice could never go out except by the owner switching the hold off by hand.
When the slice is released, each open after-deploy item gets a note saying its deploy is out, and
one `owner_todo` notice names them all. A job with a half on each side is two items.

A task's slice is its own `Unit`, else its ticket's slice, else - like an unplaced release note -
the earliest agreed slice not yet released. Ticking one off on the Steward page is marking the
task done, so there is one place it is done.

The migration that adds the table moves every config and action note across: an unticked one is
an open task, a ticked one a done task, and the note row goes.

## Dedupe that an agent cannot skip

- **By key.** `owner_todo_add` with a `key` that matches an open or waiting item on the same
  project does not add: it logs the new ticket as another source on the existing item and returns
  its ref.
- **By likeness.** With no key match, the server compares the title (and any `UPPER_SNAKE`
  identifier in the body) against open, waiting and recently done items. If anything is close,
  the add is refused with the candidates, and the agent either appends to one or adds again with
  `notDuplicateOf` naming each candidate it read and ruled out.
- **`owner_todo_find`** is there for an agent that wants to look first.

## Who can do what

The limits follow the steward's: what is the owner's is a REST route with no tool behind it.

| Who | Can |
|---|---|
| Workers (`/mcp`) and the steward (`/mcp/steward`) | find, read, add, append context, suggest done or obsolete |
| The owner: the To-Do page, and a Claude Code session calling the REST routes for them | all of that, plus mark done, reopen, drop, wait, edit |

No MCP tool closes an item. A steward in `propose` mode says what it would add and adds nothing.

## What marking one done does

- Wakes the project's steward (`MarkDueAsync`), so a release or a blocked ticket it was waiting
  on is picked up without the owner going back to it.
- Leaves a note on the ticket it came from, so the run that next claims it reads what the owner
  did (an owner note, so it is delivered and must be acknowledged like any other).

## Notifications

A new item that holds something - a release, or a ticket - sends `owner_todo` through the usual
notifier, with its ref, title and project. Tick it under Settings > Notifications to receive it.

## Reading it from Claude Code

Sessions reach the board over REST already. For them:

- `GET /api/owner-todos/text` - every open item as markdown, grouped by what it holds up.
- `GET /api/owner-todos/{id}/text` - one item (`14` or `OT-14`), with its log, written to be acted on.
- `POST /api/owner-todos/{id}/done` with `{ "note": "..." }`, the note optional - the same as the page's button.

A `/todo` skill in `~/.claude/skills/todo/` wraps those: `/todo` lists, `/todo OT-14` reads the
item, opens its Where in the browser, walks through the Steps, checks Done when, and marks it done
with the owner's note. The skill lives in the owner's home, not this repo, because the owner asks
for it from whichever project folder they happen to be in.

## The page

A To-Do tab beside Steward, with the open count as its badge. One list across projects: what
holds a release first, then by kind; filters for project and status; each item opens to its body,
its log and its buttons. "Copy for Claude" puts `Read OT-14 from the Whetstone board and help me
do it` on the clipboard, for a session without the skill.

## Not in this

- Recurring items ("rotate the key every 90 days"). A done item can be reopened.
- Assigning to anyone but the owner. The board has one owner.
