using MvsBuddy.Games;

namespace MvsBuddy.Tests;

public sealed class VdfTests
{
    [Fact]
    public void Quoted_strings_unescape_and_comments_are_skipped()
    {
        var vdf = Vdf.Parse("// a comment\n\"root\" { \"path\" \"D:\\\\SteamLibrary\" \"quote\" \"a \\\"b\\\"\" bare word }");

        var root = vdf.Child("ROOT")!;
        Assert.Equal(@"D:\SteamLibrary", root.Value("path"));
        Assert.Equal("a \"b\"", root.Value("quote"));
        Assert.Equal("word", root.Value("bare"));
    }

    [Theory]
    [InlineData("\"a\" {")]
    [InlineData("\"a\" \"b\" }")]
    [InlineData("\"a\"")]
    [InlineData("\"a\" \"unterminated")]
    public void Broken_text_is_a_format_error(string text) => Assert.Throws<FormatException>(() => Vdf.Parse(text));
}
