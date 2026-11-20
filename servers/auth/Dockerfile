FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY servers/common/core/Z.Vtuber.Common.Core.csproj servers/common/core/
COPY servers/common/live/Z.Vtuber.Common.Live.csproj servers/common/live/
COPY servers/auth/Z.Vtuber.Auth.csproj servers/auth/
RUN dotnet restore servers/auth/Z.Vtuber.Auth.csproj

COPY servers/common/ servers/common/
COPY servers/auth/ servers/auth/
RUN dotnet publish servers/auth/Z.Vtuber.Auth.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Z.Vtuber.Auth.dll"]
