using System;

namespace Ecanakli.SaveSystem
{
    /// <summary>Identifies a save profile: Guest, Account(id) or Local(name).</summary>
    public readonly struct ProfileId : IEquatable<ProfileId>
    {
        /// <summary>Maximum length of a local profile name.</summary>
        public const int MaxLocalNameLength = 32;

        private readonly string _value;

        private ProfileId(ProfileKind kind, string value)
        {
            Kind = kind;
            _value = value;
        }

        /// <summary>The signed-out guest profile. Also the default value.</summary>
        public static ProfileId Guest => default;

        public ProfileKind Kind { get; }

        /// <summary>Account id; null unless Kind is Account.</summary>
        public string AccountId => Kind == ProfileKind.Account ? _value : null;

        /// <summary>Local profile name; null unless Kind is Local.</summary>
        public string LocalName => Kind == ProfileKind.Local ? _value : null;

        /// <summary>True only for account profiles.</summary>
        public bool IsCloudBacked => Kind == ProfileKind.Account;

        /// <summary>Creates an account profile. Throws ArgumentException on a null or empty id.</summary>
        public static ProfileId Account(string accountId)
        {
            if (string.IsNullOrEmpty(accountId))
            {
                throw new ArgumentException("Account id must not be null or empty.", nameof(accountId));
            }

            return new ProfileId(ProfileKind.Account, accountId);
        }

        /// <summary>Creates a local profile. Throws ArgumentException unless the name matches ^[a-z0-9_-]{1,32}$.</summary>
        public static ProfileId Local(string name)
        {
            if (!IsValidLocalName(name))
            {
                throw new ArgumentException(
                    "Local profile name must match ^[a-z0-9_-]{1,32}$ (lower case only).", nameof(name));
            }

            return new ProfileId(ProfileKind.Local, name);
        }

        /// <summary>True when the name matches ^[a-z0-9_-]{1,32}$.</summary>
        public static bool IsValidLocalName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxLocalNameLength)
            {
                return false;
            }

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool valid = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        public bool Equals(ProfileId other)
        {
            return Kind == other.Kind && string.Equals(_value, other._value, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is ProfileId other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Kind * 397;
                return _value == null ? hash : hash ^ StringComparer.Ordinal.GetHashCode(_value);
            }
        }

        public static bool operator ==(ProfileId left, ProfileId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ProfileId left, ProfileId right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case ProfileKind.Account:
                    return "account:" + _value;
                case ProfileKind.Local:
                    return "local:" + _value;
                default:
                    return "guest";
            }
        }
    }
}
