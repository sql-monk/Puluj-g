using Puluj.Analytics.Text;

namespace Puluj.Analytics.Tests;

public class TextNormalizerTests
{
    [Fact]
    public void Strips_links_mentions_emoji_and_case()
    {
        var text = "🏍 Реактивний БпЛА курсом на Ковель! https://t.me/kpszsu/1 @kpszsu #тривога\n➡ Підписатися\n\n";
        Assert.Equal("реактивний бпла курсом на ковель", TextNormalizer.Canonical(text));
    }

    [Fact]
    public void Same_content_different_decoration_is_equal()
    {
        var a = TextNormalizer.Canonical("Шахеди на Чернігівщині, курсом на Київщину.");
        var b = TextNormalizer.Canonical("🛵 ШАХЕДИ на Чернігівщині — курсом на Київщину 🔴");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Empty_and_null_are_empty()
    {
        Assert.Equal("", TextNormalizer.Canonical(null));
        Assert.Equal("", TextNormalizer.Canonical("   \n 🔴 "));
    }

    [Fact]
    public void Keeps_subscribe_sentence_that_is_content()
    {
        // A long line is content even if it mentions subscribing.
        var line = "підписатися на канал можна за посиланням нижче щоб отримувати сповіщення про тривоги";
        Assert.Equal(line, TextNormalizer.Canonical(line));
    }
}
public class ShinglerTests
{
    [Fact]
    public void Identical_texts_have_jaccard_one()
    {
        var a = Shingler.Shingles("реактивний бпла курсом на ковель");
        var b = Shingler.Shingles("реактивний бпла курсом на ковель");
        Assert.Equal(1, Shingler.Jaccard(a, b));
        Assert.Equal(1, Shingler.Containment(a, b));
    }

    [Fact]
    public void Small_edit_keeps_high_similarity_and_different_texts_low()
    {
        var a = Shingler.Shingles("групи ударних бпла на сумщині в районі боромля лебедин західним курсом");
        var b = Shingler.Shingles("групи ударних бпла на сумщині в районі боромля лебедин та недригайлів західним курсом");
        var c = Shingler.Shingles("пуски керованих авіаційних бомб ворожою тактичною авіацією на південь харківщини");
        Assert.InRange(Shingler.Jaccard(a, b), 0.6, 0.95);
        Assert.True(Shingler.Containment(a, b) > 0.9);
        Assert.True(Shingler.Jaccard(a, c) < 0.1);
    }

    [Fact]
    public void Short_text_gets_one_shingle()
    {
        Assert.Single(Shingler.Shingles("бпл"));
        Assert.Empty(Shingler.Shingles(""));
    }
}
