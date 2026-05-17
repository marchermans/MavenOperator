FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY MavenOperator.ImportJob/MavenOperator.ImportJob.csproj MavenOperator.ImportJob/
RUN dotnet restore MavenOperator.ImportJob/MavenOperator.ImportJob.csproj

COPY MavenOperator.ImportJob/ MavenOperator.ImportJob/
RUN dotnet publish MavenOperator.ImportJob/MavenOperator.ImportJob.csproj \
    -c Release -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# Non-root user for security
RUN addgroup --gid 10001 appgroup && \
    adduser  --uid 10001 --gid 10001 --disabled-password --no-create-home appuser
USER appuser

COPY --from=build /app/publish .

ENTRYPOINT ["./MavenOperator.ImportJob"]

