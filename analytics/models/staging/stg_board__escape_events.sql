-- Finished work a person sent back (EscapeEvent): a move by a person from NeedsReview or Done back
-- into the working phases, with the build it escaped from and, once someone has said, what kind of
-- miss it was. A null kind is one nobody has classified yet.
select
    "Id"                            as escape_id,
    "TicketId"                      as ticket_id,
    "ProjectKey"                    as project_key,
    coalesce("Track", 'Delivery')   as track,
    "FromPhase"                     as from_phase,
    "ToPhase"                       as to_phase,
    "Reason"                        as reason,
    "NoteCommentId"                 as note_comment_id,
    "Build"                         as build,
    {{ board_ts('"At"') }}          as escaped_at,
    coalesce("Backfilled", false)   as backfilled,
    "Kind"                          as kind,
    "Source"                        as source,
    "ClassifierNote"                as why,
    {{ board_ts('"ClassifiedAt"') }} as classified_at,
    "ClassifiedBy"                  as classified_by,
    "ClassifiedByRunId"             as classified_by_lease_run_id,
    coalesce("Overridden", false)   as overridden
from {{ source('board', 'EscapeEvents') }}
