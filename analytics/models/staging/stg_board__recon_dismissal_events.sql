-- The owner setting a Recon suggestion aside (Not now) or taking that back, appended and never
-- rewritten. History only: the board applies set-asides live from its own table.
select
    "Id"                                as event_id,
    "ProjectKey"                        as project_key,
    "SuggestionKey"                     as suggestion_key,
    "Kind"                              as kind,
    nullif(trim("AreaHeadCommit"), '')  as area_head_commit,
    {{ board_ts('"Until"') }}           as until_at,
    {{ board_ts('"At"') }}              as acted_at
from {{ source('board', 'ReconDismissalEvents') }}
