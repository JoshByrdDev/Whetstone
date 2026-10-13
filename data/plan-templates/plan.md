# {{NAME}}

> Started {{DATE}}. This is the whole plan: what is to be built and why. It is built a unit at a
> time, and each unit is a spec in [{{UNITS_DIR}}/]({{UNITS_DIR}}/) that points back at the sections
> here it comes from.
>
> **How to read it.** A plain statement is something the owner has decided. Anything not yet
> decided says so beside it: *Proposed* or *Open*. A steward reads a plain statement as a decision
> made and never reads a proposed or open one that way, so the difference is worth being strict about.

## 1. What it is

Two or three paragraphs. What exists when this plan is finished that does not exist today, who it
is for, and what it is not. If a paragraph could be cut without anyone building something
different, cut it.

## 2. What is fixed

The things every unit takes as given: who the users are, what already exists and is built on, what
may not change. One line each, in a table when there are more than a handful.

## 3. The parts

One section per part of the thing, in whatever order explains it best. Each says what a person can
do and what the system does about it, in sentences a tester could check. Decisions go in as plain
statements; anything still open is marked.

## 4. Order of work

The units, in the order they are built, one line each: what a person can do at the end of it that
they could not at the start. A unit is a slice through every layer that leaves something working,
never a layer on its own. Only the next unit needs a spec; the ones after it are a line here until
their turn.

1. **First unit.** ...
2. **Second unit.** ...

## 5. Decisions

Numbered, oldest first. Each is a question that came up, what was decided, and when. Units and
tickets cite them by number, so a number is never reused.

1. ...

## 6. Not in this plan

What somebody might reasonably expect to find here and will not, and where it lives instead.
