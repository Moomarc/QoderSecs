using QoderSecs.Core.Messages;

namespace QoderSecs.Tests;

[TestClass]
public sealed class AutoReplyAuditTests
{
    private static MessageLibrary Library(string messages) =>
        MessageLibrary.Load($"<SXML><MessageDictionary>{messages}</MessageDictionary></SXML>");

    private static string Message(
        string name,
        int stream,
        int function,
        string? autoReply = null,
        bool wBit = false) =>
        $"""
         <Message Name="{name}">
             <Stream>{stream}</Stream>
             <Function>{function}</Function>
             {(wBit ? "<WBit>1</WBit>" : string.Empty)}
             {(autoReply is null ? string.Empty : $"<AutoReply>{autoReply}</AutoReply>")}
             <Structure/>
         </Message>
         """;

    private static MessageEntry Entry(MessageLibrary library, string name) =>
        library.Messages.First(entry => entry.Name == name);

    [TestMethod]
    public void TheSampleLibraryIsConsistent()
    {
        var library = MessageLibrary.Load(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-library.sxml")));

        var report = AutoReplyAudit.Run(library);

        Assert.IsEmpty(report.Issues);
        // 这一条特意写成断言：RecipeBody 有 W 位但没配 AutoReply，不算不一致。
        // W 位说的是发出去要不要等回复，AutoReply 管的是收到时回不回。
        Assert.IsTrue(Entry(library, "RecipeBody").WBit);
        Assert.IsNull(Entry(library, "RecipeBody").AutoReply);
    }

    [TestMethod]
    public void WBitWithoutAutoReplyIsNotAnIssue()
    {
        var library = Library(Message("Req", 1, 1, wBit: true));

        Assert.IsEmpty(AutoReplyAudit.Run(library).Issues);
    }

    [TestMethod]
    public void ATargetThatIsNotInTheLibraryIsAnError()
    {
        var library = Library(Message("Req", 1, 1, autoReply: "Missing"));

        var report = AutoReplyAudit.Run(library);

        Assert.HasCount(1, report.Issues);
        Assert.AreEqual(1, report.ErrorCount);
        var issue = report.Issues[0];
        Assert.AreEqual(AutoReplyIssueSeverity.Error, issue.Severity);
        Assert.AreEqual(AutoReplyIssueKind.BrokenTarget, issue.Kind);
        Assert.AreEqual("S1F1", issue.Message.Signature);
        StringAssert.Contains(issue.Text, "Missing");
        StringAssert.Contains(issue.Text, "不会回复");
    }

    [TestMethod]
    public void PointingAtItselfIsAnError()
    {
        var library = Library(Message("Loop", 1, 1, autoReply: "Loop"));

        var report = AutoReplyAudit.Run(library);

        Assert.HasCount(1, report.Issues);
        Assert.AreEqual(AutoReplyIssueKind.SelfReference, report.Issues[0].Kind);
        Assert.AreEqual(AutoReplyIssueSeverity.Error, report.Issues[0].Severity);
    }

    [TestMethod]
    public void OnlyTheFirstAutoReplyOnTheSameSxFyTakesEffect()
    {
        var library = Library(
            Message("Start", 2, 41, autoReply: "StartAck")
            + Message("Stop", 2, 41, autoReply: "StopAck")
            + Message("StartAck", 2, 42)
            + Message("StopAck", 2, 42));

        var report = AutoReplyAudit.Run(library);

        Assert.HasCount(1, report.Issues);
        var issue = report.Issues[0];
        Assert.AreEqual(AutoReplyIssueKind.Shadowed, issue.Kind);
        Assert.AreEqual(AutoReplyIssueSeverity.Warning, issue.Severity);
        Assert.AreEqual("Stop", issue.Message.Name, "报的是被遮住的那条，不是生效的那条");
        StringAssert.Contains(issue.Text, "Start");
    }

    [TestMethod]
    public void ATargetNameSharedByTwoMessagesIsAmbiguous()
    {
        var library = Library(
            Message("Req", 1, 1, autoReply: "Ack")
            + Message("Ack", 1, 2)
            + Message("Ack", 2, 2));

        var report = AutoReplyAudit.Run(library);

        Assert.HasCount(1, report.Issues);
        var issue = report.Issues[0];
        Assert.AreEqual(AutoReplyIssueKind.DuplicateTargetName, issue.Kind);
        Assert.AreEqual(AutoReplyIssueSeverity.Warning, issue.Severity);
        Assert.AreEqual("Req", issue.Message.Name);
        StringAssert.Contains(issue.Text, "S1F2");
        StringAssert.Contains(issue.Text, "S2F2");
    }

    [TestMethod]
    public void TargetNamesAreTrimmedTheSameWayTheResolverDoes()
    {
        var library = Library(
            Message("Req", 1, 1, autoReply: "  Ack  ")
            + Message("Ack", 1, 2));

        Assert.IsEmpty(AutoReplyAudit.Run(library).Issues, "名字两边的空格不改变指向，不应报断指");
    }

    [TestMethod]
    public void ATargetThatIsNotTheNextFunctionIsAllowed()
    {
        // 程序不按 SxF(y+1) 推导回复，检查也不替使用者规定目标必须是下一功能号。
        var library = Library(
            Message("Req", 1, 1, autoReply: "Elsewhere")
            + Message("Elsewhere", 5, 9));

        Assert.IsEmpty(AutoReplyAudit.Run(library).Issues);
    }

    [TestMethod]
    public void ErrorsComeBeforeWarnings()
    {
        var library = Library(
            Message("A", 1, 1, autoReply: "Missing")
            + Message("B", 1, 1, autoReply: "Ack")
            + Message("C", 1, 1)
            + Message("Ack", 1, 2));

        var report = AutoReplyAudit.Run(library);

        Assert.HasCount(2, report.Issues);
        Assert.AreEqual(AutoReplyIssueKind.BrokenTarget, report.Issues[0].Kind);
        Assert.AreEqual(AutoReplyIssueKind.Shadowed, report.Issues[1].Kind);
        Assert.AreEqual(1, report.ErrorCount);
        Assert.AreEqual(1, report.WarningCount);
    }
}
