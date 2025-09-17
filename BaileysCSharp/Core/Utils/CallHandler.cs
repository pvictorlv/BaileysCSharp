



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
    /// Handles call signaling, management, and history for WhatsApp calls
    /// </summary>
    public class CallHandler
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, CallContext> _activeCalls = new();
        private readonly Dictionary<string, List<CallHistoryEntry>> _callHistory = new();
        private readonly object _lock = new();
        private readonly Timer _callCleanupTimer;

        public class CallContext
        {
            public string CallId { get; set; } = string.Empty;
            public string FromJid { get; set; } = string.Empty;
            public string ToJid { get; set; } = string.Empty;
            public CallDirection Direction { get; set; }
            public CallStatus Status { get; set; }
            public CallType CallType { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public TimeSpan? Duration { get; set; }
            public bool IsMuted { get; set; }
            public bool IsVideoOn { get; set; }
            public string? OfferSdp { get; set; }
            public string? AnswerSdp { get; set; }
            public List<IceCandidate> IceCandidates { get; set; } = new();
            public Dictionary<string, object> Metadata { get; set; } = new();
            public List<CallParticipant> Participants { get; set; } = new();
        }

        public class CallParticipant
        {
            public string Jid { get; set; } = string.Empty;
            public ParticipantStatus Status { get; set; }
            public DateTime JoinTime { get; set; }
            public DateTime? LeaveTime { get; set; }
            public bool IsAudioMuted { get; set; }
            public bool IsVideoOff { get; set; }
            public bool IsScreenSharing { get; set; }
        }

        public class CallHistoryEntry
        {
            public string CallId { get; set; } = string.Empty;
            public string FromJid { get; set; } = string.Empty;
            public string ToJid { get; set; } = string.Empty;
            public CallDirection Direction { get; set; }
            public CallStatus Status { get; set; }
            public CallType CallType { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public TimeSpan? Duration { get; set; }
            public bool WasMissed { get; set; }
            public Dictionary<string, object> AdditionalData { get; set; } = new();
        }

        public class IceCandidate
        {
            public string Candidate { get; set; } = string.Empty;
            public string SdpMid { get; set; } = string.Empty;
            public int SdpMLineIndex { get; set; }
            public string UsernameFragment { get; set; } = string.Empty;
        }

        public class CallOffer
        {
            public string CallId { get; set; } = string.Empty;
            public string FromJid { get; set; } = string.Empty;
            public string ToJid { get; set; } = string.Empty;
            public CallType CallType { get; set; }
            public string Sdp { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        public class CallAnswer
        {
            public string CallId { get; set; } = string.Empty;
            public string FromJid { get; set; } = string.Empty;
            public string ToJid { get; set; } = string.Empty;
            public string Sdp { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        public class CallReject
        {
            public string CallId { get; set; } = string.Empty;
            public string FromJid { get; set; } = string.Empty;
            public string ToJid { get; set; } = string.Empty;
            public RejectReason Reason { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class CallEnd
        {
            public string CallId { get; set; } = string.Empty;
            public string FromJid { get; set; } = string.Empty;
            public string ToJid { get; set; } = string.Empty;
            public EndReason Reason { get; set; }
            public DateTime Timestamp { get; set; }
            public TimeSpan? Duration { get; set; }
        }

        public enum CallDirection
        {
            Incoming,
            Outgoing
        }

        public enum CallStatus
        {
            Ringing,
            Connecting,
            Active,
            Held,
            Ended,
            Failed,
            Missed
        }

        public enum CallType
        {
            Audio,
            Video,
            ScreenShare
        }

        public enum ParticipantStatus
        {
            Invited,
            Joined,
            Left,
            Declined,
            Removed
        }

        public enum RejectReason
        {
            Busy,
            Declined,
            Timeout,
            Unavailable
        }

        public enum EndReason
        {
            Ended,
            Failed,
            Rejected,
            Timeout,
            Disconnected
        }

        public class CallEventArgs : EventArgs
        {
            public string CallId { get; set; } = string.Empty;
            public string FromJid { get; set; } = string.Empty;
            public string ToJid { get; set; } = string.Empty;
            public CallDirection Direction { get; set; }
            public CallStatus Status { get; set; }
            public CallType CallType { get; set; }
            public Dictionary<string, object> AdditionalData { get; set; } = new();
        }

        public event EventHandler<CallEventArgs>? CallStarted;
        public event EventHandler<CallEventArgs>? CallEnded;
        public event EventHandler<CallEventArgs>? CallMissed;
        public event EventHandler<CallEventArgs>? CallStatusChanged;

        public CallHandler(ILogger logger)
        {
            _logger = logger;
            
            // Start cleanup timer - run every hour
            _callCleanupTimer = new Timer(CleanupOldCallHistory, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }

        #region Call Management

        /// <summary>
        /// Start a new outgoing call
        /// </summary>
        public async Task<string> StartCall(string toJid, CallType callType = CallType.Audio)
        {
            var callId = GenerateCallId();
            var fromJid = "self"; // This would be the actual user JID

            var callContext = new CallContext
            {
                CallId = callId,
                FromJid = fromJid,
                ToJid = toJid,
                Direction = CallDirection.Outgoing,
                Status = CallStatus.Ringing,
                CallType = callType,
                StartTime = DateTime.Now,
                IsMuted = false,
                IsVideoOn = callType == CallType.Video
            };

            lock (_lock)
            {
                _activeCalls[callId] = callContext;
            }

            try
            {
                _logger.Info($"Starting {callType} call to {toJid}");

                // Generate and send call offer
                var offer = await GenerateCallOffer(callContext);
                await SendCallOffer(offer);

                EmitCallEvent(CallStarted, callContext);

                return callId;
            }
            catch (Exception ex)
            {
                await EndCall(callId, EndReason.Failed);
                _logger.Error(ex, $"Failed to start call to {toJid}");
                throw;
            }
        }

        /// <summary>
        /// Accept an incoming call
        /// </summary>
        public async Task<bool> AcceptCall(string callId)
        {
            lock (_lock)
            {
                if (!_activeCalls.TryGetValue(callId, out var callContext))
                {
                    _logger.Warning($"Call not found: {callId}");
                    return false;
                }

                if (callContext.Direction != CallDirection.Incoming)
                {
                    _logger.Warning($"Cannot accept outgoing call: {callId}");
                    return false;
                }

                callContext.Status = CallStatus.Connecting;
            }

            try
            {
                _logger.Info($"Accepting call: {callId}");

                // Generate and send call answer
                var answer = await GenerateCallAnswer(callId);
                await SendCallAnswer(answer);

                // Update call status to active
                UpdateCallStatus(callId, CallStatus.Active);

                return true;
            }
            catch (Exception ex)
            {
                await EndCall(callId, EndReason.Failed);
                _logger.Error(ex, $"Failed to accept call: {callId}");
                return false;
            }
        }

        /// <summary>
        /// Reject an incoming call
        /// </summary>
        public async Task<bool> RejectCall(string callId, RejectReason reason = RejectReason.Declined)
        {
            lock (_lock)
            {
                if (!_activeCalls.TryGetValue(callId, out var callContext))
                {
                    _logger.Warning($"Call not found: {callId}");
                    return false;
                }

                if (callContext.Direction != CallDirection.Incoming)
                {
                    _logger.Warning($"Cannot reject outgoing call: {callId}");
                    return false;
                }
            }

            try
            {
                _logger.Info($"Rejecting call: {callId} with reason: {reason}");

                // Send call reject
                var reject = new CallReject
                {
                    CallId = callId,
                    FromJid = "self",
                    ToJid = _activeCalls[callId].FromJid,
                    Reason = reason,
                    Timestamp = DateTime.Now
                };

                await SendCallReject(reject);

                // End the call
                await EndCall(callId, EndReason.Rejected);

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to reject call: {callId}");
                return false;
            }
        }

        /// <summary>
        /// End an active call
        /// </summary>
        public async Task<bool> EndCall(string callId, EndReason reason = EndReason.Ended)
        {
            lock (_lock)
            {
                if (!_activeCalls.TryGetValue(callId, out var callContext))
                {
                    _logger.Warning($"Call not found: {callId}");
                    return false;
                }

                callContext.Status = CallStatus.Ended;
                callContext.EndTime = DateTime.Now;
                callContext.Duration = callContext.EndTime - callContext.StartTime;
            }

            try
            {
                _logger.Info($"Ending call: {callId} with reason: {reason}");

                // Send call end signal
                var callEnd = new CallEnd
                {
                    CallId = callId,
                    FromJid = "self",
                    ToJid = _activeCalls[callId].ToJid,
                    Reason = reason,
                    Timestamp = DateTime.Now,
                    Duration = _activeCalls[callId].Duration
                };

                await SendCallEnd(callEnd);

                // Move to call history
                await MoveToCallHistory(callId);

                // Remove from active calls
                lock (_lock)
                {
                    _activeCalls.Remove(callId);
                }

                EmitCallEvent(CallEnded, _activeCalls[callId]);

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to end call: {callId}");
                return false;
            }
        }

        /// <summary>
        /// Handle incoming call offer
        /// </summary>
        public async Task<bool> HandleCallOffer(CallOffer offer)
        {
            var callContext = new CallContext
            {
                CallId = offer.CallId,
                FromJid = offer.FromJid,
                ToJid = offer.ToJid,
                Direction = CallDirection.Incoming,
                Status = CallStatus.Ringing,
                CallType = offer.CallType,
                StartTime = DateTime.Now,
                OfferSdp = offer.Sdp
            };

            lock (_lock)
            {
                _activeCalls[offer.CallId] = callContext;
            }

            _logger.Info($"Incoming call from {offer.FromJid}: {offer.CallId}");

            // Emit call event for UI to handle
            EmitCallEvent(CallStarted, callContext);

            return true;
        }

        /// <summary>
        /// Handle incoming call answer
        /// </summary>
        public async Task<bool> HandleCallAnswer(CallAnswer answer)
        {
            lock (_lock)
            {
                if (!_activeCalls.TryGetValue(answer.CallId, out var callContext))
                {
                    _logger.Warning($"Call not found for answer: {answer.CallId}");
                    return false;
                }

                callContext.Status = CallStatus.Connecting;
                callContext.AnswerSdp = answer.Sdp;
            }

            _logger.Info($"Call answered: {answer.CallId}");

            // Update call status to active
            UpdateCallStatus(answer.CallId, CallStatus.Active);

            return true;
        }

        /// <summary>
        /// Handle incoming call reject
        /// </summary>
        public async Task<bool> HandleCallReject(CallReject reject)
        {
            lock (_lock)
            {
                if (!_activeCalls.TryGetValue(reject.CallId, out var callContext))
                {
                    _logger.Warning($"Call not found for reject: {reject.CallId}");
                    return false;
                }

                callContext.Status = CallStatus.Ended;
                callContext.EndTime = DateTime.Now;
            }

            _logger.Info($"Call rejected: {reject.CallId} with reason: {reject.Reason}");

            // Move to call history
            await MoveToCallHistory(reject.CallId);

            // Remove from active calls
            lock (_lock)
            {
                _activeCalls.Remove(reject.CallId);
            }

            // Emit missed call event if it was an outgoing call
            var callContext = _activeCalls[reject.CallId];
            if (callContext.Direction == CallDirection.Outgoing)
            {
                EmitCallEvent(CallMissed, callContext);
            }

            return true;
        }

        /// <summary>
        /// Handle incoming call end
        /// </summary>
        public async Task<bool> HandleCallEnd(CallEnd end)
        {
            lock (_lock)
            {
                if (!_activeCalls.TryGetValue(end.CallId, out var callContext))
                {
                    _logger.Warning($"Call not found for end: {end.CallId}");
                    return false;
                }

                callContext.Status = CallStatus.Ended;
                callContext.EndTime = DateTime.Now;
                callContext.Duration = end.Duration;
            }

            _logger.Info($"Call ended: {end.CallId} with reason: {end.Reason}");

            // Move to call history
            await MoveToCallHistory(end.CallId);

            // Remove from active calls
            lock (_lock)
            {
                _activeCalls.Remove(end.CallId);
            }

            EmitCallEvent(CallEnded, _activeCalls[end.CallId]);

            return true;
        }

        /// <summary>
        /// Update call status
        /// </summary>
        private void UpdateCallStatus(string callId, CallStatus status)
        {
            lock (_lock)
            {
                if (_activeCalls.TryGetValue(callId, out var callContext))
                {
                    callContext.Status = status;
                    EmitCallEvent(CallStatusChanged, callContext);
                }
            }
        }

        #endregion

        #region Call Signaling

        /// <summary>
        /// Generate call offer
        /// </summary>
        private async Task<CallOffer> GenerateCallOffer(CallContext callContext)
        {
            // This would integrate with WebRTC to generate actual SDP offer
            // For now, return a placeholder
            await Task.Delay(10); // Simulate async operation

            return new CallOffer
            {
                CallId = callContext.CallId,
                FromJid = callContext.FromJid,
                ToJid = callContext.ToJid,
                CallType = callContext.CallType,
                Sdp = "placeholder_sdp_offer",
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// Generate call answer
        /// </summary>
        private async Task<CallAnswer> GenerateCallAnswer(string callId)
        {
            // This would integrate with WebRTC to generate actual SDP answer
            // For now, return a placeholder
            await Task.Delay(10); // Simulate async operation

            return new CallAnswer
            {
                CallId = callId,
                FromJid = "self",
                ToJid = _activeCalls[callId].FromJid,
                Sdp = "placeholder_sdp_answer",
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// Send call offer
        /// </summary>
        private async Task SendCallOffer(CallOffer offer)
        {
            // This would send the actual offer through WhatsApp signaling
            await Task.Delay(10); // Simulate network call
            _logger.Debug($"Call offer sent: {offer.CallId}");
        }

        /// <summary>
        /// Send call answer
        /// </summary>
        private async Task SendCallAnswer(CallAnswer answer)
        {
            // This would send the actual answer through WhatsApp signaling
            await Task.Delay(10); // Simulate network call
            _logger.Debug($"Call answer sent: {answer.CallId}");
        }

        /// <summary>
        /// Send call reject
        /// </summary>
        private async Task SendCallReject(CallReject reject)
        {
            // This would send the actual reject through WhatsApp signaling
            await Task.Delay(10); // Simulate network call
            _logger.Debug($"Call reject sent: {reject.CallId}");
        }

        /// <summary>
        /// Send call end
        /// </summary>
        private async Task SendCallEnd(CallEnd end)
        {
            // This would send the actual end signal through WhatsApp signaling
            await Task.Delay(10); // Simulate network call
            _logger.Debug($"Call end sent: {end.CallId}");
        }

        #endregion

        #region Call History

        /// <summary>
        /// Move call to history
        /// </summary>
        private async Task MoveToCallHistory(string callId)
        {
            lock (_lock)
            {
                if (!_activeCalls.TryGetValue(callId, out var callContext))
                {
                    return;
                }

                var historyEntry = new CallHistoryEntry
                {
                    CallId = callContext.CallId,
                    FromJid = callContext.FromJid,
                    ToJid = callContext.ToJid,
                    Direction = callContext.Direction,
                    Status = callContext.Status,
                    CallType = callContext.CallType,
                    StartTime = callContext.StartTime,
                    EndTime = callContext.EndTime,
                    Duration = callContext.Duration,
                    WasMissed = callContext.Status == CallStatus.Missed
                };

                if (!_callHistory.ContainsKey(callContext.FromJid))
                {
                    _callHistory[callContext.FromJid] = new List<CallHistoryEntry>();
                }

                _callHistory[callContext.FromJid].Add(historyEntry);
            }

            await Task.CompletedTask; // For async consistency
        }

        /// <summary>
        /// Get call history for a contact
        /// </summary>
        public List<CallHistoryEntry> GetCallHistory(string jid)
        {
            lock (_lock)
            {
                return _callHistory.TryGetValue(jid, out var history) 
                    ? history.OrderByDescending(h => h.StartTime).ToList() 
                    : new List<CallHistoryEntry>();
            }
        }

        /// <summary>
        /// Get all call history
        /// </summary>
        public Dictionary<string, List<CallHistoryEntry>> GetAllCallHistory()
        {
            lock (_lock)
            {
                var result = new Dictionary<string, List<CallHistoryEntry>>();
                foreach (var kvp in _callHistory)
                {
                    result[kvp.Key] = kvp.Value.OrderByDescending(h => h.StartTime).ToList();
                }
                return result;
            }
        }

        /// <summary>
        /// Get recent calls
        /// </summary>
        public List<CallHistoryEntry> GetRecentCalls(int count = 20)
        {
            lock (_lock)
            {
                return _callHistory.Values
                    .SelectMany(h => h)
                    .OrderByDescending(h => h.StartTime)
                    .Take(count)
                    .ToList();
            }
        }

        /// <summary>
        /// Clear call history for a contact
        /// </summary>
        public void ClearCallHistory(string jid)
        {
            lock (_lock)
            {
                _callHistory.Remove(jid);
            }
        }

        /// <summary>
        /// Clear all call history
        /// </summary>
        public void ClearAllCallHistory()
        {
            lock (_lock)
            {
                _callHistory.Clear();
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Generate unique call ID
        /// </summary>
        private string GenerateCallId()
        {
            return Guid.NewGuid().ToString("N")[..16];
        }

        /// <summary>
        /// Emit call event
        /// </summary>
        private void EmitCallEvent(EventHandler<CallEventArgs>? eventHandler, CallContext callContext)
        {
            eventHandler?.Invoke(this, new CallEventArgs
            {
                CallId = callContext.CallId,
                FromJid = callContext.FromJid,
                ToJid = callContext.ToJid,
                Direction = callContext.Direction,
                Status = callContext.Status,
                CallType = callContext.CallType,
                AdditionalData = callContext.Metadata
            });
        }

        /// <summary>
        /// Get active call
        /// </summary>
        public CallContext? GetActiveCall(string callId)
        {
            lock (_lock)
            {
                return _activeCalls.TryGetValue(callId, out var context) ? context : null;
            }
        }

        /// <summary>
        /// Get all active calls
        /// </summary>
        public List<CallContext> GetActiveCalls()
        {
            lock (_lock)
            {
                return _activeCalls.Values.ToList();
            }
        }

        /// <summary>
        /// Get call statistics
        /// </summary>
        public Dictionary<string, object> GetCallStatistics()
        {
            lock (_lock)
            {
                var totalCalls = _callHistory.Values.Sum(h => h.Count);
                var missedCalls = _callHistory.Values.Sum(h => h.Count(c => c.WasMissed));
                var totalDuration = _callHistory.Values.Sum(h => h.Sum(c => c.Duration?.TotalSeconds ?? 0));
                var averageDuration = totalCalls > 0 ? totalDuration / totalCalls : 0;

                return new Dictionary<string, object>
                {
                    { "TotalCalls", totalCalls },
                    { "MissedCalls", missedCalls },
                    { "ActiveCalls", _activeCalls.Count },
                    { "TotalDurationSeconds", totalDuration },
                    { "AverageDurationSeconds", averageDuration }
                };
            }
        }

        /// <summary>
        /// Cleanup old call history
        /// </summary>
        private void CleanupOldCallHistory(object? state)
        {
            lock (_lock)
            {
                var cutoffDate = DateTime.Now.AddDays(-30); // Keep calls from last 30 days
                
                foreach (var jid in _callHistory.Keys.ToList())
                {
                    var history = _callHistory[jid];
                    var recentCalls = history.Where(h => h.StartTime > cutoffDate).ToList();
                    
                    if (recentCalls.Any())
                    {
                        _callHistory[jid] = recentCalls;
                    }
                    else
                    {
                        _callHistory.Remove(jid);
                    }
                }

                _logger.Debug("Cleanup of old call history completed");
            }
        }

        #endregion

        public void Dispose()
        {
            _callCleanupTimer?.Dispose();
        }
    }
}





