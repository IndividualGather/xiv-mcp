using XivMcp.Ui;

namespace XivMcp.Tests;

public class GameFeedbackTests
{
    [Theory]
    [InlineData(57)] // system message ("You cannot carry any more ...")
    [InlineData(60)] // error message, the red text ("Unable to execute command while jumping.")
    public void SystemAndErrorNoticesAreKept(int chatType) => Assert.True(GameFeedback.IsNotice(chatType));

    [Theory]
    [InlineData(41)]   // damage ("... hits you for 123 damage.")
    [InlineData(42)]   // miss
    [InlineData(43)]   // action used
    [InlineData(45)]   // healing
    [InlineData(46)]   // gains the effect of
    [InlineData(49)]   // loses the effect of
    [InlineData(58)]   // battle system ("You defeat ...")
    [InlineData(2089)] // damage, with the source/target bits the game sets on battle messages
    [InlineData(2091)]
    [InlineData(10)]   // say
    [InlineData(61)]   // NPC dialogue
    [InlineData(0)]
    public void BattleLogAndChatAreDropped(int chatType) => Assert.False(GameFeedback.IsNotice(chatType));

    [Fact]
    public void ANoticeWithSourceBitsStaysANotice()
    {
        // The upper bits only say who the message is about; the channel is in the low seven bits.
        Assert.True(GameFeedback.IsNotice(0x0800 | 60));
        Assert.False(GameFeedback.IsNotice(0x0800 | 41));
    }

    [Fact]
    public void TextWithoutWordsIsDropped()
    {
        // A log message template whose placeholders were stripped leaves only fragments like "  for  damage.".
        Assert.Null(GameFeedback.Clean("   "));
        Assert.Null(GameFeedback.Clean(null));
        Assert.Equal("Unable to execute command while jumping.", GameFeedback.Clean("  Unable to execute command while jumping.  "));
    }
}
