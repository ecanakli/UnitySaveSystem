using System;

namespace Ecanakli.SaveSystem
{
    /// <summary>Error detail attached to a failed result.</summary>
    public sealed class SaveError
    {
        internal SaveError(SaveErrorCode code, string message, Exception exception = null, CloudError cloudError = null)
        {
            Code = code;
            Message = message ?? code.ToString();
            Exception = exception;
            CloudError = cloudError;
        }

        public SaveErrorCode Code { get; }

        public string Message { get; }

        /// <summary>Underlying exception, when one caused the error.</summary>
        public Exception Exception { get; }

        /// <summary>Provider error, set when Code is CloudError.</summary>
        public CloudError CloudError { get; }

        internal static SaveError FromLocalWriteKind(LocalWriteErrorKind kind, string message, Exception exception)
        {
            return new SaveError(ToCode(kind), message, exception);
        }

        internal static SaveErrorCode ToCode(LocalWriteErrorKind kind)
        {
            switch (kind)
            {
                case LocalWriteErrorKind.DiskFull:
                    return SaveErrorCode.DiskFull;
                case LocalWriteErrorKind.AccessDenied:
                    return SaveErrorCode.AccessDenied;
                default:
                    return SaveErrorCode.IoError;
            }
        }

        public override string ToString()
        {
            return Code + ": " + Message;
        }
    }
}
