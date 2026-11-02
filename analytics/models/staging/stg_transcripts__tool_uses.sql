-- One tool call as the model asked for it. A resumed session can copy earlier lines into a new
-- file, so a call is kept once, at its first sighting.
select session_id, ts as called_at, is_sidechain, tool_use_id, tool_name, input_summary
from {{ source('transcripts', 'transcript_tool_uses') }}
where tool_use_id is not null and ts is not null and tool_name is not null
qualify row_number() over (partition by tool_use_id order by ts, file) = 1
