using Puluj.Processing.Llm;

namespace Puluj.Processing.Tests.Llm;

public class LlmCostTests
{
    [Fact]
    public void Uses_each_billed_token_bucket_from_the_price_card()
    {
        var options = new LlmOptions
        {
            InputUsdPerMillionTokens = 5m,
            CacheWriteUsdPerMillionTokens = 6.25m,
            CacheReadUsdPerMillionTokens = .5m,
            OutputUsdPerMillionTokens = 25m,
        };

        var cost = LlmCost.Calculate(options, 1_000_000, 1_000_000, 1_000_000, 1_000_000);

        Assert.Equal(36.75m, cost);
    }
}
