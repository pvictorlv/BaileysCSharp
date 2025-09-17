using Proto;
using System.Diagnostics.CodeAnalysis;
using BaileysCSharp.Core.Models;
using BaileysCSharp.Core.Utils;
using static BaileysCSharp.Core.Utils.MessageUtil;
using static BaileysCSharp.Core.Utils.GenericUtils;
using static BaileysCSharp.Core.Utils.JidUtils;
using static BaileysCSharp.Core.WABinary.Constants;
using static BaileysCSharp.Core.Utils.ValidateConnectionUtil;
using static BaileysCSharp.Core.Utils.SignalUtils;
using BaileysCSharp.Core.Extensions;
using System.Collections.Generic;
using BaileysCSharp.Core.Models.Sessions;
using BaileysCSharp.Core.Signal;
using Google.Protobuf;
using BaileysCSharp.Core.Events;
using BaileysCSharp.LibSignal;
using BaileysCSharp.Core.Models.Sending;
using BaileysCSharp.Core.Models.Sending.Interfaces;
using BaileysCSharp.Core.Models.SenderKeys;
using BaileysCSharp.Core.Helper;
using Org.BouncyCastle.Asn1.X509;
using BaileysCSharp.Core.Types;
using BaileysCSharp.Core.WABinary;

namespace BaileysCSharp.Core.Sockets
{
    public class ParticipantNode
    {
        public BinaryNode[] Nodes { get; set; }
        public bool ShouldIncludeDeviceIdentity { get; set; }
    }

    public abstract class MessagesSendSocket : GroupSocket
    {
        public new void Dispose()
        {
            userDevicesCache.Dispose();
            base.Dispose();
        }

        public MediaConnInfo CurrentMedia { get; set; }

        NodeCache userDevicesCache = new NodeCache();
        
        // Message handling utilities
        private readonly MessageRetryHandler _retryHandler = new();
        private readonly MessageHistorySyncHandler _historySyncHandler = new();
        private readonly MessageEditHandler _editHandler = new();
        private readonly MessageReactionHandler _reactionHandler = new();
        private readonly MediaHandler _mediaHandler;
        private readonly PresenceHandler _presenceHandler;
        private readonly Timer _retryTimer;

        public MessagesSendSocket([NotNull] SocketConfig config) : base(config)
        {
            // Initialize handlers
            _mediaHandler = new MediaHandler(Logger, SocketConfig);
            _presenceHandler = new PresenceHandler(Logger);
            
            // Start retry timer - check for messages to retry every 10 seconds
            _retryTimer = new Timer(ProcessRetryQueue, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            
            // Start presence tracking
            _presenceHandler.StartPresenceTracking();
            
            // Subscribe to presence events
            _presenceHandler.PresenceUpdated += OnPresenceUpdated;
            _presenceHandler.StatusUpdated += OnStatusUpdated;
            _presenceHandler.StoryUpdated += OnStoryUpdated;
        }

        #region Enhanced Message Handling

        /// <summary>
        /// Send message with enhanced retry mechanism
        /// </summary>
        public async Task<WebMessageInfo?> SendMessageWithRetry(string jid, IAnyMessageContent content, IMiscMessageGenerationOptions? options = null, bool enableRetry = true)
        {
            var result = await SendMessage(jid, content, options);
            
            if (result != null && enableRetry)
            {
                // Add to retry queue if message was sent
                _retryHandler.AddToRetryQueue(result.Key.Id, jid, result.Message, new MessageRelayOptions());
            }
            
            return result;
        }

        /// <summary>
        /// Edit an existing message
        /// </summary>
        public async Task<WebMessageInfo?> EditMessage(string jid, string messageId, IAnyMessageContent newContent)
        {
            var originalMessage = Store.GetMessage(new MessageKey
            {
                RemoteJid = jid,
                Id = messageId
            });

            if (originalMessage == null)
            {
                throw new Exception($"Original message not found: {messageId}");
            }

            var editContent = newContent as IEditable;
            if (editContent == null)
            {
                throw new Exception("Message content does not support editing");
            }

            // Set edit information
            editContent.Edit = messageId;

            var result = await SendMessage(jid, newContent);
            
            if (result != null)
            {
                // Record the edit
                _editHandler.EditMessage(messageId, jid, originalMessage.Message, result.Message, "User edited message");
                
                Logger.Info($"Message edited: {messageId} in chat {jid}");
            }

            return result;
        }

        /// <summary>
        /// Delete a message for everyone
        /// </summary>
        public async Task<bool> DeleteMessage(string jid, string messageId, bool everyone = true)
        {
            var message = Store.GetMessage(new MessageKey
            {
                RemoteJid = jid,
                Id = messageId
            });

            if (message == null)
            {
                Logger.Warning($"Message not found for deletion: {messageId}");
                return false;
            }

            var deleteContent = new DeleteMessage
            {
                Delete = new MessageKey
                {
                    RemoteJid = jid,
                    Id = messageId,
                    FromMe = message.Key.FromMe
                }
            };

            try
            {
                await SendMessage(jid, deleteContent);
                
                // Record the deletion
                _editHandler.DeleteMessage(messageId, jid, message.Message, everyone ? "Deleted for everyone" : "Deleted for me");
                
                Logger.Info($"Message deleted: {messageId} in chat {jid}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to delete message: {messageId}");
                return false;
            }
        }

        /// <summary>
        /// Add reaction to a message
        /// </summary>
        public async Task<bool> AddReaction(string jid, string messageId, string emoji)
        {
            var reactionContent = new ReactionMessage
            {
                Reaction = new Message.Types.Reaction
                {
                    Key = new MessageKey
                    {
                        RemoteJid = jid,
                        Id = messageId
                    },
                    Text = emoji
                }
            };

            try
            {
                await SendMessage(jid, reactionContent);
                
                // Record the reaction
                _reactionHandler.AddReaction(messageId, Creds.Me.ID, emoji);
                
                Logger.Info($"Reaction added: {emoji} to message {messageId}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to add reaction: {emoji} to message {messageId}");
                return false;
            }
        }

        /// <summary>
        /// Remove reaction from a message
        /// </summary>
        public async Task<bool> RemoveReaction(string jid, string messageId)
        {
            var reactionContent = new ReactionMessage
            {
                Reaction = new Message.Types.Reaction
                {
                    Key = new MessageKey
                    {
                        RemoteJid = jid,
                        Id = messageId
                    },
                    Text = "" // Empty text removes the reaction
                }
            };

            try
            {
                await SendMessage(jid, reactionContent);
                
                // Remove the reaction record
                _reactionHandler.RemoveReaction(messageId, Creds.Me.ID);
                
                Logger.Info($"Reaction removed from message: {messageId}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to remove reaction from message: {messageId}");
                return false;
            }
        }

        /// <summary>
        /// Get message edit history
        /// </summary>
        public List<MessageEditHandler.MessageEditContext> GetMessageEditHistory(string messageId)
        {
            var context = _editHandler.GetEditContext(messageId);
            return context != null ? new List<MessageEditHandler.MessageEditContext> { context } : new List<MessageEditHandler.MessageEditContext>();
        }

        /// <summary>
        /// Get reactions for a message
        /// </summary>
        public List<MessageReactionHandler.MessageReactionContext> GetMessageReactions(string messageId)
        {
            return _reactionHandler.GetReactions(messageId);
        }

        /// <summary>
        /// Get message retry status
        /// </summary>
        public MessageRetryHandler.MessageRetryContext? GetMessageRetryStatus(string messageId)
        {
            return _retryHandler.GetRetryStatus(messageId);
        }

        /// <summary>
        /// Cancel message retry
        /// </summary>
        public void CancelMessageRetry(string messageId)
        {
            _retryHandler.RemoveFromRetryQueue(messageId);
            Logger.Info($"Message retry cancelled: {messageId}");
        }

        /// <summary>
        /// Get all active message retries
        /// </summary>
        public List<MessageRetryHandler.MessageRetryContext> GetActiveMessageRetries()
        {
            return _retryHandler.GetAllActiveRetries();
        }

        /// <summary>
        /// Process message retry queue
        /// </summary>
        private async void ProcessRetryQueue(object? state)
        {
            try
            {
                var messagesToRetry = _retryHandler.GetMessagesReadyForRetry();
                
                foreach (var retryContext in messagesToRetry)
                {
                    try
                    {
                        Logger.Info($"Retrying message: {retryContext.MessageId} (attempt {retryContext.AttemptCount + 1})");
                        
                        await RelayMessage(retryContext.Jid, retryContext.Message, retryContext.RelayOptions ?? new MessageRelayOptions());
                        
                        // Update retry context as successful
                        _retryHandler.UpdateRetryContext(retryContext.MessageId, true);
                        
                        Logger.Info($"Message retry successful: {retryContext.MessageId}");
                    }
                    catch (Exception ex)
                    {
                        var errorMessage = $"Retry attempt {retryContext.AttemptCount + 1} failed: {ex.Message}";
                        Logger.Error(ex, $"Message retry failed: {retryContext.MessageId}");
                        
                        // Update retry context as failed
                        _retryHandler.UpdateRetryContext(retryContext.MessageId, false, errorMessage);
                        
                        // Emit retry failed event
                        EV.Emit(EmitType.MessageRetryFailed, new
                        {
                            MessageId = retryContext.MessageId,
                            Jid = retryContext.Jid,
                            Attempt = retryContext.AttemptCount,
                            Error = ex.Message
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error processing message retry queue");
            }
        }

        /// <summary>
        /// Synchronize message history for a chat
        /// </summary>
        public async Task<List<WebMessageInfo>> SyncMessageHistory(string jid, DateTime? sinceTime = null)
        {
            try
            {
                Logger.Info($"Starting message history sync for: {jid}");
                
                var messages = await _historySyncHandler.StartHistorySync(jid, sinceTime);
                
                Logger.Info($"Message history sync completed for: {jid}, {messages.Count} messages synced");
                
                return messages;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Message history sync failed for: {jid}");
                throw;
            }
        }

        /// <summary>
        /// Get message history sync status
        /// </summary>
        public MessageHistorySyncHandler.HistorySyncContext? GetHistorySyncStatus(string jid)
        {
            return _historySyncHandler.GetSyncStatus(jid);
        }

        /// <summary>
        /// Get all active history sync operations
        /// </summary>
        public List<MessageHistorySyncHandler.HistorySyncContext> GetActiveHistorySyncs()
        {
            return _historySyncHandler.GetAllActiveSyncs();
        }

        #endregion

        #region Enhanced Media Handling

        /// <summary>
        /// Send media message with enhanced processing
        /// </summary>
        public async Task<WebMessageInfo?> SendMediaMessage(string jid, string filePath, string caption = "", MediaProcessingOptions? options = null)
        {
            try
            {
                Logger.Info($"Sending media message: {filePath} to {jid}");

                // Set default options if not provided
                var processingOptions = options ?? new MediaProcessingOptions
                {
                    Compress = true,
                    GenerateThumbnail = true,
                    Encrypt = true,
                    Quality = 80,
                    MaxFileSize = 100 * 1024 * 1024 // 100MB
                };

                // Upload media with processing
                var uploadResult = await UploadMedia(filePath, processingOptions);

                // Create appropriate media message based on file type
                var mimeType = MimeTypeUtils.GetMimeType(filePath);
                var mediaContent = CreateMediaContent(filePath, mimeType, uploadResult, caption);

                // Send the message
                var result = await SendMessage(jid, mediaContent);

                Logger.Info($"Media message sent successfully: {filePath} to {jid}");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to send media message: {filePath} to {jid}");
                throw;
            }
        }

        /// <summary>
        /// Upload media with enhanced processing
        /// </summary>
        private async Task<MediaUploadResult> UploadMedia(string filePath, MediaProcessingOptions options)
        {
            return await _mediaHandler.UploadMedia(filePath, options, RefreshMediaConn);
        }

        /// <summary>
        /// Create media content based on file type
        /// </summary>
        private IAnyMessageContent CreateMediaContent(string filePath, string mimeType, MediaUploadResult uploadResult, string caption)
        {
            if (mimeType.StartsWith("image/"))
            {
                return new ImageMessage
                {
                    Image = new Message.Types.ImageMessage
                    {
                        Url = uploadResult.Url,
                        Mimetype = mimeType,
                        FileSha256 = Google.Protobuf.ByteString.CopyFrom(uploadResult.FileSha256 ?? Array.Empty<byte>()),
                        FileEncSha256 = Google.Protobuf.ByteString.CopyFrom(uploadResult.FileEncSha256 ?? Array.Empty<byte>()),
                        MediaKey = Google.Protobuf.ByteString.CopyFrom(uploadResult.MediaKey ?? Array.Empty<byte>()),
                        Caption = caption,
                        Width = 800, // These would be determined during processing
                        Height = 600
                    }
                };
            }
            else if (mimeType.StartsWith("video/"))
            {
                return new VideoMessage
                {
                    Video = new Message.Types.VideoMessage
                    {
                        Url = uploadResult.Url,
                        Mimetype = mimeType,
                        FileSha256 = Google.Protobuf.ByteString.CopyFrom(uploadResult.FileSha256 ?? Array.Empty<byte>()),
                        FileEncSha256 = Google.Protobuf.ByteString.CopyFrom(uploadResult.FileEncSha256 ?? Array.Empty<byte>()),
                        MediaKey = Google.Protobuf.ByteString.CopyFrom(uploadResult.MediaKey ?? Array.Empty<byte>()),
                        Caption = caption,
                        Width = 1280, // These would be determined during processing
                        Height = 720,
                        Seconds = 30 // This would be determined during processing
                    }
                };
            }
            else if (mimeType.StartsWith("audio/"))
            {
                return new AudioMessage
                {
                    Audio = new Message.Types.AudioMessage
                    {
                        Url = uploadResult.Url,
                        Mimetype = mimeType,
                        FileSha256 = Google.Protobuf.ByteString.CopyFrom(uploadResult.FileSha256 ?? Array.Empty<byte>()),
                        FileEncSha256 = Google.Protobuf.ByteString.CopyFrom(uploadResult.FileEncSha256 ?? Array.Empty<byte>()),
                        MediaKey = Google.Protobuf.ByteString.CopyFrom(uploadResult.MediaKey ?? Array.Empty<byte>()),
                        Seconds = 180 // This would be determined during processing
                    }
                };
            }
            else
            {
                return new DocumentMessage
                {
                    Document = new Message.Types.DocumentMessage
                    {
                        Url = uploadResult.Url,
                        Mimetype = mimeType,
                        FileSha256 = Google.Protobuf.ByteString.CopyFrom(uploadResult.FileSha256 ?? Array.Empty<byte>()),
                        FileEncSha256 = Google.Protobuf.ByteString.CopyFrom(uploadResult.FileEncSha256 ?? Array.Empty<byte>()),
                        MediaKey = Google.Protobuf.ByteString.CopyFrom(uploadResult.MediaKey ?? Array.Empty<byte>()),
                        FileName = Path.GetFileName(filePath),
                        Caption = caption
                    }
                };
            }
        }

        /// <summary>
        /// Download media message
        /// </summary>
        public async Task<byte[]> DownloadMediaMessage(WebMessageInfo message, string savePath = "")
        {
            try
            {
                Logger.Info($"Downloading media from message: {message.Key.Id}");

                // Extract media information from message
                var mediaInfo = ExtractMediaInfo(message);
                if (mediaInfo == null)
                {
                    throw new Exception("Message does not contain downloadable media");
                }

                // Download the media
                var downloadOptions = new MediaDownloadOptions();
                var mediaData = await _mediaHandler.DownloadMedia(
                    mediaInfo.DirectPath, 
                    mediaInfo.MediaKey, 
                    mediaInfo.MediaType, 
                    downloadOptions
                );

                // Save to file if path is provided
                if (!string.IsNullOrEmpty(savePath))
                {
                    await File.WriteAllBytesAsync(savePath, mediaData);
                    Logger.Info($"Media saved to: {savePath}");
                }

                return mediaData;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to download media from message: {message.Key.Id}");
                throw;
            }
        }

        /// <summary>
        /// Extract media information from message
        /// </summary>
        private (string DirectPath, byte[] MediaKey, string MediaType)? ExtractMediaInfo(WebMessageInfo message)
        {
            var msg = message.Message;

            if (msg?.ImageMessage != null)
            {
                return (
                    msg.ImageMessage.DirectPath,
                    msg.ImageMessage.MediaKey.ToByteArray(),
                    "Image"
                );
            }
            else if (msg?.VideoMessage != null)
            {
                return (
                    msg.VideoMessage.DirectPath,
                    msg.VideoMessage.MediaKey.ToByteArray(),
                    "Video"
                );
            }
            else if (msg?.AudioMessage != null)
            {
                return (
                    msg.AudioMessage.DirectPath,
                    msg.AudioMessage.MediaKey.ToByteArray(),
                    "Audio"
                );
            }
            else if (msg?.DocumentMessage != null)
            {
                return (
                    msg.DocumentMessage.DirectPath,
                    msg.DocumentMessage.MediaKey.ToByteArray(),
                    "Document"
                );
            }
            else if (msg?.StickerMessage != null)
            {
                return (
                    msg.StickerMessage.DirectPath,
                    msg.StickerMessage.MediaKey.ToByteArray(),
                    "Sticker"
                );
            }

            return null;
        }

        /// <summary>
        /// Get media processing status
        /// </summary>
        public MediaHandler.MediaProcessingContext? GetMediaProcessingStatus(string mediaId)
        {
            return _mediaHandler.GetProcessingStatus(mediaId);
        }

        /// <summary>
        /// Cancel media processing
        /// </summary>
        public bool CancelMediaProcessing(string mediaId)
        {
            return _mediaHandler.CancelProcessing(mediaId);
        }

        /// <summary>
        /// Get all active media processing contexts
        /// </summary>
        public List<MediaHandler.MediaProcessingContext> GetActiveMediaProcessingContexts()
        {
            return _mediaHandler.GetActiveProcessingContexts();
        }

        /// <summary>
        /// Cleanup completed media processing contexts
        /// </summary>
        public void CleanupMediaProcessingContexts()
        {
            _mediaHandler.CleanupCompletedContexts();
        }

        /// <summary>
        /// Process media file without sending
        /// </summary>
        public async Task<MediaHandler.MediaInfo> ProcessMediaFile(string filePath, MediaProcessingOptions? options = null)
        {
            var processingOptions = options ?? new MediaProcessingOptions
            {
                Compress = true,
                GenerateThumbnail = true,
                Encrypt = true
            };

            return await _mediaHandler.ProcessMedia(filePath, processingOptions);
        }

        #endregion

        #region Presence and Status Management

        /// <summary>
        /// Set own presence status
        /// </summary>
        public void SetPresence(PresenceHandler.PresenceStatus status, string? statusMessage = null)
        {
            _presenceHandler.SetOwnPresence(status, statusMessage);
        }

        /// <summary>
        /// Set own status text
        /// </summary>
        public void SetStatus(string statusText)
        {
            _presenceHandler.SetOwnStatus(statusText);
        }

        /// <summary>
        /// Get presence information for a contact
        /// </summary>
        public PresenceHandler.PresenceContext? GetPresence(string jid)
        {
            return _presenceHandler.GetPresence(jid);
        }

        /// <summary>
        /// Get status information for a contact
        /// </summary>
        public PresenceHandler.StatusContext? GetContactStatus(string jid)
        {
            return _presenceHandler.GetStatus(jid);
        }

        /// <summary>
        /// Get all online contacts
        /// </summary>
        public List<PresenceHandler.PresenceContext> GetOnlineContacts()
        {
            return _presenceHandler.GetOnlineContacts();
        }

        /// <summary>
        /// Get presence status for multiple contacts
        /// </summary>
        public Dictionary<string, PresenceHandler.PresenceContext> GetPresenceStatus(string[] jids)
        {
            return _presenceHandler.GetPresenceStatus(jids);
        }

        /// <summary>
        /// Subscribe to presence updates for contacts
        /// </summary>
        public void SubscribeToPresence(string[] jids)
        {
            _presenceHandler.SubscribeToPresence(jids);
        }

        /// <summary>
        /// Unsubscribe from presence updates
        /// </summary>
        public void UnsubscribeFromPresence(string[] jids)
        {
            _presenceHandler.UnsubscribeFromPresence(jids);
        }

        /// <summary>
        /// Set status privacy level
        /// </summary>
        public void SetStatusPrivacy(PresenceHandler.StatusPrivacyLevel privacyLevel, string[]? exceptContacts = null)
        {
            _presenceHandler.SetStatusPrivacy(privacyLevel, exceptContacts);
        }

        /// <summary>
        /// Get status privacy settings
        /// </summary>
        public PresenceHandler.StatusPrivacyLevel? GetStatusPrivacy()
        {
            return _presenceHandler.GetStatusPrivacy();
        }

        /// <summary>
        /// Create and add a status story
        /// </summary>
        public void AddStatusStory(string mediaPath, string caption = "", PresenceHandler.StoryType type = PresenceHandler.StoryType.Image)
        {
            var story = _presenceHandler.CreateStatusStory(mediaPath, caption, type);
            _presenceHandler.AddStatusStory(Creds.Me.ID, story);
        }

        /// <summary>
        /// Remove a status story
        /// </summary>
        public void RemoveStatusStory(string storyId)
        {
            _presenceHandler.RemoveStatusStory(Creds.Me.ID, storyId);
        }

        /// <summary>
        /// Get all active stories for a contact
        /// </summary>
        public List<PresenceHandler.StatusStory> GetActiveStories(string jid)
        {
            return _presenceHandler.GetActiveStories(jid);
        }

        /// <summary>
        /// Get all active stories from all contacts
        /// </summary>
        public Dictionary<string, List<PresenceHandler.StatusStory>> GetAllActiveStories()
        {
            return _presenceHandler.GetAllActiveStories();
        }

        /// <summary>
        /// Mark a story as viewed
        /// </summary>
        public void MarkStoryAsViewed(string jid, string storyId)
        {
            _presenceHandler.MarkStoryAsViewed(jid, storyId, Creds.Me.ID);
        }

        /// <summary>
        /// Get presence statistics
        /// </summary>
        public Dictionary<string, object> GetPresenceStatistics()
        {
            return _presenceHandler.GetPresenceStatistics();
        }

        /// <summary>
        /// Update presence for a contact (internal use)
        /// </summary>
        internal void UpdateContactPresence(string jid, PresenceHandler.PresenceStatus status, bool isOnline = false, Dictionary<string, object>? additionalData = null)
        {
            _presenceHandler.UpdatePresence(jid, status, isOnline, additionalData);
        }

        /// <summary>
        /// Update status for a contact (internal use)
        /// </summary>
        internal void UpdateContactStatus(string jid, string statusText)
        {
            _presenceHandler.UpdateStatus(jid, statusText);
        }

        /// <summary>
        /// Event handler for presence updates
        /// </summary>
        private void OnPresenceUpdated(object? sender, PresenceHandler.PresenceUpdateEventArgs e)
        {
            // Emit presence update event
            EV.Emit(EmitType.Update, new
            {
                Type = "Presence",
                Jid = e.Jid,
                Status = e.Status,
                IsOnline = e.IsOnline,
                LastSeen = e.LastSeen,
                AdditionalData = e.AdditionalData
            });
        }

        /// <summary>
        /// Event handler for status updates
        /// </summary>
        private void OnStatusUpdated(object? sender, PresenceHandler.StatusUpdateEventArgs e)
        {
            // Emit status update event
            EV.Emit(EmitType.Update, new
            {
                Type = "Status",
                Jid = e.Jid,
                StatusText = e.StatusText,
                StatusSetTime = e.StatusSetTime,
                IsNewStatus = e.IsNewStatus
            });
        }

        /// <summary>
        /// Event handler for story updates
        /// </summary>
        private void OnStoryUpdated(object? sender, PresenceHandler.StoryUpdateEventArgs e)
        {
            // Emit story update event
            EV.Emit(EmitType.Update, new
            {
                Type = "Story",
                Jid = e.Jid,
                Story = e.Story,
                UpdateType = e.UpdateType
            });
        }

        #endregion

        private async Task<List<JidWidhDevice>> GetUSyncDevices(string[] jids, bool useCache, bool ignoreZeroDevices)
        {
            var deviceResults = new List<JidWidhDevice>();
            if (!useCache)
            {
                Logger.Debug("not using cache for devices");
            }

            var users = new List<BinaryNode>();
            foreach (var jid in jids)
            {
                var user = JidDecode(jid).User;
                var normalJid = JidNormalizedUser(jid);

                var devices = userDevicesCache.Get<JidWidhDevice[]>(user);
                if (devices != null && devices.Length > 0 && useCache)
                {
                    deviceResults.AddRange(devices);
                    Logger.Trace(new { user }, "using cache for devices");
                }
                else
                {
                    users.Add(new BinaryNode("user")
                    {
                        attrs = { { "jid", normalJid } }
                    });
                }
            }

            var iq = new BinaryNode()
            {
                tag = "iq",
                attrs =
                {
                    {"to", S_WHATSAPP_NET },
                    {"type" , "get" },
                    {"xmlns","usync" }
                },
                content = new BinaryNode[]
                {
                    new BinaryNode()
                    {
                        tag = "usync",
                        attrs =
                        {
                            {"sid", GenerateMessageTag() },
                            {"mode","query" },
                            {"last","true" },
                            {"index","0" },
                            {"context","message" }
                        },
                        content = new BinaryNode[]
                        {
                            new BinaryNode()
                            {
                                tag = "query",
                                content = new BinaryNode[]
                                {
                                    new BinaryNode()
                                    {
                                        tag = "devices",
                                        attrs =
                                        {
                                            {"version", "2" }
                                        }
                                    }
                                },
                            },
                            new BinaryNode()
                            {
                                tag ="list",
                                content = users.ToArray()
                            }
                        }
                    }
                }
            };

            var result = await Query(iq);
            var extracted = ExtractDeviceJids(result, Creds.Me.ID, ignoreZeroDevices);
            Dictionary<string, List<JidWidhDevice>> deviceMap = new Dictionary<string, List<JidWidhDevice>>();

            foreach (var item in extracted)
            {
                deviceMap[item.User] = deviceMap.ContainsKey(item.User) == true ? deviceMap[item.User] : new List<JidWidhDevice>();

                deviceMap[item.User].Add(item);
                deviceResults.Add(item);
            }

            foreach (var item in deviceMap)
            {
                userDevicesCache.Set(item.Key, item.Value);
            }


            return deviceResults;
        }

        protected async Task<bool> AssertSessions(List<string> jids, bool force)
        {
            var didFetchNewSession = false;
            List<string> jidsRequiringFetch = new List<string>();
            if (force)
            {
                jidsRequiringFetch = jids.ToList();
            }
            else
            {
                var addrs = jids.Select(x => new ProtocolAddress(x)).ToList();
                var sessions = Keys.Get<SessionRecord>(addrs.Select(x => x.ToString()).ToList());
                foreach (var jid in jids)
                {
                    if (!sessions.ContainsKey(new ProtocolAddress(jid).ToString()))
                    {
                        jidsRequiringFetch.Add(jid.ToString());
                    }
                    else if (sessions[new ProtocolAddress(jid).ToString()] == null)
                    {
                        jidsRequiringFetch.Add(jid.ToString());
                    }
                }
            }

            if (jidsRequiringFetch.Count > 0)
            {
                Logger.Debug(new { jidsRequiringFetch }, "fetching sessions");
                var result = await Query(new BinaryNode()
                {
                    tag = "iq",
                    attrs =
                    {
                        {"xmlns", "encrypt" },
                        {"type", "get" },
                        {"to", S_WHATSAPP_NET }
                    },
                    content = new BinaryNode[]
                    {
                        new BinaryNode()
                        {
                            tag = "key",
                            attrs = { },
                            content = jidsRequiringFetch.Select(x => new BinaryNode()
                            {
                                tag = "user",
                                attrs =
                                {
                                    {"jid",x }
                                }

                            }).ToArray()
                        }
                    }
                });

                ParseAndInjectE2ESessions(result, Repository);

                didFetchNewSession = true;
            }


            return didFetchNewSession;
        }


        private async Task<MediaUploadResult> WaUploadToServer(MemoryStream stream, MediaUploadOptions options)
        {
            return await MediaMessageUtil.GetWAUploadToServer(SocketConfig, stream, options, RefreshMediaConn);
        }


        public async Task<WebMessageInfo?> SendMessage(string jid, IAnyMessageContent content, IMiscMessageGenerationOptions? options = null)
        {
            var userJid = Creds.Me.ID;

            if (IsJidNewsletter(jid))
            {
                throw new Exception("Cannot send to newsletter, use SendNewsletterMessage instead");
            }

            if (IsJidGroup(jid) && content.DisappearingMessagesInChat.HasValue)
            {
                if (content.DisappearingMessagesInChat == true)
                {
                    await GroupToggleEphemeral(jid, 7 * 24 * 60 * 60);
                }
                else
                {
                    await GroupToggleEphemeral(jid, 0);
                }
                return null;
            }
            else
            {
                var fullMsg = await GenerateWAMessage(jid, content, new MessageGenerationOptions(options)
                {
                    UserJid = userJid,
                    Logger = Logger,
                    Upload = WaUploadToServer
                });

                var deleteModel = content as IDeleteable;
                var editMessage = content as IEditable;
                var additionalAttributes = new Dictionary<string, string>();

                // required for delete
                if (deleteModel?.Delete != null)
                {
                    // if the chat is a group, and I am not the author, then delete the message as an admin
                    if (IsJidGroup(deleteModel.Delete.RemoteJid) && !deleteModel.Delete.FromMe)
                    {
                        additionalAttributes["edit"] = "8";
                    }
                    else
                    {
                        additionalAttributes["edit"] = "7";
                    }
                }
                else if (editMessage?.Edit != null)
                {
                    additionalAttributes["edit"] = "1";
                }


                await RelayMessage(jid, fullMsg.Message, new MessageRelayOptions()
                {
                    MessageID = fullMsg.Key.Id,
                    StatusJidList = options?.StatusJidList,
                    AdditionalAttributes = additionalAttributes,
                });

                fullMsg.MessageTimestamp = (ulong)DateTimeOffset.Now.ToUnixTimeMilliseconds();
                await UpsertMessage(fullMsg, MessageEventType.Append);

                return fullMsg;
            }

        }

        public async Task RelayMessage(string jid, Message message, IMessageRelayOptions options)
        {
            var meId = Creds.Me.ID;
            var shouldIncludeDeviceIdentity = false;

            var jidDecoded = JidDecode(jid);
            var user = jidDecoded.User;
            var server = jidDecoded.Server;

            var statusId = "status@broadcast";
            var isNewsletter = server == "newsletter";
            var isGroup = server == "g.us";
            var isStatus = jid == statusId;
            var isLid = server == "lid";

            options.MessageID = options.MessageID ?? GenerateMessageID();

            //options.UseUserDevicesCache = options.UseUserDevicesCache != false;


            var participants = new List<BinaryNode>();
            var destinationJid = (!isStatus) ? JidEncode(user, isLid ? "lid" : isGroup ? "g.us" : isNewsletter ? "newsletter" : "s.whatsapp.net") : statusId;
            var binaryNodeContent = new List<BinaryNode>();
            var devices = new List<JidWidhDevice>();

            var meMsg = new Message()
            {
                DeviceSentMessage = new Message.Types.DeviceSentMessage()
                {
                    DestinationJid = destinationJid,
                    Message = message
                }
            };


            if (options.Participant != null)
            {
                // when the retry request is not for a group
                // only send to the specific device that asked for a retry
                // otherwise the message is sent out to every device that should be a recipient
                if (!isGroup && !isStatus)
                {
                    options.AdditionalAttributes["device_fanout"] = "false";
                }

                var participantJidDecoded = JidDecode(options.Participant.Jid);
                devices.Add(new JidWidhDevice()
                {
                    Device = participantJidDecoded.Device,
                    User = participantJidDecoded.User,
                });
            }


            //Transaction thingy ?
            var mediaType = GetMediaType(message);
            if (isGroup || isStatus) // Group and Status
            {

                var groupData = Store.GetGroup(jid);
                if (groupData == null)
                {
                    groupData = await GroupMetaData(jid);
                }


                var senderKeyMap = Keys.Get<SenderKeyMemory>(jid) ?? new SenderKeyMemory();

                if (options.Participant == null)
                {
                    var participantsList = groupData.Participants.Select(x => x.ID).ToList();

                    if (isStatus && options.StatusJidList?.Count > 0)//TODO
                    {
                        participantsList.AddRange(options.StatusJidList);
                    }


                    var additionalDevices = await GetUSyncDevices(participantsList.ToArray(), options.UseUserDevicesCache ?? false, false);

                    devices.AddRange(additionalDevices);
                }


                var patched = SocketConfig.PatchMessageBeforeSending(message, devices.Select(x => JidEncode(x.User, isLid ? "lid" : "s.whatsapp.net", x.Device)).ToArray());
                var bytes = EncodeWAMessage(patched);//.ToByteArray();

                var encGroup = Repository.EncryptGroupMessage(destinationJid, meId, bytes);


                List<string> senderKeyJids = new List<string>();
                // ensure a connection is established with every device
                foreach (var item in devices)
                {
                    var deviceJid = JidEncode(item.User, isLid ? "lid" : "s.whatsapp.net", item.Device);
                    if (!senderKeyMap.ContainsKey(deviceJid) || options.Participant != null)
                    {
                        senderKeyJids.Add(deviceJid);
                        // store that this person has had the sender keys sent to them
                        senderKeyMap[deviceJid] = true;
                    }
                }

                // if there are some participants with whom the session has not been established
                // if there are, we re-send the senderkey
                if (senderKeyJids.Count > 0)
                {
                    var senderKeyMsg = new Message()
                    {
                        SenderKeyDistributionMessage = new Message.Types.SenderKeyDistributionMessage()
                        {
                            AxolotlSenderKeyDistributionMessage = encGroup.SenderKeyDistributionMessage.ToByteString(),
                            GroupId = jid
                        }
                    };

                    await AssertSessions(senderKeyJids, false);

                    Dictionary<string, string> mediaTypeAttr = new Dictionary<string, string>()
                {
                    {"mediatype",mediaType }
                };
                    var result = CreateParticipantNodes(senderKeyJids.ToArray(), senderKeyMsg, mediaType != null ? mediaTypeAttr : null);
                    shouldIncludeDeviceIdentity = shouldIncludeDeviceIdentity || result.ShouldIncludeDeviceIdentity;

                    participants.AddRange(result.Nodes);
                }


                binaryNodeContent.Add(new BinaryNode()
                {
                    tag = "enc",
                    attrs =
                    {
                        {"v","2" },
                        {"type","skmsg" },

                    },
                    content = encGroup.CipherText

                });

                Keys.Set(jid, senderKeyMap);
            }
            else if (isNewsletter)
            {
                var patched = SocketConfig.PatchMessageBeforeSending(message, []);
                var bytes = EncodeNewsletterMessage(patched);
                binaryNodeContent.Add(new BinaryNode()
                {
                    tag = "plaintext",
                    content = bytes
                });
            }
            else
            {
                var me = JidDecode(meId);
                var meUser = me.User;
                var meDevice = me.Device;

                if (options.Participant == null)
                {
                    //options.Participant = new MessageParticipant()
                    //{
                    //    Count = 0,
                    //    Jid = jid
                    //};
                    devices.Add(new JidWidhDevice() { User = user });
                    // do not send message to self if the device is 0 (mobile)
                    if (meDevice != null && meDevice != 0)
                    {
                        devices.Add(new JidWidhDevice() { User = meUser });
                    }
                    var additionalDevices = await GetUSyncDevices([meId, jid], options.UseUserDevicesCache ?? false, true);
                    devices.AddRange(additionalDevices);
                }

                List<string> allJids = new List<string>();
                List<string> meJids = new List<string>();
                List<string> otherJids = new List<string>();
                foreach (var item in devices)
                {
                    var iuser = item.User;
                    var idevice = item.Device;
                    var isMe = iuser == meUser;
                    var addJid = JidEncode((isMe && isLid) ? Creds.Me.LID.Split(":")[0] ?? iuser : iuser, isLid ? "lid" : "s.whatsapp.net", idevice);
                    if (isMe)
                    {
                        meJids.Add(addJid);
                    }
                    else
                    {
                        otherJids.Add(addJid);
                    }
                    allJids.Add(addJid);
                }

                await AssertSessions(allJids, false);

                Dictionary<string, string> mediaTypeAttr = new Dictionary<string, string>()
                {
                    {"mediatype",mediaType }
                };


                //TODO Add Media Type
                var meNode = CreateParticipantNodes(meJids.ToArray(), meMsg, mediaType != null ? mediaTypeAttr : null);
                var otherNode = CreateParticipantNodes(otherJids.ToArray(), message, mediaType != null ? mediaTypeAttr : null);

                participants.AddRange(meNode.Nodes);
                participants.AddRange(otherNode.Nodes);
                shouldIncludeDeviceIdentity = shouldIncludeDeviceIdentity || meNode.ShouldIncludeDeviceIdentity || otherNode.ShouldIncludeDeviceIdentity;

            }

            if (participants.Count > 0)
            {
                binaryNodeContent.Add(new BinaryNode()
                {
                    tag = "participants",
                    attrs = { },
                    content = participants.ToArray()
                });
            }

            var stanza = new BinaryNode()
            {
                tag = "message",
                attrs = {
                        {"id", options.MessageID  },
                        {"type" , "text" }
                }
            };

            if (options.AdditionalAttributes != null)
            {
                foreach (var item in options.AdditionalAttributes)
                {
                    stanza.attrs.Add(item.Key, item.Value);
                }
            }

            // if the participant to send to is explicitly specified (generally retry recp)
            // ensure the message is only sent to that person
            // if a retry receipt is sent to everyone -- it'll fail decryption for everyone else who received the msg
            if (options.Participant != null)
            {
                if (IsJidGroup(destinationJid))
                {
                    stanza.attrs["to"] = destinationJid;
                    stanza.attrs["participant"] = options.Participant.Jid;
                }
                else if (AreJidsSameUser(options.Participant.Jid, meId))
                {
                    stanza.attrs["to"] = options.Participant.Jid;
                    stanza.attrs["participant"] = destinationJid;
                }
                else
                {
                    stanza.attrs["to"] = options.Participant.Jid;
                }
            }
            else
            {
                stanza.attrs["to"] = destinationJid;
            }

            if (shouldIncludeDeviceIdentity)
            {
                binaryNodeContent.Add(new BinaryNode()
                {
                    tag = "device-identity",
                    attrs = { },
                    content = EncodeSignedDeviceIdentity(Creds.Account, true)
                });
            }

            stanza.content = binaryNodeContent.ToArray();

            //TODO: Button Type

            Logger.Debug(new { msgId = options.MessageID }, $"sending message to ${participants.Count} devices");

            SendNode(stanza);
        }

        private byte[] EncodeNewsletterMessage(Message patched)
        {
            return patched.ToByteArray();
        }

        public ParticipantNode CreateParticipantNodes(string[] jids, Message message, Dictionary<string, string>? attrs)
        {
            ParticipantNode result = new ParticipantNode();
            var patched = SocketConfig.PatchMessageBeforeSending(message, jids);
            var bytes = EncodeWAMessage(patched);//.ToByteArray();

            result.ShouldIncludeDeviceIdentity = false;
            List<BinaryNode> nodes = new List<BinaryNode>();

            foreach (var jid in jids)
            {
                var enc = Repository.EncryptMessage(jid, bytes);
                if (enc.Type == "pkmsg")
                {
                    result.ShouldIncludeDeviceIdentity = true;
                }
                var encNode = new BinaryNode()
                {
                    tag = "enc",
                    attrs =
                            {
                                {"v","2" },
                                {"type",enc.Type },
                            },
                    content = enc.CipherText
                };
                if (attrs != null)
                {
                    foreach (var attr in attrs)
                    {
                        encNode.attrs[attr.Key] = attr.Value;
                    }
                }
                var node = new BinaryNode()
                {
                    tag = "to",
                    attrs = { { "jid", jid } },
                    content = new BinaryNode[]
                    {
                        encNode
                    }
                };
                nodes.Add(node);
            }

            result.Nodes = nodes.ToArray();

            return result;
        }

        public string GetMediaType(Message message)
        {
            if (message.ImageMessage != null)
            {
                return "image";
            }
            else if (message.VideoMessage != null)
            {
                return message.VideoMessage.GifPlayback ? "gif" : "video";
            }
            else if (message.AudioMessage != null)
            {
                return message.AudioMessage.Ptt ? "ptt" : "audio";
            }
            else if (message.ContactMessage != null)
            {
                return "vcard";
            }
            else if (message.DocumentMessage != null)
            {
                return "document";
            }
            else if (message.ContactsArrayMessage != null)
            {
                return "contact_array";
            }
            else if (message.LiveLocationMessage != null)
            {
                return "livelocation";
            }
            else if (message.StickerMessage != null)
            {
                return "sticker";
            }
            else if (message.ListMessage != null)
            {
                return "list";
            }
            else if (message.ListResponseMessage != null)
            {
                return "list_response";
            }
            else if (message.ButtonsResponseMessage != null)
            {
                return "buttons_response";
            }
            else if (message.OrderMessage != null)
            {
                return "order";
            }
            else if (message.ProductMessage != null)
            {
                return "product";
            }
            else if (message.InteractiveResponseMessage != null)
            {
                return "native_flow_response";
            }
            else
            {
                return null; // Or handle any other cases accordingly
            }
        }

        public async Task<MediaConnInfo> RefreshMediaConn(bool refresh = false)
        {
            if (CurrentMedia == null)
            {
                refresh = true;
            }
            else
            {
                DateTime currentTime = DateTime.Now;
                DateTime fetchDateTime = CurrentMedia.FetchDate;
                var ttlInSeconds = CurrentMedia.Ttl;
                TimeSpan timeDifference = currentTime - fetchDateTime;
                double millisecondsDifference = timeDifference.TotalMilliseconds;
                if (millisecondsDifference > ttlInSeconds * 1000)
                {
                    refresh = true;
                }
            }

            if (refresh)
            {
                var result = await Query(new BinaryNode()
                {
                    tag = "iq",
                    attrs =
                    {
                        { "type","set" },
                        { "xmlns", "w:m" },
                        {"to" , S_WHATSAPP_NET }
                    },
                    content = new BinaryNode[]
                    {
                    new BinaryNode()
                    {
                        tag = "media_conn",
                        attrs = {}
                    }
                    }
                });

                var mediaConnNode = GetBinaryNodeChild(result, "media_conn");
                var hostNodes = GetBinaryNodeChildren(mediaConnNode, "host");
                CurrentMedia = new MediaConnInfo()
                {
                    Auth = mediaConnNode.getattr("auth") ?? "",
                    Ttl = mediaConnNode.getattr("ttl")?.ToUInt64(),
                    FetchDate = DateTime.Now,
                    Hosts = hostNodes.Select(x => new MediaHost
                    {
                        HostName = x.getattr("hostname") ?? "",
                        MaxContentLengthBytes = x.getattr("maxContentLengthBytes").ToUInt32(),
                    }).ToArray()
                };
                Logger.Debug("fetched media connection");
            }

            return CurrentMedia;
        }
    }
}
