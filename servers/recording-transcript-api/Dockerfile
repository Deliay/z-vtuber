FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY servers/common/core/Z.Vtuber.Common.Core.csproj servers/common/core/
COPY servers/common/live/Z.Vtuber.Common.Live.csproj servers/common/live/
COPY servers/common/recording/Z.Vtuber.Common.Recording.csproj servers/common/recording/
COPY servers/common/transcript/Z.Vtuber.Common.Transcript.csproj servers/common/transcript/
COPY servers/recording-transcript-api/Z.Vtuber.RecordingTranscript.Api.csproj servers/recording-transcript-api/
RUN dotnet restore servers/recording-transcript-api/Z.Vtuber.RecordingTranscript.Api.csproj

COPY servers/common/ servers/common/
COPY servers/recording-transcript-api/ servers/recording-transcript-api/
RUN dotnet publish servers/recording-transcript-api/Z.Vtuber.RecordingTranscript.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Z.Vtuber.RecordingTranscript.Api.dll"]
