# The Linux agent. Build from the repository root: docker build -f src/LogPulse.Agent/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first, from the project files and lock files only, so this layer is cached until a dependency changes.
COPY Directory.Build.props .editorconfig ./
COPY src/LogPulse.Core/LogPulse.Core.csproj src/LogPulse.Core/packages.lock.json src/LogPulse.Core/
COPY src/LogPulse.Agent/LogPulse.Agent.csproj src/LogPulse.Agent/packages.lock.json src/LogPulse.Agent/
RUN dotnet restore src/LogPulse.Agent/LogPulse.Agent.csproj --locked-mode

COPY src/LogPulse.Core/ src/LogPulse.Core/
COPY src/LogPulse.Agent/ src/LogPulse.Agent/
RUN dotnet publish src/LogPulse.Agent/LogPulse.Agent.csproj -c Release --no-restore -o /app

# A worker service: the plain .NET runtime is enough, no ASP.NET Core.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
# Saved log positions; compose mounts a volume here so a new container resumes where the old one stopped.
RUN mkdir -p /var/lib/logpulse-agent && chown $APP_UID /var/lib/logpulse-agent
ENV Agent__StateDirectory=/var/lib/logpulse-agent
# The non-root user that the .NET 8 images ship with.
USER $APP_UID
ENTRYPOINT ["dotnet", "LogPulse.Agent.dll"]
