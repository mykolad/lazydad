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

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "LazyDad.Api.dll"]
