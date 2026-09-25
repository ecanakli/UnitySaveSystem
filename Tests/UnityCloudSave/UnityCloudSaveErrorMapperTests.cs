using System;
using System.Reflection;
using Ecanakli.SaveSystem;
using NUnit.Framework;
using Unity.Services.CloudSave;
using Unity.Services.Core;

namespace Ecanakli.SaveSystem.UnityCloudSave.Tests
{
    /// <summary>Pure exception -> CloudError classification. No SDK calls; no network.</summary>
    [TestFixture]
    public sealed class UnityCloudSaveErrorMapperTests
    {
        [TestCase(CloudSaveExceptionReason.NoInternetConnection, CloudErrorKind.Transient)]
        [TestCase(CloudSaveExceptionReason.ServiceUnavailable, CloudErrorKind.Transient)]
        [TestCase(CloudSaveExceptionReason.ProjectIdMissing, CloudErrorKind.NotSignedIn)]
        [TestCase(CloudSaveExceptionReason.PlayerIdMissing, CloudErrorKind.NotSignedIn)]
        [TestCase(CloudSaveExceptionReason.AccessTokenMissing, CloudErrorKind.NotSignedIn)]
        [TestCase(CloudSaveExceptionReason.Unauthorized, CloudErrorKind.Unauthorized)]
        [TestCase(CloudSaveExceptionReason.KeyLimitExceeded, CloudErrorKind.QuotaExceeded)]
        [TestCase(CloudSaveExceptionReason.TooManyRequests, CloudErrorKind.RateLimited)]
        [TestCase(CloudSaveExceptionReason.Conflict, CloudErrorKind.Conflict)]
        [TestCase(CloudSaveExceptionReason.InvalidArgument, CloudErrorKind.PayloadTooLarge)]
        [TestCase(CloudSaveExceptionReason.NotFound, CloudErrorKind.Permanent)]
        [TestCase(CloudSaveExceptionReason.Unknown, CloudErrorKind.Permanent)]
        public void MapReason_CoversEveryReason(CloudSaveExceptionReason reason, CloudErrorKind expected)
        {
            Assert.That(UnityCloudSaveErrorMapper.MapReason(reason), Is.EqualTo(expected));
        }

        [Test]
        public void Map_ServicesInitializationException_ReturnsNotSignedIn()
        {
            CloudError error = UnityCloudSaveErrorMapper.Map(new ServicesInitializationException("core not initialized"));

            Assert.That(error.Kind, Is.EqualTo(CloudErrorKind.NotSignedIn));
        }

        [Test]
        public void Map_UnknownExceptionType_ReturnsPermanent()
        {
            CloudError error = UnityCloudSaveErrorMapper.Map(new InvalidOperationException("boom"));

            Assert.That(error.Kind, Is.EqualTo(CloudErrorKind.Permanent));
            Assert.That(error.IsRetryable, Is.False);
        }

        [Test]
        public void Map_RateLimitedException_ReturnsRateLimitedWithRetryAfter()
        {
            CloudSaveRateLimitedException exception = CreateRateLimited(12.5f);

            CloudError error = UnityCloudSaveErrorMapper.Map(exception);

            Assert.That(error.Kind, Is.EqualTo(CloudErrorKind.RateLimited));
            Assert.That(error.IsRetryable, Is.True);
            Assert.That(error.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(12.5)));
        }

        [Test]
        public void Map_RateLimitedException_NegativeRetryAfter_ClampsToZero()
        {
            // The SDK's RateLimiter should never report a negative RetryAfter, but never surface a negative TimeSpan either.
            CloudSaveRateLimitedException exception = CreateRateLimited(-1f);

            CloudError error = UnityCloudSaveErrorMapper.Map(exception);

            Assert.That(error.RetryAfter, Is.EqualTo(TimeSpan.Zero));
        }

        [Test]
        public void SiblingBatchFailure_IsTransientAndRetryable()
        {
            CloudError error = UnityCloudSaveErrorMapper.SiblingBatchFailure("some-key");

            Assert.That(error.Kind, Is.EqualTo(CloudErrorKind.Transient));
            Assert.That(error.IsRetryable, Is.True);
            Assert.That(error.Message, Does.Contain("some-key"));
        }

        // CloudSaveRateLimitedException's constructor is internal to Unity.Services.CloudSave; reflection is the
        // only way to build one outside a live service response, and this stays a pure, offline construction.
        private static CloudSaveRateLimitedException CreateRateLimited(float retryAfterSeconds)
        {
            object instance = Activator.CreateInstance(
                typeof(CloudSaveRateLimitedException),
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new object[] { CloudSaveExceptionReason.TooManyRequests, 429, "rate limited", retryAfterSeconds, null },
                null);

            return (CloudSaveRateLimitedException)instance;
        }
    }
}
