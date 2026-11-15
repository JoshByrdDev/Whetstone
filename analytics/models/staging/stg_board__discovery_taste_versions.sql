-- One version of a project's taste file: its number, who wrote it (Fold, Owner, Revert, Outside),
-- and the commit in the project folder that holds it. Appended, never rewritten.
select
    "Id"                                as taste_version_id,
    "ProjectKey"                        as project_key,
    "Number"                            as number,
    "Hash"                              as hash,
    "Source"                            as source,
    "Commit"                            as commit_sha,
    coalesce("Verdicts", 0)             as verdicts,
    "RevertOf"                          as revert_of,
    "RodeOnTicketId"                    as rode_on_ticket_id,
    {{ board_ts('"At"') }}              as written_at
from {{ source('board', 'DiscoveryTasteVersions') }}
