namespace Teos.OpportunityNetwork.Matching;

/// <summary>
/// Produces RAW semantic output for an (opportunity, profile) pair. Implementations must
/// return the model's content verbatim; validation is done separately by
/// <see cref="SemanticValidator"/> before any value reaches the scorer.
/// </summary>
public interface ISemanticEvaluator
{
    Task<string> EvaluateAsync(string opportunityText, string profileText, CancellationToken ct = default);
}

/// <summary>Deterministic evaluator for tests/replay harnesses; returns a fixed raw output.</summary>
public sealed class StaticSemanticEvaluator : ISemanticEvaluator
{
    private readonly string _output;

    public StaticSemanticEvaluator(string output) => _output = output;

    public Task<string> EvaluateAsync(string opportunityText, string profileText, CancellationToken ct = default) =>
        Task.FromResult(_output);
}

/// <summary>
/// Guard used to prove the replay/benchmark path never calls an LLM: any invocation throws.
/// </summary>
public sealed class ForbiddenSemanticEvaluator : ISemanticEvaluator
{
    public Task<string> EvaluateAsync(string opportunityText, string profileText, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "LLM evaluation is forbidden on this path (replay/benchmark must use the stored snapshot).");
}