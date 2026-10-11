FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY DanLoopServer.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
RUN mkdir -p /data/keys && chown -R app:app /data
COPY --from=build --chown=app:app /app/publish ./
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Storage__StatePath=/data/state.json \
    Storage__SqlitePath=/data/dan-loop.db \
    Storage__KeyPath=/data/keys
EXPOSE 8080
ENTRYPOINT ["dotnet", "DanLoopServer.dll"]
