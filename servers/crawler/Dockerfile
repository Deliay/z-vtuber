FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY servers/common/Z.Vtuber.Common.csproj servers/common/
COPY servers/crawler/Z.Vtuber.Crawler.csproj servers/crawler/
RUN dotnet restore servers/crawler/Z.Vtuber.Crawler.csproj

COPY servers/common/ servers/common/
COPY servers/crawler/ servers/crawler/
RUN dotnet publish servers/crawler/Z.Vtuber.Crawler.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Z.Vtuber.Crawler.dll"]