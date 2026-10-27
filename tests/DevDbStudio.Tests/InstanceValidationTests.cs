using DevDbStudio.Services.Dbms;

namespace DevDbStudio.Tests;

public class InstanceValidationTests
{
    private static InstanceCreateRequest Valid(
        string name = "pg-1", int port = 5432, string pw = "Str0ng!Pass", string db = "appdb", string tag = "16") =>
        new(name, "postgres", port, pw, db, tag);

    [Fact]
    public void ValidRequest_HasNoErrors() => Assert.Empty(InstanceValidation.Validate(Valid()));

    [Theory]
    [InlineData("Sh0rt!")]                 // too short
    [InlineData("alllowercase1!")]         // no uppercase
    [InlineData("ALLUPPERCASE1!")]         // no lowercase
    [InlineData("NoDigitsHere!")]          // no digit
    [InlineData("NoSymbol1234")]           // no symbol
    [InlineData("Has Space1!")]            // whitespace
    public void WeakPasswords_AreRejected(string pw) => Assert.NotEmpty(InstanceValidation.PasswordErrors(pw));

    [Fact]
    public void NullPassword_IsRejected() => Assert.NotEmpty(InstanceValidation.PasswordErrors(null));

    [Fact]
    public void GeneratedPasswords_AreValidAndDiffer()
    {
        var a = InstanceValidation.GeneratePassword();
        Assert.Empty(InstanceValidation.PasswordErrors(a));
        Assert.NotEqual(a, InstanceValidation.GeneratePassword());
    }

    [Fact]
    public void TooLongPassword_IsRejected() =>
        Assert.Contains(InstanceValidation.PasswordErrors("Aa1!" + new string('x', 130)), e => e.Contains("At most 128"));

    [Theory]
    [InlineData(1023)]
    [InlineData(65536)]
    [InlineData(0)]
    public void PortOutsideRange_IsRejected(int port) =>
        Assert.Contains(InstanceValidation.Validate(Valid(port: port)), e => e.Contains("port"));

    [Theory]
    [InlineData(1024)]
    [InlineData(65535)]
    public void PortBoundaries_AreAccepted(int port) => Assert.Empty(InstanceValidation.Validate(Valid(port: port)));

    [Theory]
    [InlineData("")]
    [InlineData("1db")]            // must start with a letter
    [InlineData("my-db")]          // hyphen not allowed
    [InlineData("db; DROP TABLE x")]
    public void InvalidDatabaseNames_AreRejected(string db) =>
        Assert.Contains(InstanceValidation.Validate(Valid(db: db)), e => e.Contains("Database name"));

    [Theory]
    [InlineData("")]
    [InlineData("-bad")]
    [InlineData("a b")]
    [InlineData("tag;rm -rf")]
    public void InvalidImageTags_AreRejected(string tag) =>
        Assert.Contains(InstanceValidation.Validate(Valid(tag: tag)), e => e.Contains("Image tag"));

    [Theory]
    [InlineData("16")]
    [InlineData("2022-latest")]
    [InlineData("8.4.1")]
    public void ValidImageTags_AreAccepted(string tag) => Assert.Empty(InstanceValidation.Validate(Valid(tag: tag)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankName_IsRejected(string name) =>
        Assert.Contains(InstanceValidation.Validate(Valid(name: name)), e => e.Contains("Name"));

    [Fact]
    public void NameLongerThan50_IsRejected() =>
        Assert.Contains(InstanceValidation.Validate(Valid(name: new string('a', 51))), e => e.Contains("Name"));
}
