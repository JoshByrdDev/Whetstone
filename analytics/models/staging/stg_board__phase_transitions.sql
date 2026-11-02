-- One move of a ticket from one phase to another.
select
    "Id"                          as transition_id,
    "TicketId"                    as ticket_id,
    "ProjectKey"                  as project_key,
    coalesce("Track", 'Delivery') as track,
    "From"                        as from_phase,
    "To"                          as to_phase,
    "Actor"                       as actor,
    "Reason"                      as reason,
    coalesce("Backward", false)   as backward,
    coalesce("Backfilled", false) as backfilled,
    {{ board_ts('"At"') }}        as moved_at
from {{ source('board', 'PhaseTransitions') }}
