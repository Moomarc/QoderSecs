using System.Xml.Linq;
using QoderSecs.Core.Messages;

namespace QoderSecs.Tests;

[TestClass]
public sealed class StructureNodeTests
{
    private static StructureNode Node(string xml) => new(XElement.Parse(xml));

    [TestMethod]
    public void TryParse_RecognizesEverySecsTwoTypeAndRejectsUnknown()
    {
        Assert.IsTrue(SecsItemTypes.TryParse("U4", out var u4));
        Assert.AreEqual(SecsItemType.U4, u4);
        Assert.IsTrue(SecsItemTypes.TryParse("Boolean", out _));
        Assert.IsTrue(SecsItemTypes.TryParse("J", out var jis8));
        Assert.AreEqual(SecsItemType.J, jis8);

        foreach (var name in new[] { "L", "A", "B", "Boolean", "J", "I1", "I2", "I4", "I8", "U1", "U2", "U4", "U8", "F4", "F8" })
        {
            Assert.IsTrue(SecsItemTypes.TryParse(name, out _), name);
        }

        Assert.IsFalse(SecsItemTypes.TryParse("U4AUTO", out _));
        Assert.IsFalse(SecsItemTypes.TryParse("Value", out _));
        Assert.IsFalse(SecsItemTypes.TryParse(string.Empty, out _));
    }

    [TestMethod]
    public void TryParse_AcceptsAnyCaseAndReportsTheCanonicalElementName()
    {
        Assert.IsTrue(SecsItemTypes.TryParse("l", out var list));
        Assert.AreEqual(SecsItemType.List, list);
        Assert.IsTrue(SecsItemTypes.TryGetElementName("boolean", out var elementName));
        Assert.AreEqual("Boolean", elementName);
    }

    [TestMethod]
    public void Jis8BehavesLikeAscii()
    {
        var node = Node("<J><Value>何でも</Value></J>");

        Assert.IsTrue(node.HasKnownType);
        Assert.IsTrue(node.IsEditable);
        Assert.IsNull(node.ValidationError);
    }

    [TestMethod]
    public void UnknownElementIsNotEditable()
    {
        var unknown = Node("<U4AUTO><Value>1</Value></U4AUTO>");

        Assert.IsFalse(unknown.HasKnownType);
        Assert.IsFalse(unknown.IsEditable);
        StringAssert.Contains(unknown.ValidationError!, "未知元素类型");
    }

    [TestMethod]
    public void Children_ExcludeValueAndMaps()
    {
        var list = Node("""
            <L>
                <U4 Name="Id"><Value>1</Value><Maps Type="64"></Maps></U4>
                <A Name="Text"><Value>x</Value></A>
            </L>
            """);

        Assert.HasCount(2, list.Children);
        Assert.AreEqual("Id", list.Children[0].Name);
        Assert.IsEmpty(list.Children[0].Children);
        Assert.HasCount(1, list.Children[0].Items);
    }

    [TestMethod]
    public void Numeric_ValuesAreRangeChecked()
    {
        Assert.IsNull(Node("<I1><Value>127</Value></I1>").ValidationError);
        StringAssert.Contains(Node("<I1><Value>128</Value></I1>").ValidationError!, "I1");
        StringAssert.Contains(Node("<U1><Value>-1</Value></U1>").ValidationError!, "U1");
        Assert.IsNull(Node("<U4><Value>4294967295</Value></U4>").ValidationError);
        Assert.IsNull(Node("<F4><Value>1.5</Value></F4>").ValidationError);
        StringAssert.Contains(Node("<F4><Value>abc</Value></F4>").ValidationError!, "F4");
    }

    [TestMethod]
    public void Numeric_EmptyPlaceholderIsValidAndReadsAsZero()
    {
        foreach (var xml in new[] { "<U2><Value></Value></U2>", "<U4><Value></Value></U4>", "<F8><Value>  </Value></F8>" })
        {
            var node = Node(xml);
            Assert.IsNull(node.ValidationError, xml);
            Assert.AreEqual("0", node.SingleValue, xml);
        }
    }

    [TestMethod]
    public void SingleValue_KeepsNonNumericTextAsIs()
    {
        Assert.AreEqual("", Node("<A><Value></Value></A>").SingleValue);
        Assert.AreEqual("42", Node("<U4><Value>42</Value></U4>").SingleValue);
        Assert.AreEqual("", Node("<L></L>").SingleValue);

        // 纯空白只有 PreserveWhitespace 才留得住，MessageLibrary.Load 用的正是这个选项。
        var blank = new StructureNode(XElement.Parse("<A><Value> </Value></A>", LoadOptions.PreserveWhitespace));
        Assert.AreEqual(" ", blank.SingleValue);
    }

    [TestMethod]
    public void Binary_RequiresHexPairs()
    {
        Assert.IsNull(Node("<B><Value>00FF</Value></B>").ValidationError);
        Assert.IsNull(Node("<B><Value></Value></B>").ValidationError);
        StringAssert.Contains(Node("<B><Value>0FF</Value></B>").ValidationError!, "十六进制");
        StringAssert.Contains(Node("<B><Value>ZZ</Value></B>").ValidationError!, "十六进制");
    }

    [TestMethod]
    public void Binary_AcceptsSecsExpressSpaceSeparatedBytes()
    {
        Assert.IsNull(Node("<B><Value>20 20 C3 50 00 00 00 01</Value></B>").ValidationError);
        Assert.IsNull(Node("<B><Value>00 FF\t01\n</Value></B>").ValidationError);
        Assert.AreEqual("2020C350", StructureNode.NormalizeBinary("20 20 C3 50"));
        Assert.AreEqual(string.Empty, StructureNode.NormalizeBinary(null));
        StringAssert.Contains(Node("<B><Value>0F F</Value></B>").ValidationError!, "十六进制");
        StringAssert.Contains(Node("<B><Value>ZZ 00</Value></B>").ValidationError!, "十六进制");
    }

    /// <summary>
    /// 旧程序把 B 里的高位字节显示成 -45 这类十进制数。这里只认 -1 到 -127：
    /// 按 8 位二补码换成两位大写十六进制；-128 及以外的值原样留着，让校验报红。
    /// </summary>
    [TestMethod]
    public void Binary_NegativeBytesBecomeTwosComplementHex()
    {
        Assert.AreEqual("D3", StructureNode.ConvertNegativeBytes("-45"));
        Assert.AreEqual("FF", StructureNode.ConvertNegativeBytes("-1"));
        Assert.AreEqual("81", StructureNode.ConvertNegativeBytes("-127"));
        Assert.AreEqual("12 D3 FF", StructureNode.ConvertNegativeBytes("12 -45 -1"));
        Assert.AreEqual("12D3FF", StructureNode.NormalizeBinary("12 -45 -1"));

        Assert.AreEqual("-128", StructureNode.ConvertNegativeBytes("-128"));
        Assert.AreEqual("45", StructureNode.ConvertNegativeBytes("45"));
        Assert.AreEqual("-45x", StructureNode.ConvertNegativeBytes("-45x"), "只有单独成项的负数才转");

        Assert.IsNull(Node("<B><Value>-45</Value></B>").ValidationError);
        Assert.IsNull(Node("<B><Value>12 -45 -1</Value></B>").ValidationError);
        StringAssert.Contains(Node("<B><Value>-128</Value></B>").ValidationError!, "十六进制");
    }

    [TestMethod]
    public void Boolean_AcceptsTrueAndFalseOnly()
    {
        Assert.IsNull(Node("<Boolean><Value>True</Value></Boolean>").ValidationError);
        Assert.IsNull(Node("<Boolean><Value>false</Value></Boolean>").ValidationError);
        StringAssert.Contains(Node("<Boolean><Value>1</Value></Boolean>").ValidationError!, "Boolean");
    }

    /// <summary>数组照 SecsExpress 的写法来：单个 &lt;Value&gt;、值之间空格分隔，可以编辑也能发送。</summary>
    [TestMethod]
    public void NumericArray_IsEditableAndValidatesEachValue()
    {
        var array = Node("<I2><Value>0 0 -1 300</Value></I2>");

        Assert.IsTrue(array.IsArray);
        Assert.IsTrue(array.IsEditable);
        Assert.IsTrue(array.IsValueEditable);
        Assert.HasCount(4, array.Items);
        Assert.IsNull(array.ValidationError);

        StringAssert.Contains(Node("<I1><Value>0 300</Value></I1>").ValidationError!, "第 2 个值");
        StringAssert.Contains(Node("<I1><Value>300 0</Value></I1>").ValidationError!, "第 1 个值");
        StringAssert.Contains(Node("<Boolean><Value>True 1</Value></Boolean>").ValidationError!, "第 2 个值");
    }

    /// <summary>真碰上多个 &lt;Value&gt; 也拼成一段文本，不让它变成界面里没有的第二种形状。</summary>
    [TestMethod]
    public void RepeatedValueElements_FoldIntoOneEditableArray()
    {
        var array = Node("<U4><Value>1</Value><Value>2</Value></U4>");

        Assert.IsTrue(array.IsArray);
        Assert.IsTrue(array.IsValueEditable);
        Assert.AreEqual("1 2", array.ValueText);
        Assert.IsNull(array.ValidationError);
    }

    /// <summary>单值不算数组，界面上不带「N 个值」的标记，也不走逐个校验那条路。</summary>
    [TestMethod]
    public void SingleValue_IsNotAnArray()
    {
        Assert.IsFalse(Node("<U4><Value>1</Value></U4>").IsArray);
        Assert.IsFalse(Node("<U4><Value></Value></U4>").IsArray);
    }

    /// <summary>
    /// A / B 的那一段数字是字节数不是元素个数（wafer map 就是 &lt;A[7482] '图'&gt;），
    /// 拆了会把正文切成两半，所以这几个类型永远不算数组。
    /// </summary>
    [TestMethod]
    public void ByteTextTypes_NeverSplitIntoItems()
    {
        foreach (var xml in new[] { "<A><Value>AA BB CC</Value></A>", "<B><Value>00 FF 01</Value></B>", "<J><Value>x y</Value></J>" })
        {
            Assert.IsFalse(Node(xml).IsArray, xml);
            Assert.IsEmpty(Node(xml).Items, xml);
        }
    }

    [TestMethod]
    public void List_RejectsValueAndAsciiAcceptsAnything()
    {
        StringAssert.Contains(Node("<L><Value>1</Value></L>").ValidationError!, "L 不能带");
        Assert.IsNull(Node("<L></L>").ValidationError);
        Assert.IsNull(Node("<A><Value>line1\nline2\n</Value></A>").ValidationError);
        Assert.IsNull(Node("<A><Value></Value></A>").ValidationError);
    }

    [TestMethod]
    public void Fixture_StructureTreeIsNavigable()
    {
        var library = MessageLibrary.Load(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-library.sxml")));
        var root = StructureNode.FromStructure(library.Messages[2].Structure)!;

        Assert.AreEqual(SecsItemType.List, root.ItemType);
        Assert.HasCount(5, root.Children);
        Assert.AreEqual(SecsItemType.A, root.Children[0].ItemType);
        Assert.AreEqual("PPID", root.Children[0].Name);
        Assert.AreEqual(SecsItemType.B, root.Children[2].ItemType);
        Assert.AreEqual(SecsItemType.Boolean, root.Children[3].ItemType);
        Assert.AreEqual(SecsItemType.U4, root.Children[4].ItemType);
        Assert.IsTrue(root.Children.All(child => child.ValidationError is null));
        Assert.IsNull(StructureNode.FromStructure(library.Messages[0].Structure));
    }
}
