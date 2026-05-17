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

# Use the built-in non-root user provided by .NET runtime images.
USER $APP_UID

COPY --from=build /app/publish .

ENTRYPOINT ["./MavenOperator.ImportJob"]

