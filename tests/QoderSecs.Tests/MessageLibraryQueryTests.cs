using QoderSecs.Core.Messages;

namespace QoderSecs.Tests;

[TestClass]
public sealed class MessageLibraryQueryTests
{
    private const string UnsortedXml = """
        <SXML>
            <MessageDictionary>
                <Message Name="Third"><Stream>7</Stream><Function>25</Function><Structure/></Message>
                <Message Name="First"><Stream>1</Stream><Function>13</Function><Structure/></Message>
                <Message Name="SecondA"><Stream>2</Stream><Function>41</Function><Structure/></Message>
                <Message Name="SecondB"><Stream>2</Stream><Function>41</Function><Structure/></Message>
            </MessageDictionary>
        </SXML>
        """;

    private static MessageLibrary Unsorted() => MessageLibrary.Load(UnsortedXml);

    private static MessageLibrary Sample() => MessageLibrary.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-library.sxml")));

    private static string[] Names(IReadOnlyList<MessageEntry> entries) => [.. entries.Select(entry => entry.Name)];

    [TestMethod]
    public void Query_SortsByStreamThenFunctionAndKeepsDocumentOrderOnTies()
    {
        var result = Unsorted().Query(null);

        CollectionAssert.AreEqual(new[] { "First", "SecondA", "SecondB", "Third" }, Names(result));
    }

    [TestMethod]
    public void Query_BlankSearchReturnsEverything()
    {
        var library = Unsorted();

        Assert.HasCount(4, library.Query(null));
        Assert.HasCount(4, library.Query(string.Empty));
        Assert.HasCount(4, library.Query("   "));
    }

    [TestMethod]
    public void Query_AcceptsEverySignatureSpelling()
    {
        var library = Unsorted();

        foreach (var spelling in new[] { "S2F41", "s2f41", "2,41", "2 41", "S2F41 ", " 2f41 " })
        {
            CollectionAssert.AreEqual(new[] { "SecondA", "SecondB" }, Names(library.Query(spelling)), spelling);
        }
    }

    [TestMethod]
    public void Query_MatchesSignatureWithoutLetterPrefix()
    {
        CollectionAssert.AreEqual(new[] { "First" }, Names(Unsorted().Query("1 13")));
    }

    [TestMethod]
    public void Query_SearchesNameIgnoringCase()
    {
        var library = Sample();

        CollectionAssert.AreEqual(new[] { "RecipeBody" }, Names(library.Query("recipe")));
        CollectionAssert.AreEqual(new[] { "S1F2_OnlineData" }, Names(library.Query("ONLINEDATA")));
    }

    [TestMethod]
    public void Query_ReturnsEmptyWhenNothingMatches()
    {
        var library = Sample();

        Assert.IsEmpty(library.Query("S9F9"));
        Assert.IsEmpty(library.Query("不存在的名字"));
        Assert.IsEmpty(library.Query("S300F1"));
    }

    [TestMethod]
    public void Query_OnClearedLibraryReturnsNothing()
    {
        var library = Sample();

        library.Clear();

        Assert.IsEmpty(library.Query(null));
        Assert.IsEmpty(library.Query("S1F1"));
    }
}
