FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY servers/common/Z.Vtuber.Common.csproj servers/common/
COPY servers/notifier/Z.Vtuber.Notifier.csproj servers/notifier/
RUN dotnet restore servers/notifier/Z.Vtuber.Notifier.csproj

COPY servers/common/ servers/common/
COPY servers/notifier/ servers/notifier/
RUN dotnet publish servers/notifier/Z.Vtuber.Notifier.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Z.Vtuber.Notifier.dll"]
