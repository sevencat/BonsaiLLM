using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BonsaiLLM.Core.Settings;

/// <summary>settings.json 的读写。读失败时保留损坏文件并给出错误，不静默重置。</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BonsaiLLM",
            "settings.json");
    }

    public string FilePath { get; }

    public string? LastError { get; private set; }

    public AppSettings Load()
    {
        LastError = null;

        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LastError = $"读取设置失败：{ex.Message}";
            TryBackupBrokenFile();
            return new AppSettings();
        }
    }

    /// <summary>写入设置；失败返回 false 并把原因放在 <see cref="LastError"/>。</summary>
    public bool TrySave(AppSettings settings)
    {
        LastError = null;

        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"保存设置失败：{ex.Message}";
            return false;
        }
    }

    private void TryBackupBrokenFile()
    {
        try
        {
            File.Move(FilePath, FilePath + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 备份失败也不阻塞启动
        }
    }
}
