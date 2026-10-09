FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/LazyDad.Api/LazyDad.Api.csproj", "src/LazyDad.Api/"]
COPY ["src/LazyDad.Data/LazyDad.Data.csproj", "src/LazyDad.Data/"]
RUN dotnet restore "src/LazyDad.Api/LazyDad.Api.csproj"

COPY . .
RUN dotnet publish "src/LazyDad.Api/LazyDad.Api.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# The app writes wwwroot/index.html at startup, so its user owns that one file. Everything else, the wwwroot folder
# included, stays root's: owning the folder would let the app delete or replace app.js and the other files.
RUN touch /app/wwwroot/index.html && chown $APP_UID /app/wwwroot/index.html
USER $APP_UID

# The base image already listens on 8080 (ASPNETCORE_HTTP_PORTS=8080). Setting ASPNETCORE_URLS as well only made the
# app warn at every start that one overrides the other.
EXPOSE 8080

# Version metadata baked into the image (Deploy Master passes the commit), so the image describes
# itself wherever it runs. Read by the app as App:Version etc., and exposed as standard OCI labels.
ARG VERSION=dev
ARG REVISION=
ARG COMMIT_DATE=
ARG SOURCE_URL=
LABEL org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$REVISION \
      org.opencontainers.image.created=$COMMIT_DATE \
      org.opencontainers.image.source=$SOURCE_URL
ENV App__Version=$VERSION \
    App__Revision=$REVISION \
    App__CommitDate=$COMMIT_DATE \
    App__SourceUrl=$SOURCE_URL

ENTRYPOINT ["dotnet", "LazyDad.Api.dll"]
