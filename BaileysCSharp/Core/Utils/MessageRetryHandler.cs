


using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BaileysCSharp.Core.Models;
using BaileysCSharp.Core.Types;
using BaileysCSharp.Core.WABinary;
using Proto;
using static BaileysCSharp.Core.Utils.GenericUtils;

namespace BaileysCSharp.Core.Utils
{
    /// <summary>
    /// Handles message retry mechanisms with exponential backoff
    /// </summary>
    public class MessageRetryHandler
    {
        private readonly Dictionary<string, MessageRetryContext> _retryContexts = new();
        private readonly Timer _cleanupTimer;
        private readonly object _lock = new();

        public class MessageRetryContext
        {
            public string MessageId { get; set; } = string.Empty;
            public string Jid { get; set; } = string.Empty;
            public Message Message { get; set; }
            public int AttemptCount { get; set; }
            public DateTime FirstAttempt { get; set; }
            public DateTime LastAttempt { get; set; }
            public DateTime NextAttempt { get; set; }
            public TimeSpan RetryDelay { get; set; }
            public bool IsActive { get; set; }
            public List<string> ErrorMessages { get; set; } = new();
            public MessageRelayOptions? RelayOptions { get; set; }
        }

        public MessageRetryHandler()
        {
            // Cleanup expired retry contexts every hour
            _cleanupTimer = new Timer(CleanupExpiredContexts, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }

        /// <summary>
        /// Add a message to the retry queue
        /// </summary>
        public void AddToRetryQueue(string messageId, string jid, Message message, MessageRelayOptions? relayOptions = null)
        {
            lock (_lock)
            {
                var context = new MessageRetryContext
                {
                    MessageId = messageId,
                    Jid = jid,
                    Message = message,
                    AttemptCount = 0,
                    FirstAttempt = DateTime.Now,
                    LastAttempt = DateTime.Now,
                    NextAttempt = DateTime.Now,
                    RetryDelay = TimeSpan.FromSeconds(5),
                    IsActive = true,
                    RelayOptions = relayOptions
                };

                _retryContexts[messageId] = context;
            }
        }

        /// <summary>
        /// Get messages that are ready for retry
        /// </summary>
        public List<MessageRetryContext> GetMessagesReadyForRetry()
        {
            lock (_lock)
            {
                return _retryContexts.Values
                    .Where(c => c.IsActive && c.NextAttempt <= DateTime.Now)
                    .ToList();
            }
        }

        /// <summary>
        /// Update retry context with attempt result
        /// </summary>
        public void UpdateRetryContext(string messageId, bool success, string? errorMessage = null)
        {
            lock (_lock)
            {
                if (_retryContexts.TryGetValue(messageId, out var context))
                {
                    if (success)
                    {
                        context.IsActive = false;
                        _retryContexts.Remove(messageId);
                    }
                    else
                    {
                        context.AttemptCount++;
                        context.LastAttempt = DateTime.Now;
                        context.RetryDelay = CalculateExponentialBackoff(context.AttemptCount);
                        context.NextAttempt = DateTime.Now + context.RetryDelay;
                        
                        if (!string.IsNullOrEmpty(errorMessage))
                        {
                            context.ErrorMessages.Add(errorMessage);
                        }

                        // Check if max retry attempts reached
                        if (context.AttemptCount >= 5)
                        {
                            context.IsActive = false;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Remove message from retry queue
        /// </summary>
        public void RemoveFromRetryQueue(string messageId)
        {
            lock (_lock)
            {
                _retryContexts.Remove(messageId);
            }
        }

        /// <summary>
        /// Get retry status for a specific message
        /// </summary>
        public MessageRetryContext? GetRetryStatus(string messageId)
        {
            lock (_lock)
            {
                return _retryContexts.TryGetValue(messageId, out var context) ? context : null;
            }
        }

        /// <summary>
        /// Get all active retry contexts
        /// </summary>
        public List<MessageRetryContext> GetAllActiveRetries()
        {
            lock (_lock)
            {
                return _retryContexts.Values.Where(c => c.IsActive).ToList();
            }
        }

        /// <summary>
        /// Calculate exponential backoff delay
        /// </summary>
        private TimeSpan CalculateExponentialBackoff(int attemptCount)
        {
            var baseDelay = TimeSpan.FromSeconds(5);
            var maxDelay = TimeSpan.FromMinutes(30);
            
            var delay = baseDelay * Math.Pow(2, attemptCount - 1);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds, maxDelay.TotalMilliseconds));
            
            // Add some jitter to prevent thundering herd
            var jitter = new Random().NextDouble() * 0.1 * delay.TotalMilliseconds;
            delay += TimeSpan.FromMilliseconds(jitter);

            return delay;
        }

        /// <summary>
        /// Cleanup expired retry contexts
        /// </summary>
        private void CleanupExpiredContexts(object? state)
        {
            lock (_lock)
            {
                var expiredKeys = _retryContexts
                    .Where(c => !c.IsActive && DateTime.Now - c.LastAttempt > TimeSpan.FromHours(24))
                    .Select(c => c.MessageId)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _retryContexts.Remove(key);
                }
            }
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
        }
    }

    /// <summary>
    /// Handles message history synchronization
    /// </summary>
    public class MessageHistorySyncHandler
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, HistorySyncContext> _syncContexts = new();

        public class HistorySyncContext
        {
            public string Jid { get; set; } = string.Empty;
            public DateTime LastSyncTime { get; set; }
            public bool IsSyncing { get; set; }
            public int SyncCount { get; set; }
            public List<WebMessageInfo> SyncedMessages { get; set; } = new();
            public DateTime LastSyncCompleted { get; set; }
            public string? LastError { get; set; }
        }

        /// <summary>
        /// Start history synchronization for a chat
        /// </summary>
        public async Task<List<WebMessageInfo>> StartHistorySync(string jid, DateTime? sinceTime = null)
        {
            lock (_lock)
            {
                if (_syncContexts.TryGetValue(jid, out var context) && context.IsSyncing)
                {
                    throw new InvalidOperationException($"History sync already in progress for {jid}");
                }

                context = new HistorySyncContext
                {
                    Jid = jid,
                    LastSyncTime = sinceTime ?? DateTime.MinValue,
                    IsSyncing = true,
                    SyncCount = 0
                };

                _syncContexts[jid] = context;
            }

            try
            {
                var messages = await PerformHistorySync(jid, sinceTime);
                
                lock (_lock)
                {
                    if (_syncContexts.TryGetValue(jid, out var context))
                    {
                        context.IsSyncing = false;
                        context.LastSyncCompleted = DateTime.Now;
                        context.SyncedMessages = messages;
                    }
                }

                return messages;
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    if (_syncContexts.TryGetValue(jid, out var context))
                    {
                        context.IsSyncing = false;
                        context.LastError = ex.Message;
                    }
                }
                throw;
            }
        }

        /// <summary>
        /// Get sync status for a chat
        /// </summary>
        public HistorySyncContext? GetSyncStatus(string jid)
        {
            lock (_lock)
            {
                return _syncContexts.TryGetValue(jid, out var context) ? context : null;
            }
        }

        /// <summary>
        /// Get all active sync contexts
        /// </summary>
        public List<HistorySyncContext> GetAllActiveSyncs()
        {
            lock (_lock)
            {
                return _syncContexts.Values.Where(c => c.IsSyncing).ToList();
            }
        }

        /// <summary>
        /// Perform actual history sync (placeholder for implementation)
        /// </summary>
        private async Task<List<WebMessageInfo>> PerformHistorySync(string jid, DateTime? sinceTime)
        {
            // This would integrate with WhatsApp's history sync API
            // For now, return empty list as placeholder
            await Task.Delay(100); // Simulate network call
            return new List<WebMessageInfo>();
        }
    }

    /// <summary>
    /// Handles message editing and deletion
    /// </summary>
    public class MessageEditHandler
    {
        private readonly Dictionary<string, MessageEditContext> _editContexts = new();
        private readonly object _lock = new();

        public class MessageEditContext
        {
            public string MessageId { get; set; } = string.Empty;
            public string Jid { get; set; } = string.Empty;
            public Message OriginalMessage { get; set; }
            public Message? EditedMessage { get; set; }
            public DateTime EditTime { get; set; }
            public bool IsDeleted { get; set; }
            public string? EditReason { get; set; }
            public int EditVersion { get; set; }
        }

        /// <summary>
        /// Edit a message
        /// </summary>
        public void EditMessage(string messageId, string jid, Message originalMessage, Message editedMessage, string? reason = null)
        {
            lock (_lock)
            {
                var context = new MessageEditContext
                {
                    MessageId = messageId,
                    Jid = jid,
                    OriginalMessage = originalMessage,
                    EditedMessage = editedMessage,
                    EditTime = DateTime.Now,
                    EditReason = reason,
                    EditVersion = 1
                };

                // Update existing context if it exists
                if (_editContexts.TryGetValue(messageId, out var existingContext))
                {
                    context.EditVersion = existingContext.EditVersion + 1;
                }

                _editContexts[messageId] = context;
            }
        }

        /// <summary>
        /// Delete a message
        /// </summary>
        public void DeleteMessage(string messageId, string jid, Message message, string? reason = null)
        {
            lock (_lock)
            {
                var context = new MessageEditContext
                {
                    MessageId = messageId,
                    Jid = jid,
                    OriginalMessage = message,
                    EditTime = DateTime.Now,
                    IsDeleted = true,
                    EditReason = reason
                };

                _editContexts[messageId] = context;
            }
        }

        /// <summary>
        /// Get edit context for a message
        /// </summary>
        public MessageEditContext? GetEditContext(string messageId)
        {
            lock (_lock)
            {
                return _editContexts.TryGetValue(messageId, out var context) ? context : null;
            }
        }

        /// <summary>
        /// Get all edit contexts for a chat
        /// </summary>
        public List<MessageEditContext> GetEditContextsForChat(string jid)
        {
            lock (_lock)
            {
                return _editContexts.Values.Where(c => c.Jid == jid).ToList();
            }
        }

        /// <summary>
        /// Check if message was edited
        /// </summary>
        public bool IsMessageEdited(string messageId)
        {
            lock (_lock)
            {
                return _editContexts.TryGetValue(messageId, out var context) && 
                       context.EditedMessage != null && 
                       !context.IsDeleted;
            }
        }

        /// <summary>
        /// Check if message was deleted
        /// </summary>
        public bool IsMessageDeleted(string messageId)
        {
            lock (_lock)
            {
                return _editContexts.TryGetValue(messageId, out var context) && context.IsDeleted;
            }
        }
    }

    /// <summary>
    /// Handles message reactions
    /// </summary>
    public class MessageReactionHandler
    {
        private readonly Dictionary<string, List<MessageReactionContext>> _reactions = new();
        private readonly object _lock = new();

        public class MessageReactionContext
        {
            public string MessageId { get; set; } = string.Empty;
            public string ReactionJid { get; set; } = string.Empty;
            public string ReactionEmoji { get; set; } = string.Empty;
            public DateTime ReactionTime { get; set; }
            public bool IsRemoved { get; set; }
            public string? ReactionId { get; set; }
        }

        /// <summary>
        /// Add or update a reaction to a message
        /// </summary>
        public void AddReaction(string messageId, string reactionJid, string emoji, string? reactionId = null)
        {
            lock (_lock)
            {
                if (!_reactions.ContainsKey(messageId))
                {
                    _reactions[messageId] = new List<MessageReactionContext>();
                }

                // Remove existing reaction from same user if it exists
                _reactions[messageId].RemoveAll(r => r.ReactionJid == reactionJid);

                // Add new reaction
                var reaction = new MessageReactionContext
                {
                    MessageId = messageId,
                    ReactionJid = reactionJid,
                    ReactionEmoji = emoji,
                    ReactionTime = DateTime.Now,
                    ReactionId = reactionId
                };

                _reactions[messageId].Add(reaction);
            }
        }

        /// <summary>
        /// Remove a reaction from a message
        /// </summary>
        public void RemoveReaction(string messageId, string reactionJid)
        {
            lock (_lock)
            {
                if (_reactions.TryGetValue(messageId, out var reactions))
                {
                    var reaction = reactions.FirstOrDefault(r => r.ReactionJid == reactionJid);
                    if (reaction != null)
                    {
                        reaction.IsRemoved = true;
                        reaction.ReactionTime = DateTime.Now;
                    }
                }
            }
        }

        /// <summary>
        /// Get all reactions for a message
        /// </summary>
        public List<MessageReactionContext> GetReactions(string messageId)
        {
            lock (_lock)
            {
                return _reactions.TryGetValue(messageId, out var reactions) 
                    ? reactions.Where(r => !r.IsRemoved).ToList() 
                    : new List<MessageReactionContext>();
            }
        }

        /// <summary>
        /// Get reaction count for a message
        /// </summary>
        public int GetReactionCount(string messageId)
        {
            lock (_lock)
            {
                return _reactions.TryGetValue(messageId, out var reactions) 
                    ? reactions.Count(r => !r.IsRemoved) 
                    : 0;
            }
        }

        /// <summary>
        /// Get all messages with reactions
        /// </summary>
        public Dictionary<string, List<MessageReactionContext>> GetAllReactions()
        {
            lock (_lock)
            {
                var result = new Dictionary<string, List<MessageReactionContext>>();
                foreach (var kvp in _reactions)
                {
                    var activeReactions = kvp.Value.Where(r => !r.IsRemoved).ToList();
                    if (activeReactions.Any())
                    {
                        result[kvp.Key] = activeReactions;
                    }
                }
                return result;
            }
        }
    }
}


