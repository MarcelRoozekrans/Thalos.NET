# Changelog

## [0.4.0](https://github.com/MarcelRoozekrans/Thalos.NET/compare/v0.3.0...v0.4.0) (2026-08-21)


### ⚠ BREAKING CHANGES

* **channels:** note keyword.

### Features

* **channels:** Thalos.NET.Channels and Thalos.NET.Channels.Telegram ([#41](https://github.com/MarcelRoozekrans/Thalos.NET/issues/41)) ([9bf872c](https://github.com/MarcelRoozekrans/Thalos.NET/commit/9bf872caa5bc0b763af3dfd4646754267c20bfd0))


### Bug Fixes

* **ci:** read release-please config so 0.x breaking changes bump minor ([03ad60a](https://github.com/MarcelRoozekrans/Thalos.NET/commit/03ad60a36504963a399ed0c29e60bc8aff37ef6f))

## [0.3.0](https://github.com/MarcelRoozekrans/Thalos.NET/compare/v0.2.0...v0.3.0) (2026-08-18)


### Features

* **skills:** Thalos.NET.Skills — agent-scoped procedure documents ([#28](https://github.com/MarcelRoozekrans/Thalos.NET/issues/28)) ([904cbf5](https://github.com/MarcelRoozekrans/Thalos.NET/commit/904cbf571a3429de93c2db7f57e44ae45a87f772))
* **abstractions:** `AgentDefinition` gains `Skills`, a glob allow-list over skill names. It is additive
  and defaults to **empty**, so definitions written for 0.2.0 behave exactly as before — unlike `Tools`,
  an agent opts into a skill catalogue explicitly, because a catalogue costs tokens on every turn.
* **memory:** `MemoryRecallBlock` now neutralises `<skills` and `</skills` as well as its own tag.
  Recalled memory text is untrusted, and from 0.3.0 the `<skills>` block carries meaning in the prompt,
  so without this a stored memory could forge a skill entry beside the real catalogue. The pattern also
  gained a word boundary, which stops it over-escaping unrelated text such as `<memoriesX`. Escaping is
  therefore slightly different from 0.2.0 for text that was never a tag.

### Bug Fixes

* unbreak main after the AwesomeAssertions v9 major bump ([#25](https://github.com/MarcelRoozekrans/Thalos.NET/issues/25)) ([119375d](https://github.com/MarcelRoozekrans/Thalos.NET/commit/119375dd9afe969e5c48baf92c0fa302c71be96c))


### Miscellaneous Chores

* set the release version ([e60209c](https://github.com/MarcelRoozekrans/Thalos.NET/commit/e60209ce49000569c8d2c65c9a3c977d7d80aff5))

## [0.2.0](https://github.com/MarcelRoozekrans/Thalos.NET/compare/v0.1.1...v0.2.0) (2026-08-17)


### Features

* **abstractions:** MemoryId, memory error codes/events, AgentMemorySettings, content scanner port ([69673cd](https://github.com/MarcelRoozekrans/Thalos.NET/commit/69673cd2c17c32b73de737791e9b1f427f19cc89))
* **core:** AgentFactory attaches IAgentContextProviderSource providers; memory settings in identity ([a3aadef](https://github.com/MarcelRoozekrans/Thalos.NET/commit/a3aadefe9fb53c0ff7dc039e8c038b2df7119489))
* **core:** TurnScope carries the agent id and accepts events from extensions ([aea1290](https://github.com/MarcelRoozekrans/Thalos.NET/commit/aea129012bcfd6db90c05d70358835a3a161fdf5))
* **memory-ragnet:** probe with dimension check; Postgres/transport error mapping, no raw messages ([97d5e9b](https://github.com/MarcelRoozekrans/Thalos.NET/commit/97d5e9b42f97d28b46d73aa6710ab1db6baefbdf))
* **memory-ragnet:** RagNetMemoryIndex — upsert/search/remove over PgVectorStore, owner partitions ([ace072e](https://github.com/MarcelRoozekrans/Thalos.NET/commit/ace072e525607d664b86b46afb8c4a0b953f395a))
* **memory-ragnet:** UseRagNetMemory with keyed PgVectorStore and fail-fast schema initializer ([7dfa66f](https://github.com/MarcelRoozekrans/Thalos.NET/commit/7dfa66f8983a785968ffca0de79ffd8f2966e369))
* **memory:** dedupe on remember — same owner, threshold 0.95, refresh instead of insert ([629f511](https://github.com/MarcelRoozekrans/Thalos.NET/commit/629f511442dbb7d8a000c305b48ab8d78ed36fe0))
* **memory:** forget (soft/hard, owner check), list, reindex with batched upsert ([9ceb9e5](https://github.com/MarcelRoozekrans/Thalos.NET/commit/9ceb9e50da3b7ad78bc054d41f0c1f43809a9322))
* **memory:** IMemoryIndex, InMemoryMemoryIndex (cosine), UnavailableMemoryIndex, contract tests ([ad47a76](https://github.com/MarcelRoozekrans/Thalos.NET/commit/ad47a76ecb6d5e85ecf3df4d7811e52678de8d1c))
* **memory:** IMemoryService and MemoryService.RememberAsync with index-pending fallback and events ([a631390](https://github.com/MarcelRoozekrans/Thalos.NET/commit/a631390ee26b2506ce11623b214ae5cebacdea4b))
* **memory:** IMemoryStore port, InMemoryMemoryStore and reusable MemoryStoreContractTests ([e6ecb27](https://github.com/MarcelRoozekrans/Thalos.NET/commit/e6ecb27ead3931138956d322acb9e65482610724))
* **memory:** memory model — kinds, validated record, scope, queries, requests, options, rules ([a7fb623](https://github.com/MarcelRoozekrans/Thalos.NET/commit/a7fb6239e31fe610675dc79c7554789d0dbcfef7))
* **memory:** memory tool source with remember/recall scoped to the turn caller ([b5afebe](https://github.com/MarcelRoozekrans/Thalos.NET/commit/b5afebe38a2e322a76cbc38fdca9046a7efdce8b))
* **memory:** memory__forget and memory__list; anonymous refusal; authorization through the catalog ([02915d7](https://github.com/MarcelRoozekrans/Thalos.NET/commit/02915d79d80aeb79c61b33432dfd17262e7075e0))
* **memory:** MemoryContextProvider injects a delimited, budgeted memories block per turn ([c6f290b](https://github.com/MarcelRoozekrans/Thalos.NET/commit/c6f290b397065dcdf0e908db1664dc17b3c8b66c))
* **memory:** recall failure isolation, quarantine drop, per-agent MemoryContextProviderSource ([3f2e905](https://github.com/MarcelRoozekrans/Thalos.NET/commit/3f2e905bf4a27bec28bf312b6ea5a1dc21bf67c2))
* **memory:** RecallAsync — scoped search, hydration, ordering, TopK/MaxChars budget, MarkRecalled ([9022d16](https://github.com/MarcelRoozekrans/Thalos.NET/commit/9022d16cdb33ba0c78d8d46b42783407fdf63e2b))
* **memory:** Thalos.NET.Memory and Thalos.NET.Memory.RagNet (phase 1.2) ([b639b34](https://github.com/MarcelRoozekrans/Thalos.NET/commit/b639b3462952baee9abde57cbbb1f4675df201d0))
* **memory:** UseMemory/UseMemoryStore/UseMemoryIndex builder extensions with generated registration ([c3a6e2f](https://github.com/MarcelRoozekrans/Thalos.NET/commit/c3a6e2f243df803cf1cad17a4f9042aae5506701))
* **sentinel:** IUntrustedContentScanner over the detection pipeline for recalled memories ([f111406](https://github.com/MarcelRoozekrans/Thalos.NET/commit/f111406e02ed57ab83cfea21240406af859fd760))
* **testing:** deterministic HashedBagOfWordsEmbeddingGenerator for memory tests ([55338ac](https://github.com/MarcelRoozekrans/Thalos.NET/commit/55338acf507ce8a07a9c975f0d5893f266e9b3c2))


### Bug Fixes

* **memory-ragnet:** last-wins re-registration, accurate init errors, batch dedupe, lifecycle init ([979a70d](https://github.com/MarcelRoozekrans/Thalos.NET/commit/979a70db989d7483e89f0b009b4145eaebcba939))
* **memory-ragnet:** order equal-score hits by id like InMemoryMemoryIndex ([bfce3b3](https://github.com/MarcelRoozekrans/Thalos.NET/commit/bfce3b3fc1b15625c2996d5690b6d54dc8730a47))
* **memory:** log clear-pending failures, guard dedupe threshold and MaxChars, deterministic ties ([f5397a9](https://github.com/MarcelRoozekrans/Thalos.NET/commit/f5397a927f5de9f68787d9f3e6887fc3130bd4d9))
* **memory:** normalise tags to lower-case, cap Source, harden model tests (review follow-ups) ([3fbb61e](https://github.com/MarcelRoozekrans/Thalos.NET/commit/3fbb61ea230ae4e3033da733066ce2368ee2162b))
* **memory:** reindex maps a store that throws mid-stream to MemoryStoreFailed instead of throwing ([1412b78](https://github.com/MarcelRoozekrans/Thalos.NET/commit/1412b78c6fa5af811ea0328f8254b02a8f317f9a))
* **memory:** soft forget marks IndexPending, TopK &lt;= 0 means 1, StreamAsync and text-copy contracts ([7a54d6f](https://github.com/MarcelRoozekrans/Thalos.NET/commit/7a54d6feb185ece78cbb78703d8e5f142f2190f5))
* **memory:** store contract hardening — query-tag normalisation, delete race, tie-break docs ([0fa994d](https://github.com/MarcelRoozekrans/Thalos.NET/commit/0fa994d6db1fefdf864e9ad9cda5674a0e063eed))
* **memory:** tool list honours scope visibility, recall/list scanned and delimited, sanitiser fixes ([52c3cf8](https://github.com/MarcelRoozekrans/Thalos.NET/commit/52c3cf8ac7cd091d6c24f61d2f669f5fe1b94509))


### Miscellaneous Chores

* set the release version ([4f06277](https://github.com/MarcelRoozekrans/Thalos.NET/commit/4f06277408e9d62fc8bc87cec45473b31721620b))

## [0.1.1](https://github.com/MarcelRoozekrans/Thalos.NET/compare/v0.1.0...v0.1.1) (2026-08-17)


### Bug Fixes

* **release:** honour the release tag on a detached checkout so a tag dispatch can publish ([67fbfb7](https://github.com/MarcelRoozekrans/Thalos.NET/commit/67fbfb72ed8993a9d6f65c8ddaca6c20b1227b13))

## 0.1.0 (2026-08-17)


### Features

* **abstractions:** AgentError, AgentErrorCode, AgentTurnException ([dc786ca](https://github.com/MarcelRoozekrans/Thalos.NET/commit/dc786caa37bbff291934ce9b4f7fb36089453dc4))
* **abstractions:** AgentTurnRequest/Result and AgentEvent hierarchy ([ab5ea55](https://github.com/MarcelRoozekrans/Thalos.NET/commit/ab5ea55fbdded93ea4e7ca38d6c0cd03d67a5382))
* **abstractions:** runtime, store, tool, provider, decorator, authorizer, publisher, channel ports and notifications ([498ddf1](https://github.com/MarcelRoozekrans/Thalos.NET/commit/498ddf111b2e62dc8fc77306bbc2829664cb2912))
* **abstractions:** session record, turn usage, tool-call summary, validated AgentDefinition ([750cfef](https://github.com/MarcelRoozekrans/Thalos.NET/commit/750cfef22797b424cce91cb7a2844f1460f0364c))
* **abstractions:** typed ids AgentId, SessionId, TurnId, ToolCallId ([c286c67](https://github.com/MarcelRoozekrans/Thalos.NET/commit/c286c67ddb733ab6706cfe64e44d14eedf6a04f8))
* **anthropic:** Anthropic chat-client provider and UseAnthropic builder extension ([02124bb](https://github.com/MarcelRoozekrans/Thalos.NET/commit/02124bb7719043aacd6d80a455425ed799a9f628))
* **core:** AgentEventHub (ZeroAlloc.AsyncEvents) and null notification publisher ([7884ba1](https://github.com/MarcelRoozekrans/Thalos.NET/commit/7884ba174f96f628de6f673b2414de8ac04cd69c))
* **core:** AgentFactory composing provider, decorators and MAF ChatClientAgent ([9afff07](https://github.com/MarcelRoozekrans/Thalos.NET/commit/9afff07f9440cdd57967ef7e1f40dfa0d38c35c9))
* **core:** AuthorizingAIFunction — tool authorization + events at the function boundary ([cf0db95](https://github.com/MarcelRoozekrans/Thalos.NET/commit/cf0db95630f5ac85b2261441d704a067278e2097))
* **core:** glob matcher, ToolPolicyBinding, DefaultToolAuthorizer over ZeroAlloc.Authorization policies ([8e0298b](https://github.com/MarcelRoozekrans/Thalos.NET/commit/8e0298b148288a2dba91fb329a6a03baa3ac8114))
* **core:** InMemorySessionStore + reusable SessionStoreContractTests in Thalos.NET.Testing ([647b477](https://github.com/MarcelRoozekrans/Thalos.NET/commit/647b477d587d0aaff1f16ee8cb8838de50803f0d))
* **core:** LocalToolSource — in-process [ThalosTool] methods with per-invocation DI scope ([f49e1c5](https://github.com/MarcelRoozekrans/Thalos.NET/commit/f49e1c5d0bbc15519d17329d55d5a5ec466f9067))
* **core:** SessionStoreChatHistoryProvider bridging MAF sessions to IAgentSessionStore ([42d0c2a](https://github.com/MarcelRoozekrans/Thalos.NET/commit/42d0c2a96d636f5687d04d82e86a85208df01dc0))
* **core:** source-generated AgentSessionMachine with rehydration ([7b16323](https://github.com/MarcelRoozekrans/Thalos.NET/commit/7b163232c62b725f7d22203dac37e2cb4fa26485))
* **core:** ThalosAgentRuntime — sessions, buffered + streaming turns, telemetry ([65fb059](https://github.com/MarcelRoozekrans/Thalos.NET/commit/65fb05993361ec9d3e736ab353c2a013f7743671))
* **core:** ThalosOptions, ThalosBuilder, AddThalos with Inject-generated core registration and telemetry-wrapped store ([c923e9c](https://github.com/MarcelRoozekrans/Thalos.NET/commit/c923e9cb4312612c27552bd4ccee6054229d380e))
* **core:** ToolCatalog with source prefixing, allow-list globs and authorization wrapping ([b4ddd49](https://github.com/MarcelRoozekrans/Thalos.NET/commit/b4ddd49e899c3aa3895210aa8f6faeabf698511b))
* **core:** TurnScope ambient turn context with tool-event channel ([3b67eee](https://github.com/MarcelRoozekrans/Thalos.NET/commit/3b67eee134d957611afff08da2ba46ff2134a523))
* **mcp:** McpToolSource (stdio/http), .mcp.json loader, builder extensions, stdio test server ([fa7c8cd](https://github.com/MarcelRoozekrans/Thalos.NET/commit/fa7c8cd73a25c724e421531bca956641b9fe47a0))
* **sentinel:** AI.Sentinel decorator with quarantine → AgentError mapping ([522045c](https://github.com/MarcelRoozekrans/Thalos.NET/commit/522045c04ff7b1a912dc5846ea880ef00e1b8df4))
* **testing:** ScriptedChatClient deterministic IChatClient ([a00645e](https://github.com/MarcelRoozekrans/Thalos.NET/commit/a00645ec7cf59bb1c65d541e963a4b7a50af1b0c))


### Bug Fixes

* **anthropic,mcp:** shared AnthropicClient ownership, MCP source lifecycle hardening, shutdown timeout, injectable env ([184ffd2](https://github.com/MarcelRoozekrans/Thalos.NET/commit/184ffd2b3730ec98f790b7068b94a3d1811cfcac))
* **build:** Testing package via xunit.extensibility.core (no runtimeconfig; pack --no-build works on CI); SDK pin resolvable locally, renovate ignores dotnet-sdk ([603b5d9](https://github.com/MarcelRoozekrans/Thalos.NET/commit/603b5d9e463a1196d9c5cde87682f2a3e2332d66))
* **core:** fail agent build on tool-source failure, CAS session claim, no raw exception detail, usage on failed turns ([9df7937](https://github.com/MarcelRoozekrans/Thalos.NET/commit/9df79374a46405730f763ea12df6b609996ed765))
* **core:** hub isolates foreign OCEs, factory survives thrown builds, value-compare definitions ([f0f7e1e](https://github.com/MarcelRoozekrans/Thalos.NET/commit/f0f7e1e6b7f0f7c256d76fa15eae80cb5e0124a6))
* **core:** isolate hub subscribers, single-flight AgentFactory with pipeline ownership, history-provider hardening, docs ([ab0b5ed](https://github.com/MarcelRoozekrans/Thalos.NET/commit/ab0b5ed4ddc7cac112eee8c69e595cc24ceb07e6))
* **core:** linear glob, audit tool-internal cancellations, safe previews, strict policy table, docs ([7edb6aa](https://github.com/MarcelRoozekrans/Thalos.NET/commit/7edb6aaeaadd27dc4f602ec58990aa331bb8d574))
* **core:** logging-optional runtime, post-persist close notification, clear duplicate-agent error, store fail-fast, TryAddEnumerable registrations ([ccd1976](https://github.com/MarcelRoozekrans/Thalos.NET/commit/ccd197668e3cf5e7771ad4cc91ea379acd8008d3))
* **core:** runtime session integrity on exceptional paths, provider-timeout mapping, telemetry, tests ([948fcc5](https://github.com/MarcelRoozekrans/Thalos.NET/commit/948fcc5f5586b27a1f9506b697d9ff484abffab3))
* **core:** tolerate post-persist notification failures; count last-resort failures ([63d2d8e](https://github.com/MarcelRoozekrans/Thalos.NET/commit/63d2d8ed95e0fa655cb21779f97f0c93dc73b804))
* **mcp:** connect-failure detail carries the exception type, not its message ([0f3a337](https://github.com/MarcelRoozekrans/Thalos.NET/commit/0f3a337d72332d23b58d491daa05560e2e562c74))
* **sentinel:** no reason text in AgentError detail, unwrap Sentinel wrapper, rate limit → ProviderError, innermost order, mapper unit tests ([c8e0ef4](https://github.com/MarcelRoozekrans/Thalos.NET/commit/c8e0ef4798c461e221b1bc6ccb919e135259ea10))


### Miscellaneous Chores

* set the first release version ([b0dd362](https://github.com/MarcelRoozekrans/Thalos.NET/commit/b0dd3626120fe33da2f783e91b8d8c62e0116261))
