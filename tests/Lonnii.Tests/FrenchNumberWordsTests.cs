using Lonnii.Shared.Text;

namespace Lonnii.Tests;

/// <summary>The French spelling an A4 reçu / facture prints for "Montant en lettres".</summary>
public class FrenchNumberWordsTests
{
    [Theory]
    [InlineData(0, "zéro")]
    [InlineData(1, "un")]
    [InlineData(16, "seize")]
    [InlineData(17, "dix-sept")]
    [InlineData(21, "vingt et un")]
    [InlineData(22, "vingt-deux")]
    [InlineData(61, "soixante et un")]
    [InlineData(70, "soixante-dix")]
    [InlineData(71, "soixante et onze")]
    [InlineData(77, "soixante-dix-sept")]
    [InlineData(80, "quatre-vingts")]
    [InlineData(81, "quatre-vingt-un")]
    [InlineData(90, "quatre-vingt-dix")]
    [InlineData(91, "quatre-vingt-onze")]
    [InlineData(99, "quatre-vingt-dix-neuf")]
    [InlineData(100, "cent")]
    [InlineData(101, "cent un")]
    [InlineData(200, "deux cents")]
    [InlineData(201, "deux cent un")]
    [InlineData(1000, "mille")]
    [InlineData(1250, "mille deux cent cinquante")]
    [InlineData(2000, "deux mille")]
    [InlineData(12500, "douze mille cinq cents")]
    [InlineData(80000, "quatre-vingt mille")]
    [InlineData(200000, "deux cent mille")]
    [InlineData(1000000, "un million")]
    [InlineData(2000000, "deux millions")]
    [InlineData(200000000, "deux cents millions")]
    [InlineData(4940470, "quatre millions neuf cent quarante mille quatre cent soixante-dix")]
    [InlineData(1000000000, "un milliard")]
    public void Writes_whole_numbers(long number, string expected) =>
        Assert.Equal(expected, FrenchNumberWords.ToWords(number));

    [Fact]
    public void Amount_carries_the_currency_and_decimals()
    {
        Assert.Equal("douze mille cinq cents FCFA", FrenchNumberWords.Amount(12500m, "FCFA"));
        Assert.Equal("dix virgule cinquante $", FrenchNumberWords.Amount(10.50m, "$", 2));
        Assert.Equal("dix $", FrenchNumberWords.Amount(10.00m, "$", 2));
    }

    [Fact]
    public void Sentence_names_the_document()
    {
        Assert.Equal("Arrêtée la présente facture à la somme de : cent FCFA.",
            FrenchNumberWords.Sentence(100m, "FCFA", 0, facture: true));
        Assert.Equal("Arrêté le présent reçu à la somme de : cent FCFA.",
            FrenchNumberWords.Sentence(100m, "FCFA", 0, facture: false));
    }
}
