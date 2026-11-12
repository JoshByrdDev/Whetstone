-- Reviewed work that left review only because its branch no longer merged with the base
-- (RebaseReturn), with what the implementation run that rebased it judged it needed next and where
-- its hand-off went. A null verdict is one no run has judged yet.
select
    "Id"                            as return_id,
    "TicketId"                      as ticket_id,
    "ProjectKey"                    as project_key,
    coalesce("Track", 'Delivery')   as track,
    "Actor"                         as actor,
    "Reason"                        as reason,
    {{ board_ts('"At"') }}          as returned_at,
    "Verdict"                       as verdict,
    "VerdictWhy"                    as why,
    {{ board_ts('"JudgedAt"') }}    as judged_at,
    "JudgedByRunId"                 as judged_by_lease_run_id,
    "HandedOffTo"                   as handed_off_to,
    {{ board_ts('"HandedOffAt"') }} as handed_off_at
from {{ source('board', 'RebaseReturns') }}
