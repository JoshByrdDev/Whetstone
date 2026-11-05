// One of the dashboard's own agent runs, through the Claude Agent SDK instead of the `claude` CLI.
//
// Why this exists: the monthly API credits that come with a Max or Ultra plan pay for the Agent
// SDK on a Console API key, and do not pay for Claude Code. A dashboard run (the Classroom, the
// improvement applier) started here spends those credits instead of the plan's usage limits.
//
// It takes the same flags `claude -p` is given by ClaudeAgentProvider and prints the same thing,
// so the C# side reads its output with the code it already has:
//   --model <m>  --effort <e>  --tools <a,b,c>  --permission-mode <mode>
//   --output-format stream-json|text   --json-schema <json>   --verbose (accepted, ignored)
// The prompt arrives on stdin. The API key arrives as ANTHROPIC_API_KEY in the environment, never
// on the command line, where any process listing would show it.
//
// Exit code is 1 when the run ended in an error, as the CLI's is.

import { query } from '@anthropic-ai/claude-agent-sdk';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

function parseArgs(argv) {
  const opts = { outputFormat: 'text' };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    switch (a) {
      case '-p': case '--print': case '--verbose': break;
      case '--model': opts.model = next(); break;
      case '--effort': opts.effort = next(); break;
      case '--tools': opts.tools = next().split(',').map(s => s.trim()).filter(Boolean); break;
      case '--permission-mode': opts.permissionMode = next(); break;
      case '--output-format': opts.outputFormat = next(); break;
      case '--json-schema': opts.schema = JSON.parse(next()); break;
      default: throw new Error(`unknown argument: ${a}`);
    }
  }
  return opts;
}

async function readStdin() {
  const chunks = [];
  for await (const c of process.stdin) chunks.push(c);
  return Buffer.concat(chunks).toString('utf8');
}

// What a run that never got going says, in the envelope the CLI closes every run with, so the
// reader on the other side finds its reason where it always looks.
function failure(message) {
  return { type: 'result', subtype: 'error_during_execution', is_error: true, result: message };
}

const stream = (o) => process.stdout.write(JSON.stringify(o) + '\n');

let opts;
try {
  opts = parseArgs(process.argv.slice(2));
} catch (e) {
  stream(failure(`agent-sdk runner: ${e.message}`));
  process.exit(1);
}

if (!process.env.ANTHROPIC_API_KEY) {
  // Without a key the SDK would fall back to the machine's Claude login - the plan usage this
  // runner exists to spare. Refusing is the only way "spend the credits" can never quietly mean
  // "spend the plan".
  const msg = 'agent-sdk runner: no ANTHROPIC_API_KEY was given';
  if (opts.outputFormat === 'stream-json') stream(failure(msg)); else process.stdout.write(msg + '\n');
  process.exit(1);
}

const prompt = await readStdin();

// The key must be the ONLY credential the session can find. Claude Code prefers a signed-in login
// over an API key it has not been told to trust, so a run that can see the machine's login spends
// the plan - silently, and successfully, which is the worst way for this to go wrong. An empty
// config folder of its own has no login in it, and every other way of handing it one is removed
// from the environment. With a bad or spent key the run now fails instead of falling back.
const configDir = mkdtempSync(join(tmpdir(), 'whetstone-sdk-'));
const env = { ...process.env, CLAUDE_CONFIG_DIR: configDir };
for (const k of ['CLAUDE_CODE_OAUTH_TOKEN', 'ANTHROPIC_AUTH_TOKEN', 'CLAUDE_CODE_USE_BEDROCK',
  'CLAUDE_CODE_USE_VERTEX', 'CLAUDE_CODE_USE_FOUNDRY', 'ANTHROPIC_BASE_URL']) delete env[k];

const options = {
  cwd: process.cwd(),
  model: opts.model,
  env,
  // The CLI's own system prompt, and the repo's own settings and CLAUDE.md. Not the user's: their
  // settings live in the login folder this run is kept out of, and they are also the plugins and
  // servers that make a session's opening context large - paid for here by the token.
  systemPrompt: { type: 'preset', preset: 'claude_code' },
  settingSources: ['project', 'local'],
};
if (opts.effort) options.effort = opts.effort;
if (opts.tools) options.tools = opts.tools;
if (opts.permissionMode) {
  options.permissionMode = opts.permissionMode;
  if (opts.permissionMode === 'bypassPermissions') options.allowDangerouslySkipPermissions = true;
}
if (opts.schema) options.outputFormat = { type: 'json_schema', schema: opts.schema };

let failed = false;
let sawResult = false;
let finalText = '';
try {
  for await (const message of query({ prompt, options })) {
    if (opts.outputFormat === 'stream-json') stream(message);
    if (message.type === 'result') {
      sawResult = true;
      failed = !!message.is_error;
      finalText = typeof message.result === 'string' ? message.result : '';
    }
  }
} catch (e) {
  // The SDK throws after an error result as well as instead of one. After one, the result already
  // said why, and a second envelope would only bury it.
  failed = true;
  if (!sawResult) {
    const msg = `agent-sdk runner: ${e?.message ?? e}`;
    if (opts.outputFormat === 'stream-json') stream(failure(msg)); else finalText = msg;
  }
}

try { rmSync(configDir, { recursive: true, force: true }); } catch { /* a leftover temp folder holds no login */ }

if (opts.outputFormat !== 'stream-json') process.stdout.write(finalText + '\n');
process.exit(failed ? 1 : 0);
