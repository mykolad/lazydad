namespace LazyDad.Api.Configuration;

/// <summary>
/// "You might also like": how jokes get their profiles (see JokeProfiler). Jev is the main method, embeddings the
/// fallback; either is off while its settings are empty.
/// </summary>
public class SimilarityOptions
{
    public const string SectionName = "Similarity";

    /// <summary>
    /// The most jokes profiled per tick, newest first: the tick's own jokes, then older ones without a profile
    /// (the backfill), until every joke has one.
    /// </summary>
    public int BatchSize { get; set; } = 60;

    public JevOptions Jev { get; set; } = new();
    public EmbeddingOptions Embeddings { get; set; } = new();
}

public class JevOptions
{
    public string Endpoint { get; set; } = "https://jevtypesafeai.com/api/v1/decide";
    /// <summary>Pinned, so profiles made months apart stay comparable. A new version means profiling every joke again.</summary>
    public string Model { get; set; } = "jev-1.13.0";
    /// <summary>A Key Vault reference in Azure (JevApiKey, JevApiKeyStaging); empty turns Jev off.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}

public class EmbeddingOptions
{
    /// <summary>One of <c>LlmProviders</c>; the app calls it as its managed identity, like the chat models.</summary>
    public string Provider { get; set; } = "AzureOpenAI";
    /// <summary>The embedding model's deployment name; empty turns embeddings off.</summary>
    public string Deployment { get; set; } = "text-embedding-3-small";
    public int Dimensions { get; set; } = 512;

    public bool Enabled => !string.IsNullOrWhiteSpace(Deployment);
}
