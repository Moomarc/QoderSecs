using System.IO;
using QoderSecs.Core.Profiles;

namespace QoderSecs.App;

/// <summary>
/// 样本消息库内嵌在程序里，启动时如果 exe 旁边没有这个文件，就解出来一份。
/// 解出来之后它就是普通文件——用户改它、删它、换成自己的，程序都不再过问。
///
/// 这么做只为了一件事：只拿到一个 exe 的人也有东西能导入，不用先去找人要 .sxml。
/// 程序仍然不内置任何回复规则：文件解出来不等于加载，自动回复也仍然默认不开。
/// </summary>
internal static class SampleLibrary
{
    /// <summary>
    /// 返回要给界面看的一句话，null 表示没什么要交代的。
    /// 文件已经在、或者解不出来（目录只读、权限不够）都不算错：前者说明用户早有
    /// 自己的一份，后者顶多是样本暂时不可用，都不能拦住程序启动。
    /// </summary>
    public static string? EnsureExtracted(string targetPath)
    {
        if (File.Exists(targetPath))
        {
            return null;
        }

        var assembly = typeof(SampleLibrary).Assembly;

        // 按后缀找，不写死完整资源名：那个名字跟着 RootNamespace 和目录结构变，
        // 挪一下文件就悄悄解不出来了，宁可让它在运行时报出来。
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(
                AppDataPaths.SampleLibraryFileName,
                StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            return "程序里没有内嵌的样本消息库，可能是发布配置漏了，这次只能导入自己的 .sxml。";
        }

        try
        {
            using var resource = assembly.GetManifestResourceStream(resourceName)!;
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var file = File.Create(targetPath);
            resource.CopyTo(file);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"样本消息库没能解到 {targetPath}（{exception.Message}）；" +
                "不影响其他功能，换个能写的目录再跑，或者直接导入自己的 .sxml。";
        }
    }
}
