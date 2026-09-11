namespace Z.Vtuber.Notifier.Satori;

public sealed class SatoriOptions
{
    public const string SectionName = "Z_Vtuber_Satori";

    public string Endpoint { get; set; } = string.Empty;
    public string? Token { get; set; }
    public string? Platform { get; set; }
    public string? SelfId { get; set; }
}
