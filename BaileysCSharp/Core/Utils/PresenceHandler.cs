


using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BaileysCSharp.Core.Models;
using BaileysCSharp.Core.Types;
using BaileysCSharp.Core.WABinary;
using Proto;

namespace BaileysCSharp.Core.Utils
{
    /// <summary>
    /// Handles presence and status features including online tracking, last seen, and status stories
    /// </summary>
    public class PresenceHandler
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, PresenceContext> _presenceContexts = new();
        private readonly Dictionary<string, StatusContext> _statusContexts = new();
        private readonly object _lock = new();
        private readonly Timer _presenceCleanupTimer;

        public class PresenceContext
        {
            public string Jid { get; set; } = string.Empty;
            public PresenceStatus Status { get; set; }
            public DateTime LastSeen { get; set; }
            public DateTime LastUpdated { get; set; }
            public string? DeviceStatus { get; set; }
            public bool IsOnline { get; set; }
            public TimeSpan? OnlineDuration { get; set; }
            public Dictionary<string, object> AdditionalData { get; set; } = new();
        }

        public class StatusContext
        {
            public string Jid { get; set; } = string.Empty;
            public string StatusText { get; set; } = string.Empty;
            public DateTime StatusSetTime { get; set; }
            public List<StatusStory> Stories { get; set; } = new();
            public bool IsStatusPrivacyEnabled { get; set; }
            public StatusPrivacyLevel PrivacyLevel { get; set; }
        }

        public class StatusStory
        {
            public string StoryId { get; set; } = string.Empty;
            public string MediaPath { get; set; } = string.Empty;
            public string Caption { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public DateTime ExpiresAt { get; set; }
            public StoryType Type { get; set; }
            public List<string> Viewers { get; set; } = new();
            public int ViewCount { get; set; }
            public bool IsViewed { get; set; }
        }

        public enum PresenceStatus
        {
            Available,
            Unavailable,
            Busy,
            Away,
            Offline
        }

        public enum StatusPrivacyLevel
        {
            Everyone,
            Contacts,
            ContactsExcept,
            Nobody
        }

        public enum StoryType
        {
            Image,
            Video,
            Text
        }

        public class PresenceUpdateEventArgs : EventArgs
        {
            public string Jid { get; set; } = string.Empty;
            public PresenceStatus Status { get; set; }
            public DateTime LastSeen { get; set; }
            public bool IsOnline { get; set; }
            public Dictionary<string, object> AdditionalData { get; set; } = new();
        }

        public class StatusUpdateEventArgs : EventArgs
        {
            public string Jid { get; set; } = string.Empty;
            public string StatusText { get; set; } = string.Empty;
            public DateTime StatusSetTime { get; set; }
            public bool IsNewStatus { get; set; }
        }

        public class StoryUpdateEventArgs : EventArgs
        {
            public string Jid { get; set; } = string.Empty;
            public StatusStory Story { get; set; }
            public StoryUpdateType UpdateType { get; set; }
        }

        public enum StoryUpdateType
        {
            Added,
            Removed,
            Viewed,
            Expired
        }

        public event EventHandler<PresenceUpdateEventArgs>? PresenceUpdated;
        public event EventHandler<StatusUpdateEventArgs>? StatusUpdated;
        public event EventHandler<StoryUpdateEventArgs>? StoryUpdated;

        public PresenceHandler(ILogger logger)
        {
            _logger = logger;
            
            // Start cleanup timer - run every hour
            _presenceCleanupTimer = new Timer(CleanupExpiredData, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }

        #region Presence Management

        /// <summary>
        /// Update presence for a contact
        /// </summary>
        public void UpdatePresence(string jid, PresenceStatus status, bool isOnline = false, Dictionary<string, object>? additionalData = null)
        {
            lock (_lock)
            {
                var context = GetOrCreatePresenceContext(jid);
                
                var wasOnline = context.IsOnline;
                var statusChanged = context.Status != status;
                var onlineStatusChanged = wasOnline != isOnline;

                context.Status = status;
                context.IsOnline = isOnline;
                context.LastUpdated = DateTime.Now;
                
                if (isOnline && !wasOnline)
                {
                    // User came online
                    context.OnlineDuration = null;
                }
                else if (!isOnline && wasOnline)
                {
                    // User went offline
                    if (context.OnlineDuration.HasValue)
                    {
                        context.LastSeen = DateTime.Now - context.OnlineDuration.Value;
                    }
                    else
                    {
                        context.LastSeen = DateTime.Now;
                    }
                }
                else if (isOnline && context.OnlineDuration == null)
                {
                    // Track online duration
                    context.OnlineDuration = TimeSpan.Zero;
                }

                if (additionalData != null)
                {
                    foreach (var data in additionalData)
                    {
                        context.AdditionalData[data.Key] = data.Value;
                    }
                }

                // Emit events if something changed
                if (statusChanged || onlineStatusChanged)
                {
                    EmitPresenceUpdateEvent(context);
                }

                _logger.Debug($"Presence updated for {jid}: {status}, Online: {isOnline}");
            }
        }

        /// <summary>
        /// Track online presence duration
        /// </summary>
        public void TrackOnlineDuration(string jid)
        {
            lock (_lock)
            {
                if (_presenceContexts.TryGetValue(jid, out var context) && context.IsOnline)
                {
                    if (context.OnlineDuration == null)
                    {
                        context.OnlineDuration = TimeSpan.Zero;
                    }
                    else
                    {
                        context.OnlineDuration += TimeSpan.FromSeconds(1);
                    }
                }
            }
        }

        /// <summary>
        /// Get presence information for a contact
        /// </summary>
        public PresenceContext? GetPresence(string jid)
        {
            lock (_lock)
            {
                return _presenceContexts.TryGetValue(jid, out var context) ? context : null;
            }
        }

        /// <summary>
        /// Get all online contacts
        /// </summary>
        public List<PresenceContext> GetOnlineContacts()
        {
            lock (_lock)
            {
                return _presenceContexts.Values.Where(c => c.IsOnline).ToList();
            }
        }

        /// <summary>
        /// Get presence status for multiple contacts
        /// </summary>
        public Dictionary<string, PresenceContext> GetPresenceStatus(string[] jids)
        {
            lock (_lock)
            {
                var result = new Dictionary<string, PresenceContext>();
                foreach (var jid in jids)
                {
                    if (_presenceContexts.TryGetValue(jid, out var context))
                    {
                        result[jid] = context;
                    }
                }
                return result;
            }
        }

        /// <summary>
        /// Set own presence status
        /// </summary>
        public void SetOwnPresence(PresenceStatus status, string? statusMessage = null)
        {
            // This would send presence update to WhatsApp servers
            // For now, just update local state
            UpdatePresence("self", status, status != PresenceStatus.Offline);
            
            _logger.Info($"Own presence set to: {status}");
        }

        /// <summary>
        /// Subscribe to presence updates for contacts
        /// </summary>
        public void SubscribeToPresence(string[] jids)
        {
            // This would send presence subscription requests to WhatsApp servers
            foreach (var jid in jids)
            {
                _logger.Debug($"Subscribed to presence updates for: {jid}");
            }
        }

        /// <summary>
        /// Unsubscribe from presence updates
        /// </summary>
        public void UnsubscribeFromPresence(string[] jids)
        {
            // This would send presence unsubscription requests to WhatsApp servers
            foreach (var jid in jids)
            {
                _logger.Debug($"Unsubscribed from presence updates for: {jid}");
            }
        }

        #endregion

        #region Status Management

        /// <summary>
        /// Update status text for a contact
        /// </summary>
        public void UpdateStatus(string jid, string statusText)
        {
            lock (_lock)
            {
                var context = GetOrCreateStatusContext(jid);
                var wasNewStatus = string.IsNullOrEmpty(context.StatusText);
                
                context.StatusText = statusText;
                context.StatusSetTime = DateTime.Now;

                EmitStatusUpdateEvent(context, wasNewStatus);
                
                _logger.Debug($"Status updated for {jid}: {statusText}");
            }
        }

        /// <summary>
        /// Get status information for a contact
        /// </summary>
        public StatusContext? GetStatus(string jid)
        {
            lock (_lock)
            {
                return _statusContexts.TryGetValue(jid, out var context) ? context : null;
            }
        }

        /// <summary>
        /// Set own status text
        /// </summary>
        public void SetOwnStatus(string statusText)
        {
            UpdateStatus("self", statusText);
            _logger.Info($"Own status set to: {statusText}");
        }

        /// <summary>
        /// Set status privacy level
        /// </summary>
        public void SetStatusPrivacy(StatusPrivacyLevel privacyLevel, string[]? exceptContacts = null)
        {
            lock (_lock)
            {
                var context = GetOrCreateStatusContext("self");
                context.PrivacyLevel = privacyLevel;
                context.IsStatusPrivacyEnabled = privacyLevel != StatusPrivacyLevel.Everyone;
                
                _logger.Info($"Status privacy set to: {privacyLevel}");
            }
        }

        /// <summary>
        /// Get status privacy settings
        /// </summary>
        public StatusPrivacyLevel? GetStatusPrivacy()
        {
            lock (_lock)
            {
                return _statusContexts.TryGetValue("self", out var context) ? context.PrivacyLevel : null;
            }
        }

        #endregion

        #region Status Stories Management

        /// <summary>
        /// Add a status story
        /// </summary>
        public void AddStatusStory(string jid, StatusStory story)
        {
            lock (_lock)
            {
                var context = GetOrCreateStatusContext(jid);
                context.Stories.Add(story);

                EmitStoryUpdateEvent(jid, story, StoryUpdateType.Added);
                
                _logger.Debug($"Status story added for {jid}: {story.StoryId}");
            }
        }

        /// <summary>
        /// Remove a status story
        /// </summary>
        public void RemoveStatusStory(string jid, string storyId)
        {
            lock (_lock)
            {
                if (_statusContexts.TryGetValue(jid, out var context))
                {
                    var story = context.Stories.FirstOrDefault(s => s.StoryId == storyId);
                    if (story != null)
                    {
                        context.Stories.Remove(story);
                        EmitStoryUpdateEvent(jid, story, StoryUpdateType.Removed);
                        
                        _logger.Debug($"Status story removed for {jid}: {storyId}");
                    }
                }
            }
        }

        /// <summary>
        /// Mark story as viewed
        /// </summary>
        public void MarkStoryAsViewed(string jid, string storyId, string viewerJid)
        {
            lock (_lock)
            {
                if (_statusContexts.TryGetValue(jid, out var context))
                {
                    var story = context.Stories.FirstOrDefault(s => s.StoryId == storyId);
                    if (story != null && !story.Viewers.Contains(viewerJid))
                    {
                        story.Viewers.Add(viewerJid);
                        story.ViewCount++;
                        story.IsViewed = true;
                        
                        EmitStoryUpdateEvent(jid, story, StoryUpdateType.Viewed);
                        
                        _logger.Debug($"Story marked as viewed: {storyId} by {viewerJid}");
                    }
                }
            }
        }

        /// <summary>
        /// Get all active stories for a contact
        /// </summary>
        public List<StatusStory> GetActiveStories(string jid)
        {
            lock (_lock)
            {
                if (_statusContexts.TryGetValue(jid, out var context))
                {
                    return context.Stories.Where(s => s.ExpiresAt > DateTime.Now).ToList();
                }
                return new List<StatusStory>();
            }
        }

        /// <summary>
        /// Get all stories from contacts
        /// </summary>
        public Dictionary<string, List<StatusStory>> GetAllActiveStories()
        {
            lock (_lock)
            {
                var result = new Dictionary<string, List<StatusStory>>();
                foreach (var context in _statusContexts.Values)
                {
                    var activeStories = context.Stories.Where(s => s.ExpiresAt > DateTime.Now).ToList();
                    if (activeStories.Any())
                    {
                        result[context.Jid] = activeStories;
                    }
                }
                return result;
            }
        }

        /// <summary>
        /// Create a new status story
        /// </summary>
        public StatusStory CreateStatusStory(string mediaPath, string caption = "", StoryType type = StoryType.Image, TimeSpan? duration = null)
        {
            var storyId = GenerateStoryId();
            var expiresAt = DateTime.Now.AddHours(24); // Stories expire after 24 hours

            return new StatusStory
            {
                StoryId = storyId,
                MediaPath = mediaPath,
                Caption = caption,
                CreatedAt = DateTime.Now,
                ExpiresAt = expiresAt,
                Type = type,
                ViewCount = 0,
                IsViewed = false
            };
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Get or create presence context
        /// </summary>
        private PresenceContext GetOrCreatePresenceContext(string jid)
        {
            if (!_presenceContexts.TryGetValue(jid, out var context))
            {
                context = new PresenceContext
                {
                    Jid = jid,
                    Status = PresenceStatus.Unavailable,
                    LastSeen = DateTime.MinValue,
                    LastUpdated = DateTime.Now,
                    IsOnline = false
                };
                _presenceContexts[jid] = context;
            }
            return context;
        }

        /// <summary>
        /// Get or create status context
        /// </summary>
        private StatusContext GetOrCreateStatusContext(string jid)
        {
            if (!_statusContexts.TryGetValue(jid, out var context))
            {
                context = new StatusContext
                {
                    Jid = jid,
                    StatusText = "",
                    StatusSetTime = DateTime.Now,
                    PrivacyLevel = StatusPrivacyLevel.Everyone
                };
                _statusContexts[jid] = context;
            }
            return context;
        }

        /// <summary>
        /// Generate unique story ID
        /// </summary>
        private string GenerateStoryId()
        {
            return Guid.NewGuid().ToString("N")[..16];
        }

        /// <summary>
        /// Emit presence update event
        /// </summary>
        private void EmitPresenceUpdateEvent(PresenceContext context)
        {
            PresenceUpdated?.Invoke(this, new PresenceUpdateEventArgs
            {
                Jid = context.Jid,
                Status = context.Status,
                LastSeen = context.LastSeen,
                IsOnline = context.IsOnline,
                AdditionalData = context.AdditionalData
            });
        }

        /// <summary>
        /// Emit status update event
        /// </summary>
        private void EmitStatusUpdateEvent(StatusContext context, bool isNewStatus)
        {
            StatusUpdated?.Invoke(this, new StatusUpdateEventArgs
            {
                Jid = context.Jid,
                StatusText = context.StatusText,
                StatusSetTime = context.StatusSetTime,
                IsNewStatus = isNewStatus
            });
        }

        /// <summary>
        /// Emit story update event
        /// </summary>
        private void EmitStoryUpdateEvent(string jid, StatusStory story, StoryUpdateType updateType)
        {
            StoryUpdated?.Invoke(this, new StoryUpdateEventArgs
            {
                Jid = jid,
                Story = story,
                UpdateType = updateType
            });
        }

        /// <summary>
        /// Cleanup expired data
        /// </summary>
        private void CleanupExpiredData(object? state)
        {
            lock (_lock)
            {
                // Clean up expired stories
                foreach (var context in _statusContexts.Values)
                {
                    var expiredStories = context.Stories.Where(s => s.ExpiresAt <= DateTime.Now).ToList();
                    foreach (var story in expiredStories)
                    {
                        context.Stories.Remove(story);
                        EmitStoryUpdateEvent(context.Jid, story, StoryUpdateType.Expired);
                    }
                }

                // Clean up old presence contexts (older than 30 days)
                var oldPresenceKeys = _presenceContexts
                    .Where(c => DateTime.Now - c.Value.LastUpdated > TimeSpan.FromDays(30))
                    .Select(c => c.Key)
                    .ToList();

                foreach (var key in oldPresenceKeys)
                {
                    _presenceContexts.Remove(key);
                }

                _logger.Debug("Cleanup of expired presence and status data completed");
            }
        }

        /// <summary>
        /// Start presence tracking timer
        /// </summary>
        public void StartPresenceTracking()
        {
            // Start a timer to track online durations
            var trackingTimer = new Timer(_ =>
            {
                var onlineContacts = GetOnlineContacts();
                foreach (var contact in onlineContacts)
                {
                    TrackOnlineDuration(contact.Jid);
                }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Get presence statistics
        /// </summary>
        public Dictionary<string, object> GetPresenceStatistics()
        {
            lock (_lock)
            {
                var totalContacts = _presenceContexts.Count;
                var onlineContacts = _presenceContexts.Values.Count(c => c.IsOnline);
                var availableContacts = _presenceContexts.Values.Count(c => c.Status == PresenceStatus.Available);
                var busyContacts = _presenceContexts.Values.Count(c => c.Status == PresenceStatus.Busy);
                var awayContacts = _presenceContexts.Values.Count(c => c.Status == PresenceStatus.Away);

                return new Dictionary<string, object>
                {
                    { "TotalContacts", totalContacts },
                    { "OnlineContacts", onlineContacts },
                    { "OfflineContacts", totalContacts - onlineContacts },
                    { "AvailableContacts", availableContacts },
                    { "BusyContacts", busyContacts },
                    { "AwayContacts", awayContacts }
                };
            }
        }

        #endregion

        public void Dispose()
        {
            _presenceCleanupTimer?.Dispose();
        }
    }
}




