FROM mcr.microsoft.com/dotnet/sdk:11.0.100-alpine3.24 AS build
WORKDIR /src

COPY --link RepositoryServer/*.csproj RepositoryServer/
COPY --link *.props .
COPY --link *.json .
RUN dotnet restore RepositoryServer/RepositoryServer.csproj

COPY --link RepositoryServer/. RepositoryServer/

RUN dotnet publish -c Release RepositoryServer/RepositoryServer.csproj -o /app

# final is the final runtime stage for running the app
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-alpine3.24 AS final
WORKDIR /app

COPY --link --from=build /app .
COPY docker/appsettings.Container.json /app/appsettings.Container.json
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

RUN apk update && apk add --no-cache openssl


ENTRYPOINT ["/bin/ash", "/entrypoint.sh"]