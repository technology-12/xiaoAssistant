using System;

namespace WorkAssistant.Infrastructure
{
    public static class ModelCapabilityCache
    {
        public static bool IsFresh(ModelProfile profile, ModelSettings settings, DateTime utc) =>
            profile.VerifiedFingerprint == settings.Fingerprint() && profile.LastVerifiedUtc.HasValue &&
            utc >= profile.LastVerifiedUtc.Value && utc - profile.LastVerifiedUtc.Value < TimeSpan.FromHours(24);
        public static void Apply(ModelProfile profile, ModelSettings settings, ConnectionTestResult result, DateTime utc)
        {
            if (result.AuthChecked && !result.AuthOk)
            { profile.VerifiedFingerprint = ""; profile.LastVerifiedUtc = null; profile.ToolCallingVerified = false; profile.StreamingSupported = null; return; }
            if (!result.AuthOk || !result.ModelOk || !result.ToolCallingChecked) return;
            profile.VerifiedFingerprint = settings.Fingerprint(); profile.LastVerifiedUtc = utc;
            profile.ToolCallingVerified = result.ToolCallingOk; profile.StreamingSupported = result.StreamingSupported;
        }
    }
}
