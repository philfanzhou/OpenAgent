FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY LoadDriver.csproj ./
COPY Program.cs ./
RUN dotnet publish --configuration Release --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish/ .
ENV DOTNET_NoLogo=1
ENTRYPOINT ["dotnet", "LoadDriver.dll"]
