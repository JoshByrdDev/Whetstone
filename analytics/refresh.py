"""Refresh the board's efficiency marts: load the sources, run dbt, publish the marts.

    uv run --project analytics python analytics/refresh.py [--data-root DIR] [--no-transcripts]

Everything it writes is under <data root>/analytics/ (gitignored, one machine's):

    warehouse.duckdb   the build database. Only this script ever opens it, so the board never
                       holds a lock dbt would trip over.
    marts/*.parquet    what the board reads, swapped in whole once a build has passed its tests.
    refresh.json       how the last refresh went - read by the board's status route.

Three sources, loaded into the warehouse's `raw` schema:

  * the board database, read through SQLite's backup API into a private snapshot, so the live
    database is only ever read and never held;
  * every skill's and the worker prompt's git history, so a run recorded before runs carried a
    skill hash still lands on the version it was given;
  * the Claude worker transcripts under ~/.claude/projects, found from the worktree roots the
    board's tickets name, and loaded incrementally: a file is re-read only when it has changed.

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
        # Added by AddRunContextWindow: what the peak is measured against, and whether it overflowed.
        "ContextWindowTokens": "BIGINT", "Compactions": "INTEGER",
    },
    "PhaseTransitions": {
        "Id": "INTEGER", "TicketId": "INTEGER", "ProjectKey": "VARCHAR", "Track": "VARCHAR",
        "From": "VARCHAR", "To": "VARCHAR", "Actor": "VARCHAR", "Reason": "VARCHAR",
        "Backward": "BOOLEAN", "Backfilled": "BOOLEAN", "At": "VARCHAR",
    },
    "Tickets": {
        "Id": "INTEGER", "ProjectKey": "VARCHAR", "Track": "VARCHAR", "Phase": "VARCHAR",
        "Title": "VARCHAR", "Priority": "VARCHAR", "CreatedAt": "VARCHAR", "UpdatedAt": "VARCHAR",
        "WorktreePath": "VARCHAR", "Branch": "VARCHAR", "PlanKey": "VARCHAR", "PlanUnit": "VARCHAR",
        "MergedAt": "VARCHAR", "Walkthrough": "BOOLEAN", "IsBug": "BOOLEAN",
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


# ---- 4. dbt, then publishing -----------------------------------------------------------------

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
