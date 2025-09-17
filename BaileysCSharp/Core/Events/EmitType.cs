namespace BaileysCSharp.Core.Events
{
    public enum EmitType
    {
        Set = 1,
        Upsert = 2,
        Update = 4,
        Delete = 8,
        Reaction = 16,
        MessageRetryFailed = 32,
        MessageRetrySuccess = 64,
        MessageEdit = 128,
        MessageDelete = 256,
        HistorySyncStart = 512,
        HistorySyncComplete = 1024,
        HistorySyncError = 2048,
    }
}
