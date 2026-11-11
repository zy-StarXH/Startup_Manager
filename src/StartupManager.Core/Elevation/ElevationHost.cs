using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using StartupManager.Core.Backup;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Services;

namespace StartupManager.Core.Elevation;

/// <summary>
/// Runs inside the elevated one-shot helper. It validates a request file that
/// must live directly under AppPaths.IpcDirectory, then executes only a
/// whitelisted typed operation through IStartupManager with isElevated=true.
/// The manager is resolved lazily so tests can inject a fake and the helper can
/// use CoreComposition.CreateManager at integration time.
/// </summary>
public sealed class ElevationHost
{
    private readonly AppPaths _paths;
    private readonly Func<IStartupManager> _managerFactory;
    private readonly Action<string>? _logSink;

    public ElevationHost(
        AppPaths paths,
        Func<IStartupManager> managerFactory,
        Action<string>? logSink = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(managerFactory);

        _paths = paths;
        _managerFactory = managerFactory;
        _logSink = logSink;
    }

    public async Task<ElevatedResponse> HandleRequestFileAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        if (!ElevationProtocol.TryValidateRequestPath(_paths, requestPath, out var fullPath, out var pathError))
        {
            Log("拒绝了提权请求路径：" + (pathError ?? "无效路径") + "。");
            return Failure(pathError ?? "提权请求路径无效。");
        }

        var responsePath = ElevationProtocol.GetResponsePath(fullPath);

        try
        {
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length <= 0 || fileInfo.Length > ElevationProtocol.MaxRequestFileBytes)
            {
                return await WriteResponseAsync(responsePath, Failure("提权请求大小无效。"))
                    .ConfigureAwait(false);
            }

            if (!ValidateRequestFileSecurity(fullPath, out var securityError))
            {
                Log("拒绝了提权请求文件：" + securityError);
                return await WriteResponseAsync(responsePath, Failure(securityError!))
                    .ConfigureAwait(false);
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await WriteResponseAsync(responsePath, Failure("提权操作已取消。"))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log("读取提权请求失败：" + ex.GetType().Name + "。");
                return await WriteResponseAsync(responsePath, Failure("无法读取提权请求。"))
                    .ConfigureAwait(false);
            }

            ElevatedRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<ElevatedRequest>(json, JsonDefaults.Options);
            }
            catch (JsonException ex)
            {
                Log("提权请求 JSON 无效：" + ex.GetType().Name + "。");
                return await WriteResponseAsync(responsePath, Failure("提权请求格式无效。"))
                    .ConfigureAwait(false);
            }

            var validation = ElevationRequestValidator.Validate(request, DateTimeOffset.Now);
            if (request is null || !validation.IsValid)
            {
                Log("拒绝了提权请求：" + (validation.Error ?? "请求无效"));
                return await WriteResponseAsync(
                        responsePath,
                        Failure(validation.Error ?? "提权请求无效。"))
                    .ConfigureAwait(false);
            }

            ElevatedResponse response;
            try
            {
                var manager = _managerFactory();
                response = await ExecuteAsync(manager, request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response = Failure("提权操作已取消。");
            }
            catch (Exception ex)
            {
                Log("提权操作失败：" + ex.GetType().Name + "：" + ex.Message);
                response = Failure("提权操作失败，请查看日志。");
            }

            return await WriteResponseAsync(responsePath, response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("提权处理失败：" + ex.GetType().Name + "：" + ex.Message);
            try
            {
                return await WriteResponseAsync(responsePath, Failure("提权处理失败。"))
                    .ConfigureAwait(false);
            }
            catch (Exception writeError)
            {
                Log("写入提权响应失败：" + writeError.GetType().Name + "。");
                return Failure("提权处理失败。");
            }
        }
    }

    private async Task<ElevatedResponse> ExecuteAsync(
        IStartupManager manager,
        ElevatedRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case ElevatedOperations.SetEnabled:
            {
                var batch = await manager
                    .SetEnabledAsync(request.ItemIds, request.Enabled, isElevated: true, cancellationToken)
                    .ConfigureAwait(false);

                var success = !batch.RequiresElevation && !batch.Canceled && batch.AllSucceeded;
                return new ElevatedResponse
                {
                    Success = success,
                    Canceled = batch.Canceled,
                    Message = batch.Message ?? (success ? "操作完成。" : "操作未完成。"),
                    Results = batch.Results.ToList()
                };
            }

            case ElevatedOperations.RestoreBackup:
            {
                var result = await manager
                    .RestoreBackupAsync(request.BackupId!, isElevated: true, cancellationToken)
                    .ConfigureAwait(false);

                var success = result.Success && result.Verified;
                return new ElevatedResponse
                {
                    Success = success,
                    Canceled = result.Canceled,
                    Message = result.Message ?? (success ? "操作完成。" : "操作未完成。"),
                    Results = new List<ChangeResult> { result }
                };
            }

            default:
                return Failure("不支持的提权操作。");
        }
    }

    private async Task<ElevatedResponse> WriteResponseAsync(
        string responsePath,
        ElevatedResponse response)
    {
        var json = JsonSerializer.Serialize(response, JsonDefaults.Indented);
        await AtomicFile.WriteAllTextAsync(
                responsePath,
                json,
                restrictToCurrentUser: true,
                CancellationToken.None)
            .ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Best effort: the owner must be the current user when the owner can be
    /// read. A non-owner who can write the request is only logged, because ACL
    /// inspection can be unavailable or surprising across Windows builds.
    /// </summary>
    private bool ValidateRequestFileSecurity(string path, out string? error)
    {
        error = null;

        try
        {
            var currentSid = WindowsIdentity.GetCurrent().User;
            if (currentSid is null)
            {
                return true;
            }

            var security = new FileInfo(path).GetAccessControl();
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is not null && !owner.Equals(currentSid))
            {
                error = "提权请求文件的所有者不是当前用户。";
                return false;
            }

            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                         includeExplicit: true,
                         includeInherited: true,
                         targetType: typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow)
                {
                    continue;
                }

                if (rule.IdentityReference is not SecurityIdentifier ruleSid || ruleSid.Equals(currentSid))
                {
                    continue;
                }

                if (HasWriteRights(rule.FileSystemRights))
                {
                    Log("提权请求文件对非当前用户账户开放了写入权限，已记录但不阻断。");
                }
            }
        }
        catch (Exception ex)
        {
            // ACL inspection is best effort; the typed request is still validated.
            Log("无法检查提权请求文件权限：" + ex.GetType().Name + "。");
        }

        return true;
    }

    private static bool HasWriteRights(FileSystemRights rights)
    {
        const FileSystemRights writeMask =
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.WriteAttributes |
            FileSystemRights.Delete |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;

        return (rights & writeMask) != 0;
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
            // Logging must not break the one-shot helper.
        }

        AppLog.Append(_paths.LogDirectory, "elevated", null, message);
    }
}
