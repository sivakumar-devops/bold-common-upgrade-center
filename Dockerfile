# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY Bold.UpgradeCenter.csproj ./
RUN dotnet restore Bold.UpgradeCenter.csproj

COPY . ./
RUN dotnet publish Bold.UpgradeCenter.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_RUNNING_IN_CONTAINER=true

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
    && groupadd --system --gid 10001 appgroup \
    && useradd --system --uid 10001 --gid appgroup --home-dir /app --shell /usr/sbin/nologin appuser \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

RUN mkdir -p /app/App_Data/DataProtectionKeys /app/App_Data/PlaywrightReports \
    && chown -R appuser:appgroup /app

USER appuser

EXPOSE 8080

ENTRYPOINT ["dotnet", "Bold.UpgradeCenter.dll"]
