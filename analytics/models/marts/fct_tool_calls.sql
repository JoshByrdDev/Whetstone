-- One tool call made by one run: the drill-down beneath a run.
select
    run_id,
    seq,
    tool_use_id,
    tool_name,
    tool_short,
    tool_family,
    tool_detail,
    input_summary,
    called_at,
    returned_at,
    offset_s,
    seconds,
    is_error,
    result_chars,
    is_sidechain
from {{ ref('int_run_tool_calls') }}
