


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BaileysCSharp.Core.Helper;
using BaileysCSharp.Core.Models;
using BaileysCSharp.Core.Types;
using BaileysCSharp.Core.WABinary;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;
using Proto;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;
using Size = SixLabors.ImageSharp.Size;

namespace BaileysCSharp.Core.Utils
{
    /// <summary>
    /// Enhanced media handler with comprehensive upload/download, encryption, thumbnail generation, and processing
    /// </summary>
    public class MediaHandler
    {
        private readonly ILogger _logger;
        private readonly SocketConfig _socketConfig;
        private readonly Dictionary<string, MediaProcessingContext> _processingContexts = new();
        private readonly object _lock = new();

        public class MediaProcessingContext
        {
            public string MediaId { get; set; } = string.Empty;
            public string OriginalPath { get; set; } = string.Empty;
            public string ProcessedPath { get; set; } = string.Empty;
            public string ThumbnailPath { get; set; } = string.Empty;
            public MediaProcessingStatus Status { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public double Progress { get; set; }
            public string? ErrorMessage { get; set; }
            public MediaProcessingOptions Options { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new();
        }

        public enum MediaProcessingStatus
        {
            Queued,
            Processing,
            Compressing,
            GeneratingThumbnail,
            Encrypting,
            Uploading,
            Completed,
            Failed,
            Cancelled
        }

        public class MediaProcessingOptions
        {
            public bool Compress { get; set; } = true;
            public bool GenerateThumbnail { get; set; } = true;
            public bool Encrypt { get; set; } = true;
            public int? MaxWidth { get; set; }
            public int? MaxHeight { get; set; }
            public int? Quality { get; set; }
            public int? MaxFileSize { get; set; }
            public string OutputFormat { get; set; } = "auto";
            public Dictionary<string, object> CustomSettings { get; set; } = new();
        }

        public class MediaInfo
        {
            public string FilePath { get; set; } = string.Empty;
            public string MimeType { get; set; } = string.Empty;
            public long FileSize { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int Duration { get; set; } // For audio/video
            public string? ThumbnailPath { get; set; }
            public byte[]? FileSha256 { get; set; }
            public byte[]? FileEncSha256 { get; set; }
            public byte[]? MediaKey { get; set; }
            public Dictionary<string, object> AdditionalMetadata { get; set; } = new();
        }

        public MediaHandler(ILogger logger, SocketConfig socketConfig)
        {
            _logger = logger;
            _socketConfig = socketConfig;
        }

        #region Media Processing

        /// <summary>
        /// Process media file with comprehensive handling
        /// </summary>
        public async Task<MediaInfo> ProcessMedia(string filePath, MediaProcessingOptions options)
        {
            var mediaId = GenerateMediaId();
            var context = new MediaProcessingContext
            {
                MediaId = mediaId,
                OriginalPath = filePath,
                Status = MediaProcessingStatus.Queued,
                StartTime = DateTime.Now,
                Options = options
            };

            lock (_lock)
            {
                _processingContexts[mediaId] = context;
            }

            try
            {
                _logger.Info($"Starting media processing: {filePath}");

                // Get basic media info
                var mediaInfo = await GetMediaInfo(filePath);
                context.Metadata["BasicInfo"] = mediaInfo;

                // Update status to processing
                UpdateProcessingStatus(mediaId, MediaProcessingStatus.Processing);

                // Compress if needed
                if (options.Compress && ShouldCompress(mediaInfo, options))
                {
                    UpdateProcessingStatus(mediaId, MediaProcessingStatus.Compressing);
                    var compressedPath = await CompressMedia(filePath, mediaInfo, options);
                    context.ProcessedPath = compressedPath;
                    
                    // Update media info with compressed file
                    mediaInfo = await GetMediaInfo(compressedPath);
                }
                else
                {
                    context.ProcessedPath = filePath;
                }

                // Generate thumbnail if needed
                if (options.GenerateThumbnail && ShouldGenerateThumbnail(mediaInfo))
                {
                    UpdateProcessingStatus(mediaId, MediaProcessingStatus.GeneratingThumbnail);
                    var thumbnailPath = await GenerateThumbnail(context.ProcessedPath, mediaInfo);
                    context.ThumbnailPath = thumbnailPath;
                    mediaInfo.ThumbnailPath = thumbnailPath;
                }

                // Encrypt if needed
                if (options.Encrypt)
                {
                    UpdateProcessingStatus(mediaId, MediaProcessingStatus.Encrypting);
                    var encryptedInfo = await EncryptMedia(context.ProcessedPath, mediaInfo);
                    mediaInfo.FileEncSha256 = encryptedInfo.FileEncSha256;
                    mediaInfo.MediaKey = encryptedInfo.MediaKey;
                }

                // Calculate file SHA256
                mediaInfo.FileSha256 = await CalculateFileSha256(context.ProcessedPath);

                UpdateProcessingStatus(mediaId, MediaProcessingStatus.Completed);
                context.EndTime = DateTime.Now;

                _logger.Info($"Media processing completed: {filePath}");
                return mediaInfo;
            }
            catch (Exception ex)
            {
                UpdateProcessingStatus(mediaId, MediaProcessingStatus.Failed, ex.Message);
                context.EndTime = DateTime.Now;
                _logger.Error(ex, $"Media processing failed: {filePath}");
                throw;
            }
        }

        /// <summary>
        /// Get comprehensive media information
        /// </summary>
        private async Task<MediaInfo> GetMediaInfo(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            var mimeType = MimeTypeUtils.GetMimeType(filePath);
            
            var mediaInfo = new MediaInfo
            {
                FilePath = filePath,
                MimeType = mimeType,
                FileSize = fileInfo.Length
            };

            // Get image/video dimensions
            if (mimeType.StartsWith("image/"))
            {
                using var image = await Image.LoadAsync(filePath);
                mediaInfo.Width = image.Width;
                mediaInfo.Height = image.Height;
            }
            else if (mimeType.StartsWith("video/"))
            {
                var mediaInfoResult = await FFProbe.AnalyseAsync(filePath);
                mediaInfo.Width = mediaInfoResult.VideoStreams.First().Width;
                mediaInfo.Height = mediaInfoResult.VideoStreams.First().Height;
                mediaInfo.Duration = (int)mediaInfoResult.VideoStreams.First().Duration.TotalSeconds;
            }
            else if (mimeType.StartsWith("audio/"))
            {
                var mediaInfoResult = await FFProbe.AnalyseAsync(filePath);
                mediaInfo.Duration = (int)mediaInfoResult.AudioStreams.First().Duration.TotalSeconds;
            }

            return mediaInfo;
        }

        /// <summary>
        /// Check if media should be compressed
        /// </summary>
        private bool ShouldCompress(MediaInfo mediaInfo, MediaProcessingOptions options)
        {
            if (!options.Compress) return false;

            // Check file size
            if (options.MaxFileSize.HasValue && mediaInfo.FileSize > options.MaxFileSize.Value)
                return true;

            // Check dimensions for images
            if (mediaInfo.MimeType.StartsWith("image/") && 
                (options.MaxWidth.HasValue && mediaInfo.Width > options.MaxWidth.Value ||
                 options.MaxHeight.HasValue && mediaInfo.Height > options.MaxHeight.Value))
                return true;

            return false;
        }

        /// <summary>
        /// Compress media file
        /// </summary>
        private async Task<string> CompressMedia(string filePath, MediaInfo mediaInfo, MediaProcessingOptions options)
        {
            var outputPath = Path.Combine(Path.GetTempPath(), $"compressed_{Path.GetFileName(filePath)}");

            try
            {
                if (mediaInfo.MimeType.StartsWith("image/"))
                {
                    await CompressImage(filePath, outputPath, mediaInfo, options);
                }
                else if (mediaInfo.MimeType.StartsWith("video/"))
                {
                    await CompressVideo(filePath, outputPath, mediaInfo, options);
                }
                else if (mediaInfo.MimeType.StartsWith("audio/"))
                {
                    await CompressAudio(filePath, outputPath, mediaInfo, options);
                }

                return outputPath;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to compress media: {filePath}");
                throw;
            }
        }

        /// <summary>
        /// Compress image file
        /// </summary>
        private async Task CompressImage(string inputPath, string outputPath, MediaInfo mediaInfo, MediaProcessingOptions options)
        {
            using var image = await Image.LoadAsync(inputPath);
            
            var targetWidth = options.MaxWidth ?? mediaInfo.Width;
            var targetHeight = options.MaxHeight ?? mediaInfo.Height;
            
            // Maintain aspect ratio
            var aspectRatio = (double)mediaInfo.Width / mediaInfo.Height;
            if (targetWidth / targetHeight > aspectRatio)
            {
                targetWidth = (int)(targetHeight * aspectRatio);
            }
            else
            {
                targetHeight = (int)(targetWidth / aspectRatio);
            }

            image.Mutate(x => x.Resize(new Size(targetWidth, targetHeight)));

            var quality = options.Quality ?? 80;
            var encoder = options.OutputFormat.ToLower() switch
            {
                "png" => new PngEncoder(),
                "webp" => new WebpEncoder { Quality = quality },
                _ => new JpegEncoder { Quality = quality }
            };

            await image.SaveAsync(outputPath, encoder);
        }

        /// <summary>
        /// Compress video file
        /// </summary>
        private async Task CompressVideo(string inputPath, string outputPath, MediaInfo mediaInfo, MediaProcessingOptions options)
        {
            var targetWidth = options.MaxWidth ?? mediaInfo.Width;
            var targetHeight = options.MaxHeight ?? mediaInfo.Height;
            var quality = options.Quality ?? 23; // CRF value for libx264

            await FFMpegArguments
                .FromFile(inputPath)
                .OutputToFile(outputPath, false, options => options
                    .WithVideoCodec("libx264")
                    .WithConstantRateFactor(quality)
                    .WithSize(targetWidth, targetHeight)
                    .WithAudioCodec("aac")
                    .WithAudioBitrate("128k"))
                .ProcessAsynchronously();
        }

        /// <summary>
        /// Compress audio file
        /// </summary>
        private async Task CompressAudio(string inputPath, string outputPath, MediaInfo mediaInfo, MediaProcessingOptions options)
        {
            var bitrate = options.CustomSettings.TryGetValue("audioBitrate", out var bitrateObj) 
                ? bitrateObj.ToString() 
                : "128k";

            await FFMpegArguments
                .FromFile(inputPath)
                .OutputToFile(outputPath, false, options => options
                    .WithAudioCodec("libmp3lame")
                    .WithAudioBitrate(bitrate))
                .ProcessAsynchronously();
        }

        /// <summary>
        /// Check if thumbnail should be generated
        /// </summary>
        private bool ShouldGenerateThumbnail(MediaInfo mediaInfo)
        {
            return mediaInfo.MimeType.StartsWith("image/") || 
                   mediaInfo.MimeType.StartsWith("video/");
        }

        /// <summary>
        /// Generate thumbnail for media
        /// </summary>
        private async Task<string> GenerateThumbnail(string filePath, MediaInfo mediaInfo)
        {
            var thumbnailPath = Path.Combine(Path.GetTempPath(), $"thumb_{Path.GetFileNameWithoutExtension(filePath)}.jpg");

            try
            {
                if (mediaInfo.MimeType.StartsWith("image/"))
                {
                    using var image = await Image.LoadAsync(filePath);
                    image.Mutate(x => x.Resize(new Size(200, 200)));
                    await image.SaveAsJpegAsync(thumbnailPath);
                }
                else if (mediaInfo.MimeType.StartsWith("video/"))
                {
                    await FFMpegArguments
                        .FromFile(filePath)
                        .OutputToFile(thumbnailPath, false, options => options
                            .WithVideoCodec("mjpeg")
                            .WithFrameOutputCount(1)
                            .WithSize(200, 200))
                        .ProcessAsynchronously();
                }

                return thumbnailPath;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to generate thumbnail: {filePath}");
                throw;
            }
        }

        /// <summary>
        /// Encrypt media file
        /// </summary>
        private async Task<(byte[] FileEncSha256, byte[] MediaKey)> EncryptMedia(string filePath, MediaInfo mediaInfo)
        {
            var fileData = await File.ReadAllBytesAsync(filePath);
            var mediaKey = CryptoUtils.RandomBytes(32);
            
            var encryptedData = CryptoUtils.EncryptAesCbc(fileData, mediaKey.Slice(0, 16), mediaKey.Slice(16, 32));
            var fileEncSha256 = CryptoUtils.Sha256(encryptedData);

            // Write encrypted data back to file
            await File.WriteAllBytesAsync(filePath, encryptedData);

            return (fileEncSha256, mediaKey);
        }

        /// <summary>
        /// Calculate SHA256 hash of file
        /// </summary>
        private async Task<byte[]> CalculateFileSha256(string filePath)
        {
            var fileData = await File.ReadAllBytesAsync(filePath);
            return CryptoUtils.Sha256(fileData);
        }

        /// <summary>
        /// Generate unique media ID
        /// </summary>
        private string GenerateMediaId()
        {
            return Guid.NewGuid().ToString("N")[..16];
        }

        /// <summary>
        /// Update processing status
        /// </summary>
        private void UpdateProcessingStatus(string mediaId, MediaProcessingStatus status, string? errorMessage = null)
        {
            lock (_lock)
            {
                if (_processingContexts.TryGetValue(mediaId, out var context))
                {
                    context.Status = status;
                    context.ErrorMessage = errorMessage;
                }
            }
        }

        #endregion

        #region Media Upload/Download

        /// <summary>
        /// Upload media with enhanced error handling and retry mechanism
        /// </summary>
        public async Task<MediaUploadResult> UploadMedia(string filePath, MediaProcessingOptions options, Func<bool, Task<MediaConnInfo>> refreshMediaConn)
        {
            try
            {
                _logger.Info($"Starting media upload: {filePath}");

                // Process media first
                var mediaInfo = await ProcessMedia(filePath, options);

                // Prepare upload options
                var uploadOptions = new MediaUploadOptions
                {
                    MediaType = GetMediaTypeFromMimeType(mediaInfo.MimeType),
                    FileEncSha256B64 = Convert.ToBase64String(mediaInfo.FileEncSha256 ?? Array.Empty<byte>())
                };

                // Upload using existing utility
                using var stream = new MemoryStream(await File.ReadAllBytesAsync(mediaInfo.FilePath));
                var result = await MediaMessageUtil.GetWAUploadToServer(_socketConfig, stream, uploadOptions, refreshMediaConn);

                _logger.Info($"Media upload completed: {filePath}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Media upload failed: {filePath}");
                throw;
            }
        }

        /// <summary>
        /// Download media with enhanced error handling and progress tracking
        /// </summary>
        public async Task<byte[]> DownloadMedia(string directPath, byte[] mediaKey, string mediaType, MediaDownloadOptions options)
        {
            try
            {
                _logger.Info($"Starting media download: {directPath}");

                // Create blob reference
                var blob = new ExternalBlobReference
                {
                    DirectPath = directPath,
                    MediaKey = Google.Protobuf.ByteString.CopyFrom(mediaKey)
                };

                // Download using existing utility
                var result = await MediaMessageUtil.DownloadContentFromMessage(blob, mediaType, options);

                _logger.Info($"Media download completed: {directPath}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Media download failed: {directPath}");
                throw;
            }
        }

        /// <summary>
        /// Get media type from MIME type
        /// </summary>
        private string GetMediaTypeFromMimeType(string mimeType)
        {
            return mimeType switch
            {
                string s when s.StartsWith("image/") => "Image",
                string s when s.StartsWith("video/") => "Video",
                string s when s.StartsWith("audio/") => "Audio",
                string s when s.Contains("pdf") => "Document",
                _ => "Document"
            };
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Get processing status for media
        /// </summary>
        public MediaProcessingContext? GetProcessingStatus(string mediaId)
        {
            lock (_lock)
            {
                return _processingContexts.TryGetValue(mediaId, out var context) ? context : null;
            }
        }

        /// <summary>
        /// Cancel media processing
        /// </summary>
        public bool CancelProcessing(string mediaId)
        {
            lock (_lock)
            {
                if (_processingContexts.TryGetValue(mediaId, out var context))
                {
                    if (context.Status is MediaProcessingStatus.Queued or MediaProcessingStatus.Processing)
                    {
                        context.Status = MediaProcessingStatus.Cancelled;
                        context.EndTime = DateTime.Now;
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Get all active processing contexts
        /// </summary>
        public List<MediaProcessingContext> GetActiveProcessingContexts()
        {
            lock (_lock)
            {
                return _processingContexts.Values
                    .Where(c => c.Status is MediaProcessingStatus.Queued or 
                                       MediaProcessingStatus.Processing or 
                                       MediaProcessingStatus.Compressing or 
                                       MediaProcessingStatus.GeneratingThumbnail or 
                                       MediaProcessingStatus.Encrypting or 
                                       MediaProcessingStatus.Uploading)
                    .ToList();
            }
        }

        /// <summary>
        /// Cleanup completed processing contexts
        /// </summary>
        public void CleanupCompletedContexts()
        {
            lock (_lock)
            {
                var completedKeys = _processingContexts
                    .Where(c => c.Status is MediaProcessingStatus.Completed or 
                                       MediaProcessingStatus.Failed or 
                                       MediaProcessingStatus.Cancelled)
                    .Where(c => c.EndTime.HasValue && DateTime.Now - c.EndTime.Value > TimeSpan.FromHours(1))
                    .Select(c => c.MediaId)
                    .ToList();

                foreach (var key in completedKeys)
                {
                    _processingContexts.Remove(key);
                }
            }
        }

        #endregion
    }
}



