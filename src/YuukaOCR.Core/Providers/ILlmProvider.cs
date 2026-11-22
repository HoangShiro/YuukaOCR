namespace YuukaOCR.Core.Providers;

public interface ILlmProvider
{
    ProviderInfo Info { get; }
    Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken);
    Task<ModelListResult> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Proves the key works, without promising a model list. Kept separate from
    /// <see cref="ListModelsAsync"/> because a few providers list models without auth.
    /// </summary>
    Task<ModelListResult> ValidateKeyAsync(CancellationToken cancellationToken);
}
