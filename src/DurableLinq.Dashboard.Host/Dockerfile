# Build context must be the repository root, e.g.:
#   docker build -f src/DurableLinq.Dashboard.Host/Dockerfile -t durablelinq-dashboard .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet publish src/DurableLinq.Dashboard.Host/DurableLinq.Dashboard.Host.csproj \
    -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV DURABLE_STORE=file \
    DURABLE_CONNECTION=/data \
    PORT=8080
EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "DurableLinq.Dashboard.Host.dll"]
