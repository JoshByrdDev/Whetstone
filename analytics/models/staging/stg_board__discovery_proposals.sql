-- One proposal a sift (round 0) or a recheck (round N) wrote, or a line it dropped. The proposal's
-- columns are its current state; the verdict history is stg_board__discovery_verdict_events.
select
    "Id"                                    as proposal_id,
    "TicketId"                              as discovery_id,
    "Rank"                                  as rank,
    "Kind"                                  as kind,
    coalesce("DraftKind", "Kind")           as draft_kind,
    "Status"                                as status,
    "Title"                                 as title,
    "DraftTitle"                            as draft_title,
    "Size"                                  as size,
    "Confidence"                            as confidence,
    nullif(trim("Area"), '')                as area,
    coalesce(from_json("ObservationIds", '["INTEGER"]'), [])  as observation_ids,
    nullif(trim("DropReason"), '')          as drop_reason,
    "RejectReason"                          as reject_reason,
    "FiledTicketId"                         as filed_ticket_id,
    coalesce("Edited", false)               as edited,
    "NewToMe"                               as new_to_me,
    "RunId"                                 as lease_run_id,
    coalesce("Round", 0)                    as round,
    "RecheckKind"                           as recheck_kind,
    "GoalId"                                as goal_id,
    coalesce("Pinned", false)               as pinned,
    {{ board_ts('"SnoozeUntil"') }}         as snooze_until,
    {{ board_ts('"CreatedAt"') }}           as created_at,
    {{ board_ts('"DecidedAt"') }}           as decided_at
from {{ source('board', 'DiscoveryProposals') }}
