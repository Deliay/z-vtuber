FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY servers/api/Z.Vtuber.csproj servers/api/
RUN dotnet restore servers/api/Z.Vtuber.csproj

COPY servers/api/ servers/api/
RUN dotnet publish servers/api/Z.Vtuber.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Z.Vtuber.dll"]
