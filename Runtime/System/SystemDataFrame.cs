using SWNetwork.Core;
using SWNetwork.Core.DataStructure;
using System;

namespace SWNetwork.FrameSync
{
    public interface IRestorable
    {
        void Restore();
        void Clear();
    }

    public class SWSystemDataFrame : IPersistentArrayData
    {
        public SWBytes bytes;
        IRestorable _userRestorable;

        internal int FrameNumber;

        public SWSystemDataFrame()
        {
            bytes = new SWBytes(FrameSyncConstant.DATA_FRAME_SIZE);
            FrameNumber = 0;
        }

        internal SWSystemDataFrame(int frameNumber)
        {
            bytes = new SWBytes(FrameSyncConstant.DATA_FRAME_SIZE);
            FrameNumber = frameNumber;
        }

        internal void Reset()
        {
            bytes.Reset();
            if(_userRestorable != null)
            {
                _userRestorable.Clear();
                _userRestorable = null;
            }
        }

        internal void SetUserRestorable(IRestorable restorable)
        {
            _userRestorable = restorable;
        }

        internal IRestorable GetUserRestorable()
        {
            return _userRestorable;
        }

        public void Export(SWBytes buffer)
        {
            buffer.PushFront((UInt16)bytes.DataLength);
            buffer.PushAll(bytes);
        }

        public void Import(SWBytes buffer)
        {
            UInt16 dataLength = buffer.PopUInt16();
            buffer.PopByteBuffer(bytes, 0, (int)dataLength);
        }
    }
}
