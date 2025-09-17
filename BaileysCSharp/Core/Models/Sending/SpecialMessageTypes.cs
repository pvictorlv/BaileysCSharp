


using BaileysCSharp.Core.Models.Sending.Interfaces;
using Proto;

namespace BaileysCSharp.Core.Models.Sending
{
    /// <summary>
    /// Message content for deleting messages
    /// </summary>
    public class DeleteMessage : IAnyMessageContent, IDeleteable
    {
        public MessageKey? Delete { get; set; }
        public bool? DisappearingMessagesInChat { get; set; }
    }

    /// <summary>
    /// Message content for reactions
    /// </summary>
    public class ReactionMessage : IAnyMessageContent
    {
        public Message.Types.Reaction? Reaction { get; set; }
        public bool? DisappearingMessagesInChat { get; set; }
    }

    /// <summary>
    /// Message content for editing messages
    /// </summary>
    public class EditMessage : IAnyMessageContent, IEditable
    {
        public string? Edit { get; set; }
        public object? Content { get; set; }
        public bool? DisappearingMessagesInChat { get; set; }
    }

    /// <summary>
    /// Message content for poll messages
    /// </summary>
    public class PollMessage : IAnyMessageContent
    {
        public Message.Types.PollCreationMessage? Poll { get; set; }
        public bool? DisappearingMessagesInChat { get; set; }
    }

    /// <summary>
    /// Message content for poll updates
    /// </summary>
    public class PollUpdateMessage : IAnyMessageContent
    {
        public Message.Types.PollUpdateMessage? PollUpdate { get; set; }
        public bool? DisappearingMessagesInChat { get; set; }
    }

    /// <summary>
    /// Message content for sticker messages
    /// </summary>
    public class StickerMessage : IAnyMessageContent, IMediaMessage
    {
        public Message.Types.StickerMessage? Sticker { get; set; }
        public bool? DisappearingMessagesInChat { get; set; }
        
        // IMediaMessage implementation
        public string? Mimetype => Sticker?.Mimetype;
        public byte[]? FileSha256 => Sticker?.FileSha256?.ToByteArray();
        public ulong? FileLength => Sticker?.FileLength;
        public uint? MediaKeyTimestamp => Sticker?.MediaKeyTimestamp;
        public string? Url => Sticker?.Url;
        public string? DirectPath => Sticker?.DirectPath;
    }

    /// <summary>
    /// Message content for contact messages
    /// </summary>
    public class ContactMessage : IAnyMessageContent
    {
        public Message.Types.ContactMessage? Contact { get; set; }
        public bool? DisappearingMessagesInChat { get; set; }
    }

    /// <summary>
    /// Message content for location messages
    /// </summary>
    public class LocationMessage : IAnyMessageContent
    {
        public Message.Types.LocationMessage? Location { get; set; }
        public bool? DisappearingMessagesInChat { get; set; }
    }

    /// <summary>
    /// Message content for live location messages
    /// </summary>
    public class LiveLocationMessage : IAnyMessageContent
    {
        public Message.Types.LiveLocationMessage? LiveLocation { get; set; }
        public bool? DisappearingMessagesInChat { get; set; }
    }

    /// <summary>
    /// Message content for document messages
    /// </summary>
    public class DocumentMessage : IAnyMessageContent, IMediaMessage
    {
        public Message.Types.DocumentMessage? Document { get; set; }
        public bool? DisappearingMessagesInChat { get; set; }
        
        // IMediaMessage implementation
        public string? Mimetype => Document?.Mimetype;
        public byte[]? FileSha256 => Document?.FileSha256?.ToByteArray();
        public ulong? FileLength => Document?.FileLength;
        public uint? MediaKeyTimestamp => Document?.MediaKeyTimestamp;
        public string? Url => Document?.Url;
        public string? DirectPath => Document?.DirectPath;
    }

    /// <summary>
    /// Message content for audio messages
    /// </summary>
    public class AudioMessage : IAnyMessageContent, IMediaMessage
    {
        public Message.Types.AudioMessage? Audio { get; set; }
        public bool? DisappearingMessagesInChat { get; set; }
        
        // IMediaMessage implementation
        public string? Mimetype => Audio?.Mimetype;
        public byte[]? FileSha256 => Audio?.FileSha256?.ToByteArray();
        public ulong? FileLength => Audio?.FileLength;
        public uint? MediaKeyTimestamp => Audio?.MediaKeyTimestamp;
        public string? Url => Audio?.Url;
        public string? DirectPath => Audio?.DirectPath;
    }

    /// <summary>
    /// Message content for video messages
    /// </summary>
    public class VideoMessage : IAnyMessageContent, IMediaMessage
    {
        public Message.Types.VideoMessage? Video { get; set; }
        public bool? DisappearingMessagesInChat { get; set; }
        
        // IMediaMessage implementation
        public string? Mimetype => Video?.Mimetype;
        public byte[]? FileSha256 => Video?.FileSha256?.ToByteArray();
        public ulong? FileLength => Video?.FileLength;
        public uint? MediaKeyTimestamp => Video?.MediaKeyTimestamp;
        public string? Url => Video?.Url;
        public string? DirectPath => Video?.DirectPath;
    }

    /// <summary>
    /// Message content for image messages
    /// </summary>
    public class ImageMessage : IAnyMessageContent, IMediaMessage
    {
        public Message.Types.ImageMessage? Image { get; set; }
        public bool? DisappearingMessagesInChat { get; set; }
        
        // IMediaMessage implementation
        public string? Mimetype => Image?.Mimetype;
        public byte[]? FileSha256 => Image?.FileSha256?.ToByteArray();
        public ulong? FileLength => Image?.FileLength;
        public uint? MediaKeyTimestamp => Image?.MediaKeyTimestamp;
        public string? Url => Image?.Url;
        public string? DirectPath => Image?.DirectPath;
    }
}


