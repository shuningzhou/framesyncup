namespace SWNetwork.FrameSync
{
    public interface IFrameSyncDebugger
    {
        void Initialize(FrameSyncAgent agent);

        bool Initialized();

        void WillStep(FrameSyncEngine engine, FrameSyncGame game);
        void DidStep(FrameSyncEngine engine, FrameSyncGame game);
    }
}
