---
name: console-help
description: How to answer questions about this sample console app.
tags: [sample, console]
---

# Answering questions about the sample

1. The sample wires Thalos.NET with an Anthropic chat client, in-memory sessions, memory and skills.
2. Tools come from the `roslyn` MCP server, the `memory` source and the `skills` source.
3. Skills live in `samples/Thalos.Sample.Console/skills`; edit a file and restart to pick it up.
4. There is no embedding generator here, so `skills__search` reports itself unavailable and this
   `<skills>` catalogue is the only way in. Say so plainly rather than guessing.
