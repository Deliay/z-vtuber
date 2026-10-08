using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Z.Vtuber.Crawler.Configuration;

namespace Z.Vtuber.Tests;

public sealed class S3OptionsTests
{
    private static IReadOnlyList<ValidationResult> Validate(S3Options options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Validate_DisabledWithEmptyFields_IsValid()
        => Assert.Empty(Validate(new S3Options { Enabled = false }));

    [Fact]
    public void Validate_EnabledWithAllFields_IsValid()
    {
        var options = new S3Options
        {
            Enabled = true,
            ServiceUrl = "http://minio:9000",
            Bucket = "bucket",
            AccessKey = "key",
            SecretKey = "secret",
        };

        Assert.Empty(Validate(options));
    }

    [Fact]
    public void Validate_EnabledWithMissingFields_ReportsEachRequiredField()
    {
        var results = Validate(new S3Options { Enabled = true });

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(S3Options.ServiceUrl)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(S3Options.Bucket)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(S3Options.AccessKey)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(S3Options.SecretKey)));
    }

    [Fact]
    public void Defaults_PrefixAndPathStyleAreMinioFriendly()
    {
        var options = new S3Options();

        Assert.Equal("video-cache", options.Prefix);
        Assert.True(options.ForcePathStyle);
        Assert.Equal("us-east-1", options.Region);
        Assert.False(options.Enabled);
    }

    [Fact]
    public void OptionsValidationPipeline_InvokesCustomValidation()
    {
        var services = new ServiceCollection();
        services.AddOptions<S3Options>()
            .Configure(options =>
            {
                options.Enabled = true;
                options.ServiceUrl = "http://minio:9000";
                options.Bucket = "bucket";
                options.AccessKey = "key";
                options.SecretKey = "secret";
            })
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IOptions<S3Options>>().Value);
    }

    [Fact]
    public void OptionsValidationPipeline_EnabledMissingFields_Throws()
    {
        var services = new ServiceCollection();
        services.AddOptions<S3Options>()
            .Configure(options => options.Enabled = true)
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<S3Options>>().Value);
    }
}
