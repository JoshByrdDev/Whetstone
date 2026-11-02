-- One suggested, confirmed or rejected cause of a bug: the bug ticket, the ticket whose commits
-- wrote the lines it fixed (null when they came from outside the board), and the owner's call.
-- Only a confirmed link counts as a bug (docs/bugs-and-experiments-plan.md, 1.5 to 1.7).
select
    "Id"                            as link_id,
    "BugTicketId"                   as bug_ticket_id,
    "SourceTicketId"                as source_ticket_id,
    "ProjectKey"                    as project_key,
    "Status"                        as status,
    "Origin"                        as origin,
    "Lines"                         as lines,
    "Share"                         as share,
    "CommitsJson"                   as commits_json,
    coalesce("Neighbours", false)   as neighbours,
    coalesce("Escaped", false)      as escaped,
    "FoundBy"                       as found_by,
    "MissedBy"                      as missed_by,
    {{ board_ts('"TracedAt"') }}    as traced_at,
    {{ board_ts('"DecidedAt"') }}   as decided_at
from {{ source('board', 'BugLinks') }}
