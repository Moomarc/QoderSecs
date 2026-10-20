using System.Text;
using System.Xml;
using System.Xml.Linq;
using QoderSecs.Core.Messages;

namespace QoderSecs.Tests;

[TestClass]
public sealed class LibraryFileTests
{
    private string _testDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    private string PathOf(string name) => Path.Combine(_testDirectory, name);

    private static string FixtureText() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-library.sxml"));

    private static MessageLibrary Fixture() => MessageLibrary.Load(FixtureText());

    /// <summary>导出只承诺 XML 等价，不承诺换行和空元素写法，所以比对前先规范化。</summary>
    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace(" />", "/>").TrimEnd('\r', '\n');

    [TestMethod]
    public async Task SaveThenLoadRoundTripsEveryMessage()
    {
        var path = PathOf("round-trip.sxml");
        var original = Fixture();

        await LibraryFile.SaveAsync(original, path);
        var loaded = await LibraryFile.LoadAsync(path);

        Assert.HasCount(original.Messages.Count, loaded.Messages);
        for (var i = 0; i < original.Messages.Count; i++)
        {
            Assert.AreEqual(original.Messages[i].Name, loaded.Messages[i].Name);
            Assert.AreEqual(original.Messages[i].Stream, loaded.Messages[i].Stream);
            Assert.AreEqual(original.Messages[i].Function, loaded.Messages[i].Function);
            Assert.AreEqual(original.Messages[i].WBit, loaded.Messages[i].WBit);
            Assert.AreEqual(original.Messages[i].AutoReply, loaded.Messages[i].AutoReply);
            Assert.AreEqual(
                original.Messages[i].Structure?.ToString(SaveOptions.None),
                loaded.Messages[i].Structure?.ToString(SaveOptions.None));
        }

        Assert.AreEqual(0, Directory.EnumerateFiles(_testDirectory, "*.tmp").Count(), "临时文件必须被替换掉");
    }

    /// <summary>导出默认覆盖回导入的那个文件，替换失败时不能把原来那份也毁掉。</summary>
    [TestMethod]
    public async Task FailedOverwriteLeavesThePreviousLibraryIntact()
    {
        var path = PathOf("locked.sxml");
        await LibraryFile.SaveAsync(Fixture(), path);
        var before = await File.ReadAllTextAsync(path);

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
                () => LibraryFile.SaveAsync(Fixture(), path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.AreEqual(before, await File.ReadAllTextAsync(path), "原文件必须原样还在");
        Assert.AreEqual(0, Directory.EnumerateFiles(_testDirectory, "*.tmp").Count(), "临时文件要清掉");
    }

    [TestMethod]
    public async Task SaveWritesUtf8WithoutBomAndWithoutAnXmlDeclaration()
    {
        var path = PathOf("encoding.sxml");

        await LibraryFile.SaveAsync(Fixture(), path);
        var bytes = await File.ReadAllBytesAsync(path);

        Assert.AreEqual("<SXML>", Encoding.UTF8.GetString(bytes, 0, 6), "文件开头不该有 BOM 也不该有 <?xml?>");
    }

    [TestMethod]
    public async Task SaveKeepsNonAsciiTextIntact()
    {
        var path = PathOf("unicode.sxml");
        var library = Fixture();
        var added = StructureEditor.AddChild(library.Messages[0].Structure!, SecsItemType.J)!;
        added.Element("Value")!.Value = "日本語テスト";

        await LibraryFile.SaveAsync(library, path);
        var loaded = await LibraryFile.LoadAsync(path);

        StringAssert.Contains(await File.ReadAllTextAsync(path), "注释必须原样保留");
        Assert.AreEqual(
            "日本語テスト",
            StructureNode.FromStructure(loaded.Messages[0].Structure)!.ValueText);
    }

    [TestMethod]
    public async Task SaveOfAnUneditedLibraryMatchesTheSourceFile()
    {
        var path = PathOf("faithful.sxml");
        var source = FixtureText();

        await LibraryFile.SaveAsync(Fixture(), path);

        Assert.AreEqual(Normalize(source), Normalize(await File.ReadAllTextAsync(path)));
    }

    [TestMethod]
    public async Task SaveKeepsEditsFromBeingLostOnTheNextLoad()
    {
        var path = PathOf("edited.sxml");
        var library = Fixture();
        library.NewMessage(10, 3, "S10F3");
        StructureEditor.AddChild(library.Messages[0].Structure!, SecsItemType.U4);

        await LibraryFile.SaveAsync(library, path);
        var loaded = await LibraryFile.LoadAsync(path);

        Assert.HasCount(4, loaded.Messages);
        Assert.AreEqual("S10F3", loaded.Messages[3].Name);
        Assert.AreEqual(
            "U4",
            StructureNode.FromStructure(loaded.Messages[0].Structure)!.Element.Name.LocalName);
    }

    [TestMethod]
    public async Task LoadRejectsAFileThatIsNotSxml()
    {
        var notXml = PathOf("broken.sxml");
        await File.WriteAllTextAsync(notXml, "这不是 XML");

        var wrongRoot = PathOf("wrong-root.sxml");
        await File.WriteAllTextAsync(wrongRoot, "<NotSXML><MessageDictionary /></NotSXML>");

        await Assert.ThrowsExactlyAsync<XmlException>(() => LibraryFile.LoadAsync(notXml));
        var exception = await Assert.ThrowsExactlyAsync<FormatException>(
            () => LibraryFile.LoadAsync(wrongRoot));
        StringAssert.Contains(exception.Message, "SXML");
    }

    [TestMethod]
    public async Task LoadRejectsAMessageWithoutStreamOrFunction()
    {
        var path = PathOf("incomplete.sxml");
        await File.WriteAllTextAsync(path, """
            <SXML>
                <DataDictionary />
                <MessageDictionary>
                    <Message Name="残缺"><Stream>1</Stream></Message>
                </MessageDictionary>
            </SXML>
            """);

        var exception = await Assert.ThrowsExactlyAsync<FormatException>(() => LibraryFile.LoadAsync(path));
        StringAssert.Contains(exception.Message, "残缺");
    }

    [TestMethod]
    public void HasLibraryExtension_AcceptsSxmlAndXmlOnly()
    {
        Assert.IsTrue(LibraryFile.HasLibraryExtension(@"C:\库\common-commands.sxml"));
        Assert.IsTrue(LibraryFile.HasLibraryExtension(@"C:\库\SAMPLE.XML"));

        // 拖进来的还可能是文件夹、没后缀的文件和别的文本：一律不收，不去读内容。
        Assert.IsFalse(LibraryFile.HasLibraryExtension("notes.txt"));
        Assert.IsFalse(LibraryFile.HasLibraryExtension("notes.xml.txt"));
        Assert.IsFalse(LibraryFile.HasLibraryExtension(@"C:\库"));
        Assert.IsFalse(LibraryFile.HasLibraryExtension("README"));
    }
}
