using Xunit;
using XzBotCs.Helpers;

namespace XzBotCs.Tests
{
    public class TelegramEscaperTests
    {
        [Fact]
        public void EscapeMarkdownV2_EmptyString_ReturnsEmpty()
        {
            Assert.Equal("", TelegramEscaper.EscapeMarkdownV2(""));
        }

        [Fact]
        public void EscapeMarkdownV2_PlainText_Unchanged()
        {
            Assert.Equal("hello world", TelegramEscaper.EscapeMarkdownV2("hello world"));
        }

        [Theory]
        [InlineData("_", "\\_")]
        [InlineData("*", "\\*")]
        [InlineData("[", "\\[")]
        [InlineData("]", "\\]")]
        [InlineData("(", "\\(")]
        [InlineData(")", "\\)")]
        [InlineData("~", "\\~")]
        [InlineData(">", "\\>")]
        [InlineData("#", "\\#")]
        [InlineData("+", "\\+")]
        [InlineData("-", "\\-")]
        [InlineData("=", "\\=")]
        [InlineData("|", "\\|")]
        [InlineData("{", "\\{")]
        [InlineData("}", "\\}")]
        [InlineData(".", "\\.")]
        [InlineData("!", "\\!")]
        public void EscapeMarkdownV2_SpecialChars_Escaped(string input, string expected)
        {
            Assert.Equal(expected, TelegramEscaper.EscapeMarkdownV2(input));
        }

        [Fact]
        public void EscapeMarkdownV2_Backslash_EscapedFirst()
        {
            // Обратный слэш должен экранироваться, иначе получится битая последовательность
            Assert.Equal("\\\\", TelegramEscaper.EscapeMarkdownV2("\\"));
        }

        [Fact]
        public void EscapeMarkdownV2_MixedText_AllEscaped()
        {
            string result = TelegramEscaper.EscapeMarkdownV2("Hello_World!");
            Assert.Equal("Hello\\_World\\!", result);
        }
    }
}
