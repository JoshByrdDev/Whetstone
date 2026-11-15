-- One page a scout or recheck run was on: where, how wide, as whom, for how long, on which commit.
-- The scout's own Area and RoutePattern are kept as it logged them; Recon re-maps the URL to an
-- area at refresh time (docs/recon-tab-plan.md, int_recon_visit_area).
select
    "Id"                                as visit_id,
    "TicketId"                          as discovery_id,
    "Url"                               as url,
    -- The path alone, for counting pages: no scheme, host, query or fragment.
    coalesce(nullif(regexp_extract("Url", '^(?:[a-zA-Z][a-zA-Z0-9+.-]*://[^/]*)?([^?#]*)', 1), ''), '/') as url_path,
    nullif(trim("RoutePattern"), '')    as route_pattern,
    nullif(trim("Width"), '')           as width,
    nullif(trim("SignedInAs"), '')      as signed_in_as,
    "Seconds"                           as seconds,
    nullif(trim("Area"), '')            as area,
    nullif(trim("Case"), '')            as showcase_case,
    nullif(trim("Commit"), '')          as commit_sha,
    "RunId"                             as lease_run_id,
    {{ board_ts('"At"') }}              as visited_at
from {{ source('board', 'DiscoveryVisits') }}
