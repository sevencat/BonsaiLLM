using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BonsaiLLM.Core.Sessions;

/// <summary>
/// 会话读写。一个会话一个文件，按 UpdatedAt 倒序返回；
/// 单个文件损坏只跳过该文件，不影响其它会话。
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public SessionStore(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BonsaiLLM",
            "sessions");
        ImagesDirectory = Path.Combine(Path.GetDirectoryName(Directory) ?? Directory, "images");
    }

    public string Directory { get; }

    /// <summary>会话图片的存放目录，与会话 JSON 分开（图片不进 JSON，避免文件膨胀）。</summary>
    public string ImagesDirectory { get; }

    public string? LastError { get; private set; }

    public IReadOnlyList<ChatSession> LoadAll()
    {
        LastError = null;

        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var sessions = new List<ChatSession>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                if (JsonSerializer.Deserialize<ChatSession>(json, JsonOptions) is { } session)
                {
                    sessions.Add(session);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LastError = $"读取会话失败：{Path.GetFileName(file)}（{ex.Message}）";
            }
        }

        return sessions.OrderByDescending(session => session.UpdatedAt).ToList();
    }

    public bool TrySave(ChatSession session)
    {
        LastError = null;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(session.Id);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(session, JsonOptions));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"保存会话失败：{ex.Message}";
            return false;
        }
    }

    public bool TryDelete(string id)
    {
        LastError = null;

        try
        {
            var path = PathFor(id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"删除会话失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>把图片写进 images 目录，返回文件名；失败返回 null。</summary>
    public string? TrySaveImage(byte[] bytes, string extension = ".jpg")
    {
        LastError = null;

        try
        {
            System.IO.Directory.CreateDirectory(ImagesDirectory);
            var fileName = Guid.NewGuid().ToString("N") + extension;
            File.WriteAllBytes(Path.Combine(ImagesDirectory, fileName), bytes);
            return fileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"保存图片失败：{ex.Message}";
            return null;
        }
    }

    public string? ImagePath(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName)
            ? null
            : Path.Combine(ImagesDirectory, fileName);

    public byte[]? TryReadImage(string? fileName)
    {
        var path = ImagePath(fileName);
        if (path is null)
        {
            return null;
        }

        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>会话删除时一起清掉它引用的图片文件。</summary>
    public void TryDeleteImages(ChatSession session)
    {
        foreach (var file in session.Messages.Select(message => message.ImageFile).OfType<string>().Distinct())
        {
            TryDeleteImage(file);
        }
    }

    public void TryDeleteImage(string? fileName)
    {
        var path = ImagePath(fileName);
        if (path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 图片是附属资源，删不掉不影响会话本身
        }
    }

    private string PathFor(string id) => Path.Combine(Directory, id + ".json");
}
