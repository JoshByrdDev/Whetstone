-- One of a project's areas (discovery.areas), in config order: its page patterns and code paths, and
-- the roles it is used as (its own `roles`, else every one the project lists; empty when it lists none).
select
    project_key,
    area,
    ordinal                              as area_ordinal,
    coalesce(pages, [])                  as pages,
    coalesce(code, [])                   as code,
    len(coalesce(code, [])) > 0          as has_code,
    coalesce(roles, [])::varchar[]       as roles
from {{ source('recon', 'recon_areas') }}
