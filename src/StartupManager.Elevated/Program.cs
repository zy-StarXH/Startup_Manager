using System.Text;
using StartupManager.Core;
using StartupManager.Core.Elevation;
using StartupManager.Core.Models;

namespace StartupManager.Elevated;

/// <summary>
/// One-shot elevated helper. It never shows UI and only handles a typed
/// --request &lt;absolute path&gt; file written by the main app.
/// CoreComposition.CreateManager is the integration seam; it is resolved lazily
/// so this project builds and runs in workstream copies.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var paths = AppPaths.CreateDefault();
        try
        {
            paths.EnsureCreated();
        }
        catch (Exception ex)
        {
            WriteLog(paths, "创建数据目录失败：" + ex.GetType().Name + "。");
        }

        var requestPath = ParseRequestPath(args);
        if (requestPath is null)
        {
            WriteLog(paths, "参数无效，期望 --request <绝对路径>，未执行任何操作。");
            return 2;
        }

        try
        {
            var host = new ElevationHost(
                paths,
                CoreComposition.CreateManager,
                message => WriteLog(paths, message));

            var response = host.HandleRequestFileAsync(requestPath, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            WriteLog(
                paths,
                "提权请求处理完成：" +
                (response.Success ? "成功" : response.Canceled ? "已取消" : "失败") +
                (string.IsNullOrWhiteSpace(response.Message) ? "。" : "：" + response.Message));

            return response.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteLog(paths, "提权助手异常：" + ex.GetType().Name + "：" + ex.Message);
            return 1;
        }
    }

    private static string? ParseRequestPath(string[] args)
    {
        if (args is null || args.Length != 2)
        {
            return null;
        }

        if (!string.Equals(args[0], "--request", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = args[1];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        return path;
    }

    private static void WriteLog(AppPaths paths, string message)
    {
        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            var line = string.Concat(
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
                "\t",
                message.Replace("\r", " ", StringComparison.Ordinal)
                    .Replace("\n", " ", StringComparison.Ordinal),
                Environment.NewLine);

            File.AppendAllText(
                Path.Combine(paths.LogDirectory, "elevated.log"),
                line,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception)
        {
            // A WinExe must never surface an error dialog; logging is best effort.
        }
    }
}
