using Z.Vtuber.RecordingTranscript.Consumer.Configuration;

namespace Z.Vtuber.RecordingTranscript.Consumer.Services;

public interface IRecordingServiceClient
{
    string BuildLiveAudioUrl(long roomId);
    string BuildFileUrl(long roomId, string fileName);
    Task<bool> IsLiveStreamAvailableAsync(long roomId, CancellationToken ct = default);
}

public sealed class RecordingServiceClient(
    HttpClient httpClient,
    ILogger<RecordingServiceClient> logger) : IRecordingServiceClient
{
    public string BuildLiveAudioUrl(long roomId) => BuildUrl($"api/{roomId}/stream/audio");

    public string BuildFileUrl(long roomId, string fileName) =>
        BuildUrl($"api/{roomId}/recordings/{Uri.EscapeDataString(fileName)}");

    public async Task<bool> IsLiveStreamAvailableAsync(long roomId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, $"api/{roomId}/stream/audio");
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Probe live audio for room {RoomId} failed", roomId);
            return false;
        }
    }

    private string BuildUrl(string relative)
    {
        var baseAddress = httpClient.BaseAddress ?? new Uri(RecordingServiceOptions.DefaultBaseUrl);
        return new Uri(baseAddress, relative).ToString();
    }
}
