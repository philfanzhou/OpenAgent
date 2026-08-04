FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
COPY . .
EXPOSE 5208
EXPOSE 7175
VOLUME /app/data
ENTRYPOINT ["dotnet", "OpenAgent.Engine.Host.dll"]

