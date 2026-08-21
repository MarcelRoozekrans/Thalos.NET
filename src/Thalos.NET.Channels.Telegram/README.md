# Thalos.NET.Channels.Telegram

Telegram Bot API transport for `Thalos.NET.Channels`: a source that long-polls `getUpdates` and turns accepted
messages into agent turns, and an adapter that renders a streaming turn into an edited Telegram message, with
MarkdownV2 escaping and message splitting for anything past Telegram's length limit. It is opt-in:
`.AddTelegramChannel(...)` on the `ThalosBuilder`, alongside `.UseChannels(...)` — nothing pumps the source without
that call too.

## Quick start

```csharp
services.AddThalos(thalos => thalos
    .UseChannels(configuration)          // Thalos:Channels — must be called too, or nothing pumps this source
    .AddTelegramChannel(configuration)   // Thalos:Channels:Telegram section; see below
    .AddAgent(new AgentDefinition { /* … */ }));
```

```json
{
  "Thalos": {
    "Channels": {
      "Enabled": true,
      "DefaultAgent": "Architect"
    },
    "Channels:Telegram": {
      "Enabled": true,
      "BotToken": "123456:ABC-your-bot-token",
      "AllowedUserIds": [123456789],
      "PrincipalId": "telegram:marcel",
      "Roles": [],
      "PollTimeoutSeconds": 50
    }
  }
}
```

- **`Enabled`** — a runtime switch, not a registration one: the source and adapter are always registered by
  `AddTelegramChannel`; when this is `false` the source's `ReadAsync` completes immediately without ever calling
  `getUpdates` — zero Bot API calls, nothing pumped.
- **`BotToken`** — issued by `@BotFather`; authenticates every Bot API call. Required — a blank token fails host
  start.
- **`AllowedUserIds`** — the Telegram user ids permitted to talk to the bot. Required and must not be empty (see
  Security below).
- **`PrincipalId`** — the caller id (e.g. `telegram:marcel`) given to every accepted message's security context.
  Every allow-listed sender is attributed to this one configured principal; Telegram's own per-sender identity is
  not carried through. Required.
- **`Roles`** — the roles given to that same security context. See Security below for why the recommended value is
  an empty list.
- **`PollTimeoutSeconds`** (default `50`) — how long Telegram should hold one `getUpdates` long-poll open waiting
  for the next update.

## Security

This package's entire reason to exist is a phone-reachable path to an agent that can hold tools, so its defaults
are deliberately restrictive rather than convenient:

- **Private chats only.** A message from a group or channel chat is dropped before it ever becomes an
  `InboundMessage` — the bot only ever answers a one-to-one conversation.
- **A non-allow-listed sender is dropped silently, and never answered.** No "you are not authorised" reply is
  sent, because replying at all confirms to whoever is probing that the bot exists and is listening — the only
  safe answer to an unrecognised sender is silence. The drop is still logged server-side (including the sender's
  id), so it is never invisible to the operator, just invisible to the prober.
- **An empty `AllowedUserIds` is a startup failure, never "allow everyone".** It is the one misconfiguration that
  would expose the agent to anyone who finds the bot, so `TelegramOptions.Describe` refuses it outright rather than
  treating a blank list as permissive.
- **The bot runs as one configured principal with configured roles, and nothing Telegram-derived.** Every accepted
  sender — whoever they are, among the allow-listed ids — is attributed to `PrincipalId` with exactly `Roles`.
  Telegram's user id, username or display name never become part of the security context; they cannot elevate or
  narrow what the agent is authorized to do.
- **The recommended `Roles` is an empty set.** Roles only matter to a policy that checks one, and the absence of
  `developer` or `admin` is precisely what keeps a bot reachable from a phone from being able to mutate a
  repository, run privileged tools, or do anything a policy gates behind a role the operator did not explicitly
  grant it. Add a role only for a specific, deliberate reason — never as a default.

## Operational notes

These are accepted limitations found during implementation, not omissions — read them before running this in
production:

- **Single-instance only.** Telegram's `getUpdates` refuses a second concurrent long-poll against the same bot
  token — Telegram itself rejects the second poller, it is not merely a client-side restriction. `TelegramChannelSource`
  enforces single-consumer *per instance* (a second enumeration of the same source throws immediately), but that
  guard cannot see a second, independent host process polling the same token: two such hosts will fight over
  `getUpdates` and drop messages between them. Run exactly one instance per bot token.
- **Delivery is at-most-once, by design.** The update offset is acknowledged to Telegram before the message it
  names is handed to a turn — not after the turn finishes. A crash between those two points loses that message
  permanently; it is never redelivered on restart. This is a deliberate trade-off, not an oversight: the
  alternative (acknowledge after processing) is at-least-once, and would silently re-run a turn that may already
  have written a memory or touched a repository. Losing a message the operator can see in their own chat history
  and retype is the cheaper failure.
- **A notice that interleaves a running turn causes a cosmetic re-render.** If an operator notice (e.g. the busy
  notice) is delivered to a conversation while a turn is still streaming to it, the notice takes over that
  conversation's outbound message slot; the running turn's next render then starts a new message rather than
  continuing to edit the one it had been editing, so its accumulated text appears a second time, below the notice.
  Nothing is lost — the answer still completes, in the new message — but it is visually duplicated.
- **The per-conversation delivery gate is never evicted.** `TelegramChannelAdapter` keeps one semaphore per
  conversation to serialise deliveries into it, and never removes an entry — a conversation id is a stable
  Telegram chat, and removing a gate that another delivery is waiting on would be a real bug, so the dictionary
  instead just grows with every distinct chat the bot has ever seen. Trivial for a single-operator bot talking to
  itself; worth knowing if you point one bot at many chats over a long-running process.
- **`retry_after` is clamped to 300 seconds.** Telegram's flood-control responses (`429`) carry a
  `parameters.retry_after` the client is expected to honour before retrying. That value comes from the far end of
  an HTTP response and is treated as untrusted input: it is clamped to a maximum of 300 seconds before anything
  waits on it, so a malformed or hostile response cannot stall the poll loop indefinitely.

## Why `IChannelAdapter.DeliverAsync` takes a `ConversationId`

The Telegram adapter needs no session lookup to answer: the `ConversationId` *is* the Telegram chat id, so a
delivery addresses the chat directly and cannot fail to resolve. See the
[root README's breaking-change note](../../README.md#breaking-change-ichanneladapterdeliverasync-now-takes-a-conversationid)
for the full rationale.
