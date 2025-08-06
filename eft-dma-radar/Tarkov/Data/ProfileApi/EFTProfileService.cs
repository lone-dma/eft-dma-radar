﻿using eft_dma_radar.DMA;
using eft_dma_radar.Tarkov.Data.ProfileApi.Providers;
using eft_dma_radar.Tarkov.Player;

namespace eft_dma_radar.Tarkov.Data.ProfileApi
{
    internal static class EFTProfileService
    {
        #region Fields / Constructor
        private static readonly Lock _syncRoot = new();
        private static readonly ConcurrentQueue<PlayerProfile> _profiles = new();
        private static CancellationTokenSource _cts = new();

        /// <summary>
        /// Persistent Cache Access.
        /// </summary>
        private static ConcurrentDictionary<string, CachedProfileData> Cache { get; } = App.Config.Cache.ProfileService;

        static EFTProfileService()
        {
            RuntimeHelpers.RunClassConstructor(typeof(EftApiTechProvider).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(TarkovDevProvider).TypeHandle);
            new Thread(Worker)
            {
                Priority = ThreadPriority.Lowest,
                IsBackground = true
            }.Start();
            MemDMA.ProcessStopped += MemDMA_ProcessStopped;
            // Cleanup Cache
            var expiredProfiles = Cache.Where(x => x.Value.IsExpired);
            foreach (var expired in expiredProfiles)
                Cache.TryRemove(expired.Key, out _);
        }

        private static void MemDMA_ProcessStopped(object sender, EventArgs e)
        {
            lock (_syncRoot)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = new();
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Attempt to register a Profile for lookup.
        /// </summary>
        /// <param name="accountId">Profile's Account ID.</param>
        public static void RegisterProfile(PlayerProfile profile)
        {
            if (!ulong.TryParse(profile.AccountID, out _))
                return; // Skip invalid Account IDs
            _profiles.Enqueue(profile);
        }

        #endregion

        #region Internal API
        private static void Worker()
        {
            while (true)
            {
                try
                {
                    if (MemDMA.WaitForProcess())
                    {
                        CancellationToken ct;
                        lock (_syncRoot)
                        {
                            ct = _cts.Token;
                        }
                        while (_profiles.TryDequeue(out var profile))
                        {
                            ct.ThrowIfCancellationRequested();
                            ProcessProfileAsync(profile, ct).GetAwaiter().GetResult();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[EFTProfileService] Unhandled Exception: {ex}");
                }
                finally
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(250));
                }
            }
        }

        /// <summary>
        /// Get profile data for a particular Account ID.
        /// </summary>
        /// <param name="profile">Profile to lookup.</param>
        private static async Task ProcessProfileAsync(PlayerProfile profile, CancellationToken ct)
        {
            if (Cache.TryGetValue(profile.AccountID, out var cachedProfile) && !cachedProfile.IsExpired)
            {
                profile.Data ??= cachedProfile.Data;
                return;
            }
            var usableProviders = IProfileApiProvider.AllProviders.Where(CanUseProvider);
            if (!usableProviders.Any())
                return;
            IProfileApiProvider provider = null;
            while ((provider = usableProviders
                .Where(x => x.CanRun)
                .OrderBy(x => x.Priority)
                .FirstOrDefault()) is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            var result = await provider.GetProfileAsync(profile.AccountID, ct);
            if (result is not null)
            {
                Cache[profile.AccountID] = new CachedProfileData()
                {
                    Data = result
                };
                profile.Data ??= result;
            }
            else if (IProfileApiProvider.AllProviders.Any(CanUseProvider))
            {
                _profiles.Enqueue(profile); // Re-queue for later processing
            }
            bool CanUseProvider(IProfileApiProvider provider) => provider.IsEnabled && provider.CanLookup(profile.AccountID);
        }

        #endregion
    }
}
