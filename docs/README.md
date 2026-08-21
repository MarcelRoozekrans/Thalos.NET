# Thalos.NET docs

The design docs for Thalos.NET live in the Daedalus repository (the first host of the framework), not here:
`C:\Projects\Prive\daedalus\docs\plans\2026-08-16-thalos-agent-core-design.md` (architecture and ports) and
`C:\Projects\Prive\daedalus\docs\plans\2026-08-16-thalos-net-plan-a.md` (the implementation plan, whose §0 records the
verified package APIs and every reviewed amendment — the accepted API facts). Phase 1.2 (long-term memory, 0.2.0):
`C:\Projects\Prive\daedalus\docs\plans\2026-08-17-thalos-memory-design.md` (ports, turn integration, Rag.NET adapter,
security) and `C:\Projects\Prive\daedalus\docs\plans\2026-08-17-thalos-memory-plan-a.md` (plan; §0.7 holds the
amendments found during execution). Phase 1.3 (skills, 0.3.0):
`C:\Projects\Prive\daedalus\docs\plans\2026-08-18-thalos-skills-design.md` (the skill file, sync, the turn, the trust
boundary) and `C:\Projects\Prive\daedalus\docs\plans\2026-08-18-thalos-skills-plan-a.md` (plan; §0.8 holds the
amendments found during execution — including the API deviations the prose predates). Phase 1.4 (channels, 0.4.0):
`C:\Projects\Prive\daedalus\docs\plans\2026-08-20-thalos-channels-design.md` (the pump, the conversation lifecycle,
the console and Telegram adapters, the identity gates) and
`C:\Projects\Prive\daedalus\docs\plans\2026-08-20-thalos-channels-plan-a.md` (the implementation plan). The
package-level documentation for the channel packages themselves — quick start, configuration, the six chat
commands, security posture, accepted operational limitations — lives with the code, not here: see
[`src/Thalos.NET.Channels/README.md`](../src/Thalos.NET.Channels/README.md) and
[`src/Thalos.NET.Channels.Telegram/README.md`](../src/Thalos.NET.Channels.Telegram/README.md), and the root
[`README.md`](../README.md#channels) for the cross-package summary and the `IChannelAdapter.DeliverAsync`
breaking-change note. This folder holds what belongs with the
library itself: `docs/samples/` collects manual smoke-test transcripts of `samples/Thalos.Sample.Console` against a real
Anthropic key (`console-smoke-YYYY-MM-DD.md`, see the sample README), and package-level notes will be added here as the
API stabilises towards 1.0.
