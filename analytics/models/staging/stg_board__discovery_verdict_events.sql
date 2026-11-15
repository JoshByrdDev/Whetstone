-- One thing the owner did to a proposal, appended and never rewritten: approve, reject (with its
-- reason), snooze, undo, restore from the dropped list, the one-tap new to me, and Complete's filing.
-- Each measures the proposal as acted on against Sift's draft.
select
    "Id"                                as event_id,
    "TicketId"                          as discovery_id,
    "ProposalId"                        as proposal_id,
    "ProjectKey"                        as project_key,
    "Kind"                              as kind,
    "RejectReason"                      as reject_reason,
    "NewToMe"                           as new_to_me,
    coalesce("Edited", false)           as edited,
    coalesce("KindChanged", false)      as kind_changed,
    coalesce("EditDistance", 0)         as edit_distance,
    coalesce("DraftLength", 0)          as draft_length,
    "FiledTicketId"                     as filed_ticket_id,
    {{ board_ts('"At"') }}              as acted_at
from {{ source('board', 'DiscoveryVerdictEvents') }}
