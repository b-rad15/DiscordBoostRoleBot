using Xunit;
using System.Text.RegularExpressions;

namespace DiscordBotTests
{
    public class UnitTest1x
    {
        [Theory]
        [InlineData("<:Tiffany:884668053357465651>", true, "Tiffany:884668053357465651", "Tiffany", "884668053357465651", false)]
        [InlineData("<a:aliaKMS:613552973452410890>", true, "aliaKMS:613552973452410890", "aliaKMS", "613552973452410890", true)]
        [InlineData("<Tiffany:884668053357465651>", false, null, null, null, false)]
        [InlineData("<:Tiffany884668053357465651>", false, null, null, null, false)]
        [InlineData("<::884668053357465651>", false, null, null, null, false)]
        public void TestEmoteRegex(string emoteString, bool isEmote, string emoteWithId, string emoteName, string emoteId, bool isAnimated)
        {
            Match match =
                DiscordBoostRoleBot.AddReactionsToMediaArchiveMessageResponder.EmoteWithRequiredIdRegex.Match(
                    emoteString);
            Assert.Equal(isEmote, match.Success);
            if (!isEmote) return;
            Assert.Equal(emoteName, match.Groups["name"].Value);
            Assert.Equal(emoteId, match.Groups["id"].Value);
            Assert.Equal(isAnimated, match.Groups.ContainsKey("animated") && !string.IsNullOrWhiteSpace(match.Groups["animated"].Value));

        }
    }
}
