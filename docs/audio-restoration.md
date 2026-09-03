# Audio restoration

GK3's restored audio is an optional overlay. The legally installed 1999 Barn archives
remain complete and untouched; a restored clip replaces one recording at a time.

The implementation borrows the safety rules from IndyFoA's audio restoration work, not
its measured settings. The source corpora are materially different:

| | IndyFoA | GK3 |
|---|---:|---:|
| audio | 8-bit PCM | mostly 64 kbit/s MP3 in RIFF |
| usual rate | about 11 kHz | 44.1 kHz |
| principal defect | quantisation and missing bandwidth | codec damage |
| dialogue clips identified by | speech index | YAK reference plus GK3 voice-over naming |

Thresholds calibrated on Indy's 11 kHz, 8-bit source therefore do not transfer to GK3.
In particular, do not use Indy's noise-floor gate, de-click gate or speaker-distance gate
unchanged.

## Workspace

Run:

```text
GK3Reborn.Tools extract-audio --source <GK3>/Data --workspace <ContentWorkspace>
```

It produces:

```text
ContentWorkspace/
  raw/audio/
    dialogue/       untouched RIFF assets named exactly as the Barn names them
    sfx/            untouched non-YAK audio
  normalized/audio/
    dialogue/       decoded 16-bit PCM, <original-name>.wav
    sfx/            decoded 16-bit PCM, <original-name>.wav
  enhanced/audio/
    dialogue/       reviewed 48 kHz restored masters
    sfx/            reviewed 48 kHz general-audio masters
  manifests/audio.json
```

`raw` is evidence and never an input to an iterative encode. `normalized` is the decoded
source every experiment starts from. A restoration is promoted into the matching
`enhanced` lane under the same wrapper filename. Extraction creates those directories but
never writes or overwrites a file in them.

The apparent double extension is intentional. `A0NQIB44.QR1` and `A0NQIB44.QR2` are two
different dialogue recordings: `.QR1` is a sequence code, not a format. Their editable
masters are `A0NQIB44.QR1.wav` and `A0NQIB44.QR2.wav`; packing removes only the final
`.wav` wrapper and retains the complete original identity. A conventional original such
as `DOOR.WAV` consequently becomes `DOOR.WAV.wav` in the editable trees.

`audio.json` records the source and normalized hashes, archive, lane, sample rate,
channels, duration, YAKs, speakers and captions. A YAK reference which names no effective
audio asset is listed explicitly rather than silently counted as dialogue.

A YAK `SOUNDS` section is not by itself proof of speech: the animations also trigger
telephone hooks, page turns and through-door ambience. Voice-over recordings use GK3's
`A….<sequence>` asset convention and go to `dialogue`; referenced conventional `.WAV`
assets stay in `sfx`. This keeps those effects out of a speech model while retaining their
YAK context in the manifest. Composite or ambiguous conventional WAVs remain in the safer
general-audio lane until reviewed.

## Two restoration lanes

Never send the whole corpus through one speech workflow.

### Dialogue

Start each branch from `normalized/audio/dialogue`, in parallel:

```text
normalized PCM
  -> measured, mild cleanup (DC; repair clicks/clips only when detected)
  -> one restoration branch
  -> identity, spectrum, duration and level checks
  -> subtle mastering
  -> 48 kHz / 24-bit PCM master
```

For GK3's 44.1 kHz but heavily compressed speech, pilot ClearVoice speech enhancement,
Resemble Enhance and VoiceFixer. Pick one branch per voice class after a blind pilot; do
not choose per clip and do not chain model outputs. A model that sounds cleaner but changes
the actor is a failure.

The pilot must include consecutive lines, short and long clips, whispers, shouting,
sibilants, male and female voices, and the major speakers. GK3 has enough source bandwidth
that invented high frequencies are less useful than in Indy; codec-artifact repair and
speaker consistency carry more weight.

### SFX and ambience

`normalized/audio/sfx` includes effects, footsteps, room beds and music-like soundtrack
assets. A speech enhancer can turn these into watery near-speech or erase them. The
ComfyUI speech workflow is therefore prohibited on this lane.

Use a separate AudioSR/general-audio pilot. Preserve stereo, transients, loop boundaries
and relative dynamics. Until that pilot passes, the safe enhanced result is conventional
cleanup/resampling only, or no replacement at all—the runtime will then use the Barn.
Further sub-classification may be added to the manifest, but it must be evidence-based;
the coarse SFX name means “not claimed by a YAK”, not “one homogeneous kind of sound”.

## Refusal checks

Before a master is promoted:

- duration is unchanged: at 48 kHz the expected frame count is
  `round(sourceFrames * 48000 / sourceRate)`;
- channel count and intended stereo placement are preserved;
- dialogue stays within a pilot-calibrated speaker-embedding distance;
- the source-band spectral envelope remains within a pilot-calibrated limit;
- SFX transients and loop seams are compared rather than using a speaker metric;
- clipping, excessive sibilance, collapsed level and unexpected noise are refused or
  recorded for review;
- every model, weights hash, setting, attempt and review decision is recorded.

Do not normalize every clip independently: that destroys the original mix between a
whisper and a shout. Determine one gain per coherent corpus or class, then limit after the
gain. Encode only once from the reviewed master.

## Runtime and packing

`pack-content` recursively includes `enhanced/audio/dialogue/*.wav` and
`enhanced/audio/sfx/*.wav` in `Reborn.rebarn` as `RebarnKind.Audio`. PCM WAV is stored
verbatim. The engine reads 16-, 24- and 32-bit integer PCM and 32-bit float WAV masters.

At runtime the lookup order is:

```text
overrides/audio/<original-name>.wav
  -> Reborn*.rebarn audio entry
  -> original *.brn asset
```

An absent or rejected restoration therefore costs one enhanced clip, never the sound and
never the game. Loose enhanced masters are authoring inputs; the runtime consumes their
packed form so development and shipped lookup have the same precedence.
