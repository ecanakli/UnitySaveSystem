using System;
using System.Security.Cryptography;

namespace Ecanakli.SaveSystem
{
    /// <summary>Lowercase hex SHA-256 over raw payload bytes.</summary>
    internal static class PayloadChecksum
    {
        /// <summary>Length of a hex SHA-256 string.</summary>
        public const int HexLength = 64;

        private const string HexDigits = "0123456789abcdef";

        /// <summary>Hashes a whole array.</summary>
        public static string Compute(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            return Compute(bytes, 0, bytes.Length);
        }

        /// <summary>Hashes a byte range.</summary>
        public static string Compute(byte[] bytes, int offset, int count)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (offset < 0 || count < 0 || offset > bytes.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Range is outside the array.");
            }

            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(bytes, offset, count);
            }

            return ToHex(hash);
        }

        /// <summary>True when the text is 64 hex characters (either case).</summary>
        public static bool IsWellFormed(string hex)
        {
            if (hex == null || hex.Length != HexLength)
            {
                return false;
            }

            for (int i = 0; i < hex.Length; i++)
            {
                char c = hex[i];
                bool valid = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Case-insensitive comparison of an expected hex hash with the hash of a byte range.</summary>
        public static bool Matches(string expectedHex, byte[] bytes, int offset, int count)
        {
            return IsWellFormed(expectedHex)
                && string.Equals(expectedHex, Compute(bytes, offset, count), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Lowercase hex encoding.</summary>
        public static string ToHex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = HexDigits[bytes[i] >> 4];
                chars[(i * 2) + 1] = HexDigits[bytes[i] & 0x0F];
            }

            return new string(chars);
        }
    }
}
