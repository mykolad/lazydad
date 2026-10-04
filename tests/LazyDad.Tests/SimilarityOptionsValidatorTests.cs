using LazyDad.Api.Configuration;
using Microsoft.Extensions.Options;

namespace LazyDad.Tests;

public class SimilarityOptionsValidatorTests
{
    private static string[] Errors(SimilarityOptions options)
        => Errors(options, new() { ["AzureOpenAI"] = new LlmProviderOptions() });

    private static string[] Errors(SimilarityOptions options, Dictionary<string, LlmProviderOptions> providers)
        => new SimilarityOptionsValidator(Options.Create(providers)).Validate(null, options).Failures?.ToArray() ?? [];

    [Fact]
    public void Validate_TheDefaultsWithAKey_Pass()
        => Assert.Empty(Errors(new SimilarityOptions { Jev = { ApiKey = "key" } }));

    [Theory]
    [InlineData(0)]
    [InlineData(SimilarityOptionsValidator.MaxBatchSize + 1)]
    public void Validate_RejectsABatchSizeOutOfRange(int batchSize)
        => Assert.Contains(Errors(new SimilarityOptions { BatchSize = batchSize }), e => e.Contains("BatchSize"));

    [Theory]
    [InlineData("not a url")]
    [InlineData("http://jevtypesafeai.com/api/v1/decide")]
    public void Validate_WithJevOn_NeedsAnHttpsEndpoint(string endpoint)
        => Assert.Contains(Errors(new SimilarityOptions { Jev = { ApiKey = "key", Endpoint = endpoint } }), e => e.Contains("Jev:Endpoint"));

    [Fact]
    public void Validate_WithJevOn_NeedsAPinnedModel()
        => Assert.Contains(Errors(new SimilarityOptions { Jev = { ApiKey = "key", Model = " " } }), e => e.Contains("Jev:Model"));

    [Fact]
    public void Validate_WithJevOff_IgnoresItsSettings()
        => Assert.Empty(Errors(new SimilarityOptions { Jev = { ApiKey = "", Endpoint = "", Model = "" } }));

    [Theory]
    [InlineData(0)]
    [InlineData(SimilarityOptionsValidator.MaxDimensions + 1)]
    public void Validate_WithEmbeddingsOn_RejectsDimensionsOutOfRange(int dimensions)
        => Assert.Contains(Errors(new SimilarityOptions { Embeddings = { Dimensions = dimensions } }), e => e.Contains("Dimensions"));

    [Theory]
    [InlineData("")]
    [InlineData("AzureOpenAi")]
    public void Validate_WithEmbeddingsOn_NeedsAConfiguredProvider(string provider)
        => Assert.Contains(Errors(new SimilarityOptions { Embeddings = { Provider = provider } }), e => e.Contains("Embeddings:Provider"));

    [Fact]
    public void Validate_WithEmbeddingsOn_NeedsAProviderThatSupportsThem()
        => Assert.Contains(Errors(new SimilarityOptions { Embeddings = { Provider = "Other" } }, new() { ["Other"] = new LlmProviderOptions() }),
            e => e.Contains("Embeddings:Provider"));

    [Fact]
    public void Validate_WithEmbeddingsOff_IgnoresTheirSettings()
        => Assert.Empty(Errors(new SimilarityOptions { Embeddings = { Deployment = "", Provider = "", Dimensions = 0 } }));
}
