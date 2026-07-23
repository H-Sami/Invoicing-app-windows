using MHC.Invoicing.Domain.Search;

namespace MHC.Invoicing.Domain.Tests.Search;

public sealed class ArabicSearchNormalizerTests
{
    [Theory]
    [InlineData("  شَرِكة  الـتقنية  ", "شركه التقنيه")]
    [InlineData("آفاق أفق إتقان ٱسم", "افاق افق اتقان اسم")]
    [InlineData("على  هدى", "علي هدي")]
    [InlineData("MHC   Technology", "mhc technology")]
    [InlineData("١٢٣-ABC", "123-abc")]
    public void Normalize_ProducesStableSearchKey(string input, string expected)
    {
        Assert.Equal(expected, ArabicSearchNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_HandlesNullAndWhitespace()
    {
        Assert.Equal(string.Empty, ArabicSearchNormalizer.Normalize(null));
        Assert.Equal(string.Empty, ArabicSearchNormalizer.Normalize("   "));
    }
}
