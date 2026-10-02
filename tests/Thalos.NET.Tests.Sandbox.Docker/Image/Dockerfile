# The run sandbox image the end-to-end tests run: the sample sandbox host on the .NET SDK, with git and RoslynCodeLens.
# Built from the repository root as context; the tests send only what the build stage needs.
#
# TEST_LABEL is set by the tests and applied last in each stage, so both stages' final images carry the test's label
# and its clean-up removes them, with their layers.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
COPY samples/Thalos.Sample.SandboxHost/ samples/Thalos.Sample.SandboxHost/
RUN dotnet publish samples/Thalos.Sample.SandboxHost/Thalos.Sample.SandboxHost.csproj -c Release -f net10.0 -o /out --nologo
ARG TEST_LABEL=
LABEL thalos.sandbox.test=${TEST_LABEL}

FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends git \
 && rm -rf /var/lib/apt/lists/*
RUN dotnet tool install RoslynCodeLens.Mcp --version 2.18.1 --tool-path /opt/tools
ENV PATH=/opt/tools:$PATH \
    THALOS_SANDBOX_ROSLYN_COMMAND=roslyn-codelens-mcp \
    HOME=/work/home \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# The work volume is created from these on first mount, so it starts owned by the run user. /work is never the working
# directory or the content root: a restarted host would otherwise load an appsettings.json the agent wrote there.
RUN mkdir -p /work/home /work/nuget /work/repo /work/scratch /work/import \
 && chown -R 10001:10001 /work
COPY --from=build /out /app
WORKDIR /app
USER 10001
ENTRYPOINT ["dotnet", "/app/Thalos.Sample.SandboxHost.dll"]
ARG TEST_LABEL=
LABEL thalos.sandbox.test=${TEST_LABEL}
