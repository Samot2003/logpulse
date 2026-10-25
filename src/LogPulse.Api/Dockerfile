# Build from the repository root: docker build -f src/LogPulse.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first, from the project files and lock files only, so this layer is cached until a dependency changes.
COPY Directory.Build.props .editorconfig ./
COPY src/LogPulse.Core/LogPulse.Core.csproj src/LogPulse.Core/packages.lock.json src/LogPulse.Core/
COPY src/LogPulse.Data/LogPulse.Data.csproj src/LogPulse.Data/packages.lock.json src/LogPulse.Data/
COPY src/LogPulse.Api/LogPulse.Api.csproj src/LogPulse.Api/packages.lock.json src/LogPulse.Api/
RUN dotnet restore src/LogPulse.Api/LogPulse.Api.csproj --locked-mode

COPY src/LogPulse.Core/ src/LogPulse.Core/
COPY src/LogPulse.Data/ src/LogPulse.Data/
COPY src/LogPulse.Api/ src/LogPulse.Api/
RUN dotnet publish src/LogPulse.Api/LogPulse.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# The non-root user that the .NET 8 images ship with.
USER $APP_UID
ENTRYPOINT ["dotnet", "LogPulse.Api.dll"]
