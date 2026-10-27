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

    /// <summary>
    /// 内嵌在程序里、启动时解到数据目录第一层的两份样本库。
    /// rich-set 是 GEM 常用命令的全量骨架：结构留着、数值一律空，自动回复只配到同名的标准应答；
    /// raw-set 只留 S1F1/2、S1F13/14 四条，变量带默认值，导入就能走完一轮自动回复。
    /// 这两个名字同时是 exe 里内嵌资源的后缀，改一处要改两处（见 QoderSecs.App.csproj）。
    /// </summary>
    public const string RichSampleFileName = "rich-set.sxml";

    public const string RawSampleFileName = "raw-set.sxml";

    /// <summary>启动时按这个顺序解包。两份都缺才需要提示，缺一份也只提示一份。</summary>
    public static readonly IReadOnlyList<string> SampleFileNames = new[]
    {
        RichSampleFileName,
        RawSampleFileName
    };

    public AppDataPaths(string executableDirectory)
    {
        var root = Path.GetFullPath(executableDirectory);
        RootDirectory = root;
        DataDirectory = Path.Combine(root, DataDirectoryName);
        LegacyDataDirectory = Path.Combine(root, LegacyDataDirectoryName);
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        SampleDirectory = DataDirectory;
        SampleFilePath = Path.Combine(SampleDirectory, RichSampleFileName);
        RawSampleFilePath = Path.Combine(SampleDirectory, RawSampleFileName);
    }

    /// <summary>exe 所在目录，所有伴生文件都以这儿为根。</summary>
    public string RootDirectory { get; }

    public string DataDirectory { get; }

    /// <summary>改名之前的老目录名，只为兼容保留。</summary>
    public string LegacyDataDirectory { get; }

    public string SettingsPath { get; }

    public string LogsDirectory { get; }

    /// <summary>样本消息库所在目录：和 settings.json 同层，不再单独占一个 samples 文件夹。</summary>
    public string SampleDirectory { get; }

    /// <summary>rich-set 解出来之后的完整路径。程序只负责在文件不存在时解一份，之后不再管。</summary>
    public string SampleFilePath { get; }

    /// <summary>raw-set 解出来之后的完整路径，规则同上。</summary>
    public string RawSampleFilePath { get; }

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
