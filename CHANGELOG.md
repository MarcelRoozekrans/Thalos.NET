# Changelog

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
