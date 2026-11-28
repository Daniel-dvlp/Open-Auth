# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0.428 AS build
WORKDIR /src
COPY global.json Directory.Build.props EscudoYuma.sln ./
COPY src/Yuma.Comun/Yuma.Comun.csproj src/Yuma.Comun/
COPY src/EscudoYuma.Api/EscudoYuma.Api.csproj src/EscudoYuma.Api/
RUN dotnet restore src/EscudoYuma.Api/EscudoYuma.Api.csproj
COPY src/Yuma.Comun/ src/Yuma.Comun/
COPY src/EscudoYuma.Api/ src/EscudoYuma.Api/
COPY training/artefactos/ training/artefactos/
RUN dotnet publish src/EscudoYuma.Api/EscudoYuma.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0.34 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080 8443
ENTRYPOINT ["dotnet", "EscudoYuma.Api.dll"]
