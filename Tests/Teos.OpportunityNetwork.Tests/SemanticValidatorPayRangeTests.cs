using Teos.OpportunityNetwork.Matching;
using Xunit;

namespace Teos.OpportunityNetwork.Tests;

/// <summary>Pure unit tests for the optional pay_range schema gate. No PostgreSQL, no network.</summary>
public class SemanticValidatorPayRangeTests
{
    [Fact]
    public void Absent_pay_range_is_allowed_and_yields_nulls()
    {
        var j = SemanticValidator.Validate("{\"relevance\": 70, \"rationale\": \"ok\"}");
        Assert.Null(j.PayRangeMin);
        Assert.Null(j.PayRangeMax);
        Assert.Null(j.PayCurrency);
    }

    [Fact]
    public void Explicit_null_pay_range_is_allowed()
    {
        var j = SemanticValidator.Validate("{\"relevance\": 70, \"pay_range\": null}");
        Assert.Null(j.PayRangeMin);
        Assert.Null(j.PayRangeMax);
    }

    [Fact]
    public void Valid_pay_range_is_parsed()
    {
        var j = SemanticValidator.Validate(
            "{\"relevance\": 88, \"pay_range\": {\"min\": 193, \"max\": 442, \"currency\": \"USD\"}}");
        Assert.Equal(193m, j.PayRangeMin);
        Assert.Equal(442m, j.PayRangeMax);
        Assert.Equal("USD", j.PayCurrency);
    }

    [Fact]
    public void Only_one_bound_is_allowed()
    {
        var j = SemanticValidator.Validate("{\"relevance\": 70, \"pay_range\": {\"min\": 100}}");
        Assert.Equal(100m, j.PayRangeMin);
        Assert.Null(j.PayRangeMax);
        Assert.Null(j.PayCurrency);
    }

    [Fact]
    public void Equal_bounds_are_allowed()
    {
        var j = SemanticValidator.Validate("{\"relevance\": 70, \"pay_range\": {\"min\": 500, \"max\": 500}}");
        Assert.Equal(500m, j.PayRangeMin);
        Assert.Equal(500m, j.PayRangeMax);
    }

    [Fact]
    public void Min_greater_than_max_is_rejected()
    {
        Assert.Throws<SemanticValidationException>(() =>
            SemanticValidator.Validate("{\"relevance\": 70, \"pay_range\": {\"min\": 500, \"max\": 100}}"));
    }

    [Fact]
    public void Negative_bounds_are_rejected()
    {
        Assert.Throws<SemanticValidationException>(() =>
            SemanticValidator.Validate("{\"relevance\": 70, \"pay_range\": {\"min\": -1, \"max\": 100}}"));
        Assert.Throws<SemanticValidationException>(() =>
            SemanticValidator.Validate("{\"relevance\": 70, \"pay_range\": {\"max\": -5}}"));
    }

    [Fact]
    public void Non_numeric_bound_is_rejected()
    {
        Assert.Throws<SemanticValidationException>(() =>
            SemanticValidator.Validate("{\"relevance\": 70, \"pay_range\": {\"min\": \"cheap\", \"max\": 100}}"));
    }

    [Fact]
    public void Non_object_pay_range_is_rejected()
    {
        Assert.Throws<SemanticValidationException>(() =>
            SemanticValidator.Validate("{\"relevance\": 70, \"pay_range\": 193}"));
    }

    [Fact]
    public void Empty_pay_range_object_is_rejected()
    {
        Assert.Throws<SemanticValidationException>(() =>
            SemanticValidator.Validate("{\"relevance\": 70, \"pay_range\": {}}"));
    }
}
