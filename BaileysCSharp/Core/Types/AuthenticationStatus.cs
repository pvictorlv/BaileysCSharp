

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BaileysCSharp.Core.Types
{
    /// <summary>
    /// Represents the current authentication status of the connection
    /// </summary>
    public enum AuthenticationStatus
    {
        /// <summary>
        /// Authentication credentials have not been initialized
        /// </summary>
        NotInitialized = 0,

        /// <summary>
        /// Credentials exist but user is not authenticated
        /// </summary>
        NotAuthenticated = 1,

        /// <summary>
        /// User is authenticated but not registered with WhatsApp
        /// </summary>
        NotRegistered = 2,

        /// <summary>
        /// User is authenticated and registered but not connected
        /// </summary>
        Authenticated = 3,

        /// <summary>
        /// User is authenticated, registered, and connected to WhatsApp
        /// </summary>
        Connected = 4,

        /// <summary>
        /// Authentication failed due to invalid credentials
        /// </summary>
        AuthenticationFailed = 5,

        /// <summary>
        /// Session has expired and needs re-authentication
        /// </summary>
        SessionExpired = 6,

        /// <summary>
        /// Connection is in progress
        /// </summary>
        Connecting = 7,

        /// <summary>
        /// QR code is being generated and waiting for scan
        /// </summary>
        QRPending = 8,

        /// <summary>
        /// Pairing code is being generated and waiting for input
        /// </summary>
        PairingCodePending = 9
    }

    /// <summary>
    /// Authentication event arguments
    /// </summary>
    public class AuthenticationEventArgs : EventArgs
    {
        public AuthenticationStatus Status { get; set; }
        public string? Message { get; set; }
        public Exception? Error { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// QR code event arguments
    /// </summary>
    public class QRCodeEventArgs : EventArgs
    {
        public string QRCode { get; set; } = string.Empty;
        public byte[]? QRImage { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool IsRefresh { get; set; }
    }

    /// <summary>
    /// Pairing code event arguments
    /// </summary>
    public class PairingCodeEventArgs : EventArgs
    {
        public string PairingCode { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; }
        public TimeSpan ValidDuration { get; set; }
    }

    /// <summary>
    /// Session event arguments
    /// </summary>
    public class SessionEventArgs : EventArgs
    {
        public string SessionId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public bool IsRestored { get; set; }
    }
}


