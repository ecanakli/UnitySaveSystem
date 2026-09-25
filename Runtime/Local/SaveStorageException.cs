using System;
using System.IO;

namespace Ecanakli.SaveSystem
{
    /// <summary>Storage failure with an explicit classification; custom ISaveStorage implementations may throw it.</summary>
    public class SaveStorageException : IOException
    {
        /// <summary>Creates an exception classified as kind.</summary>
        public SaveStorageException(LocalWriteErrorKind kind, string message, Exception inner = null)
            : base(message, inner)
        {
            Kind = kind;
        }

        /// <summary>Classification used instead of exception inspection.</summary>
        public LocalWriteErrorKind Kind { get; }
    }
}
