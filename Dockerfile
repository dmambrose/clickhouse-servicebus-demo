FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source
COPY src/ClickHouseDemo/ClickHouseDemo.csproj src/ClickHouseDemo/
RUN dotnet restore src/ClickHouseDemo/ClickHouseDemo.csproj
COPY src/ClickHouseDemo/ src/ClickHouseDemo/
COPY src/Shared/ src/Shared/
COPY sql/ sql/
RUN dotnet publish src/ClickHouseDemo/ClickHouseDemo.csproj -c Release -o /app --no-restore
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "ClickHouseDemo.dll"]
