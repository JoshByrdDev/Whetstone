-- One discovery: a ticket on the Discovery track, with its direction (the ticket's description,
-- which is what the owner wrote and what Chart was given) and the Recon suggestion it was filed
-- from, if any. The direction lineage is worked out in fct_discovery_proposals.
select
    "Id"                                  as discovery_id,
    "ProjectKey"                          as project_key,
    'Discovery'                           as track,
    "Phase"                               as phase,
    "Title"                               as title,
    nullif(trim("Description"), '')       as direction,
    coalesce("RequiresRouteReview", false) as requires_route_review,
    nullif("ReconFrom", '')               as recon_from,
    {{ board_ts('"CreatedAt"') }}         as created_at,
    {{ board_ts('"UpdatedAt"') }}         as updated_at
from {{ source('board', 'Tickets') }}
where "Track" = 'Discovery'
