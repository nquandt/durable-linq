# Build context is the repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
RUN dotnet publish examples/04-Postgres-Approvals/04-Postgres-Approvals.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
EXPOSE 5004
ENTRYPOINT ["dotnet", "PostgresApprovals.dll"]
