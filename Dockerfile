FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MediaPlayer.Web/MediaPlayer.Web.csproj src/MediaPlayer.Web/
RUN dotnet restore src/MediaPlayer.Web/MediaPlayer.Web.csproj
COPY src/MediaPlayer.Web/ src/MediaPlayer.Web/
RUN dotnet publish src/MediaPlayer.Web/MediaPlayer.Web.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MediaPlayer.Web.dll"]

