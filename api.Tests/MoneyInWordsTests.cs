using BidBuilder.Api.Services;
using Xunit;

namespace BidBuilder.Api.Tests;

/// <summary>Pure unit tests for the number-to-words engine used on the bid letter.</summary>
public class MoneyInWordsTests
{
    [Theory]
    [InlineData(0, "Zero")]
    [InlineData(7, "Seven")]
    [InlineData(21, "Twenty-One")]
    [InlineData(100, "One Hundred")]
    [InlineData(142, "One Hundred Forty-Two")]
    [InlineData(1000, "One Thousand")]
    [InlineData(65000, "Sixty-Five Thousand")]
    [InlineData(7065941, "Seven Million Sixty-Five Thousand Nine Hundred Forty-One")]
    public void Integer_words_are_correct(long n, string expected) =>
        Assert.Equal(expected, MoneyInWords.Integer(n));

    [Fact]
    public void Money_includes_currency_fraction_and_only_suffix()
    {
        Assert.Equal(
            "Seven Million Sixty-Five Thousand Nine Hundred Forty-One AED and Sixty-Six Fils only",
            MoneyInWords.Money(7065941.66m, "AED"));
    }

    [Fact]
    public void Money_uses_Cents_for_non_AED()
    {
        Assert.Equal("One Hundred USD and Fifty Cents only", MoneyInWords.Money(100.50m, "USD"));
    }

    [Fact]
    public void Money_with_no_fraction_omits_the_subunit_clause()
    {
        Assert.Equal("Twenty-One AED only", MoneyInWords.Money(21m, "AED"));
        Assert.Equal("Zero AED only", MoneyInWords.Money(0m, "AED"));
    }

    [Fact]
    public void Money_rounds_a_fraction_that_carries_into_the_whole()
    {
        // 0.999 rounds the fractional part to 100 → carries to 1 whole, 0 fraction.
        Assert.Equal("One AED only", MoneyInWords.Money(0.999m, "AED"));
    }
}
