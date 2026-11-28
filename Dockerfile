FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /source

COPY src/TraktMyYear.Api/TraktMyYear.Api.csproj src/TraktMyYear.Api/
COPY src/TraktMyYear.Application/TraktMyYear.Application.csproj src/TraktMyYear.Application/
COPY src/TraktMyYear.Infrastructure/TraktMyYear.Infrastructure.csproj src/TraktMyYear.Infrastructure/
RUN dotnet restore src/TraktMyYear.Api/TraktMyYear.Api.csproj

COPY src/ src/
RUN dotnet publish src/TraktMyYear.Api/TraktMyYear.Api.csproj \
    --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "TraktMyYear.Api.dll"]
