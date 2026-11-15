-- Each page path a project's visits and observations went to, with the area it belongs to: the
-- most specific of the project's page patterns that matches it (more literal segments, then fewer
-- '**', then more segments, then the area listed first). Worked out at refresh time, so redefining
-- an area re-maps every visit already recorded. A path no pattern matches is absent.
with patterns as (
    select project_key, area, area_ordinal, pattern,
           {{ recon_page_regex('pattern') }}                                                  as pattern_regex,
           len(list_filter({{ recon_segments('pattern') }}, s -> s <> '**' and not {{ recon_is_one('s') }})) as literal_segments,
           len(list_filter({{ recon_segments('pattern') }}, s -> s = '**'))                  as deep_wildcards,
           len({{ recon_segments('pattern') }})                                              as segments
    from (select project_key, area, area_ordinal, unnest(pages) as pattern from {{ ref('stg_recon__areas') }})
    where trim(pattern) <> ''
),
d as (
    select discovery_id, project_key from {{ ref('stg_board__discovery_tickets') }}
),
pages as (
    select distinct d.project_key, {{ recon_path('v.url') }} as path
    from {{ ref('stg_board__discovery_visits') }} v join d using (discovery_id)
    where v.url is not null
    union
    select distinct d.project_key, {{ recon_path('o.url') }} as path
    from {{ ref('stg_board__discovery_observations') }} o join d using (discovery_id)
    where o.url is not null
)
select p.project_key, p.path, a.area, a.area_ordinal, a.pattern
from pages p
join patterns a
  on a.project_key = p.project_key and regexp_full_match(p.path, a.pattern_regex)
qualify row_number() over (partition by p.project_key, p.path
                           order by a.literal_segments desc, a.deep_wildcards, a.segments desc, a.area_ordinal, a.pattern) = 1
