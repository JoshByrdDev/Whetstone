-- One thing a scout (round 0) or the Nth recheck (round N) saw.
select
    "Id"                                as observation_id,
    "TicketId"                          as discovery_id,
    "Kind"                              as kind,
    "Title"                             as title,
    "Job"                               as job,
    "Url"                               as url,
    nullif(trim("Width"), '')           as width,
    nullif(trim("SignedInAs"), '')      as signed_in_as,
    nullif(trim("Area"), '')            as area,
    nullif(trim("Commit"), '')          as commit_sha,
    "RunId"                             as lease_run_id,
    "VideoAt"                           as video_at,
    "RecordingId"                       as recording_id,
    coalesce("Round", 0)                as round,
    {{ board_ts('"ObservedAt"') }}      as observed_at,
    {{ board_ts('"CreatedAt"') }}       as created_at
from {{ source('board', 'DiscoveryObservations') }}
