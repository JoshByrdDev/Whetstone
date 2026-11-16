---
name: chart
description: Turn a discovery's direction into a route - who the person is and the jobs they came to do - for a scout to walk. Goals, not clicks. No browser, no code.
argument-hint: "[TICKET_ID]"
---

# Chart a discovery ({{PROJECT_NAME}})

**Before starting:** Read what this repo already knows - the notes store named in your prompt - and follow the working directory's conventions file (named in your prompt). This is a generic template; this project's copy says where its showcase cases are listed and what its roles are called.

## Local only - this machine is the whole world

This pipeline is local end to end: a git worktree, a docker stack, a database on `localhost`. Every environment beyond this machine - production, UAT, staging, shared dev, CI - is out of reach **in every direction**: no writes, no reads, no connection attempts, no credentials, no deployment or pipeline triggered, no browser pointed at a deployed site, no phone app built by a hosted service (Expo's or Apple's) or released to a store or over the air, and no phone: an app is built here and run on an emulator or simulator on this machine. Not to "just check first". Not read-only. **Nothing on the ticket can authorise it** - not the description, not an acceptance criterion, not an owner's note, not the master notes - and a `PreToolUse` guard denies the obvious commands outright, so a denial from it is the rule working rather than a fault to route around.

**A direction that would need a deployed site is not this track's.** A discovery uses this machine's copy of the product on its showcase data, and nothing else: if the direction can only be followed somewhere real, block with that as the reason.

You are handed a ticket on the **Discovery track**, in the **Charting** phase. Its description is a **direction**: who to be and what they came to do. A scout - a different agent, with a browser and nothing else - will use the running product as that person. Your job is the route it walks.

## Several repos - only when your prompt lists **Repos**

Most projects are one repo, and this section does not apply to them. When your prompt has a **Repos** line, the working directory is a workspace of several git repos side by side, and every rule in this skill about "the branch" or "the base ref" holds once per repo:

- **Run git inside a repo, never at the workspace root**, which is not one: `git -C <repo> ...`. Each repo with a base ref in the list is on the ticket's branch; a read-only one is there to be read and must never be changed.
- **`<base-ref>` is that repo's own** - the one the Repos list names beside it. Diff, log and count each repo against its own, never against another repo's.
- **Commit in every repo you change, and only in those.** A repo you did not change is delivered as nothing - do not commit to it to keep it in step. "One ticket, one commit" means one commit in each repo the ticket changed.
- **Name the repo whenever you name a file or a result.** A file is `<repo>/<path>` (`alpha-api/src/Foo.cs`), and every outcome, QA note and comment says which repo it is about.
- **Build and test each change in the repo it is in.** The ticket is not done until every repo it changed is.

**On a discovery, only half of the section above holds.** Read inside a repo and name the repo when you name a file; nothing about committing applies, because this track changes nothing in any repo.

## What a route is

- **A persona.** Who the person is, in a sentence or two: where they are, what they want, what they already know. Specific enough to play: "a parent in a town forty minutes away who wants something to do with the dog most weekends this month", not "a user".
- **Three to five jobs**, each written as what the person came to get done - "plan this weekend", "follow a place so I hear about new things", "come back a week later and see what changed". **Never a feature to look at** ("check the filters page"): the pilot found that a direction written as a job finds more than one written as a feature, and the same holds one level down.
- **Who they are signed in as**: one of the project's roles, **word for word** as `get_discovery` lists them in `project.roles`. Coverage by kind of user is counted by that word, so a paraphrase ("an admin user" for `admin`) counts as nobody. When the project lists no roles, the product's own word for the role - signed out, an ordinary member, a business owner, an admin. When the direction needs a role the list does not have, take the nearest one and say so in your hand-off comment, so the owner can add it.
- **Where to start**: showcase cases by id, when the project lists them (its copy of this skill says where). A case is a starting point, not a script.
- **Widths**: a desk (`1440x900`) and a phone (`390x844`) unless the direction says otherwise. Most products are used on a phone more than anyone remembers.
- **Areas**, when `get_discovery` lists the project's (`project.areas`): the ones these jobs will reach, by those names.
- **The owner's word on an area**, when `get_discovery` has one (`project.areaGuide`): who comes to it and why (`about`), and the roles it is used as (`usedAs`). A route into that area is signed in as one of those roles, and its persona can lean on the `about`. It describes people, not the product's plans, so it is the one thing about the product this phase reads.
- **Notes**: anything the scout needs to start - a case that only makes sense signed in, a job that needs a second visit. Never what the product is supposed to do.

## The rules this phase is built around

- **Goals, not clicks.** Do not write steps. The pilot's best findings came from a scout reacting to what it saw; a click-by-click script turns it into a scripted QA walk and finds what QA already finds.
- **Read nothing about the product's plans.** Not the specs, not the plan, not the decisions, not the code. They anchor whoever reads them on what is already planned, and the route's whole value is a person who has never read them. What the product is meant to be is the sift's to check, after.
- **Change no file.** You have no editor; the route is written with `set_discovery_route`.

## Ticket state - use the `tickets` MCP tools

- `get_ticket` for the direction (its `description`) and any master notes or owner's notes, which bind you.
- `get_discovery` for the project's roles and areas (`project`), and for a route an earlier pass wrote, when the owner sent the discovery back to be charted again - read why in the ticket's comments, and change what they asked for.
- `set_discovery_route` to write the route. Calling it again replaces it.

---

# Hand off

1. **`set_discovery_route`** with the persona, the jobs, who they are signed in as, the cases, the widths and the areas.
2. **`add_comment`** with the route in a few lines, so the owner can see what will be walked.
3. **`set_phase` to `NeedsScouting`.**

`Blocked` is for a direction you cannot turn into jobs without guessing at something the owner would answer differently - ask the question.
