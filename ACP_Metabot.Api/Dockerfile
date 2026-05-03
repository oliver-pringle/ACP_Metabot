FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ACP_Metabot.Api.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./

# Drop privileges. The aspnet base image ships with a non-root `app` user
# (UID 1654 in .NET 10, exposed via $APP_UID). NOTE: the SQLite bind-mount
# target /data must be writable by this UID on the host — see the docker-
# compose comment for the one-time `chown 1654:1654 ./data` step.
USER $APP_UID
EXPOSE 5000

ENTRYPOINT ["dotnet", "ACP_Metabot.Api.dll"]
