"""Refresh the board's efficiency marts: load the sources, run dbt, publish the marts.

    uv run --project analytics python analytics/refresh.py [--data-root DIR] [--no-transcripts]

Everything it writes is under <data root>/analytics/ (gitignored, one machine's):

    warehouse.duckdb   the build database. Only this script ever opens it, so the board never
                       holds a lock dbt would trip over.
    marts/*.parquet    what the board reads, swapped in whole once a build has passed its tests.
    refresh.json       how the last refresh went - read by the board's status route.

Four sources, loaded into the warehouse's `raw` schema:

  * the board database, read through SQLite's backup API into a private snapshot, so the live
    database is only ever read and never held;
  * every skill's and the worker prompt's git history, so a run recorded before runs carried a
    skill hash still lands on the version it was given;
  * the Claude worker transcripts under ~/.claude/projects, found from the worktree roots the
    board's tickets name, and loaded incrementally: a file is re-read only when it has changed;
  * Recon's inputs: each project's areas and roles and each repo's resolved base ref, from the
    recon-inputs.json the board writes before it starts a refresh, and each base's first-parent
    history read from git (docs/recon-tab-plan.md, "Loading").

A refresh that fails leaves the published marts as they were. See docs/analytics.md.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import time
import traceback
from datetime import datetime, timezone
from pathlib import Path

import duckdb

HERE = Path(__file__).resolve().parent
REPO = HERE.parent

# The marts the board reads, published as parquet. Everything else stays in the warehouse.
PUBLISHED = [
    "fct_runs", "fct_tool_calls", "fct_tickets",
    "mart_phase_breakdown", "mart_time_split", "mart_rework", "mart_tool_time",
    "mart_start_rebase", "mart_diff_size", "mart_first_pass_qa", "mart_version_trend", "mart_daily",
    "fct_bug_links", "mart_bugs", "fct_experiment_tickets", "mart_experiment_arms", "mart_experiments",
    "mart_qa_rulings", "mart_escapes", "mart_rebase_returns",
    # The Discovery track's measures (docs/discovery-track-plan.md, "What is measured"; step 5).
    "fct_discovery_proposals", "mart_discovery_funnel", "mart_discovery_rejections", "mart_discovery_versions",
    # Recon: where to point the next scout (docs/recon-tab-plan.md, "The marts").
    "mart_recon_projects", "mart_recon_coverage", "mart_recon_patterns", "mart_recon_yield", "mart_recon_suggestions",
]

# The board tables read, and the columns of each. A column the database does not have yet (an
# older schema, before a migration has run) is loaded as NULL rather than failing the refresh, so
# the models can be written against the newest schema and still build on the oldest.
BOARD_TABLES: dict[str, dict[str, str]] = {
    "CycleRuns": {
        "Id": "INTEGER", "TicketId": "INTEGER", "ProjectKey": "VARCHAR", "Track": "VARCHAR",
        "Phase": "VARCHAR", "Skill": "VARCHAR", "Resumed": "BOOLEAN", "Model": "VARCHAR",
        "StartedAt": "VARCHAR", "EndedAt": "VARCHAR", "DurationSeconds": "DOUBLE",
        "ApiDurationSeconds": "DOUBLE", "NumTurns": "INTEGER", "InputTokens": "BIGINT",
        "OutputTokens": "BIGINT", "CacheReadTokens": "BIGINT", "CacheCreationTokens": "BIGINT",
        "PeakContextTokens": "BIGINT", "ToolResultChars": "BIGINT", "Mutations": "INTEGER",
        "Outcome": "VARCHAR", "PhaseAfter": "VARCHAR",
        # Added by the run-tracking migration (AddRunTracking).
        "EntryReason": "VARCHAR", "Pass": "INTEGER", "Provider": "VARCHAR", "Effort": "VARCHAR",
        "CliVersion": "VARCHAR", "SkillHash": "VARCHAR", "PromptHash": "VARCHAR",
        "SessionId": "VARCHAR", "PrepSeconds": "DOUBLE", "StartupSeconds": "DOUBLE",
        "HandoffSeconds": "DOUBLE", "StartRebase": "VARCHAR", "BaseBehind": "INTEGER",
        "FilesChanged": "INTEGER", "LinesAdded": "INTEGER", "LinesRemoved": "INTEGER",
        "RunCommits": "INTEGER", "ChangedFilesJson": "VARCHAR",
        # Added by the bug-tracing and experiments migrations (docs/bugs-and-experiments-plan.md).
        "CommitRangeJson": "VARCHAR", "RunFilesJson": "VARCHAR",
        "ExperimentId": "INTEGER", "ExperimentArm": "VARCHAR", "ExperimentApplied": "BOOLEAN",
        # Added by AddRunHost: the machine and berth a run ran in.
        "Host": "VARCHAR", "Slot": "INTEGER",
        # Added by AddRunContextWindow: what the peak is measured against, and whether it overflowed.
        "ContextWindowTokens": "BIGINT", "Compactions": "INTEGER",
        # Added by AddQaRulingLog: the run's ticket lease id, which QA rulings record as their run.
        "LeaseRunId": "INTEGER",
    },
    # Every ruling on a QA item, append-only (AddQaRulingLog): QA's statuses, carry-overs, and
    # refinement's expiries, each linked to the call it overturned.
    "QaRulingEvents": {
        "Id": "INTEGER", "TicketId": "INTEGER", "QaItemId": "INTEGER", "ProjectKey": "VARCHAR",
        "Track": "VARCHAR", "Phase": "VARCHAR", "Kind": "VARCHAR", "PriorVerdict": "VARCHAR",
        "VerifiedOn": "VARCHAR", "Note": "VARCHAR", "At": "VARCHAR", "Actor": "VARCHAR",
        "RunId": "INTEGER", "OverturnsEventId": "INTEGER",
    },
    # Finished work a person sent back (AddEscapeLog), with their classification of the miss.
    "EscapeEvents": {
        "Id": "INTEGER", "TicketId": "INTEGER", "ProjectKey": "VARCHAR", "Track": "VARCHAR",
        "FromPhase": "VARCHAR", "ToPhase": "VARCHAR", "Reason": "VARCHAR", "NoteCommentId": "INTEGER",
        "Build": "VARCHAR", "At": "VARCHAR", "Backfilled": "BOOLEAN", "Kind": "VARCHAR",
        "Source": "VARCHAR", "ClassifierNote": "VARCHAR", "ClassifiedAt": "VARCHAR",
        # Added by AddEscapeClassifiedByRun: the run (or person) that classified it, and a person's override.
        "ClassifiedBy": "VARCHAR", "ClassifiedByRunId": "INTEGER", "Overridden": "BOOLEAN",
    },
    "RebaseReturns": {
        "Id": "INTEGER", "TicketId": "INTEGER", "ProjectKey": "VARCHAR", "Track": "VARCHAR",
        "Actor": "VARCHAR", "Reason": "VARCHAR", "At": "VARCHAR", "Verdict": "VARCHAR",
        "VerdictWhy": "VARCHAR", "JudgedAt": "VARCHAR", "JudgedByRunId": "INTEGER",
        "HandedOffTo": "VARCHAR", "HandedOffAt": "VARCHAR",
    },
    "PhaseTransitions": {
        "Id": "INTEGER", "TicketId": "INTEGER", "ProjectKey": "VARCHAR", "Track": "VARCHAR",
        "From": "VARCHAR", "To": "VARCHAR", "Actor": "VARCHAR", "Reason": "VARCHAR",
        "Backward": "BOOLEAN", "Backfilled": "BOOLEAN", "At": "VARCHAR",
    },
    "Tickets": {
        "Id": "INTEGER", "ProjectKey": "VARCHAR", "Track": "VARCHAR", "Phase": "VARCHAR",
        "Title": "VARCHAR", "Priority": "VARCHAR", "CreatedAt": "VARCHAR", "UpdatedAt": "VARCHAR",
        "WorktreePath": "VARCHAR", "Branch": "VARCHAR", "PlanUnit": "VARCHAR",
        "MergedAt": "VARCHAR", "Walkthrough": "BOOLEAN", "IsBug": "BOOLEAN",
        # A Delivery ticket filed from a discovery's proposal names it (AddDiscoveryTrack). The
        # description is a discovery's direction; only stg_board__discovery_tickets reads it.
        "DiscoveryId": "INTEGER", "Description": "VARCHAR", "RequiresRouteReview": "BOOLEAN",
        # Recon's "Scout this" key (docs/recon-tab-plan.md, R1): gap:/changed:/rerun:<id>. A rerun's
        # results count toward the direction it reran.
        "ReconFrom": "VARCHAR",
    },
    # The Discovery track's record (docs/discovery-track-plan.md). Lists (Jobs, Areas, ObservationIds,
    # EventIds...) are the JSON arrays EF writes; long prose (a proposal's body, a taste file's text)
    # is left out - nothing here measures words. Shared with the Recon marts (docs/recon-tab-plan.md).
    "DiscoveryRoutes": {
        "TicketId": "INTEGER", "SignedInAs": "VARCHAR", "Jobs": "VARCHAR", "StartCases": "VARCHAR",
        "Widths": "VARCHAR", "Areas": "VARCHAR", "RunId": "INTEGER", "WrittenAt": "VARCHAR",
        "ApprovedAt": "VARCHAR", "OwnerEdited": "BOOLEAN",
    },
    "DiscoveryVisits": {
        "Id": "INTEGER", "TicketId": "INTEGER", "Url": "VARCHAR", "RoutePattern": "VARCHAR",
        "Width": "VARCHAR", "SignedInAs": "VARCHAR", "Seconds": "INTEGER", "Area": "VARCHAR",
        "Case": "VARCHAR", "Commit": "VARCHAR", "RunId": "INTEGER", "At": "VARCHAR",
    },
    "DiscoveryObservations": {
        "Id": "INTEGER", "TicketId": "INTEGER", "Kind": "VARCHAR", "Title": "VARCHAR", "Job": "INTEGER",
        "Url": "VARCHAR", "Width": "VARCHAR", "SignedInAs": "VARCHAR", "Area": "VARCHAR",
        "Commit": "VARCHAR", "RunId": "INTEGER", "VideoAt": "DOUBLE", "RecordingId": "INTEGER",
        "ObservedAt": "VARCHAR", "Round": "INTEGER", "CreatedAt": "VARCHAR",
    },
    "DiscoveryProposals": {
        "Id": "INTEGER", "TicketId": "INTEGER", "Rank": "INTEGER", "Kind": "VARCHAR", "Status": "VARCHAR",
        "Title": "VARCHAR", "Size": "VARCHAR", "Confidence": "VARCHAR", "Area": "VARCHAR",
        "ObservationIds": "VARCHAR", "DropReason": "VARCHAR", "RejectReason": "VARCHAR",
        "SnoozeUntil": "VARCHAR", "FiledTicketId": "INTEGER", "Edited": "BOOLEAN", "DraftKind": "VARCHAR",
        "DraftTitle": "VARCHAR", "NewToMe": "BOOLEAN", "RunId": "INTEGER", "Round": "INTEGER",
        "RecheckKind": "VARCHAR", "GoalId": "INTEGER", "CreatedAt": "VARCHAR", "DecidedAt": "VARCHAR",
        "Pinned": "BOOLEAN",
    },
    # Every owner action on a proposal, append-only (DiscoveryMeasures): what the marts count.
    "DiscoveryVerdictEvents": {
        "Id": "INTEGER", "TicketId": "INTEGER", "ProposalId": "INTEGER", "ProjectKey": "VARCHAR",
        "Kind": "VARCHAR", "RejectReason": "VARCHAR", "NewToMe": "BOOLEAN", "Edited": "BOOLEAN",
        "KindChanged": "BOOLEAN", "EditDistance": "INTEGER", "DraftLength": "INTEGER",
        "FiledTicketId": "INTEGER", "At": "VARCHAR",
    },
    "DiscoveryGoalChecks": {
        "Id": "INTEGER", "TicketId": "INTEGER", "ProposalId": "INTEGER", "FiledTicketId": "INTEGER",
        "Round": "INTEGER", "Outcome": "VARCHAR", "ObservationIds": "VARCHAR", "RunId": "INTEGER",
        "Commit": "VARCHAR", "At": "VARCHAR",
    },
    # The taste file's versions and which one each sift or recheck run read (DiscoveryTaste).
    "DiscoveryTasteVersions": {
        "Id": "INTEGER", "ProjectKey": "VARCHAR", "Number": "INTEGER", "Hash": "VARCHAR", "Source": "VARCHAR",
        "Commit": "VARCHAR", "Verdicts": "INTEGER", "RevertOf": "INTEGER", "RodeOnTicketId": "INTEGER",
        "At": "VARCHAR",
    },
    "DiscoveryTasteReads": {
        "Id": "INTEGER", "TicketId": "INTEGER", "RunId": "INTEGER", "Round": "INTEGER", "Phase": "VARCHAR",
        "VersionId": "INTEGER", "VersionNumber": "INTEGER", "Hash": "VARCHAR", "At": "VARCHAR",
    },
    # Recon's Not now, append-only (docs/recon-tab-plan.md). Loaded for history only: the board
    # applies set-asides live, from its own table, so one takes effect without a refresh.
    "ReconDismissalEvents": {
        "Id": "INTEGER", "ProjectKey": "VARCHAR", "SuggestionKey": "VARCHAR", "Kind": "VARCHAR",
        "AreaHeadCommit": "VARCHAR", "Until": "VARCHAR", "At": "VARCHAR",
    },
    # Bug tracing: a bug ticket traced to the tickets whose commits wrote the lines it fixed.
    "BugLinks": {
        "Id": "INTEGER", "BugTicketId": "INTEGER", "SourceTicketId": "INTEGER", "ProjectKey": "VARCHAR",
        "Status": "VARCHAR", "Origin": "VARCHAR", "Lines": "DOUBLE", "Share": "DOUBLE",
        "CommitsJson": "VARCHAR", "Neighbours": "BOOLEAN", "Escaped": "BOOLEAN", "FoundBy": "VARCHAR",
        "MissedBy": "VARCHAR", "Note": "VARCHAR", "TracedAt": "VARCHAR", "DecidedAt": "VARCHAR",
    },
    "BugTraces": {
        "TicketId": "INTEGER", "TracedAt": "VARCHAR", "LinesBlamed": "DOUBLE", "RangesJson": "VARCHAR",
        "Note": "VARCHAR",
    },
    "TicketCommits": {
        "Id": "INTEGER", "TicketId": "INTEGER", "ProjectKey": "VARCHAR", "Repo": "VARCHAR", "Sha": "VARCHAR",
        "PatchId": "VARCHAR", "Subject": "VARCHAR", "Source": "VARCHAR", "RecordedAt": "VARCHAR",
    },
    # A/B experiments on one phase's skill, model, effort or provider.
    "Experiments": {
        "Id": "INTEGER", "ProjectKey": "VARCHAR", "Skill": "VARCHAR", "Name": "VARCHAR",
        "Hypothesis": "VARCHAR", "Status": "VARCHAR", "MinTicketsPerArm": "INTEGER", "WaitDays": "INTEGER",
        "StartedAt": "VARCHAR", "StoppedAt": "VARCHAR", "StopKind": "VARCHAR", "StopReason": "VARCHAR",
        "ConcludedAt": "VARCHAR", "Winner": "VARCHAR", "ConclusionNote": "VARCHAR",
    },
    "ExperimentArms": {
        "Id": "INTEGER", "ExperimentId": "INTEGER", "Key": "VARCHAR", "Name": "VARCHAR",
        "SkillVariant": "VARCHAR", "Model": "VARCHAR", "Effort": "VARCHAR", "Provider": "VARCHAR",
    },
    "ExperimentAssignments": {
        "Id": "INTEGER", "ExperimentId": "INTEGER", "TicketId": "INTEGER", "ArmKey": "VARCHAR",
        "Ordinal": "INTEGER", "AssignedAt": "VARCHAR",
    },
}


# ---- small helpers ---------------------------------------------------------------------------

def log(msg: str) -> None:
    print(f"[{datetime.now().strftime('%H:%M:%S')}] {msg}", flush=True)


def default_data_root() -> Path:
    env = os.environ.get("DASHBOARD_DATA_ROOT")
    if env and env.strip():
        return Path(env.strip()).resolve()
    return REPO / "data"


def claude_projects_dir() -> Path:
    cfg = os.environ.get("CLAUDE_CONFIG_DIR")
    base = Path(cfg) if cfg else Path.home() / ".claude"
    return base / "projects"


def encode_cwd(path: str) -> str:
    """How Claude Code names a session folder after its working directory."""
    return re.sub(r"[^A-Za-z0-9]", "-", path)


def text_hash(text: str | None) -> str | None:
    """The worker's Get-TextHash: the first 12 hex digits of SHA-256 over the text as UTF-8."""
    if not text:
        return None
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:12]


def git(cwd: Path, *args: str) -> str | None:
    try:
        out = subprocess.run(["git", "-C", str(cwd), *args], capture_output=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        return None
    if out.returncode != 0:
        return None
    return out.stdout.decode("utf-8", errors="replace")


def git_bytes(cwd: Path, *args: str) -> bytes | None:
    try:
        out = subprocess.run(["git", "-C", str(cwd), *args], capture_output=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        return None
    return out.stdout if out.returncode == 0 else None


# ---- 1. the board database -------------------------------------------------------------------

def snapshot_board(db_file: Path, out_dir: Path) -> Path:
    """Copy the live database through SQLite's backup API: a consistent read, WAL included."""
    snap = out_dir / "board.snapshot.db"
    if snap.exists():
        snap.unlink()
    src = sqlite3.connect(f"file:{db_file.as_posix()}?mode=ro", uri=True, timeout=30)
    try:
        dst = sqlite3.connect(snap)
        try:
            src.backup(dst)
        finally:
            dst.close()
    finally:
        src.close()
    return snap


def load_board(con: duckdb.DuckDBPyConnection, snap: Path) -> dict[str, int]:
    counts: dict[str, int] = {}
    src = sqlite3.connect(snap)
    try:
        for table, cols in BOARD_TABLES.items():
            have = {r[1] for r in src.execute(f'PRAGMA table_info("{table}")')}
            select = ", ".join(f'"{c}"' if c in have else "NULL" for c in cols)
            # A table a later migration adds is simply empty on a database that predates it.
            rows = src.execute(f'SELECT {select} FROM "{table}"').fetchall() if have else []
            ddl = ", ".join(f'"{c}" {t}' for c, t in cols.items())
            con.execute(f'CREATE OR REPLACE TABLE raw."{table}" ({ddl})')
            if rows:
                # Through a JSON-lines file rather than executemany, which inserts row by row and
                # takes minutes over a few thousand; and rather than DuckDB's sqlite extension,
                # which it would fetch from the internet on first use.
                staged = snap.with_name(f"{table}.jsonl")
                with staged.open("w", encoding="utf-8") as f:
                    for row in rows:
                        f.write(json.dumps(dict(zip(cols, row)), default=str) + "\n")
                spec = "{" + ", ".join(f"'{c}': '{t}'" for c, t in cols.items()) + "}"
                con.execute(f"""INSERT INTO raw."{table}" SELECT * FROM read_json(?, format = 'newline_delimited',
                                columns = {spec}, maximum_object_size = 67108864)""", [staged.as_posix()])
                staged.unlink(missing_ok=True)
            counts[table] = len(rows)
            missing = [c for c in cols if c not in have]
            if not have:
                log(f"  {table}: not in this database yet, loaded empty")
            elif missing:
                log(f"  {table}: not in this database yet, loaded as NULL: {', '.join(missing)}")
    finally:
        src.close()
    return counts


# ---- 2. what each run was told: skill and prompt history ------------------------------------

def eol_of(repo: Path, rel: str) -> str:
    """Whether the working copy of a file has CRLF where git stores LF - the hash is of the working copy."""
    work = repo / rel
    blob = git_bytes(repo, "show", f"HEAD:{rel}")
    if not work.exists() or blob is None:
        return "lf"
    data = work.read_bytes()
    return "crlf" if b"\r\n" in data and b"\r\n" not in blob else "lf"


def history_of(repo: Path, pathspec: str) -> list[tuple[str, str, str, str]]:
    """(sha, committed_at, subject, path) for each commit touching pathspec, oldest first."""
    out = git(repo, "log", "--reverse", "--format=@@%H\t%cI\t%s", "--name-only", "--", pathspec)
    if not out:
        return []
    rows, current = [], None
    for line in out.splitlines():
        if line.startswith("@@"):
            sha, at, subject = (line[2:].split("\t", 2) + ["", ""])[:3]
            current = (sha, at, subject)
        elif line.strip() and current:
            rows.append((*current, line.strip()))
    return rows


def blobs(repo: Path, specs: list[str]) -> dict[str, bytes | None]:
    """Many `<sha>:<path>` blobs through one `git cat-file --batch`, rather than a git per version."""
    if not specs:
        return {}
    try:
        out = subprocess.run(["git", "-C", str(repo), "cat-file", "--batch"],
                             input=("\n".join(specs) + "\n").encode("utf-8"), capture_output=True, timeout=120)
    except (OSError, subprocess.TimeoutExpired):
        return {s: None for s in specs}
    data, pos, found = out.stdout, 0, {}
    for spec in specs:
        nl = data.find(b"\n", pos)
        if nl < 0:
            found[spec] = None
            continue
        header = data[pos:nl].decode("utf-8", errors="replace").split()
        pos = nl + 1
        if len(header) == 3 and header[1] == "blob":
            size = int(header[2])
            found[spec] = data[pos:pos + size]
            pos += size + 1  # the blob, then its newline
        else:
            found[spec] = None  # "<spec> missing": deleted in that commit
    return found


def as_text(raw: bytes | None, eol: str) -> str | None:
    if raw is None:
        return None
    text = raw.decode("utf-8-sig", errors="replace")
    return text.replace("\r\n", "\n").replace("\n", "\r\n") if eol == "crlf" else text


def load_versions(con: duckdb.DuckDBPyConnection, data_root: Path) -> dict[str, int]:
    skills: list[tuple] = []
    projects = data_root / "projects"
    if projects.is_dir():
        for proj in sorted(p for p in projects.iterdir() if (p / ".git").exists()):
            history = [(sha, at, subject, rel, m.group(1)) for sha, at, subject, rel in history_of(proj, "skills")
                       if (m := re.fullmatch(r"skills/([^/]+)/skill\.md", rel))]
            found = blobs(proj, [f"{sha}:{rel}" for sha, _, _, rel, _ in history])
            eols: dict[str, str] = {}
            for sha, at, subject, rel, skill in history:
                if rel not in eols:
                    eols[rel] = eol_of(proj, rel)
                eol = eols[rel]
                text = as_text(found.get(f"{sha}:{rel}"), eol)
                # The worker reads the skill as UTF-8 and trims it (Get-SkillText).
                skills.append((proj.name, skill, sha, at, subject[:200],
                               text_hash(text.strip()) if text is not None else None))

    prompts: list[tuple] = []
    rel = "data/heartbeat-prompt.md"
    eol = eol_of(REPO, rel)
    history = [h for h in history_of(REPO, rel) if h[3] == rel]
    found = blobs(REPO, [f"{sha}:{rel}" for sha, _, _, _ in history])
    for sha, at, subject, _ in history:
        text = as_text(found.get(f"{sha}:{rel}"), eol)
        # The template is read whole (Get-Content -Raw) and hashed untrimmed.
        prompts.append((sha, at, subject[:200], text_hash(text) if text is not None else None))

    con.execute("""CREATE OR REPLACE TABLE raw.skill_versions (
        project_key VARCHAR, skill VARCHAR, commit_sha VARCHAR, committed_at VARCHAR,
        subject VARCHAR, content_hash VARCHAR)""")
    con.execute("""CREATE OR REPLACE TABLE raw.prompt_versions (
        commit_sha VARCHAR, committed_at VARCHAR, subject VARCHAR, content_hash VARCHAR)""")
    if skills:
        con.executemany("INSERT INTO raw.skill_versions VALUES (?, ?, ?, ?, ?, ?)", skills)
    if prompts:
        con.executemany("INSERT INTO raw.prompt_versions VALUES (?, ?, ?, ?)", prompts)
    return {"skill_versions": len(skills), "prompt_versions": len(prompts)}


# ---- 3. the worker transcripts ---------------------------------------------------------------

TRANSCRIPT_DDL = {
    "transcript_files": "path VARCHAR PRIMARY KEY, size BIGINT, mtime_ns BIGINT, loaded_at TIMESTAMP",
    "transcript_lines": """file VARCHAR, session_id VARCHAR, ts TIMESTAMP, type VARCHAR, subtype VARCHAR,
        uuid VARCHAR, is_sidechain BOOLEAN, cwd VARCHAR, cli_version VARCHAR, model VARCHAR,
        message_id VARCHAR, input_tokens BIGINT, output_tokens BIGINT, cache_read_tokens BIGINT,
        cache_creation_tokens BIGINT, prompt_ticket_id INTEGER, prompt_phase VARCHAR""",
    "transcript_tool_uses": """file VARCHAR, session_id VARCHAR, ts TIMESTAMP, is_sidechain BOOLEAN,
        tool_use_id VARCHAR, tool_name VARCHAR, input_summary VARCHAR""",
    "transcript_tool_results": """file VARCHAR, session_id VARCHAR, ts TIMESTAMP, is_sidechain BOOLEAN,
        tool_use_id VARCHAR, is_error BOOLEAN, result_chars BIGINT""",
    "transcript_costs": """file VARCHAR, session_id VARCHAR, total_cost_usd DOUBLE, api_ms DOUBLE,
        tool_ms DOUBLE, duration_ms DOUBLE""",
}

# What read_json is told each line holds. `message` stays JSON: its content is a string on some
# lines and a list of blocks on others, which no fixed struct can hold.
JSON_COLUMNS = (
    "{type: 'VARCHAR', subtype: 'VARCHAR', timestamp: 'VARCHAR', sessionId: 'VARCHAR', "
    "uuid: 'VARCHAR', parentUuid: 'VARCHAR', isSidechain: 'BOOLEAN', cwd: 'VARCHAR', "
    "version: 'VARCHAR', message: 'JSON', content: 'VARCHAR', totalCostUSD: 'DOUBLE', "
    "totalAPIDuration: 'DOUBLE', totalToolDuration: 'DOUBLE', totalDuration: 'DOUBLE'}"
)


def transcript_dirs(con: duckdb.DuckDBPyConnection, projects_dir: Path) -> list[Path]:
    """The session folders of every worktree the board has made, found from the roots its tickets name."""
    roots = {r[0] for r in con.execute(r"""
        SELECT DISTINCT regexp_replace(replace(WorktreePath, '\', '/'), '/[^/]+/?$', '')
        FROM raw."Tickets" WHERE WorktreePath IS NOT NULL AND WorktreePath <> ''""").fetchall()}
    prefixes = sorted({encode_cwd(r) + "-" for r in roots if r})
    if not projects_dir.is_dir() or not prefixes:
        return []
    return sorted(d for d in projects_dir.iterdir()
                  if d.is_dir() and any(d.name.startswith(p) for p in prefixes))


def load_transcripts(con: duckdb.DuckDBPyConnection, projects_dir: Path) -> dict[str, int]:
    for name, ddl in TRANSCRIPT_DDL.items():
        con.execute(f"CREATE TABLE IF NOT EXISTS raw.{name} ({ddl})")

    files = {}
    for d in transcript_dirs(con, projects_dir):
        for f in d.glob("*.jsonl"):
            st = f.stat()
            files[f.as_posix()] = (st.st_size, st.st_mtime_ns)

    known = {r[0]: (r[1], r[2]) for r in con.execute(
        "SELECT path, size, mtime_ns FROM raw.transcript_files").fetchall()}
    gone = [p for p in known if p not in files]
    changed = [p for p, sig in files.items() if known.get(p) != sig]

    for batch in (gone + changed, ):
        for i in range(0, len(batch), 500):
            part = batch[i:i + 500]
            con.execute("CREATE OR REPLACE TEMP TABLE _drop AS SELECT unnest(?::VARCHAR[]) AS path", [part])
            for name in TRANSCRIPT_DDL:
                key = "path" if name == "transcript_files" else "file"
                con.execute(f"DELETE FROM raw.{name} WHERE {key} IN (SELECT path FROM _drop)")

    log(f"  transcripts: {len(files)} files, {len(changed)} new or changed, {len(gone)} gone")
    for i in range(0, len(changed), 400):
        part = changed[i:i + 400]
        load_transcript_batch(con, part)
        con.executemany("INSERT INTO raw.transcript_files VALUES (?, ?, ?, now())",
                        [(p, *files[p]) for p in part])
        if len(changed) > 400:
            log(f"    {min(i + 400, len(changed))}/{len(changed)} files")

    return {name: con.execute(f"SELECT count(*) FROM raw.{name}").fetchone()[0] for name in TRANSCRIPT_DDL}


def load_transcript_batch(con: duckdb.DuckDBPyConnection, paths: list[str]) -> None:
    con.execute(f"""
        CREATE OR REPLACE TEMP TABLE _lines AS
        SELECT replace(filename, '\\', '/') AS file, sessionId AS session_id,
               try_cast(timestamp AS TIMESTAMPTZ) AT TIME ZONE 'UTC' AS ts,
               type, subtype, uuid, parentUuid AS parent_uuid, coalesce(isSidechain, false) AS is_sidechain,
               cwd, version AS cli_version, message, content,
               totalCostUSD, totalAPIDuration, totalToolDuration, totalDuration
        FROM read_json(?, format = 'newline_delimited', union_by_name = true, ignore_errors = true,
                       filename = true, maximum_object_size = 67108864, columns = {JSON_COLUMNS})
    """, [paths])

    con.execute(r"""
        INSERT INTO raw.transcript_lines
        SELECT file, session_id, ts, type, subtype, uuid, is_sidechain, cwd, cli_version,
               message->>'$.model', message->>'$.id',
               try_cast(message->>'$.usage.input_tokens' AS BIGINT),
               try_cast(message->>'$.usage.output_tokens' AS BIGINT),
               try_cast(message->>'$.usage.cache_read_input_tokens' AS BIGINT),
               try_cast(message->>'$.usage.cache_creation_input_tokens' AS BIGINT),
               -- The worker prompt names its ticket and phase in fixed words; reading them back is
               -- what joins a session to a run recorded before runs carried a session id.
               CASE WHEN type = 'queue-operation' OR (type = 'user' AND parent_uuid IS NULL) THEN
                   try_cast(nullif(regexp_extract(coalesce(content, message->>'$.content', ''),
                       '\*\*Ticket:\*\* #(\d+)', 1), '') AS INTEGER) END,
               CASE WHEN type = 'queue-operation' OR (type = 'user' AND parent_uuid IS NULL) THEN
                   nullif(regexp_extract(coalesce(content, message->>'$.content', ''),
                       '\*\*Current phase:\*\* (\w+)', 1), '') END
        FROM _lines
        WHERE type IN ('user', 'assistant', 'system', 'queue-operation', 'attachment')
    """)

    con.execute("""
        INSERT INTO raw.transcript_tool_uses
        SELECT file, session_id, ts, is_sidechain, b->>'$.id', b->>'$.name',
               left(coalesce(b->>'$.input.command', b->>'$.input.file_path', b->>'$.input.pattern',
                             b->>'$.input.url', b->>'$.input.description', b->>'$.input.query',
                             b->>'$.input.skill', b->>'$.input.notebook_path', ''), 300)
        FROM (SELECT file, session_id, ts, is_sidechain,
                     unnest(json_extract(message, '$.content[*]')) AS b
              FROM _lines WHERE type = 'assistant' AND json_type(message, '$.content') = 'ARRAY')
        WHERE b->>'$.type' = 'tool_use'
    """)

    con.execute("""
        INSERT INTO raw.transcript_tool_results
        SELECT file, session_id, ts, is_sidechain, b->>'$.tool_use_id',
               coalesce(try_cast(b->>'$.is_error' AS BOOLEAN), false),
               length(coalesce(json_extract(b, '$.content')::VARCHAR, ''))
        FROM (SELECT file, session_id, ts, is_sidechain,
                     unnest(json_extract(message, '$.content[*]')) AS b
              FROM _lines WHERE type = 'user' AND json_type(message, '$.content') = 'ARRAY')
        WHERE b->>'$.type' = 'tool_result'
    """)

    con.execute("""
        INSERT INTO raw.transcript_costs
        SELECT file, session_id, totalCostUSD, totalAPIDuration, totalToolDuration, totalDuration
        FROM _lines WHERE type = 'cost-state'
    """)
    con.execute("DROP TABLE _lines")


# ---- 4. Recon's inputs: each project's areas, roles and base history ---------------------------

RECON_DDL = {
    # One row per project with a `discovery` block, as the board's config had it when it started
    # this refresh.
    "recon_projects": "project_key VARCHAR, ordinal INTEGER, areas INTEGER, roles INTEGER, inputs_at VARCHAR",
    "recon_areas": "project_key VARCHAR, area VARCHAR, ordinal INTEGER, pages VARCHAR[], code VARCHAR[]",
    "recon_roles": "project_key VARCHAR, role VARCHAR, ordinal INTEGER",
    # Each repo's resolved base ref (TicketGitService's rule, applied by the board) and its tip.
    "recon_repos": "project_key VARCHAR, repo VARCHAR, base_ref VARCHAR, tip_sha VARCHAR, commits INTEGER, error VARCHAR",
    # The base's first-parent line, newest first (fp_index 0 is the tip), one row per commit and
    # file. `path` starts with the repo's folder on a project of several repos, as an area's code
    # path does. A commit that changed no file has one row with a NULL path.
    "recon_commits": """project_key VARCHAR, repo VARCHAR, sha VARCHAR, fp_index INTEGER, committed_at VARCHAR,
        subject VARCHAR, is_merge BOOLEAN, path VARCHAR""",
    # The commits a first-parent merge brought in (its second side), so a ticket whose recorded
    # commits are the merged ones is still found. A non-merge commit is its own only member.
    "recon_commit_members": "project_key VARCHAR, repo VARCHAR, sha VARCHAR, member_sha VARCHAR",
    # Where each commit a visit or observation recorded sits on that line: `line` (on it),
    # `merged` (an ancestor brought in by the first-parent commit at fp_index) or `unknown` (not an
    # ancestor of the base at all - a rewritten base - or not in this repo).
    "recon_visit_commits": "project_key VARCHAR, repo VARCHAR, commit_sha VARCHAR, resolved_sha VARCHAR, fp_index INTEGER, placement VARCHAR",
}

RECON_DAYS = 90


def load_recon(con: duckdb.DuckDBPyConnection, inputs_file: Path) -> dict:
    """Recon's inputs. Areas, roles and each repo's base ref come from recon-inputs.json, which the
    board writes before it starts a refresh, because the rules for them (the merged project config,
    which ref is the base) live in C# and must not be written a third time here. Without the file -
    a refresh started by hand - nothing is loaded and the Recon marts build empty."""
    for name, ddl in RECON_DDL.items():
        con.execute(f"CREATE OR REPLACE TABLE raw.{name} ({ddl})")
    if not inputs_file.exists():
        log(f"  recon: no {inputs_file.name} (a refresh the board did not start), so the Recon marts build empty")
        return {"inputs": False}
    try:
        inputs = json.loads(inputs_file.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as e:
        log(f"  recon: {inputs_file.name} could not be read ({e}), so the Recon marts build empty")
        return {"inputs": False, "error": str(e)}

    at = inputs.get("writtenAt")
    projects, areas, roles, repos = [], [], [], []
    commits, members, placed = [], [], []
    for pi, proj in enumerate(inputs.get("projects") or []):
        key = proj.get("key")
        if not key:
            continue
        p_areas = [a for a in proj.get("areas") or [] if a.get("name")]
        p_roles = [r for r in proj.get("roles") or [] if r]
        projects.append((key, pi, len(p_areas), len(p_roles), at))
        for ai, a in enumerate(p_areas):
            areas.append((key, a["name"], ai, list(a.get("pages") or []), list(a.get("code") or [])))
        for ri, r in enumerate(p_roles):
            roles.append((key, r, ri))
        # The commits this project's visits and observations recorded, to place on each repo's line.
        recorded = sorted({r[0] for r in con.execute("""
            SELECT DISTINCT trim(c) FROM (
                SELECT v."Commit" AS c FROM raw."DiscoveryVisits" v JOIN raw."Tickets" t ON t."Id" = v."TicketId" WHERE t."ProjectKey" = ?
                UNION ALL
                SELECT o."Commit" FROM raw."DiscoveryObservations" o JOIN raw."Tickets" t ON t."Id" = o."TicketId" WHERE t."ProjectKey" = ?)
            WHERE c IS NOT NULL AND trim(c) <> ''""", [key, key]).fetchall()})
        for repo in proj.get("repos") or []:
            got = load_recon_repo(key, repo, recorded)
            repos.append(got["repo"])
            commits += got["commits"]
            members += got["members"]
            placed += got["placed"]

    for name, rows in (("recon_projects", projects), ("recon_areas", areas), ("recon_roles", roles),
                       ("recon_repos", repos), ("recon_commits", commits), ("recon_commit_members", members),
                       ("recon_visit_commits", placed)):
        if rows:
            con.executemany(f"INSERT INTO raw.{name} VALUES ({', '.join('?' * len(rows[0]))})", rows)
    log(f"  recon: {len(projects)} projects, {len(areas)} areas, {len(roles)} roles, "
        f"{len({(c[0], c[1], c[2]) for c in commits})} base commits, {len(placed)} recorded commits placed")
    return {"inputs": True, "projects": len(projects), "areas": len(areas), "roles": len(roles),
            "repos": len(repos), "commits": len({(c[0], c[1], c[2]) for c in commits})}


def load_recon_repo(key: str, repo: dict, recorded: list[str]) -> dict:
    """One repo's base line, from the oldest commit a visit recorded (or RECON_DAYS back) to the tip."""
    folder, path, base = repo.get("dir") or "", repo.get("path") or "", repo.get("baseRef")
    out: dict = {"repo": (key, folder, base, None, 0, None), "commits": [], "members": [], "placed": []}

    def fail(why: str) -> dict:
        log(f"  recon: {key}{'/' + folder if folder else ''}: {why}; its areas' staleness is unknown")
        out["repo"] = (key, folder, base, None, 0, why)
        out["placed"] = [(key, folder, c, None, None, "unknown") for c in recorded]
        return out

    cwd = Path(path)
    if not path or not cwd.is_dir():
        return fail(f"no repo at {path or '(no path)'}")
    if not base:
        return fail("no base ref (the base branch is neither local nor on origin)")
    tip = (git(cwd, "rev-parse", "--verify", "--quiet", f"{base}^{{commit}}") or "").strip()
    if not tip:
        return fail(f"{base} does not resolve")

    line = [l.strip() for l in (git(cwd, "rev-list", "--first-parent", base) or "").splitlines() if l.strip()]
    pos = {sha: i for i, sha in enumerate(line)}

    # Each recorded commit: on the line, merged in by a first-parent commit, or unknown.
    oldest = -1
    for c in recorded:
        full = (git(cwd, "rev-parse", "--verify", "--quiet", f"{c}^{{commit}}") or "").strip()
        if not full:
            out["placed"].append((key, folder, c, None, None, "unknown"))
            continue
        if full in pos:
            out["placed"].append((key, folder, c, full, pos[full], "line"))
            oldest = max(oldest, pos[full])
            continue
        # An ancestor but off the line: the oldest first-parent commit that contains it is where it
        # came in. Not an ancestor at all (a rewritten base): unknown, never fresh.
        if git(cwd, "merge-base", "--is-ancestor", full, base) is None:
            out["placed"].append((key, folder, c, full, None, "unknown"))
            continue
        path_in = [l.strip() for l in (git(cwd, "rev-list", "--first-parent", "--ancestry-path", f"{full}..{base}") or "").splitlines() if l.strip()]
        at = pos.get(path_in[-1]) if path_in else None
        out["placed"].append((key, folder, c, full, at, "merged" if at is not None else "unknown"))
        if at is not None:
            oldest = max(oldest, at)

    since = len([l for l in (git(cwd, "rev-list", "--first-parent", f"--since={RECON_DAYS}.days", base) or "").splitlines() if l.strip()])
    n = max(since, oldest + 1)
    out["repo"] = (key, folder, base, tip, n, None)
    if n == 0:
        return out

    log_out = git(cwd, "log", "--first-parent", "--diff-merges=first-parent", "--name-only",
                  "--format=@@%H\t%P\t%cI\t%s", "-n", str(n), base) or ""
    current, files, merges = None, [], []

    def flush() -> None:
        if current is None:
            return
        sha, parents, at, subject = current
        is_merge = len(parents) > 1
        prefix = folder + "/" if folder else ""
        for f in (files or [None]):
            out["commits"].append((key, folder, sha, pos.get(sha), at, subject[:200], is_merge, prefix + f if f else None))
        out["members"].append((key, folder, sha, sha))
        if is_merge:
            merges.append((sha, parents[0]))

    for raw_line in log_out.splitlines():
        if raw_line.startswith("@@"):
            flush()
            sha, parents, at, subject = (raw_line[2:].split("\t", 3) + ["", "", ""])[:4]
            current, files = (sha, parents.split(), at, subject), []
        elif raw_line.strip() and current is not None:
            files.append(raw_line.strip())
    flush()

    for sha, first in merges[:500]:
        for m in (git(cwd, "rev-list", f"{first}..{sha}") or "").splitlines():
            if m.strip() and m.strip() != sha:
                out["members"].append((key, folder, sha, m.strip()))
    return out


# ---- 5. dbt, then publishing -----------------------------------------------------------------

def run_dbt(warehouse: Path) -> None:
    """`dbt build`, in a process of its own: dbt-duckdb keeps its connection for the life of the
    process, and publishing has to open the warehouse after it."""
    env = dict(os.environ, WHETSTONE_WAREHOUSE=str(warehouse), DO_NOT_TRACK="1")
    exe = Path(sys.executable).parent / ("dbt.exe" if os.name == "nt" else "dbt")
    cmd = [str(exe)] if exe.exists() else [sys.executable, "-c", "from dbt.cli.main import cli; cli()"]
    cmd += ["build", "--project-dir", str(HERE), "--profiles-dir", str(HERE),
            "--target-path", str(warehouse.parent / "dbt-target"),
            "--log-path", str(warehouse.parent / "dbt-logs")]
    res = subprocess.run(cmd, env=env, cwd=str(HERE))
    if res.returncode != 0:
        raise RuntimeError("dbt build failed: a model or test failed (the dbt output above says which)")


def publish(warehouse: Path, out_dir: Path) -> dict[str, int]:
    """Write every published mart to marts.next/, then swap it in for marts/ in one rename."""
    nxt, cur, old = out_dir / "marts.next", out_dir / "marts", out_dir / "marts.old"
    for d in (nxt, old):
        if d.exists():
            shutil.rmtree(d, ignore_errors=True)
    nxt.mkdir(parents=True)
    rows: dict[str, int] = {}
    con = duckdb.connect(str(warehouse), read_only=True)
    try:
        for name in PUBLISHED:
            target = (nxt / f"{name}.parquet").as_posix()
            con.execute(f"COPY main_marts.{name} TO '{target}' (FORMAT parquet, COMPRESSION zstd)")
            rows[name] = con.execute(f"SELECT count(*) FROM main_marts.{name}").fetchone()[0]
    finally:
        con.close()

    # The board opens a parquet file only for the length of a query, so the folder is free between
    # them; a few retries cover a read that happens to be in flight.
    for attempt in range(20):
        try:
            if cur.exists():
                cur.rename(old)
            nxt.rename(cur)
            break
        except OSError:
            if old.exists() and not cur.exists():
                old.rename(cur)
            time.sleep(0.25 * (attempt + 1))
    else:
        raise RuntimeError("could not swap the new marts in: the marts folder stayed in use")
    shutil.rmtree(old, ignore_errors=True)
    return rows


# ---- the whole thing -------------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data-root", type=Path, default=None, help="the board's data folder (default: DASHBOARD_DATA_ROOT, else ./data)")
    ap.add_argument("--no-transcripts", action="store_true", help="skip the worker transcripts (a QA copy's refresh)")
    ap.add_argument("--transcripts-dir", type=Path, default=None, help="where Claude keeps session folders (default: ~/.claude/projects)")
    ap.add_argument("--rebuild", action="store_true", help="forget the incremental transcript load and read every file again")
    ap.add_argument("--out-dir", type=Path, default=None, help="where to build and publish (default: <data root>/analytics)")
    ap.add_argument("--load-only", action="store_true", help="load the raw schema and stop, without dbt or publishing")
    args = ap.parse_args()

    data_root = (args.data_root or default_data_root()).resolve()
    out_dir = (args.out_dir or data_root / "analytics").resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    status_file = out_dir / "refresh.json"
    lock = out_dir / "refresh.lock"

    try:
        fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        os.write(fd, str(os.getpid()).encode())
        os.close(fd)
    except FileExistsError:
        # A lock older than an hour belongs to a refresh that died without cleaning up.
        if time.time() - lock.stat().st_mtime < 3600:
            log(f"another refresh is running ({lock})")
            return 2
        lock.unlink(missing_ok=True)
        return main()

    started = datetime.now(timezone.utc)
    status: dict = {"startedAt": started.isoformat(), "ok": False, "transcripts": not args.no_transcripts,
                    "steps": {}}

    def step(name: str, t0: float) -> None:
        status["steps"][name] = round(time.time() - t0, 1)

    def write_status() -> None:
        tmp = status_file.with_suffix(".tmp")
        tmp.write_text(json.dumps(status, indent=2), encoding="utf-8")
        os.replace(tmp, status_file)

    try:
        db_file = data_root / "dashboard.db"
        if not db_file.exists():
            raise RuntimeError(f"no board database at {db_file}")
        warehouse = out_dir / "warehouse.duckdb"
        if args.rebuild and warehouse.exists():
            warehouse.unlink()

        t0 = time.time()
        log(f"snapshot {db_file}")
        snap = snapshot_board(db_file, out_dir)
        con = duckdb.connect(str(warehouse))
        try:
            con.execute("CREATE SCHEMA IF NOT EXISTS raw")
            status["board"] = load_board(con, snap)
            step("board", t0)

            t0 = time.time()
            log("skill and prompt history")
            status["versions"] = load_versions(con, data_root)
            step("versions", t0)

            t0 = time.time()
            if args.no_transcripts:
                log("transcripts skipped")
                for name, ddl in TRANSCRIPT_DDL.items():
                    con.execute(f"CREATE OR REPLACE TABLE raw.{name} ({ddl})")
                status["transcriptRows"] = {}
            else:
                status["transcriptRows"] = load_transcripts(con, args.transcripts_dir or claude_projects_dir())
            step("transcripts", t0)

            t0 = time.time()
            log("recon inputs")
            status["recon"] = load_recon(con, out_dir / "recon-inputs.json")
            step("recon", t0)
            con.execute("CHECKPOINT")
        finally:
            con.close()
        snap.unlink(missing_ok=True)

        if args.load_only:
            status["ok"] = True
            log("loaded; stopping before dbt (--load-only)")
            return 0

        t0 = time.time()
        log("dbt build")
        run_dbt(warehouse)
        step("dbt", t0)

        t0 = time.time()
        log("publish")
        status["published"] = publish(warehouse, out_dir)
        step("publish", t0)

        status["ok"] = True
        status["asOf"] = started.isoformat()
        log("done")
        return 0
    except Exception as e:  # noqa: BLE001 - every failure is reported in refresh.json
        status["error"] = f"{type(e).__name__}: {e}"
        traceback.print_exc()
        return 1
    finally:
        status["finishedAt"] = datetime.now(timezone.utc).isoformat()
        status["seconds"] = round((datetime.now(timezone.utc) - started).total_seconds(), 1)
        # Keep the last good build's time, so a failure does not make the marts look newer than they are.
        if not status["ok"] and status_file.exists():
            try:
                prev = json.loads(status_file.read_text(encoding="utf-8"))
                status["asOf"] = prev.get("asOf")
            except (OSError, ValueError):
                pass
        write_status()
        lock.unlink(missing_ok=True)


if __name__ == "__main__":
    sys.exit(main())
