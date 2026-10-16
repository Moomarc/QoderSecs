using System.Text.Json;
using System.Text.Json.Serialization;

namespace QoderSecs.Core.Profiles;

/// <summary>
/// 读回来的连接参数，外加读取过程里出的问题（没问题就是 null）。
/// 问题要报给用户，但不能因此让他连不上，所以参数一律给可用的默认值。
/// </summary>
public sealed record LoadedSettings(ConnectionProfile Profile, string? Problem);

/// <summary>
/// 连接参数只有一份，存在 QoderSecsData/settings.json。
/// 界面上常改的是 IP、端口、Active/Passive，其余 T 参数基本不动，
/// 所以没有多配置、没有下拉选择，一份文件从头用到尾。
/// </summary>
public sealed class ConnectionSettingsStore
{
    /// <summary>配置文件的读写格式；会话目录里的 effective-profile.json 也用这一份。</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public ConnectionSettingsStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    /// <summary>文件不存在、内容坏了、参数越界，都退回默认值，只在 Problem 里说明。</summary>
    public async Task<LoadedSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new LoadedSettings(ConnectionProfile.CreateDefault(), null);
        }

        ConnectionProfile? profile;
        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            profile = await JsonSerializer.DeserializeAsync<ConnectionProfile>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new LoadedSettings(
                ConnectionProfile.CreateDefault(),
                $"连接参数没读出来，已按默认值启动：{exception.Message}");
        }

        if (profile is null)
        {
            return new LoadedSettings(
                ConnectionProfile.CreateDefault(),
                "连接参数文件是空的，已按默认值启动。");
        }

        var errors = ConnectionProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            return new LoadedSettings(
                ConnectionProfile.CreateDefault(),
                $"连接参数无效，已按默认值启动：{string.Join("；", errors)}");
        }

        return new LoadedSettings(profile, null);
    }

    /// <summary>先写临时文件再替换，进程半路被杀掉也不会留下半个 JSON。</summary>
    public async Task SaveAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        var errors = ConnectionProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(profile));
        }

        var directory = Path.GetDirectoryName(_path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, profile, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _path, true);
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
