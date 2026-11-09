using System;
using System.IO;

namespace BonsaiLLM.Core;

/// <summary>
/// 便携模式的数据位置：设置、会话、图片都放在可执行文件同目录。
/// 安装到 Program Files 这类不可写目录时自动回退到 %AppData%\BonsaiLLM。
/// </summary>
public static class AppPaths
{
    private const string FolderName = "BonsaiLLM";

    public static string BaseDirectory { get; } = ResolveBaseDirectory();

    public static string SettingsFile => Path.Combine(BaseDirectory, "settings.json");

    public static string SessionsDirectory => Path.Combine(BaseDirectory, "sessions");

    public static string ImagesDirectory => Path.Combine(BaseDirectory, "images");

    /// <summary>旧版数据在 %AppData%\BonsaiLLM，切换目录后搬一次，别让升级后数据「消失」。</summary>
    public static void MigrateFromLegacyIfNeeded()
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

            if (!Directory.Exists(legacy) || IsSamePath(legacy, BaseDirectory))
            {
                return;
            }

            CopyFile(Path.Combine(legacy, "settings.json"), SettingsFile);
            CopyDirectory(Path.Combine(legacy, "sessions"), SessionsDirectory);
            CopyDirectory(Path.Combine(legacy, "images"), ImagesDirectory);
        }
        catch (Exception)
        {
            // 迁移失败不影响启动，用户顶多重配一次
        }
    }

    private static string ResolveBaseDirectory()
    {
        var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (IsWritable(directory))
        {
            return directory;
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch (Exception)
        {
            // 极端情况下连 AppData 都不可写，交给上层报错
        }

        return fallback;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsSamePath(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void CopyFile(string source, string destination)
    {
        if (File.Exists(source) && !File.Exists(destination))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(target))
            {
                File.Copy(file, target);
            }
        }
    }
}
