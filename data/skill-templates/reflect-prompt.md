# Reflection guidance

You are reviewing the log output from an automated Claude heartbeat run, looking for durable facts
worth recording.

This file is guidance only, and it is also the on/off switch: delete it and this project stops
reflecting. The output format, the categories, and the bar for what counts are all defined by the
OUTPUT CONTRACT appended after this text by the worker. Where anything here disagrees with that
contract, the contract wins - so do not restate format or categories here.

## What to look for

1. **Retries and workarounds** - the run tried approach A, it failed, then approach B worked. The
   fact is "B is the one that works here", plus the reason if the log shows it.
2. **Permission, tool, or configuration errors** - a call was blocked or failed because of how this
   machine or repo is set up. The fact is the setup detail, not the incident.
3. **Surprises** - something in the repo or environment behaved differently than the run expected:
   an encoding, a path, a flag that is required, a script that is a no-op, a table that does not
   exist, a container that does not pick up changes.
4. **Misunderstood instructions** - a prompt or skill was read in a way it was not meant. That one is
   a change to the pipeline, not knowledge about the repo.

## What not to report

- **Tool hygiene about the run itself.** "Avoid duplicate searches", "read the file once", "combine
  those calls", "maintain awareness of what you already read". None of this is knowledge about the
  repo or the machine, it is commentary on how the model spent its turns, and it is worthless to the
  next run. The contract lists these as disqualifying for a reason: about a dozen of them were filed
  over two weeks and every one was eventually deleted unread.
- **Anything already known.** The known-facts list and the notes-store index are both provided below.
  Check them before reporting. Re-filing something under a slightly different name is the single most
  common failure here.
- **Business logic of the code.** What the feature does belongs in the ticket, not in memory.
- **Vague advice.** "Be more careful", "plan better", "verify thoroughly". If it does not name a
  specific thing, it is not a fact.
- **Generic engineering practice.** Anything true of every codebase - run the build before you finish,
  check which files changed before diffing them - teaches this project nothing.

## The bar

One question: **would a human developer, or an interactive Claude session in this repo, also have
been wrong without this fact?**

If yes, it is knowledge about the repo or the machine, and it goes into the notes store where every
future run, every interactive session and every human will read it.

If only the unattended pipeline would have been wrong, it is a proposed change to a skill or a prompt
and it goes to a queue a human reviews. That queue is expensive - it is read by a person, one item at
a time - so send it only what genuinely needs a human decision.

Most runs teach nothing new. An empty result is the correct and common answer.
