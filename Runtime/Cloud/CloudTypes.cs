using System;

namespace Ecanakli.SaveSystem
{
    /// <summary>Who owns a cloud key.</summary>
    public enum CloudAccess
    {
        /// <summary>Written by the client (CloudSync slots).</summary>
        ClientOwned = 0,

        /// <summary>Written by a server; read only for the client (CloudReadOnly slots).</summary>
        ServerOwned = 1,
    }

    /// <summary>Classified provider error.</summary>
    public enum CloudErrorKind
    {
        Transient = 0,
        RateLimited = 1,
        Conflict = 2,
        PayloadTooLarge = 3,
        QuotaExceeded = 4,
        Unauthorized = 5,
        NotSignedIn = 6,
        Permanent = 7,
    }

    /// <summary>Provider error returned instead of an exception.</summary>
    public sealed class CloudError
    {
        public CloudError(CloudErrorKind kind, string message, TimeSpan? retryAfter = null, Exception exception = null)
        {
            Kind = kind;
            Message = message ?? kind.ToString();
            RetryAfter = retryAfter;
            Exception = exception;
        }

        public CloudErrorKind Kind { get; }

        public string Message { get; }

        /// <summary>Server-requested wait before retrying, when known.</summary>
        public TimeSpan? RetryAfter { get; }

        public Exception Exception { get; }

        /// <summary>Only Transient and RateLimited errors are retried.</summary>
        public bool IsRetryable => Kind == CloudErrorKind.Transient || Kind == CloudErrorKind.RateLimited;

        public override string ToString()
        {
            return Kind + ": " + Message;
        }
    }

    /// <summary>Request to read one key.</summary>
    public sealed class CloudReadRequest
    {
        public CloudReadRequest(string key, CloudAccess access)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key must not be null or empty.", nameof(key));
            }

            Key = key;
            Access = access;
        }

        public string Key { get; }

        public CloudAccess Access { get; }
    }

    /// <summary>Outcome of reading one key.</summary>
    public enum CloudReadStatus
    {
        Found = 0,

        /// <summary>A result, not an error; never retried.</summary>
        NotFound = 1,

        Failed = 2,
    }

    /// <summary>Result of reading one key.</summary>
    public sealed class CloudReadResult
    {
        private CloudReadResult(string key, CloudReadStatus status, byte[] value, string version, CloudError error)
        {
            Key = key;
            Status = status;
            Value = value;
            Version = version;
            Error = error;
        }

        public string Key { get; }

        public CloudReadStatus Status { get; }

        /// <summary>UTF-8 JSON bytes of the stored value; null unless Found.</summary>
        public byte[] Value { get; }

        /// <summary>Provider version token (write lock); null when unsupported.</summary>
        public string Version { get; }

        /// <summary>Set when Status is Failed.</summary>
        public CloudError Error { get; }

        public static CloudReadResult Found(string key, byte[] value, string version)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            return new CloudReadResult(key, CloudReadStatus.Found, value, version, null);
        }

        public static CloudReadResult NotFound(string key)
        {
            return new CloudReadResult(key, CloudReadStatus.NotFound, null, null, null);
        }

        public static CloudReadResult Failed(string key, CloudError error)
        {
            if (error == null)
            {
                throw new ArgumentNullException(nameof(error));
            }

            return new CloudReadResult(key, CloudReadStatus.Failed, null, null, error);
        }
    }

    /// <summary>Request to write one key.</summary>
    public sealed class CloudWriteRequest
    {
        public CloudWriteRequest(string key, byte[] value, string expectedVersion, CloudAccess access)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key must not be null or empty.", nameof(key));
            }

            Key = key;
            Value = value ?? throw new ArgumentNullException(nameof(value));
            ExpectedVersion = expectedVersion;
            Access = access;
        }

        public string Key { get; }

        /// <summary>UTF-8 JSON bytes; providers store it as a JSON document.</summary>
        public byte[] Value { get; }

        /// <summary>Conditional write token; null for an unconditional write.</summary>
        public string ExpectedVersion { get; }

        public CloudAccess Access { get; }
    }

    /// <summary>Result of writing one key.</summary>
    public sealed class CloudWriteResult
    {
        private CloudWriteResult(string key, bool isSuccess, string newVersion, CloudError error)
        {
            Key = key;
            IsSuccess = isSuccess;
            NewVersion = newVersion;
            Error = error;
        }

        public string Key { get; }

        public bool IsSuccess { get; }

        /// <summary>Version token after the write; null when unsupported.</summary>
        public string NewVersion { get; }

        public CloudError Error { get; }

        public static CloudWriteResult Succeeded(string key, string newVersion)
        {
            return new CloudWriteResult(key, true, newVersion, null);
        }

        public static CloudWriteResult Failed(string key, CloudError error)
        {
            if (error == null)
            {
                throw new ArgumentNullException(nameof(error));
            }

            return new CloudWriteResult(key, false, null, error);
        }
    }

    /// <summary>Result of deleting one key.</summary>
    public sealed class CloudDeleteResult
    {
        private CloudDeleteResult(string key, bool isSuccess, bool isNotFound, CloudError error)
        {
            Key = key;
            IsSuccess = isSuccess;
            IsNotFound = isNotFound;
            Error = error;
        }

        public string Key { get; }

        /// <summary>The key was deleted.</summary>
        public bool IsSuccess { get; }

        /// <summary>The key did not exist.</summary>
        public bool IsNotFound { get; }

        public CloudError Error { get; }

        public static CloudDeleteResult Deleted(string key)
        {
            return new CloudDeleteResult(key, true, false, null);
        }

        public static CloudDeleteResult NotFound(string key)
        {
            return new CloudDeleteResult(key, false, true, null);
        }

        public static CloudDeleteResult Failed(string key, CloudError error)
        {
            if (error == null)
            {
                throw new ArgumentNullException(nameof(error));
            }

            return new CloudDeleteResult(key, false, false, error);
        }
    }
}
