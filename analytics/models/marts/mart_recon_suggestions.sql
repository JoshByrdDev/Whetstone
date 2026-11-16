-- Where to point the next scout (docs/recon-tab-plan.md, "Scout next"): one row per suggestion of a
-- project, ranked by plain rules in a fixed order, each row saying in `reasons` why it is there.
--
--   1. rerun   - an area whose code changed since it was scouted, with a past direction that had at
--                least one proposal approved there. The best such direction by approved, then new to
--                me, then the most recent, offered again word for word (key rerun:<discovery id>, the
--                latest discovery of that lineage that went there).
--   2. gap     - an area with cells nobody has scouted, whose code changed in the last 30 days.
--   3. gap     - the same, anywhere else.
--   4. changed - an area scouted before and changed since, whose past directions had nothing
--                approved there.
--
-- Inside a rule: more files changed first (since the last scout, or in the last 90 days for an area
-- never scouted), then the area scouted longest ago, never-scouted first. An area that is `cooling` -
-- its last three judged discoveries had nothing approved there - sinks to the bottom of its rule.
-- A gap row is one per area, listing its empty cells (`cells`); its direction is a stub naming them
-- - a description of the gap for the row, never a direction to file: Scout this starts the form's
-- direction empty, for the owner to write or to have drafted. An area's cells are its own roles
-- (discovery.areas[].roles) x the two widths, so its counts are per area, not per project. An area with no code paths is never stale, so it is never a
-- rerun or changed row. Hiding a row while an open discovery carries its key, or while it is set
-- aside, is the reader's, on live tables.
with st as (
    select * from {{ ref('int_recon_area_state') }}
),
cov as (
    select * from {{ ref('mart_recon_coverage') }} where in_config
),
d as (
    select * from {{ ref('int_discovery_tickets') }}
),

-- ---- rerun: the best past direction per area
lin as (
    select d.project_key, p.area, d.direction_lineage_id as lineage_id,
           count(*) filter (where p.approved)                  as approved,
           count(*) filter (where p.new_to_me)                 as new_to_me,
           max(d.created_at)                                   as latest_at,
           arg_max(d.discovery_id, d.created_at)               as latest_discovery_id
    from {{ ref('fct_discovery_proposals') }} p
    join d using (discovery_id)
    where p.shown and p.area is not null
    group by all
    having count(*) filter (where p.approved) > 0
),
best as (
    select * from lin
    qualify row_number() over (partition by project_key, area
                               order by approved desc, new_to_me desc, latest_at desc, lineage_id desc) = 1
),

-- ---- gap: the empty cells per area, and the stub that names them
gaps as (
    select project_key, area, area_ordinal, role, role_ordinal, width, width_ordinal from cov where state = 'gap'
),
dims as (
    select project_key, area, count(distinct role) as n_roles, bool_or(role = 'any role') as any_role
    from cov group by project_key, area
),
gap_area as (
    select g.project_key, g.area, count(*) as missing, any_value(dm.n_roles) * 2 as cells_total,
           any_value(dm.any_role) as any_role,
           to_json(list({'role': g.role, 'width': g.width} order by g.role_ordinal, g.width_ordinal))::varchar as cells,
           list(case when dm.any_role then {{ recon_width_phrase('g.width') }}
                     else 'as ' || g.role || ' ' || {{ recon_width_phrase('g.width') }} end
                order by g.role_ordinal, g.width_ordinal) as cell_phrases
    from gaps g join dims dm using (project_key, area)
    group by g.project_key, g.area
),
full_w as (
    select g.project_key, g.area, g.width, g.width_ordinal
    from gaps g join dims dm using (project_key, area)
    group by g.project_key, g.area, g.width, g.width_ordinal
    having count(*) = any_value(dm.n_roles)
),
full_r as (
    select g.project_key, g.area, g.role, g.role_ordinal
    from gaps g join dims dm using (project_key, area)
    where not dm.any_role
    group by g.project_key, g.area, g.role, g.role_ordinal
    having count(*) = 2
),
parts as (
    select project_key, area, 0 as grp, role_ordinal as o, 'as ' || role as phrase from full_r
    union all
    select w.project_key, w.area, 1, w.width_ordinal,
           case when dm.any_role then {{ recon_width_phrase('w.width') }} else 'as anyone ' || {{ recon_width_phrase('w.width') }} end
    from full_w w join dims dm using (project_key, area)
    union all
    select g.project_key, g.area, 2, g.role_ordinal * 10 + g.width_ordinal,
           'as ' || g.role || ' ' || {{ recon_width_phrase('g.width') }}
    from gaps g join dims dm using (project_key, area)
    where not dm.any_role
      and not exists (select 1 from full_w w where w.project_key = g.project_key and w.area = g.area and w.width = g.width)
      and not exists (select 1 from full_r r where r.project_key = g.project_key and r.area = g.area and r.role = g.role)
),
phrase as (
    select project_key, area, list(phrase order by grp, o) as ps from parts group by all
),
role_names as (
    select project_key, area, list(role order by role_ordinal) as roles
    from (select distinct project_key, area, role, role_ordinal from cov) group by project_key, area
),
stub as (
    select ga.project_key, ga.area,
           case when ga.missing = ga.cells_total and st.last_visited_at is null and ga.any_role then 'Nobody has used ' || ga.area || ' yet.'
                -- Never scouted: say who it is for, so the row reads as the people missing, not the screens.
                when ga.missing = ga.cells_total and st.last_visited_at is null then 'Nobody has used ' || ga.area || ' yet, as '
                     || {{ recon_or_list('rn.roles') }} || '.'
                -- Visited, but only as a role or at a width the area does not list.
                when ga.missing = ga.cells_total then 'Nobody has used ' || ga.area || ' as '
                     || {{ recon_or_list('rn.roles') }} || '.'
                when len(ph.ps) = 1 then 'Nobody has used ' || ga.area || ' ' || ph.ps[1] || '.'
                else 'Nobody has used ' || ga.area || ' ' || array_to_string(ph.ps[1:len(ph.ps) - 1], ', ')
                     || ', or ' || ph.ps[len(ph.ps)] || '.' end as direction
    from gap_area ga
    left join phrase ph using (project_key, area)
    join st using (project_key, area)
    join role_names rn using (project_key, area)
),

-- ---- the rows, before ranking
raw as (
    select st.project_key, 'rerun:' || b.latest_discovery_id as suggestion_key, 'rerun' as kind, 1 as rule,
           st.area, st.area_ordinal, '[]' as cells, src.direction,
           b.latest_discovery_id as source_discovery_id, b.lineage_id as source_lineage_id,
           b.approved, b.new_to_me
    from st join best b using (project_key, area) left join d src on src.discovery_id = b.latest_discovery_id
    where st.state = 'changed'
    union all
    select st.project_key, 'gap:' || st.area, 'gap', case when st.changed_recently then 2 else 3 end,
           st.area, st.area_ordinal, ga.cells, s.direction, null, null, null, null
    from st join gap_area ga using (project_key, area) join stub s using (project_key, area)
    union all
    select st.project_key, 'changed:' || st.area, 'changed', 4,
           st.area, st.area_ordinal, '[]',
           st.area || ' has changed since it was scouted on ' || strftime(st.last_visited_at, '%-d %b')
               || '. Use it again as the people it is for, and see what is different.',
           null, null, null, null
    from st
    where st.state = 'changed' and not exists (select 1 from best b where b.project_key = st.project_key and b.area = st.area)
),
facts as (
    select r.*,
           st.cooling, st.area_head_commit, st.last_visited_at as last_scouted_at, st.last_commit, st.has_code,
           case when st.last_visited_at is not null then st.files_since else st.files_90d end   as files_changed,
           case when st.last_visited_at is not null then st.commits_since else st.commits_90d end as commits_changed,
           case when st.last_visited_at is not null then st.tickets_since else st.tickets_90d end as tickets,
           st.state as area_state, st.changed_recently, ga.cells as gap_cells, ga.cell_phrases
    from raw r
    join st using (project_key, area)
    left join gap_area ga using (project_key, area)
)
select
    project_key,
    suggestion_key,
    kind,
    rule,
    row_number() over (partition by project_key
                       order by rule, cooling, files_changed desc, last_scouted_at asc nulls first, area_ordinal, kind) as rank,
    area,
    area_ordinal,
    cells,
    direction,
    source_discovery_id,
    source_lineage_id,
    -- Short facts, in the order they are read.
    to_json(list_filter([
        case when last_scouted_at is null then 'never scouted'
             else 'scouted ' || strftime(last_scouted_at, '%-d %b') || coalesce(' on ' || left(last_commit, 7), '') end,
        case when kind in ('rerun', 'changed') then
                 {{ recon_plural('commits_changed', 'commit') }} || ', ' || {{ recon_plural('files_changed', 'file') }} || ' in its code since'
                 || case when len(tickets) > 0 then ', from ' || {{ recon_plural('len(tickets)', 'ticket') }} else '' end end,
        case when kind = 'rerun' then 'this direction: ' || approved || ' approved here'
                 || case when new_to_me > 0 then ', ' || new_to_me || ' new to me' else '' end end,
        case when kind = 'changed' then 'nothing approved from past directions here' end,
        case when kind = 'gap' and last_scouted_at is not null then 'never ' || array_to_string(cell_phrases, ', ') end,
        case when kind = 'gap' and rule = 2 then
                 'its code changed in the last 30 days (' || {{ recon_plural('commits_changed', 'commit') }} || ' in 90)' end,
        case when kind = 'gap' and not has_code then 'no code paths, so never stale' end,
        case when kind = 'gap' and area_state = 'unknown' then 'last scouted on a commit not on the base, so what changed since is unknown' end,
        case when cooling then 'cooling: its last 3 discoveries had nothing approved here' end
    ], x -> x is not null))::varchar                                     as reasons,
    cooling,
    area_head_commit,
    files_changed,
    commits_changed,
    to_json(tickets)::varchar                                            as tickets,
    last_scouted_at,
    last_commit,
    approved,
    new_to_me
from facts
