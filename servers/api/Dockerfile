FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY servers/api/Z.Vtuber.csproj servers/api/
COPY servers/common/core/Z.Vtuber.Common.Core.csproj servers/common/core/
COPY servers/common/live/Z.Vtuber.Common.Live.csproj servers/common/live/
COPY servers/common/cookie/Z.Vtuber.Common.Cookie.csproj servers/common/cookie/
COPY servers/common/weibo/Z.Vtuber.Common.Weibo.csproj servers/common/weibo/
COPY servers/common/recording/Z.Vtuber.Common.Recording.csproj servers/common/recording/
RUN dotnet restore servers/api/Z.Vtuber.csproj

COPY servers/api/ servers/api/
COPY servers/common/ servers/common/
RUN dotnet publish servers/api/Z.Vtuber.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Z.Vtuber.dll"]
