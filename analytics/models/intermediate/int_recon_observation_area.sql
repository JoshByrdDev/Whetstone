-- One observation, placed as a visit is (macros/recon.sql, recon_locate): what a proposal's area is
-- taken from, and which coverage cell its yield counts in.
with o as (
    select o.*, d.project_key, null::varchar as route_pattern
    from {{ ref('stg_board__discovery_observations') }} o
    join {{ ref('stg_board__discovery_tickets') }} d using (discovery_id)
),
located as (
    {{ recon_locate('o') }}
)
select
    observation_id,
    discovery_id,
    project_key,
    recon_area          as area,
    recon_area_source   as area_source,
    recon_area_ordinal  as area_ordinal,
    recon_role          as role,
    recon_width         as width
from located
