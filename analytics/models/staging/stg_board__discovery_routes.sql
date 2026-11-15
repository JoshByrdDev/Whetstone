-- What Chart wrote for one discovery: who the person is (as a role), the jobs, the widths and the
-- areas it expected to cover. Lists are the JSON arrays the board stores.
select
    "TicketId"                              as discovery_id,
    nullif(trim("SignedInAs"), '')          as signed_in_as,
    coalesce(json_array_length(try_cast("Jobs" as json)), 0)       as jobs,
    coalesce(from_json("StartCases", '["VARCHAR"]'), [])              as start_cases,
    coalesce(from_json("Widths", '["VARCHAR"]'), [])                  as widths,
    coalesce(from_json("Areas", '["VARCHAR"]'), [])                   as areas,
    "RunId"                                 as lease_run_id,
    {{ board_ts('"WrittenAt"') }}           as written_at,
    {{ board_ts('"ApprovedAt"') }}          as approved_at,
    coalesce("OwnerEdited", false)          as owner_edited
from {{ source('board', 'DiscoveryRoutes') }}
