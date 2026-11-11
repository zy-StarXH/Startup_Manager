using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using StartupManager.Core.Backup;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Services;

namespace StartupManager.Core.Elevation;

/// <summary>Seam used by tests and by the production Process launcher.</summary>
public interface IElevatedProcessLauncher
{
    Task<int> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Launches the helper with ShellExecute + runas (one UAC prompt) and waits for
/// it to exit. A timeout or user cancellation is surfaced by throwing
/// TimeoutException or Win32Exception 1223 respectively.
/// </summary>
public sealed class ProcessElevatedLauncher : IElevatedProcessLauncher
{
    public async Task<int> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动提权助手。");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(timeout);
        }

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                }
            }
            catch (Exception)
            {
                // The helper may already have exited between the check and the kill.
            }

            throw new TimeoutException("等待提权助手超时。");
        }
    }
}

/// <summary>
/// Writes a typed request into AppPaths.IpcDirectory, launches
/// StartupManager.Elevated.exe with UAC, waits for the response file, and
/// removes both files. UAC is never automated.
/// </summary>
public sealed class ElevationCoordinator : IElevationCoordinator
{
    private readonly AppPaths _paths;
    private readonly IElevatedProcessLauncher _launcher;
    private readonly TimeSpan _timeout;
    private readonly string _helperPath;
    private readonly Action<string>? _logSink;

    public ElevationCoordinator(
        AppPaths paths,
        IElevatedProcessLauncher? launcher = null,
        TimeSpan? timeout = null,
        string? helperPath = null,
        Action<string>? logSink = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _launcher = launcher ?? new ProcessElevatedLauncher();
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
        _helperPath = helperPath ?? Path.Combine(AppContext.BaseDirectory, "StartupManager.Elevated.exe");
        _logSink = logSink;
    }

    public async Task<ElevatedResponse> ExecuteElevatedAsync(
        ElevatedRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ElevationRequestValidator.Validate(request, DateTimeOffset.Now);
        if (!validation.IsValid)
        {
            Log("提权请求无效：" + validation.Error);
            return Failure(validation.Error ?? "提权请求无效。");
        }

        if (!File.Exists(_helperPath))
        {
            Log("未找到提权助手：" + _helperPath);
            return Failure("未找到提权助手 StartupManager.Elevated.exe，未做任何更改。");
        }

        string requestPath;
        string responsePath;

        try
        {
            Directory.CreateDirectory(_paths.IpcDirectory);
            if (!AtomicFile.TryRestrictDirectory(_paths.IpcDirectory))
            {
                Log("无法将 IPC 目录权限限制为当前用户，已继续（尽力而为）。");
            }

            requestPath = ElevationProtocol.CreateRequestPath(_paths, request.RequestId);
            responsePath = ElevationProtocol.GetResponsePath(requestPath);
            AtomicFile.TryDelete(responsePath);

            var requestJson = JsonSerializer.Serialize(request, JsonDefaults.Indented);
            await AtomicFile.WriteAllTextAsync(
                    requestPath,
                    requestJson,
                    restrictToCurrentUser: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("创建提权请求失败：" + ex.GetType().Name + "：" + ex.Message);
            return Failure("无法创建提权请求，未做任何更改。");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _helperPath,
                Arguments = "--request \"" + requestPath + "\"",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                await _launcher.RunAsync(startInfo, _timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Log("用户取消了 UAC 提示，未做任何更改。");
                return new ElevatedResponse
                {
                    Success = false,
                    Canceled = true,
                    Message = "UAC 已取消，未做任何更改。"
                };
            }
            catch (Win32Exception ex)
            {
                Log("启动提权助手失败：" + ex.NativeErrorCode + "：" + ex.Message);
                return Failure("无法启动提权助手，未做任何更改。");
            }
            catch (TimeoutException)
            {
                Log("等待提权助手超时。");
                return Failure("提权助手未在限定时间内返回结果，请刷新后检查。");
            }
            catch (Exception ex)
            {
                Log("启动提权助手失败：" + ex.GetType().Name + "：" + ex.Message);
                return Failure("无法启动提权助手，未做任何更改。");
            }

            if (!File.Exists(responsePath))
            {
                Log("提权助手未返回响应文件。");
                return Failure("提权助手未返回结果，未做任何更改。");
            }

            try
            {
                var responseInfo = new FileInfo(responsePath);
                if (responseInfo.Length <= 0 ||
                    responseInfo.Length > ElevationProtocol.MaxResponseFileBytes)
                {
                    return Failure("提权助手返回的结果无效。");
                }

                var responseJson = await File.ReadAllTextAsync(responsePath, cancellationToken)
                    .ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<ElevatedResponse>(responseJson, JsonDefaults.Options);
                if (response is null)
                {
                    return Failure("提权助手返回的结果无效。");
                }

                if (response.Results is null)
                {
                    response.Results = new List<ChangeResult>();
                }

                Log(
                    "提权响应：" +
                    (response.Success ? "成功" : response.Canceled ? "已取消" : "失败") +
                    (string.IsNullOrWhiteSpace(response.Message) ? "。" : "：" + response.Message));
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log("读取提权响应失败：" + ex.GetType().Name + "：" + ex.Message);
                return Failure("读取提权结果失败，请刷新后检查。");
            }
        }
        finally
        {
            AtomicFile.TryDelete(requestPath);
            AtomicFile.TryDelete(responsePath);
        }
    }

    private static ElevatedResponse Failure(string message)
    {
        return new ElevatedResponse
        {
            Success = false,
            Canceled = false,
            Message = message
        };
    }

    private void Log(string message)
    {
        try
        {
            _logSink?.Invoke(message);
        }
        catch (Exception)
        {
            // Logging must not break elevation.
        }

        AppLog.Append(_paths.LogDirectory, "elevation", null, message);
    }
}
