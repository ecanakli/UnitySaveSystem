using System;
using System.IO;

namespace Ecanakli.SaveSystem
{
    /// <summary>Maps storage exceptions to LocalWriteErrorKind and decides Windows replace retries.</summary>
    internal static class StorageErrorClassifier
    {
        private const int Win32FacilityMask = unchecked((int)0xFFFF0000);
        private const int Win32FacilityHResult = unchecked((int)0x80070000);
        private const int Win32CodeMask = 0xFFFF;
        private const int ErrorAccessDenied = 0x05;
        private const int ErrorHandleDiskFull = 0x27;
        private const int ErrorDiskFull = 0x70;
        private const int ErrorDiskQuotaExceeded = 0x50F;

        // Raw errno values some runtimes put into HResult
        private const int PosixEacces = 13;
        private const int PosixEnospc = 28;

        private const int MaxChainDepth = 8;

        /// <summary>SaveStorageException kind first, then disk-full and access-denied detection over the inner chain; IoError otherwise.</summary>
        public static LocalWriteErrorKind Classify(Exception exception)
        {
            Exception current = exception;
            for (int depth = 0; current != null && depth < MaxChainDepth; depth++)
            {
                if (current is SaveStorageException storageException)
                {
                    return storageException.Kind;
                }

                if (IsDiskFullSingle(current))
                {
                    return LocalWriteErrorKind.DiskFull;
                }

                if (IsAccessDeniedSingle(current))
                {
                    return LocalWriteErrorKind.AccessDenied;
                }

                current = current.InnerException;
            }

            return LocalWriteErrorKind.IoError;
        }

        /// <summary>True when the exception classifies as DiskFull.</summary>
        public static bool IsDiskFull(Exception exception)
        {
            return exception != null && Classify(exception) == LocalWriteErrorKind.DiskFull;
        }

        /// <summary>True for IOException or UnauthorizedAccessException, except not-found, path-too-long, disk-full and explicit SaveStorageException.</summary>
        public static bool IsRetryableReplaceFailure(Exception exception)
        {
            if (exception == null || exception is SaveStorageException)
            {
                return false;
            }

            if (exception is FileNotFoundException
                || exception is DirectoryNotFoundException
                || exception is PathTooLongException
                || exception is DriveNotFoundException)
            {
                return false;
            }

            if (exception is UnauthorizedAccessException)
            {
                return true;
            }

            return exception is IOException && !IsDiskFull(exception);
        }

        private static bool IsDiskFullSingle(Exception exception)
        {
            if (!(exception is IOException))
            {
                return false;
            }

            int hresult = exception.HResult;
            if ((hresult & Win32FacilityMask) == Win32FacilityHResult)
            {
                int code = hresult & Win32CodeMask;
                if (code == ErrorHandleDiskFull || code == ErrorDiskFull || code == ErrorDiskQuotaExceeded)
                {
                    return true;
                }
            }

            if (hresult == PosixEnospc)
            {
                return true;
            }

            // Fallback for runtimes that only report the condition in the message (unverified on IL2CPP devices)
            string message = exception.Message;
            return message != null
                && (message.IndexOf("disk full", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("no space left", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsAccessDeniedSingle(Exception exception)
        {
            if (exception is UnauthorizedAccessException)
            {
                return true;
            }

            if (!(exception is IOException))
            {
                return false;
            }

            int hresult = exception.HResult;
            bool win32AccessDenied = (hresult & Win32FacilityMask) == Win32FacilityHResult && (hresult & Win32CodeMask) == ErrorAccessDenied;
            return win32AccessDenied || hresult == PosixEacces;
        }
    }
}
