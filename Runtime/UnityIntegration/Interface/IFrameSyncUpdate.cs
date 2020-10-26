namespace SWNetwork.FrameSync
{
    public interface IFrameSyncUpdate
    {
        void FrameSyncUpdate(FrameSyncInput input, FrameSyncUpdateType frameSyncUpdateType);
    }
}
