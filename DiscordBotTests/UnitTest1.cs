using Xunit;
using System;

namespace DiscordBotTests
{
    public class UnitTest1x
    {
        [Theory]
        [InlineData("<:Tiffany:884668053357465651>", true, "Tiffany:884668053357465651", "Tiffany", "884668053357465651", false)]
        public void TestEmoteRegex(string emoteString, bool isEmote, string emoteWithId, string emoteName, string emoteId, bool isAnimated)
        {
            var match =
                DiscordBoostRoleBot.AddReactionsToMediaArchiveMessageResponder.ReactWithoutRequiredIdRegex.Match(
                    emoteString);
            Assert.Equal(match.Success, isEmote);
            Assert.Equal(match.Groups["emoteWithId"].Value, emoteWithId);
            Assert.Equal(match.Groups["name"].Value, emoteName);
            Assert.Equal(match.Groups["id"].Value, emoteId);
            Assert.Equal(match.Groups.ContainsKey("animated") && !string.IsNullOrWhiteSpace(match.Groups["animated"].Value), isAnimated);

        }
    }
}