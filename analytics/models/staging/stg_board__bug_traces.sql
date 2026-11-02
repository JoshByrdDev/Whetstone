-- The latest trace of one bug ticket: how many lines it blamed, and why it found nothing if it did not.
select
    "TicketId"                    as bug_ticket_id,
    {{ board_ts('"TracedAt"') }}  as traced_at,
    "LinesBlamed"                 as lines_blamed,
    "Note"                        as note
from {{ source('board', 'BugTraces') }}
