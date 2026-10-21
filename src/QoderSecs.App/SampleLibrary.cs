using System.IO;
using QoderSecs.Core.Profiles;

namespace QoderSecs.App;

/// <summary>
/// 样本消息库内嵌在程序里，启动时数据目录里缺了就解出来一份（路径在
/// <see cref="AppDataPaths.SampleFilePath"/>）。解出来之后它就是普通文件——
/// 用户改它、删它、换成自己的，程序都不再过问。
///
/// 这么做只为了一件事：只拿到一个 exe 的人也有东西能导入，不用先去找人要 .sxml。
/// 程序仍然不内置任何回复规则：文件解出来不等于加载，自动回复也仍然默认不开。
/// </summary>
internal static class SampleLibrary
{
    /// <summary>
    /// 把内嵌的样本解到 QoderSecsData 第一层，和 settings.json 并排。
    /// 已经在的不覆盖：那说明用户改过或者换成了自己的一份，程序不能再伸手。
    /// 返回要给界面看的一句话，null 表示没什么要交代的。
    /// 解不出来（目录只读、权限不够）也不算错，顶多是样本暂时不可用，不能拦住程序启动。
    /// </summary>
    public static string? EnsureExtracted(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return ExtractOne(AppDataPaths.SampleFileName, paths.SampleFilePath);
    }

    private static string? ExtractOne(string fileName, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            return null;
        }

        var assembly = typeof(SampleLibrary).Assembly;

        // 按后缀找，不写死完整资源名：那个名字跟着 RootNamespace 和目录结构变，
        // 挪一下文件就悄悄解不出来了，宁可让它在运行时报出来。
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            return $"程序里没有内嵌的样本库 {fileName}，可能是发布配置漏了，这次只能导入自己的 .sxml。";
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
            return $"{fileName} 没能解到 {targetPath}（{exception.Message}）；" +
                "不影响其他功能，换个能写的目录再跑，或者直接导入自己的 .sxml。";
        }
    }
}
