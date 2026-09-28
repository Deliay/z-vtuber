FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY servers/common/core/Z.Vtuber.Common.Core.csproj servers/common/core/
COPY servers/common/live/Z.Vtuber.Common.Live.csproj servers/common/live/
COPY servers/common/weibo/Z.Vtuber.Common.Weibo.csproj servers/common/weibo/
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
