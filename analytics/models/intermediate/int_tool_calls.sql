-- One tool call with how long it took: from the line that asked for it to the line its result came
-- back on. A call with no result (the run was killed mid-call) has no duration.
with uses as (
    select * from {{ ref('stg_transcripts__tool_uses') }}
),
results as (
    select * from {{ ref('stg_transcripts__tool_results') }}
),
joined as (
    select
        u.tool_use_id,
        u.session_id,
        u.is_sidechain,
        u.tool_name,
        u.input_summary,
        u.called_at,
        r.returned_at,
        case when r.returned_at >= u.called_at
             then datediff('millisecond', u.called_at, r.returned_at) / 1000.0 end as seconds,
        coalesce(r.is_error, false) as is_error,
        r.result_chars
    from uses u
    left join results r using (tool_use_id)
),

-- A shell command without what only sets the scene for it: `cd x &&`, `export A=1;`, `A=1 cmd`,
-- `set -e;`, `timeout 600 cmd`. What is left starts with the program the run waited on.
shell as (
    select tool_use_id,
           regexp_replace(
               regexp_replace(input_summary,
                   '^\s*((cd\s+("[^"]*"|''[^'']*''|\S+)|export\s+\S+|set\s+-\S+|source\s+\S+|[A-Za-z_][A-Za-z0-9_]*=("[^"]*"|\S*))\s*(&&|;|\n|\s)\s*)+', ''),
               '^\s*timeout\s+\S+\s+', '') as cmd
    from joined
    where tool_name in ('Bash', 'PowerShell')
)

select
    j.*,
    -- Families, so a page can say "the browser" rather than list twenty playwright tools.
    case
        when tool_name like 'mcp\_\_%' escape '\' then 'mcp:' || split_part(tool_name, '__', 2)
        when tool_name in ('Bash', 'PowerShell') then 'shell'
        when tool_name in ('Read', 'Edit', 'Write', 'MultiEdit', 'NotebookEdit') then 'files'
        when tool_name in ('Grep', 'Glob', 'LS') then 'search'
        when tool_name in ('Task', 'Agent') then 'subagent'
        when tool_name in ('WebFetch', 'WebSearch') then 'web'
        when tool_name in ('ToolSearch', 'Skill', 'TodoWrite', 'StructuredOutput') then 'harness'
        else 'other'
    end as tool_family,
    -- The short name a page shows: an MCP tool without its server prefix.
    case when tool_name like 'mcp\_\_%' escape '\'
         then regexp_replace(tool_name, '^mcp__[^_]+(?:_[^_]+)*?__', '')
         else tool_name end as tool_short,
    -- For a shell call, the program and its verb ("dotnet build", "git rebase"): what a run waits
    -- on is rarely "Bash", always one of these. A loop is named as one - in a worker it is almost
    -- always polling for something to finish, which is worth seeing as its own line.
    case
        when sh.cmd is null then null
        when regexp_matches(sh.cmd, '^\s*(for|while|until)\b')
            then regexp_extract(sh.cmd, '^\s*(for|while|until)\b', 1) || '-loop'
        when regexp_matches(sh.cmd, '^\s*sleep\b') then 'sleep'
        else nullif(trim(
            regexp_replace(regexp_extract(sh.cmd, '^\s*([^\s|;&<>()]+)', 1), '^.*[/\\]', '')
            || coalesce(' ' || nullif(regexp_extract(sh.cmd, '^\s*[^\s|;&<>()]+\s+([a-z][a-z0-9:_-]*)', 1), ''), '')
        ), '')
    end as tool_detail
from joined j
left join shell sh using (tool_use_id)
