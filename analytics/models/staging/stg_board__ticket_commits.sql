-- A delivered commit, mapped back to the ticket that made it. Repo is '' on a project of one repo.
select
    "Id"                            as ticket_commit_id,
    "TicketId"                      as ticket_id,
    "ProjectKey"                    as project_key,
    coalesce("Repo", '')            as repo,
    "Sha"                           as sha,
    "PatchId"                       as patch_id,
    "Source"                        as source,
    {{ board_ts('"RecordedAt"') }}  as recorded_at
from {{ source('board', 'TicketCommits') }}
