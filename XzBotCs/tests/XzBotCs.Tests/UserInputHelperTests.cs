using System;
using Xunit;
using XzBotCs.Helpers;

namespace XzBotCs.Tests
{
    public class UserInputHelperTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

        // --- TryParseFlag ---

        [Fact]
        public void TryParseFlag_FlagAtEnd_RemovedAndTrue()
        {
            string query = "кот --random";
            bool result = UserInputHelper.TryParseFlag(ref query, "--random", Timeout);

            Assert.True(result);
            Assert.Equal("кот", query);
        }

        [Fact]
        public void TryParseFlag_FlagAtStart_RemovedAndTrue()
        {
            string query = "--gif dance";
            bool result = UserInputHelper.TryParseFlag(ref query, "--gif", Timeout);

            Assert.True(result);
            Assert.Equal("dance", query);
        }

        [Fact]
        public void TryParseFlag_FlagInMiddle_RemovedAndTrue()
        {
            string query = "кошка --gif спит";
            bool result = UserInputHelper.TryParseFlag(ref query, "--gif", Timeout);

            Assert.True(result);
            Assert.Equal("кошка спит", query);
        }

        [Fact]
        public void TryParseFlag_NoFlag_ReturnsFalseAndKeepsQuery()
        {
            string query = "обычный запрос";
            bool result = UserInputHelper.TryParseFlag(ref query, "--random", Timeout);

            Assert.False(result);
            Assert.Equal("обычный запрос", query);
        }

        [Fact]
        public void TryParseFlag_SubstringOfWord_NotMatched()
        {
            // "--random" внутри слова "super--randomx" не должно матчиться как отдельное слово
            string query = "abc--randomxyz";
            bool result = UserInputHelper.TryParseFlag(ref query, "--random", Timeout);

            Assert.False(result);
        }

        [Fact]
        public void TryParseFlag_CaseInsensitive()
        {
            string query = "кот --RANDOM";
            bool result = UserInputHelper.TryParseFlag(ref query, "--random", Timeout);

            Assert.True(result);
            Assert.Equal("кот", query);
        }

        // --- ExtractUserId ---

        [Theory]
        [InlineData("1741079861", 1741079861L)]
        [InlineData("  1741079861  ", 1741079861L)]
        [InlineData("tg://user?id=1741079861", 1741079861L)]
        [InlineData("tg://user?id=1741079861&foo=bar", 1741079861L)]
        public void ExtractUserId_ValidInput_ReturnsId(string input, long expected)
        {
            Assert.Equal(expected, UserInputHelper.ExtractUserId(input));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-number")]
        [InlineData("tg://user?id=abc")]
        public void ExtractUserId_InvalidInput_ReturnsZero(string input)
        {
            Assert.Equal(0, UserInputHelper.ExtractUserId(input));
        }

        // --- NormalizeProxyBaseUrl ---

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NormalizeProxyBaseUrl_NullOrEmpty_ReturnsNull(string? input)
        {
            Assert.Null(UserInputHelper.NormalizeProxyBaseUrl(input));
        }

        [Fact]
        public void NormalizeProxyBaseUrl_AlreadyHasQuery_Unchanged()
        {
            string? result = UserInputHelper.NormalizeProxyBaseUrl("https://example.com/img?u=");
            Assert.Equal("https://example.com/img?u=", result);
        }

        [Fact]
        public void NormalizeProxyBaseUrl_WithTrailingSlash_AppendsPath()
        {
            string? result = UserInputHelper.NormalizeProxyBaseUrl("https://example.com/");
            Assert.Equal("https://example.com/img?u=", result);
        }

        [Fact]
        public void NormalizeProxyBaseUrl_WithoutTrailingSlash_AppendsPath()
        {
            string? result = UserInputHelper.NormalizeProxyBaseUrl("https://example.com");
            Assert.Equal("https://example.com/img?u=", result);
        }

        [Fact]
        public void NormalizeProxyBaseUrl_TrimsWhitespace()
        {
            string? result = UserInputHelper.NormalizeProxyBaseUrl("  https://example.com  ");
            Assert.Equal("https://example.com/img?u=", result);
        }
    }
}
