FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["MavenOperator.VirtualProxy/MavenOperator.VirtualProxy.csproj", "MavenOperator.VirtualProxy/"]
RUN dotnet restore "MavenOperator.VirtualProxy/MavenOperator.VirtualProxy.csproj"
COPY . .
WORKDIR "/src/MavenOperator.VirtualProxy"
RUN dotnet build "./MavenOperator.VirtualProxy.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./MavenOperator.VirtualProxy.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MavenOperator.VirtualProxy.dll"]

