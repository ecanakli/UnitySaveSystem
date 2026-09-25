using System;
using Ecanakli.SaveSystem;
using Unity.Services.CloudSave;
using Unity.Services.Core;

namespace Ecanakli.SaveSystem.UnityCloudSave
{
    /// <summary>Classifies UGS Cloud Save SDK exceptions into CloudError. Never throws.</summary>
    public static class UnityCloudSaveErrorMapper
    {
        public static CloudError Map(Exception exception)
        {
            switch (exception)
            {
                case CloudSaveRateLimitedException rateLimited:
                    return new CloudError(CloudErrorKind.RateLimited, rateLimited.Message,
                        TimeSpan.FromSeconds(Math.Max(0f, rateLimited.RetryAfter)), rateLimited);

                case CloudSaveConflictException conflict:
                    return new CloudError(CloudErrorKind.Conflict, conflict.Message, null, conflict);

                case CloudSaveValidationException validation:
                    // UGS has no separate "too large" reason; validation failures map to PayloadTooLarge.
                    return new CloudError(CloudErrorKind.PayloadTooLarge, validation.Message, null, validation);

                case CloudSaveException cloudSave:
                    return new CloudError(MapReason(cloudSave.Reason), cloudSave.Message, null, cloudSave);

                case ServicesInitializationException notInitialized:
                    // Unity Services / Authentication not initialized yet; treat like signed out, not a hard failure.
                    return new CloudError(CloudErrorKind.NotSignedIn, notInitialized.Message, null, notInitialized);

                default:
                    return new CloudError(CloudErrorKind.Permanent, exception.Message, null, exception);
            }
        }

        /// <summary>Pure reason-to-kind table; the concrete exception types above short-circuit the common cases.</summary>
        public static CloudErrorKind MapReason(CloudSaveExceptionReason reason)
        {
            switch (reason)
            {
                case CloudSaveExceptionReason.NoInternetConnection:
                case CloudSaveExceptionReason.ServiceUnavailable:
                    return CloudErrorKind.Transient;

                case CloudSaveExceptionReason.ProjectIdMissing:
                case CloudSaveExceptionReason.PlayerIdMissing:
                case CloudSaveExceptionReason.AccessTokenMissing:
                    return CloudErrorKind.NotSignedIn;

                case CloudSaveExceptionReason.Unauthorized:
                    return CloudErrorKind.Unauthorized;

                case CloudSaveExceptionReason.KeyLimitExceeded:
                    return CloudErrorKind.QuotaExceeded;

                case CloudSaveExceptionReason.TooManyRequests:
                    // Reachable only if a 429 ever arrives as a plain CloudSaveException instead of the rate-limited type.
                    return CloudErrorKind.RateLimited;

                case CloudSaveExceptionReason.Conflict:
                    // Reachable only if a 409/412 ever arrives as a plain CloudSaveException instead of the conflict type.
                    return CloudErrorKind.Conflict;

                case CloudSaveExceptionReason.InvalidArgument:
                    return CloudErrorKind.PayloadTooLarge;

                case CloudSaveExceptionReason.NotFound:
                case CloudSaveExceptionReason.Unknown:
                default:
                    return CloudErrorKind.Permanent;
            }
        }

        /// <summary>A key co-batched with a conflicting/invalid sibling; retried alone since the batch write is atomic.</summary>
        public static CloudError SiblingBatchFailure(string key)
        {
            return new CloudError(CloudErrorKind.Transient,
                "Key '" + key + "' was in a UGS batch call that failed because of another key; it will be retried alone.");
        }
    }
}
