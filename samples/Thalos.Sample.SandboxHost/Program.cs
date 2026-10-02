using Thalos.Sandbox.Host;

// The run sandbox's container entry point. Every setting arrives in the environment the sandbox runtime sets from the
// run's SandboxSpec; nothing, and never the bearer token, is passed on the command line.
var app = SandboxHost.Map(SandboxHost.CreateBuilder(args).Build());
app.Run();
