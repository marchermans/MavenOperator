# ── .NET SDK build stage ────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder
WORKDIR /src

COPY MavenOperator.MetadataBroker/ .

# The host's obj/ may ride along in the build context — start from a clean slate.
RUN rm -rf obj && dotnet restore
RUN dotnet publish -c Release -o /out --no-restore

# ── Runtime stage ───────────────────────────────────────────────────────
# aspnet (not plain runtime) — the broker is an ASP.NET Core Minimal API and needs
# the Microsoft.AspNetCore.App shared framework.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=builder /out .

ENV ASPNETCORE_URLS=http://127.0.0.1:8091 \
    REPOSITORY_ROOT=/var/maven/repository \
    DOTNET_RUNNING_IN_CONTAINER=true \
    TZ=Etc/UTC

EXPOSE 8091

# Loopback-only by design (no Service port exposes this).
ENTRYPOINT ["dotnet", "MavenOperator.MetadataBroker.dll"]
