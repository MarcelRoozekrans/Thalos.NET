# Thalos.NET.Channels

Channel hosting for Thalos.NET: a `ChannelPump` hosted service that reads every registered `IChannelSource`, binds
inbound messages to agent sessions, dispatches the six chat commands, coalesces streamed output and delivers it
through the matching `IChannelAdapter`. It is opt-in: `.UseChannels(...)` on the `ThalosBuilder` (options from a
delegate, or `UseChannels(configuration)` to bind the `Thalos:Channels` section). This package ships the in-box
console channel; `Thalos.NET.Channels.Telegram` is a separate package for Telegram.

## Quick start

```csharp
services.AddThalos(thalos => thalos
    .UseChannels(configuration)      // Thalos:Channels section; see below
    // .UseConversationMap<MyConversationMap>()   // replaces the default in-memory map
    .AddConsoleChannel()              // reads real stdin, writes real stdout
    .AddAgent(new AgentDefinition
    {
        Id = AgentId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV", null),
        Name = "Architect",
        Instructions = "You are a senior .NET architect.",
    }));

var host = builder.Build();
await host.StartAsync();   // ChannelPump is an IHostedLifecycleService participant; nothing is pumped until this runs
```

```json
{
  "Thalos": {
    "Channels": {
      "Enabled": true,
      "DefaultAgent": "Architect",
      "IdleTimeout": "12:00:00",
      "FlushInterval": "00:00:01"
    }
  }
}
```

- **`Enabled`** — a runtime switch, not a registration one: every source and adapter is still registered when this
  is `false`, but the pump starts and immediately idles, so a host can bind it from configuration that is not read
  until the provider is built.
- **`DefaultAgent`** — the `AgentDefinition.Name` (not `AgentId`, which is a ULID no one types) used when a
  conversation binds implicitly (its first message) or a bare `/new` names no agent.
- **`IdleTimeout`** — how long a bound conversation may sit before the next message rolls it onto a fresh session.
  The operator is told when this happens; a silent rollover would make the agent look amnesiac after a long gap.
- **`FlushInterval`** — the minimum spacing between two outbound renders of one running turn. It is **one host-wide
  setting, not a per-channel one**: every registered channel shares this value. `0` renders every delta; the default
  `00:00:01` is what keeps a rate-limited transport (Telegram) inside its per-chat budget, and it suppresses the
  trailing renders of every turn on every channel as a consequence. That is why an adapter must render the terminal
  event from `TurnCompletedEvent.Result.Text` rather than relying on the last delta it was sent.

## The four lifecycle edges

A conversation is either unbound (first message — bound implicitly to `DefaultAgent`, no notice needed) or bound. A
bound conversation can be **idle** (last activity older than `IdleTimeout` — rolls onto a fresh session with
`ChannelNotices.IdleRollover`), **busy** (a turn is already running for it — the new message gets
`ChannelNotices.Busy` immediately, never queued), or **dead** (the runtime no longer recognises the session —
unbound and the operator is asked to resend with `ChannelNotices.Rebound`, deliberately not auto-retried: retrying
against a runtime that just rejected the session is how a rebind loop starts).

## Chat commands

Every inbound message is parsed by `ChannelCommand.Parse` before anything else. A slash-prefixed word that is not
one of these is `Unknown` and refused the same way an unrecognised word would be — it never reaches the model as a
prompt, so a mistyped command cannot be misread as a question.

| Command | Effect |
|---|---|
| `/new [agent]` | Starts a fresh session. Closes the current one first. Names an agent by `AgentDefinition.Name` (case-insensitive); omitted, it uses `DefaultAgent`. An unknown name refuses and leaves the current session untouched. |
| `/end` | Closes the bound session and unbinds the conversation. |
| `/status` | Reports the bound agent and last-activity time, without creating a session — a status check must never have the side effect of starting one. |
| `/agents` | Lists the registered agents by name (and description, when set). |
| `/cancel` | Aborts the in-flight turn for this conversation, if any. |
| `/help` | Lists the six commands. |

## Writing your own channel

Implement `IChannelSource` (one `ReadAsync` stream of `InboundMessage`, responsible for its own authentication and
filtering — a message that reaches the pump has already been accepted) and `IChannelAdapter` (`DeliverAsync`, keyed
on `ConversationId`, not `SessionId` — see below), then register both as singletons (`TryAddEnumerable`) alongside
`.UseChannels(...)`. `ConsoleChannelSource`/`ConsoleChannelAdapter` are the reference implementation; Telegram's are
a fuller one that also handles rate limiting, message splitting and markdown escaping.

`IConversationMap` (which Thalos session serves which external conversation) defaults to `InMemoryConversationMap`.
Swap it with `.UseConversationMap<TMap>()` — a singleton; take `IServiceScopeFactory` for scoped resources — for a
host that needs bindings to survive a restart.

## Why `IChannelAdapter.DeliverAsync` takes a `ConversationId`

An adapter addresses a conversation — a chat, a socket, a terminal — not a session. Most of what a channel has to
say (`/help`, an unrecognised command, "still working on the previous message", "that session had already ended")
belongs to a conversation that has no session at all, or no longer does by the time the notice is sent. See the
[root README's breaking-change note](../../README.md#breaking-change-ichanneladapterdeliverasync-now-takes-a-conversationid)
for the full rationale if you are upgrading a custom adapter from before this package's first release.

## Operational notes

- **The pump serialises commands, not turns.** Each `IChannelSource` has one reader loop. A slash-command is
  handled inline on that loop; an ordinary message starts its turn on a detached task so the loop returns
  immediately to read the next message — otherwise `/cancel` and the busy notice would be unreachable for exactly
  the conversation that needs them.
- **A dead `IChannelAdapter` cannot end a channel.** Every failure path in the pump logs and continues; one bad
  message, one throwing adapter or one closed session never stops the reader loop for that source, let alone the
  process.
- **`FlushInterval` is host-wide, not per channel.** One value serves every registered channel, so with the default
  `00:00:01` the renders that fall inside the last second of a turn never go out. Adapters must render the terminal
  event from `TurnCompletedEvent.Result.Text`, which is the complete answer, rather than from the last delta.

## Known limitations

- **The console channel does not shut down until stdin produces a line.** `ConsoleChannelSource` reads with
  `TextReader.ReadLineAsync(ct)`, and on `System.Console.In` that call does **not** observe the token: the default
  `TextReader` implementation hands off to the blocking synchronous read, so a reader parked waiting for input stays
  parked. Practically, pressing Ctrl+C with no pending input leaves `ChannelPump.ExecuteAsync` alive until the
  host's shutdown timeout expires (`HostOptions.ShutdownTimeout`, 30 seconds by default) and the process is torn
  down anyway — the delay is the whole symptom; nothing is lost or corrupted, and a channel that has a line to read
  drains normally. A proper fix needs the read moved onto a dedicated thread that shutdown can simply abandon;
  that is deliberately not done here. Hosts that care can shorten `ShutdownTimeout`, or press Enter.
- **`DeltaCoalescer.Flush()` has no production caller.** It returns the residue the flush interval suppressed, which
  is exactly the gap `TurnCompletedEvent.Result.Text` closes more reliably. It remains on the public surface for a
  custom pump that wants it.
