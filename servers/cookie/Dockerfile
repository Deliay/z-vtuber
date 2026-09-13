FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY servers/common/Z.Vtuber.Common.csproj servers/common/
COPY servers/cookie/Z.Vtuber.Cookie.csproj servers/cookie/
RUN dotnet restore servers/cookie/Z.Vtuber.Cookie.csproj

COPY servers/common/ servers/common/
COPY servers/cookie/ servers/cookie/
RUN dotnet publish servers/cookie/Z.Vtuber.Cookie.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/playwright/dotnet:v1.62.0-noble AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Z.Vtuber.Cookie.dll"]