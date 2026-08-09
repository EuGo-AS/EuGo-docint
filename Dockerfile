FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8090

# SDK stage runs on the BUILD host's architecture and cross-publishes for TARGETARCH;
# only the (multi-arch) runtime base resolves per target platform.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/DocInt.Api/DocInt.Api.csproj", "DocInt.Api/"]
COPY ["src/ServiceDefaults/ServiceDefaults.csproj", "ServiceDefaults/"]
RUN dotnet restore "DocInt.Api/DocInt.Api.csproj" -a $TARGETARCH
COPY src/DocInt.Api/ DocInt.Api/
COPY src/ServiceDefaults/ ServiceDefaults/
RUN dotnet publish "DocInt.Api/DocInt.Api.csproj" -c $BUILD_CONFIGURATION -a $TARGETARCH --no-restore \
    -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# HEALTHCHECK intentionally omitted: this chiseled base has no shell and no
# curl/wget, so a classic `HEALTHCHECK CMD curl ...` can't run here, and adding
# one would mean bloating the image with a shell or an HTTP-probe binary --
# the opposite of what chiseled buys us. Container health is instead provided
# by Kubernetes liveness/readiness probes against /health (owned by EuGo-infra).
ENTRYPOINT ["dotnet", "DocInt.Api.dll"]
