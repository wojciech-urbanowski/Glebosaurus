using Glebosaurus.Core.Config;

namespace Glebosaurus.Tests;

/// <summary>Identyfikator czujnika sprawdzany przez <c>pair</c> przed parowaniem – ta sama reguła co w bazie.</summary>
public sealed class SensorIdsTests
{
    [Theory]
    [InlineData("monstera")]
    [InlineData("laurowisnia_szafka")]
    [InlineData("hortensje-niskie")]
    [InlineData("9-rzad")]
    [InlineData("abcdefghijabcdefghijabcdefghijabcdefghij")]
    public void Valid(string id) => Assert.True(SensorIds.IsValid(id));

    [Theory]
    [InlineData("")]
    [InlineData("Bazylia")]
    [InlineData("-bazylia")]
    [InlineData("_bazylia")]
    [InlineData("zioła")]
    [InlineData("dwa slowa")]
    [InlineData("abcdefghijabcdefghijabcdefghijabcdefghijk")]
    public void Invalid(string id) => Assert.False(SensorIds.IsValid(id));
}
