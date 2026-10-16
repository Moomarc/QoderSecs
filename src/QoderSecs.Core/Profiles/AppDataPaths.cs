namespace QoderSecs.Core.Profiles;

/// <summary>
/// 所有数据都放在 exe 旁边的数据目录里，拷到别的机器上就能用。
/// 连接参数只有一份文件；日志按配置 ID 和进程号分目录，多个实例不会互相盖。
/// </summary>
public sealed class AppDataPaths
{
    /// <summary>
    /// 目录名带上程序名：以前叫 data，拷到同事机器上跟别的工具挤在一起，
    /// 根本认不出是谁的，也不敢删。
    /// </summary>
    public const string DataDirectoryName = "QoderSecsData";

    private const string LegacyDataDirectoryName = "data";

    /// <summary>样本消息库放在 exe 旁的这个子目录里，导入对话框也停在这儿。</summary>
    public const string SampleSubdirectoryName = "samples";

    /// <summary>内嵌在程序里的那份样本库解出来就叫这个名字。</summary>
    public const string SampleLibraryFileName = "common-commands.sxml";

    public AppDataPaths(string executableDirectory)
    {
        var root = Path.GetFullPath(executableDirectory);
        RootDirectory = root;
        DataDirectory = Path.Combine(root, DataDirectoryName);
        LegacyDataDirectory = Path.Combine(root, LegacyDataDirectoryName);
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        SampleDirectory = Path.Combine(root, SampleSubdirectoryName);
        SampleLibraryPath = Path.Combine(SampleDirectory, SampleLibraryFileName);
    }

    /// <summary>exe 所在目录，所有伴生文件都以这儿为根。</summary>
    public string RootDirectory { get; }

    public string DataDirectory { get; }

    /// <summary>改名之前的老目录名，只为兼容保留。</summary>
    public string LegacyDataDirectory { get; }

    public string SettingsPath { get; }

    public string LogsDirectory { get; }

    /// <summary>样本消息库所在目录。程序只负责在它不存在时解出来一份，之后不再管。</summary>
    public string SampleDirectory { get; }

    public string SampleLibraryPath { get; }

    /// <summary>
    /// 建目录，顺手把老名字的 data 整体改名过来。
    /// 返回值是给界面显示的一句话，null 表示没什么要交代的。
    /// </summary>
    public string? EnsureCreated()
    {
        var notice = MigrateLegacyDirectory();
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        return notice;
    }

    /// <summary>
    /// 只在「老目录在、新目录不在」时动手，所以正常启动一次也不会碰。
    /// 改不动就不硬来：别的实例正占着这个目录、或者权限不够时，
    /// 老日志原样留在 data 里，程序改用新目录从零开始——
    /// 宁可让人看见一句提示，也不能为了搬目录把日志删了或者卡住启动。
    /// </summary>
    private string? MigrateLegacyDirectory()
    {
        if (!Directory.Exists(LegacyDataDirectory) || Directory.Exists(DataDirectory))
        {
            return null;
        }

        try
        {
            Directory.Move(LegacyDataDirectory, DataDirectory);
            return $"旧的 {LegacyDataDirectoryName} 目录已改名为 {DataDirectoryName}。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"旧的 {LegacyDataDirectoryName} 目录没能改名为 {DataDirectoryName}" +
                $"（{exception.Message}）；老日志留在原地，程序改用 {DataDirectoryName}。";
        }
    }

    /// <summary>
    /// 目录名带毫秒：同一个进程里断开马上重连可能落在同一秒内，
    /// 只到秒的话两次会话会写进同一个目录，日志互相搅在一起。
    /// </summary>
    public string CreateSessionDirectory(string profileId, DateTimeOffset startedAt, int processId)
    {
        if (!Guid.TryParseExact(profileId, "N", out _))
        {
            throw new ArgumentException("配置 ID 无效。", nameof(profileId));
        }

        var directory = Path.Combine(
            LogsDirectory,
            profileId,
            $"{startedAt:yyyyMMdd_HHmmssfff}_{processId}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
