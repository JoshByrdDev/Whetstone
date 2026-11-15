-- Which taste version a sift or recheck run read (null: the project had no taste file yet), once
-- per run, round and version.
select
    "Id"                                as taste_read_id,
    "TicketId"                          as discovery_id,
    "RunId"                             as lease_run_id,
    coalesce("Round", 0)                as round,
    "Phase"                             as phase,
    "VersionId"                         as taste_version_id,
    "VersionNumber"                     as taste_version_number,
    "Hash"                              as hash,
    {{ board_ts('"At"') }}              as read_at
from {{ source('board', 'DiscoveryTasteReads') }}
