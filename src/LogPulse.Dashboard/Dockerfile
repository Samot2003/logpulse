# Build from the repository root: docker build -f src/LogPulse.Dashboard/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first, from the project files and lock files only, so this layer is cached until a dependency changes.
COPY Directory.Build.props .editorconfig ./
COPY src/LogPulse.Core/LogPulse.Core.csproj src/LogPulse.Core/packages.lock.json src/LogPulse.Core/
COPY src/LogPulse.Dashboard/LogPulse.Dashboard.csproj src/LogPulse.Dashboard/packages.lock.json src/LogPulse.Dashboard/
RUN dotnet restore src/LogPulse.Dashboard/LogPulse.Dashboard.csproj --locked-mode

COPY src/LogPulse.Core/ src/LogPulse.Core/
COPY src/LogPulse.Dashboard/ src/LogPulse.Dashboard/
RUN dotnet publish src/LogPulse.Dashboard/LogPulse.Dashboard.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# Data Protection keys live here; compose mounts a volume so they survive a new container.
RUN mkdir -p /var/lib/logpulse-dashboard/keys && chown -R $APP_UID /var/lib/logpulse-dashboard
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Dashboard__KeysDirectory=/var/lib/logpulse-dashboard/keys
EXPOSE 8080
# The non-root user that the .NET 8 images ship with.
USER $APP_UID
ENTRYPOINT ["dotnet", "LogPulse.Dashboard.dll"]
