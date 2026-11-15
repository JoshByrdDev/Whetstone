-- One of a project's areas (discovery.areas), in config order: its page patterns and code paths.
select
    project_key,
    area,
    ordinal                              as area_ordinal,
    coalesce(pages, [])                  as pages,
    coalesce(code, [])                   as code,
    len(coalesce(code, [])) > 0          as has_code
from {{ source('recon', 'recon_areas') }}
