{#- Recon's matching rules (docs/recon-tab-plan.md, "The marts"). The page pattern syntax is
    written down beside DiscoveryArea in Services/DiscoveryConfig.cs; these macros are its only
    implementation, so the two must agree. -#}


{#- A URL's path as patterns are matched against it: no scheme, host, query or fragment, lower case,
    trailing slashes trimmed and a leading one ensured, so the root is '' and 'events/' is '/events'. -#}
{% macro recon_path(col) -%}
    (case
        when {{ col }} is null then null
        when rtrim(lower(coalesce(regexp_extract({{ col }}, '^(?:[a-zA-Z][a-zA-Z0-9+.-]*://[^/]*)?([^?#]*)', 1), '')), '/') = '' then ''
        when starts_with(lower(coalesce(regexp_extract({{ col }}, '^(?:[a-zA-Z][a-zA-Z0-9+.-]*://[^/]*)?([^?#]*)', 1), '')), '/')
            then rtrim(lower(regexp_extract({{ col }}, '^(?:[a-zA-Z][a-zA-Z0-9+.-]*://[^/]*)?([^?#]*)', 1)), '/')
        else '/' || rtrim(lower(regexp_extract({{ col }}, '^(?:[a-zA-Z][a-zA-Z0-9+.-]*://[^/]*)?([^?#]*)', 1)), '/')
    end)
{%- endmacro %}

{#- A page pattern's segments: '/events/[slug]/' is ['events', '[slug]']. -#}
{% macro recon_segments(pattern) -%}
    list_filter(string_split(lower(trim({{ pattern }})), '/'), x -> x <> '')
{%- endmacro %}

{#- Whether a segment stands for exactly one segment of a path: '*' or '[name]'. -#}
{% macro recon_is_one(seg) -%}
    ({{ seg }} = '*' or regexp_full_match({{ seg }}, '\[[^/\]]+\]'))
{%- endmacro %}

{#- A page pattern as an anchored regex over recon_path(): a literal segment matches itself, '*' and
    '[name]' one segment, '**' any number of segments, none included. -#}
{% macro recon_page_regex(pattern) -%}
    ('^' || coalesce(array_to_string(list_transform({{ recon_segments(pattern) }}, s ->
        case when s = '**' then '(?:/[^/]+)*'
             when {{ recon_is_one('s') }} then '/[^/]+'
             else '/' || regexp_escape(s) end), ''), '') || '/?$')
{%- endmacro %}

{#- A code path as an anchored regex over a commit's file path: a git pathspec in glob form. '**/'
    is any folders, '**' anything, '*' anything within one segment, '?' one character; a path with
    no wildcard is that file or everything under that folder. regexp_escape escapes '/', which is put
    back first so '**/' can be found. -#}
{% macro recon_code_regex(spec) -%}
    ('^' || replace(replace(replace(replace(
        replace(regexp_escape(trim(both '/' from regexp_replace(trim({{ spec }}), '^\./', ''))), '\/', '/'),
        '\*\*/', '(?:.*/)?'), '\*\*', '.*'), '\*', '[^/]*'), '\?', '[^/]') || '(?:/.*)?$')
{%- endmacro %}

{#- Phone or desk, from a width as the scout logs it ('390x844'). The split is 640, the board's own
    NARROW_PX (ClientApp/src/hooks/useNarrow.ts): the two are written twice and must agree, and
    ReconTests checks they do. A width
    that does not start with a number is 'unknown', shown so the logging can be fixed. -#}
{% macro recon_width_bucket(width) -%}
    (case
        when try_cast(nullif(regexp_extract(coalesce({{ width }}, ''), '^\s*(\d+)', 1), '') as integer) is null then 'unknown'
        when try_cast(nullif(regexp_extract(coalesce({{ width }}, ''), '^\s*(\d+)', 1), '') as integer) < 640 then 'phone'
        else 'desk'
    end)
{%- endmacro %}

{#- Where a visit or observation was, as Recon counts it: `relation` has project_key, url, area (the
    scout's own), signed_in_as, width and route_pattern. The area is the most specific page match on
    the URL (int_recon_page_area), else the scout's own when it names one of the project's areas,
    else '(unmapped)'. The role is one of discovery.roles matched without case, else 'other' - or
    'any role' on a project that lists none. The width is phone, desk or unknown. -#}
{% macro recon_locate(relation) -%}
    select
        x.*,
        coalesce(pa.area, sa.area, '(unmapped)')                                   as recon_area,
        case when pa.area is not null then 'url' when sa.area is not null then 'scout' else 'none' end as recon_area_source,
        coalesce(pa.area_ordinal, sa.area_ordinal, 1000000)                        as recon_area_ordinal,
        coalesce(pa.pattern, nullif(trim(x.route_pattern), ''), nullif({{ recon_path('x.url') }}, ''), '/') as recon_pattern,
        case when coalesce(rc.n, 0) = 0 then 'any role' else coalesce(r.role, 'other') end as recon_role,
        case when coalesce(rc.n, 0) = 0 then 0 else coalesce(r.role_ordinal, 1000000) end  as recon_role_ordinal,
        {{ recon_width_bucket('x.width') }}                                        as recon_width
    from {{ relation }} x
    left join {{ ref('int_recon_page_area') }} pa
      on pa.project_key = x.project_key and pa.path = {{ recon_path('x.url') }}
    left join {{ ref('stg_recon__areas') }} sa
      on sa.project_key = x.project_key and lower(trim(sa.area)) = lower(trim(x.area))
    left join {{ ref('stg_recon__roles') }} r
      on r.project_key = x.project_key and r.role_match = lower(trim(x.signed_in_as))
    left join (select project_key, count(*) as n from {{ ref('stg_recon__roles') }} group by project_key) rc
      on rc.project_key = x.project_key
{%- endmacro %}

{#- A width bucket's place in the grid: phone, then desk, then unknown. -#}
{% macro recon_width_ordinal(col) -%}
    (case {{ col }} when 'phone' then 0 when 'desk' then 1 else 2 end)
{%- endmacro %}

{#- A discovery's proposals as yield counts, over the proposals the owner was shown. -#}
{% macro recon_yield_counts() -%}
    count(*) filter (where shown)                                                       as shown,
    count(*) filter (where shown and approved)                                          as approved,
    count(*) filter (where shown and verdict = 'rejected')                              as rejected,
    count(*) filter (where shown and verdict = 'rejected' and reject_reason = 'Generic')        as rejected_generic,
    count(*) filter (where shown and verdict = 'rejected' and reject_reason = 'AlreadyDecided') as rejected_already_decided,
    count(*) filter (where shown and verdict = 'rejected' and reject_reason = 'NotNow')         as rejected_not_now,
    count(*) filter (where shown and verdict = 'rejected' and reject_reason = 'Wrong')          as rejected_wrong,
    count(*) filter (where shown and verdict = 'snoozed')                               as snoozed,
    count(*) filter (where shown and new_to_me)                                         as new_to_me,
    count(*) filter (where filed)                                                       as filed
{%- endmacro %}

{#- 's' after a count that is not one. -#}
{% macro recon_plural(n, word) -%}
    ({{ n }} || ' ' || '{{ word }}' || case when {{ n }} = 1 then '' else 's' end)
{%- endmacro %}

{#- How a width reads in a sentence. -#}
{% macro recon_width_phrase(col) -%}
    (case {{ col }} when 'phone' then 'on a phone' when 'desk' then 'at a desk' else 'at an unknown width' end)
{%- endmacro %}

{#- A list of names as a sentence says it: "admin", "member or admin", "signed out, member or admin". -#}
{% macro recon_or_list(col) -%}
    (case when len({{ col }}) <= 1 then coalesce({{ col }}[1], '')
          else array_to_string({{ col }}[1:len({{ col }}) - 1], ', ') || ' or ' || {{ col }}[len({{ col }})] end)
{%- endmacro %}
