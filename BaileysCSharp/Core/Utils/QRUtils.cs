

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BaileysCSharp.Core.Types;
using BaileysCSharp.Core.Helper;
using BaileysCSharp.LibSignal;
using QRCoder;
using System.Drawing;
using System.IO;

namespace BaileysCSharp.Core.Utils
{
    public static class QRUtils
    {
        /// <summary>
        /// Generate QR code for authentication
        /// </summary>
        public static string GenerateQRCode(string refId, string publicKey, string privateKey, string advSecretKey)
        {
            var qrParts = new List<string>
            {
                refId,
                Convert.ToBase64String(publicKey),
                Convert.ToBase64String(privateKey),
                advSecretKey
            };

            return string.Join(",", qrParts);
        }

        /// <summary>
        /// Parse QR code string into components
        /// </summary>
        public static (string refId, string publicKey, string privateKey, string advSecretKey)? ParseQRCode(string qrCode)
        {
            var parts = qrCode.Split(',');
            if (parts.Length != 4) return null;

            return (parts[0], parts[1], parts[2], parts[3]);
        }

        /// <summary>
        /// Generate QR code image as byte array
        /// </summary>
        public static byte[] GenerateQRImage(string qrCode, int pixelSize = 10)
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(qrCode, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new PngByteQRCode(qrCodeData);
            return qrCode.GetGraphic(pixelSize);
        }

        /// <summary>
        /// Generate pairing code for multi-device authentication
        /// </summary>
        public static string GeneratePairingCode()
        {
            var random = new Random();
            var code = new StringBuilder();
            
            for (int i = 0; i < 8; i++)
            {
                if (i > 0 && i % 2 == 0)
                {
                    code.Append("-");
                }
                code.Append(random.Next(0, 10));
            }
            
            return code.ToString();
        }

        /// <summary>
        /// Validate pairing code format
        /// </summary>
        public static bool ValidatePairingCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            
            // Format should be XX-XX-XX-XX where X is a digit
            var parts = code.Split('-');
            if (parts.Length != 4) return false;
            
            return parts.All(part => 
                part.Length == 2 && 
                part.All(char.IsDigit));
        }
    }

    public class AuthenticationSession
    {
        public string RefId { get; set; } = Guid.NewGuid().ToString("N")[..16];
        public string PublicKey { get; set; }
        public string PrivateKey { get; set; }
        public string AdvSecretKey { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime ExpiresAt { get; set; } = DateTime.Now.AddMinutes(2);
        public bool IsExpired => DateTime.Now > ExpiresAt;

        public AuthenticationSession()
        {
            var keyPair = Curve.GenerateKeyPair();
            PublicKey = Convert.ToBase64String(keyPair.Public);
            PrivateKey = Convert.ToBase64String(keyPair.Private);
            AdvSecretKey = RandomBytes(32).ToBase64();
        }

        public string GetQRCode()
        {
            return QRUtils.GenerateQRCode(RefId, PublicKey, PrivateKey, AdvSecretKey);
        }

        public byte[] GetQRImage()
        {
            return QRUtils.GenerateQRImage(GetQRCode());
        }
    }
}

