-- One tool call's result as it came back.
select session_id, ts as returned_at, tool_use_id, is_error, result_chars
from {{ source('transcripts', 'transcript_tool_results') }}
where tool_use_id is not null and ts is not null
qualify row_number() over (partition by tool_use_id order by ts, file) = 1
