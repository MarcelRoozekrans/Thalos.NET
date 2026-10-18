using Thalos.Sandbox.Host;

// A consumer's whole sandbox entry point. Every setting arrives in the THALOS_SANDBOX_* environment.
var app = SandboxHost.Map(SandboxHost.CreateBuilder(args).Build());
await app.RunAsync();
