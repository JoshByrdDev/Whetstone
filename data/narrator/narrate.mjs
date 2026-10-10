// Voices a walkthrough. Whetstone runs this; nothing else should need to.
//
// stdin:  { "voice": "af_heart", "outDir": "<abs>", "items": [ { "id": "<hash>", "sentences": ["...", ...] } ] }
// stdout: one JSON object per line - { "id", "ok": true } or { "id", "error" } per item, then { "done": true }.
//
// Pronunciation and sentence splitting are the server's job (Pronunciation.cs); this only turns the
// sentences it is handed into sound. One generate() per sentence, because Kokoro truncates past 510
// phoneme tokens WITHOUT an error - a sentence that long is refused here instead of losing its end.
import { KokoroTTS } from "kokoro-js";
import { phonemize } from "phonemizer";
import { existsSync, mkdirSync, readFileSync, renameSync } from "node:fs";
import { join } from "node:path";

const MAX_PHONEMES = 480;
const GAP_SECONDS = 0.12;

const input = JSON.parse(readFileSync(0, "utf8"));
mkdirSync(input.outDir, { recursive: true });
const say = o => process.stdout.write(JSON.stringify(o) + "\n");

let tts = null;
for (const item of input.items) {
  const file = join(input.outDir, `${item.id}.wav`);
  if (existsSync(file)) { say({ id: item.id, ok: true, cached: true }); continue; }
  try {
    for (const s of item.sentences) {
      const n = (await phonemize(s, "en-us")).join(" ").length;
      if (n > MAX_PHONEMES) throw new Error(`a sentence is too long to voice in one piece (${n} phonemes, limit ${MAX_PHONEMES}): "${s.slice(0, 80)}..."`);
    }
    tts ??= await KokoroTTS.from_pretrained("onnx-community/Kokoro-82M-v1.0-ONNX", { dtype: "q8", device: "cpu" });
    const clips = [];
    for (const s of item.sentences) clips.push(await tts.generate(s, { voice: input.voice }));
    const rate = clips[0].sampling_rate;
    const gap = Math.round(rate * GAP_SECONDS);
    const merged = new Float32Array(clips.reduce((n, c) => n + c.audio.length, 0) + gap * (clips.length - 1));
    let o = 0;
    for (const c of clips) { merged.set(c.audio, o); o += c.audio.length + gap; }
    // Written aside and renamed, so a run killed mid-write never leaves a truncated clip that the
    // cache check above would then trust forever.
    const tmp = file + ".part";
    await new clips[0].constructor(merged, rate).save(tmp);
    renameSync(tmp, file);
    say({ id: item.id, ok: true });
  } catch (e) {
    say({ id: item.id, error: String(e?.message ?? e) });
  }
}
say({ done: true });
