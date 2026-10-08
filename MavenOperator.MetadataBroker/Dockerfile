# ── .NET SDK build stage ────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder
WORKDIR /build

COPY MavenOperator.MetadataBroker/MavenOperator.MetadataBroker.csproj \
     MavenOperator.MetadataBroker/

RUN dotnet restore MavenOperator.MetadataBroker

COPY MavenOperator.MetadataBroker/ .
RUN dotnet publish MavenOperator.MetadataBroker -c Release -o /out --no-restore

# ── Runtime stage ───────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

COPY --from=builder /out .

ENV ASPNETCORE_URLS=http://127.0.0.1:8091 \
    REPOSITORY_ROOT=/var/maven/repository \
    DOTNET_RUNNING_IN_CONTAINER=true \
    TZ=Etc/UTC

EXPOSE 8091

# Loopback-only by design (no Service port exposes this).
ENTRYPOINT ["dotnet", "MavenOperator.MetadataBroker.dll"]
