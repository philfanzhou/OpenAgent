FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Backend/ Backend/
RUN dotnet restore Backend/OpenAgent.sln
RUN dotnet publish Backend/src/OpenAgent.Engine.Host/OpenAgent.Engine.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish/ .

ENV ASPNETCORE_URLS=http://+:5208
EXPOSE 5208
ENTRYPOINT ["dotnet", "OpenAgent.Engine.Host.dll"]
