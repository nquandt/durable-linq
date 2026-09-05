# Build context is the repository root:  docker build -f examples/03-Redis-Workers/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
RUN dotnet publish examples/03-Redis-Workers/03-Redis-Workers.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "RedisWorkers.dll"]
