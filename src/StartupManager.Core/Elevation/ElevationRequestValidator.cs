namespace StartupManager.Core.Elevation;

public sealed record ElevationValidationResult(bool IsValid, string? Error)
{
    public static ElevationValidationResult Valid { get; } = new(true, null);

    public static ElevationValidationResult Invalid(string error) => new(false, error);
}

/// <summary>
/// Validates a typed ElevatedRequest. Only the two whitelisted operations and
/// stable item/backup ids are accepted; there are no paths or commands.
/// </summary>
public static class ElevationRequestValidator
{
    public const int MaxItemCount = 64;

    public static ElevationValidationResult Validate(ElevatedRequest? request, DateTimeOffset now)
    {
        if (request is null)
        {
            return ElevationValidationResult.Invalid("提权请求为空。");
        }

        if (request.Version != 1)
        {
            return ElevationValidationResult.Invalid("提权请求版本不受支持。");
        }

        if (!ElevationProtocol.IsValidRequestId(request.RequestId))
        {
            return ElevationValidationResult.Invalid("提权请求标识无效。");
        }

        if (!ElevationProtocol.IsValidNonce(request.Nonce))
        {
            return ElevationValidationResult.Invalid("提权请求随机数无效。");
        }

        if (request.ExpiresAt < now - ElevationProtocol.MaxClockSkew)
        {
            return ElevationValidationResult.Invalid("提权请求已过期。");
        }

        switch (request.Operation)
        {
            case ElevatedOperations.SetEnabled:
                if (request.ItemIds is null || request.ItemIds.Count == 0)
                {
                    return ElevationValidationResult.Invalid("提权请求未包含启动项。");
                }

                if (request.ItemIds.Count > MaxItemCount)
                {
                    return ElevationValidationResult.Invalid("提权请求包含过多启动项。");
                }

                foreach (var itemId in request.ItemIds)
                {
                    if (!ElevationProtocol.IsValidItemId(itemId))
                    {
                        return ElevationValidationResult.Invalid("提权请求包含无效的启动项标识。");
                    }
                }

                return ElevationValidationResult.Valid;

            case ElevatedOperations.RestoreBackup:
                if (string.IsNullOrWhiteSpace(request.BackupId) ||
                    !Guid.TryParse(request.BackupId, out _))
                {
                    return ElevationValidationResult.Invalid("提权请求包含无效的备份标识。");
                }

                return ElevationValidationResult.Valid;

            default:
                return ElevationValidationResult.Invalid("不支持的提权操作。");
        }
    }
}
