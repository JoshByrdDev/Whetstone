{#- Helpers for reading the board's own conventions. -#}

{#- The board writes timestamps as .NET does, with seven fractional digits and no zone (always UTC).
    DuckDB keeps microseconds, so the seventh digit is dropped rather than failing the cast. -#}
{% macro board_ts(col) -%}
    try_cast(left(replace({{ col }}, 'T', ' '), 26) as timestamp)
{%- endmacro %}

{#- The refresh's own clock, in UTC like every board timestamp. Windows are measured back from it. -#}
{% macro as_of() -%}
    cast(timezone('UTC', now()) as timestamp)
{%- endmacro %}

{#- Where a phase sits in the delivery pipeline, so "went backward" is a comparison. The order is
    PhaseMachine's; Blocked and Backlog sit outside it (null). -#}
{% macro phase_order(col) -%}
    case {{ col }}
        when 'NeedsSpeculation' then 1 when 'Speculating' then 2 when 'NeedsApproval' then 3
        when 'NeedsImplementation' then 4 when 'Implementing' then 5
        when 'NeedsImplementationReview' then 6 when 'NeedsQa' then 7 when 'QaTesting' then 8
        when 'NeedsRefinement' then 9 when 'Refining' then 10 when 'NeedsReview' then 11
        when 'Done' then 12
    end
{%- endmacro %}

{#- A path as the board and Claude Code both might write it, made comparable. -#}
{% macro norm_path(col) -%}
    rtrim(lower(replace({{ col }}, '\', '/')), '/')
{%- endmacro %}

{#- Explode a run-grain (or ticket-grain) relation into every scope the board can ask for: each
    window it falls inside, its own project and all projects ('*'), its own track and all tracks.
    A mart grouped by these three columns answers any filter on the page with a plain WHERE, which
    is what lets the board stay a reader with no aggregation of its own. -#}
{% macro scoped(relation, ts_col='started_at') -%}
    with _w as (
        select unnest({{ var('windows') }}) as window_days
    ), _p as (
        select *, unnest([project_key, '*']) as project_scope from {{ relation }}
    ), _t as (
        select *, unnest([track, '*']) as track_scope from _p
    )
    select _t.*, _w.window_days
    from _t cross join _w
    where _w.window_days = 0
       or _t.{{ ts_col }} >= {{ as_of() }} - to_days(cast(_w.window_days as integer))
{%- endmacro %}
