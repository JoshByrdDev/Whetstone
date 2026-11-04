-- One ruling on one QA item, as the board logged it (QaRulingEvent): QA's statuses and carry-overs,
-- and refinement's expiries, each with the run that made it (its ticket lease id, matched to
-- CycleRuns.LeaseRunId) and, for an expiry, the ruling it overturned.
select
    "Id"                          as event_id,
    "TicketId"                    as ticket_id,
    "QaItemId"                    as qa_item_id,
    "ProjectKey"                  as project_key,
    coalesce("Track", 'Delivery') as track,
    "Phase"                       as phase,
    "Kind"                        as kind,
    "PriorVerdict"                as prior_verdict,
    "VerifiedOn"                  as verified_on,
    "Note"                        as note,
    {{ board_ts('"At"') }}        as ruled_at,
    "Actor"                       as actor,
    "RunId"                       as lease_run_id,
    "OverturnsEventId"            as overturns_event_id
from {{ source('board', 'QaRulingEvents') }}
