FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY servers/common/Z.Vtuber.Common.csproj servers/common/
COPY servers/training/Z.Vtuber.Training.csproj servers/training/
RUN dotnet restore servers/training/Z.Vtuber.Training.csproj

COPY servers/common/ servers/common/
COPY servers/training/ servers/training/
RUN dotnet publish servers/training/Z.Vtuber.Training.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Z.Vtuber.Training.dll"]
