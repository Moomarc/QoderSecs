using System.Text;

namespace QoderSecs.Core.Messages;

/// <summary>
/// 消息库的磁盘读写。编码规则照 SecsExpress 自己的 sxml：UTF-8 不带 BOM，
/// 不写 XML 声明（Export 返回的字符串本来就没有），换行交给文件里已有的空白节点决定。
/// </summary>
public static class LibraryFile
{
    private static readonly UTF8Encoding NoBom = new(false);

    /// <summary>
    /// 消息库文件的后缀口径，只认 SecsExpress 的 .sxml 和通用的 .xml。
    /// 拖放的悬停判定和松手后的导入判定必须走这一个方法，各写一份就会出现
    /// 「光标显示能松手、松手了说不是库文件」那类断裂。
    /// </summary>
    public static bool HasLibraryExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".sxml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<MessageLibrary> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var xml = await File.ReadAllTextAsync(path, cancellationToken);
        return MessageLibrary.Load(xml);
    }

    /// <summary>
    /// 导出是消息库唯一的持久化手段，而且界面上默认就是覆盖回导入的那个文件：
    /// 直接往目标上写，写一半崩了或者磁盘满了，原来的库也一起毁了。
    /// 所以先写临时文件，写成了再替换过去。
    /// </summary>
    public static async Task SaveAsync(
        MessageLibrary library,
        string path,
        CancellationToken cancellationToken = default)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, library.Export(), NoBom, cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
