using RemoteHub.Views;

namespace RemoteHub.Tests;

public class PsSyntaxTests
{
    [Theory]
    [InlineData("Get-ChildItem -Path $env:TEMP -Filter '*.log' | Select-Object -First 2 # c")]
    [InlineData("$x = \"a $b c\"; if ($x -gt 1) { [int]$x.Length }")]
    [InlineData("Get-Item 'unterminated")]
    [InlineData("")]
    public void Segments_rebuild_the_exact_text(string text) =>
        Assert.Equal(text, string.Concat(PsSyntax.Colorize(text).Select(s => s.Text)));

    [Fact]
    public void Tokens_get_their_colours()
    {
        var segs = PsSyntax.Colorize("Get-Process -Name 'x' $y 42 # note");
        System.Windows.Media.Brush Of(string t) => segs.First(s => s.Text == t).Brush;
        Assert.Same(PsSyntax.Command, Of("Get-Process"));
        Assert.Same(PsSyntax.Parameter, Of("-Name"));
        Assert.Same(PsSyntax.String, Of("'x'"));
        Assert.Same(PsSyntax.Variable, Of("$y"));
        Assert.Same(PsSyntax.Number, Of("42"));
        Assert.Same(PsSyntax.Comment, Of("# note"));
    }
}
