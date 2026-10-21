using QoderSecs.Core.Messages;

namespace QoderSecs.Tests;

/// <summary>
/// 自动回复那一栏光看名字没用，要看得见目标消息的 S/F。
/// 下拉框只显示 Core 算好的这份 Display，所以文案在这里测，不在界面里测。
/// v1 只支持指向单条消息，不再按「像不像 F+1」加标注。
/// </summary>
[TestClass]
public sealed class AutoReplyOptionTests
{
    private const string LibraryXml = """
        <SXML>
            <MessageDictionary>
                <Message Name="查一下"><Stream>12</Stream><Function>1</Function><WBit>1</WBit><AutoReply>上传图</AutoReply><Structure/></Message>
                <Message Name="上传图"><Stream>12</Stream><Function>2</Function><Structure/></Message>
                <Message Name="别的流"><Stream>1</Stream><Function>2</Function><Structure/></Message>
                <Message Name=""><Stream>12</Stream><Function>3</Function><Structure/></Message>
                <Message Name="就是这条"><Stream>12</Stream><Function>3</Function><Structure/></Message>
            </MessageDictionary>
        </SXML>
        """;

    private static MessageEntry Current(MessageLibrary library) =>
        library.Messages.First(entry => entry.Name == "查一下");

    [TestMethod]
    public void EveryCandidateLeadsWithItsOwnSignature()
    {
        var library = MessageLibrary.Load(LibraryXml);
        var options = AutoReplyOptions.Build(library, Current(library));

        Assert.AreEqual(AutoReplyOptions.None, options[0], "第一项永远是「不自动回复」");

        var reply = options.Single(option => option.TargetName == "上传图");
        Assert.AreEqual("S12F2 · 上传图", reply.Display);
        Assert.IsFalse(
            options.Any(option => option.Display.Contains("不配对", StringComparison.Ordinal)),
            "候选栏不再标注像不像本条回复，只靠下拉框的选中高亮表示当前值。");
    }

    [TestMethod]
    public void ACandidateThatIsNotTheReplyShapeIsStillPlainSignatureAndName()
    {
        var library = MessageLibrary.Load(LibraryXml);
        var other = AutoReplyOptions.Build(library, Current(library))
            .Single(option => option.TargetName == "别的流");

        Assert.AreEqual("S1F2 · 别的流", other.Display);
        // 候选只是给人看的，目标照旧存名字，发不发送仍按名字精确取。
        Assert.AreEqual("别的流", other.TargetName);
    }

    [TestMethod]
    public void SelfAndUnnamedMessagesAreNotOffered()
    {
        var library = MessageLibrary.Load(LibraryXml);
        var options = AutoReplyOptions.Build(library, Current(library));

        // 除了「不自动回复」，只剩 别的流 / 上传图 / 就是这条：本条自己和那条没名字的不进来。
        // 排序按 Query 的 SxF 来，所以 S1F2 在 S12 之前。
        CollectionAssert.AreEqual(
            new[]
            {
                AutoReplyOptions.NoneText,
                "S1F2 · 别的流",
                "S12F2 · 上传图",
                "S12F3 · 就是这条"
            },
            options.Select(option => option.Display).ToArray());
    }

    /// <summary>
    /// 目标被改名，指着旧名的引用直接置空，
    /// 所以候选栏里不会再冒出那条旧名字（原来这里会补一项「（库里没有这条）」）。
    /// </summary>
    [TestMethod]
    public void ATargetThatHasLeftTheLibraryIsNotListed()
    {
        var library = MessageLibrary.Load(LibraryXml);
        library.SetName(library.Messages.First(entry => entry.Name == "上传图"), "上传图 2");

        var referrer = library.Messages.First(entry => entry.Name == "查一下");
        Assert.IsNull(referrer.AutoReply, "目标改名之后，指着旧名的引用要置空");

        var options = AutoReplyOptions.Build(library, referrer);
        Assert.IsNull(options.FirstOrDefault(option => option.TargetName == "上传图"), "旧名字不该再出现在候选栏里");
        Assert.IsNotNull(options.FirstOrDefault(option => option.TargetName == "上传图 2"), "新名字照常可选");
        Assert.AreEqual(AutoReplyOptions.None, AutoReplyOptions.Find(options, referrer.AutoReply));
    }

    /// <summary>
    /// 手改文件留下来的失效引用同样不列：运行时 <see cref="AutoReplyResolver.FindTarget"/> 也取不到目标，
    /// 显示成「不自动回复」和实际会不会回是一致的。
    /// </summary>
    [TestMethod]
    public void AStaleValueThatIsNotInTheLibraryIsNotListedEither()
    {
        var library = MessageLibrary.Load(LibraryXml);
        var stale = library.SetAutoReply(Current(library), "库里没有的");

        var options = AutoReplyOptions.Build(library, stale);

        Assert.IsNull(options.FirstOrDefault(option => option.TargetName == "库里没有的"));
        Assert.AreEqual(AutoReplyOptions.None, AutoReplyOptions.Find(options, stale.AutoReply), "找不到就落回「不自动回复」");
    }

    [TestMethod]
    public void FindPicksTheStoredNameBackOutOfTheCurrentList()
    {
        var library = MessageLibrary.Load(LibraryXml);
        var options = AutoReplyOptions.Build(library, Current(library));

        Assert.AreEqual(AutoReplyOptions.None, AutoReplyOptions.Find(options, null));
        Assert.AreEqual("上传图", AutoReplyOptions.Find(options, " 上传图 ").TargetName, "名字两边的空格不算换了一项");
        Assert.AreEqual(AutoReplyOptions.None, AutoReplyOptions.Find(options, "库里没有的"));
    }
}
