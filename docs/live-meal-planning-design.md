# Live meal-planning follow-up for #49

Status: proposed connection design; no live recording, AI calls, account creation or
paid infrastructure is implemented/enabled by the stage-1 importer.

## Experience

A user explicitly starts a conversation and sees a recording indicator, Stop and
Discard controls. Explain before starting that chosen audio/transcripts leave the
phone for the configured AI service. Ask for the planning week; do not infer it from
an ambiguous “next week”. Show provisional meals and purchases as the discussion
unfolds. A suggestion is unselected until agreed. Spanish conversation and corrections
such as “change Tuesday dinner to soup” update the draft, not household storage.

Each draft update must include the session ID, increasing revision and stable entry
IDs. Ignore duplicate/out-of-order revisions. Once a user begins manual review, pause
model changes until the user explicitly resumes, to prevent overwriting corrections.
Keep missing dates and uncertain alternatives as unresolved questions. Preserve explicit
purchase amounts and distinguish purchases from inferred ingredients. Stop microphone
capture and network audio when the user stops, leaves the page or backgrounds the app.

Final confirmation reuses the native atomic import store: validate the entire selected
draft, refuse conflicting existing meals, preserve quantities of existing open products,
and persist a synced replay receipt with the household changes. A failed/canceled live
session must never save its draft automatically. Reconnect requires a visible user action.

## Connection and access

For an interactive voice connection, evaluate the supported WebRTC client flow with a
small authenticated backend. That backend owns the standard API credential and mints
short-lived client credentials; an API key must never be packaged into the iPhone app.
Use authentication independent of family pairing secrets, TLS, session rate/length limits,
and per-account spending limits. Existing local household features remain usable offline.

[Official OpenAI WebRTC documentation](https://developers.openai.com/api/docs/guides/voice-webrtc?voice-api=realtime)
describes the developer-server/ephemeral-credential flow. A supported native WebRTC
library or WKWebView microphone implementation still needs a device prototype; the
current MAUI host does not yet implement that audio connection.

Use schema-constrained structured extraction where supported, followed by local domain
validation. Schema adherence does not establish that a tentative meal was actually agreed.
[Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs)
provide the shape contract, while Family App still enforces dates, selection and save rules.

## Cost and privacy decisions needed from the owner

We need the API account/backend to use, supported model, acceptable session/monthly
budget and agreed retention settings before making paid calls. Cost depends on input,
output, conversation history and optional separate transcription, so benchmark a bounded
Spanish session before estimating a normal week. Record usage totals, not household
transcripts or audio, in operational logs. See [official voice cost guidance](https://developers.openai.com/api/docs/guides/voice-latency-cost?voice-api=realtime).

Do not promise that off-device processing has no retention. Review the chosen service's
current [data controls](https://developers.openai.com/api/docs/guides/your-data) and show
the applicable policy before enabling capture. Draft/audio deletion controls must distinguish
local deletion from provider retention. No background listening is supported.

## Required checks

Test ordered/duplicate/out-of-order draft updates, manual-edit pause, Spanish agreements
versus suggestions, missing dates, corrections to existing draft entries, inference provenance,
permission denial, interruption/backgrounding, disconnect/timeout, cancellation and expired
client credentials. Test confirmed atomic save/replay with two trusted native devices. On
an iPhone 14, verify microphone stop behavior and an end-to-end conversation before
claiming the final live acceptance criteria of #49 are complete.
