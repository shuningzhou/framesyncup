using Parallel;
using SWNetwork.Core;
using SWNetwork.Core.DataStructure;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace SWNetwork.FrameSync
{
    public delegate void WillSimulate();
    public delegate IRestorable WillExport(int frameNumber);
    public delegate void WillRestore(int frameNumber, IRestorable restorable);

    public class FrameSyncEngine : IFrameSyncHandler, IFrameSyncInputProvider
    {
        string _debugName = "[FrameSyncEngine]";

        private readonly object FRAME_SYNC_LOCK = new object();

        IFrameSyncIO _io;
        FrameSyncGame _game;

        DynamicFrameSyncBehaviourManager _dynamicFrameSyncBehaviourManager = null;
        StaticFrameSyncBehaviourManager _staticFrameSyncBehaviourManager = null;

        public WillSimulate OnEngineWillSimulateEvent;
        public WillExport OnEngineWillExportEvent;

        SWFrameSyncSystem[] _systems;

        internal FrameSyncInput _input;

        public FrameSyncEngine()
        {
            _staticFrameSyncBehaviourManager = new StaticFrameSyncBehaviourManager();
            _dynamicFrameSyncBehaviourManager = new DynamicFrameSyncBehaviourManager();
            _systems = new SWFrameSyncSystem[2];
            _systems[0] = _staticFrameSyncBehaviourManager;
            _systems[1] = _dynamicFrameSyncBehaviourManager;
            _lastInputFrameForPrediction = new InputFrame();
            _inputFrameForPrediction = new InputFrame();
        }

        public void SetFrameSyncInputConfig(FrameSyncInputConfig inputConfig)
        {
            _input = new FrameSyncInput(inputConfig);
            _input.SetInputProvider(this);
        }

        public void SetNetworkIO(IFrameSyncIO io)
        {
            _io = io;
            _io.SetFrameSyncHandler(this);
        }

        string _replayFilePath;
        public void SetReplayFilePath(string filePath)
        {
            _replayFilePath = filePath;
        }

        internal void SetFrameSyncGame(FrameSyncGame game)
        {
            _game = game;
        }

        internal void AddFrameSyncSystems(params SWFrameSyncSystem[] systems)
        {
            _systems = systems;
            if (_systems == null)
            {
                _systems = new SWFrameSyncSystem[0];
            }
        }

        public void Stop()
        {
            foreach (SWFrameSyncSystem system in _systems)
            {
                system.Stop();
            }

            _input.SetInputProvider(null);
            _game.gameState = FrameSyncGameState.Stopped;
        }

        public void SaveReplay(int lastSaveEndIndex)
        {
            if (inputFrameDeltas != null)
            {
                inputFrameDeltas.Save(lastSaveEndIndex, _currentInputFrameNumber);
            }
        }

        public void Start()
        {
            foreach (SWFrameSyncSystem system in _systems)
            {
                system.Start();
            }

            _game.gameState = FrameSyncGameState.InitializingRoomFrame;
        }

        float _inputSampleTimer = 0;
        int _localCounter = 0;
        private Stopwatch _stopwatch = new Stopwatch();
        private Stopwatch _stopwatch1 = new Stopwatch();
        public bool OnUpdate(float deltaTime)
        {
            _stopwatch.Stop();
            double elapsed = _stopwatch.Elapsed.TotalSeconds;
            _stopwatch.Restart();

            if (_game.type == FrameSyncGameType.Online && _game.gameState == FrameSyncGameState.Running)
            {
                _inputSampleTimer += deltaTime;
                int serverPlayerFrameCount = PlayerFrameCountOnServer;
                int localServerFrameCount = LocalServerFrameCount;

                bool adjusted = false;

                if (_game.clientSidePrediction)
                {
                    FrameSyncTime.DoFixedTickIfNecessary((float)elapsed, serverPlayerFrameCount, () =>
                    {
                        if(_localCounter == 0)
                        {
                            _stopwatch1.Start();
                        }
                        _localCounter++;
                        SWConsole.Debug($"=====[OnUpdate] serverPlayerFrameCount={serverPlayerFrameCount} local={_localCounter} server={_lastReceivedInputFrameDeltaNumber}=====");
                        FlushInputOnlinePrediction();
                        RunningOnlineWithPrediction();

                        if(_localCounter == 900)
                        {
                            _stopwatch1.Stop();
                            double elapsed1 = _stopwatch1.Elapsed.TotalSeconds;
                            //SWConsole.Error($"elapsed={elapsed1}");
                        }
                    });
                }
                else
                {
                    adjusted = FrameSyncTime.Adjust(serverPlayerFrameCount, localServerFrameCount, deltaTime);

                    if (_inputSampleTimer > FrameSyncTime.internalInputSampleInterval)
                    {
                        _inputSampleTimer = 0;

                        FlushInputOnline();
                    }
                }

                return adjusted;
            }

            return false;
        }

        internal int PlayerFrameCountOnServer
        {
            get
            {
                if (_game.type == FrameSyncGameType.Online)
                {
                    return _playerFrameCountOnServer;
                }
                else
                {
                    return 0;
                }
            }  
        }

        internal int LocalServerFrameCount
        {
            get
            {
                if(_game.type == FrameSyncGameType.Online)
                {
                    //received is input delta
                    // inputFrame1 = inputFrame0 + inputDelta0
                    return _lastReceivedInputFrameDeltaNumber + 1 - _currentInputFrameNumber;
                }
                else
                {
                    return 0;
                }
            }
        }

        public void Step(int pingMS)
        {
            lock (FRAME_SYNC_LOCK)
            {
                switch(_game.gameState)
                {
                    case FrameSyncGameState.InitializingRoomFrame:
                        {
                            InitializingRoomFrame();
                            break;
                        }
                    case FrameSyncGameState.WaitingForRoomFrame:
                        {
                            WaitingForRoomFrame();
                            break;
                        }
                    case FrameSyncGameState.WaitingForInitialSystemData:
                        {
                            WaitingForInitialSystemData();
                            break;
                        }
                    case FrameSyncGameState.Running:
                        {
                            if(_game.type == FrameSyncGameType.Offline)
                            {
                                RunningOffline();
                            }
                            else
                            {
                                if(_game.clientSidePrediction == false)
                                {
                                    RunningOnline();
                                }
                                else
                                {
                                    //RunningOnlineWithPrediction();
                                }
                            }
                            break;
                        }
                }
            }
        }

        void InitializingRoomFrame()
        {
            if (_game.type == FrameSyncGameType.Offline)
            {
                _game.gameState = FrameSyncGameState.Running;

                _lastReceivedInputFrameDeltaNumber = int.MaxValue - 100; //a large number, -100 to make it don't overflow

                _currentInputFrameNumber = 1;

                InitializeFrames(0);
                SetSaveHandler(0);
                //create an empty input frame to start with
                //input frame delta will be created in the next FlushInput 
                inputFrames[_currentInputFrameNumber] = new InputFrame(_currentInputFrameNumber);
            }
            else
            {
                _io.StartReceivingInputFrame();
                _game.gameState = FrameSyncGameState.WaitingForRoomFrame;
                ResetTimeStamp();
            }
        }

        Action<SWBytes, int, int> _saveHandler;
        internal void SetInputSaveHandler(Action<SWBytes, int, int> handler)
        {
            _saveHandler = handler;
        }

        void InitializeFrames(int startIndex)
        {
            inputFrameDeltas = new PersistentArray<InputFrameDelta>(FrameSyncConstant.DEFAULT_FRAMES_CHUNK_SIZE, startIndex);
            inputFrames = new PersistentArray<InputFrame>(FrameSyncConstant.DEFAULT_FRAMES_CHUNK_SIZE, startIndex);
            systemDataFrames = new PersistentArray<SWSystemDataFrame>(FrameSyncConstant.DEFAULT_SYSTEM_DATA_CHUNK_SIZE, startIndex);
            localInputFrameDeltas = new PersistentArray<InputFrameDelta>(FrameSyncConstant.DEFAULT_FRAMES_CHUNK_SIZE, 0);
        }

        void SetSaveHandler(int skipSaveIndex)
        {
            SWConsole.Warn($"SetSaveHandler skipSaveIndex={skipSaveIndex}");
            if (_game.replayFileName != null)
            {
                if (_saveHandler != null)
                {
                    inputFrameDeltas.SetSaveDataHandler(_saveHandler, InputFrameDelta.DataSize);
                    inputFrameDeltas.SetSkipSaveIndex(skipSaveIndex);
                }
            }
        }

        void WaitingForRoomFrame()
        {
            if(_firstFrameReceived > 0)
            {
                SWConsole.Crit($"WaitingForRoomFrame _firstFrameReceived={_firstFrameReceived}");
                InputFrameDelta delta = inputFrameDeltas[_firstFrameReceived];

                if(delta != null)
                {
                    SWConsole.Crit($"WaitingForRoomFrame delta not null Delta.frameNumber = {delta.frameNumber}");
                    if (delta.frameNumber == _firstFrameReceived)
                    {
                        if (_firstFrameReceived > 1)
                        {
                            _game.gameState = FrameSyncGameState.WaitingForInitialSystemData;
                            SWConsole.Crit($"WaitingForRoomFrame RequestInputFrames end={_firstFrameReceived}");

                            _io.RequestInputFrames(1, _firstFrameReceived);
                            SWConsole.Crit($"WaitingForRoomFrame game WaitingForInitialSystemData now");
                        }
                        else
                        {
                            //start from 1st frame
                            _currentInputFrameNumber = 1;

                            //create an empty input frame to start with
                            inputFrames[_currentInputFrameNumber] = new InputFrame(_currentInputFrameNumber);
                            _game.gameState = FrameSyncGameState.Running;
                            SetSaveHandler(0);
                            SWConsole.Crit($"WaitingForRoomFrame game running now");
                        }

                        ResetTimeStamp();
                        return;
                    }
                }
            }
            if (CheckInterval(FrameSyncConstant.SERVER_FRAME_INITIALIZATION_INTERVAL))
            {
                SWBytes buffer = new SWBytes(32);

                buffer.Push(0); //frame number
                buffer.Push(0); //predict
                byte length = 0;
                buffer.Push(length);
                _io.SendInputFrameDeltas(buffer, 1, _input.Size);
            }
        }

        void WaitingForInitialSystemData()
        {
            if (HasNewInitialInputFrameDeltas())
            {
                //play all initial input frame
                SWConsole.Crit($"WaitingForInitialSystemData has initial input deltas startFrameNumber={_startFrameNumber}");
                InputFrame inputFrame1 = new InputFrame();
                InputFrame inputFrame2 = new InputFrame();

                int frameNumber = _startFrameNumber + 1; //if start number is 1 delta, we need to simulate 2 because 2 = 1 input + 1 delta
                foreach(InputFrameDelta delta in _initialInputFrameDeltas)
                {
                    inputFrame2.ResetBytes();
                    delta.Apply(_input, inputFrame1, inputFrame2);

                    FrameSyncUpdateType updateType = FrameSyncUpdateType.Restore;
                    SWConsole.Crit($"WaitingForInitialSystemData simulate {frameNumber}");

                    DoSimulate(updateType, inputFrame2, frameNumber);
                   
                    InputFrame temp = inputFrame1;
                    inputFrame1 = inputFrame2;
                    inputFrame2 = temp;
                    frameNumber++;
                }

                //start from the last restored frame;
                frameNumber--;
                _currentInputFrameNumber = frameNumber;
                ExportSimulationResult();
                //create an empty input frame to start with
                inputFrames[frameNumber] = inputFrame1;
                //export system data
                ExportSimulationResult();
                SWConsole.Warn($"WaitingForInitialSystemData _initialInputFramesData={_initialInputFramesData.DataLength}");
                _saveHandler(_initialInputFramesData, _startFrameNumber, _endFrameNumber);
                _game.gameState = FrameSyncGameState.Running;

                SetSaveHandler(_endFrameNumber - 1); //end frame was excluded from initial frames, so we want to save it
                SWConsole.Crit($"WaitingForInitialSystemData game is running now _currentInputFrameNumber={_currentInputFrameNumber}");
                ResetTimeStamp();
                return;
            }
        }

        //for local games
        void RunningOffline()
        {
            FlushInputOffline();

            if(CanSimulateInputFrame(_currentInputFrameNumber + 1))
            {
                SimulateInputFrame(_currentInputFrameNumber + 1);
                ExportSimulationResult();
            }
        }

        void RunningOnline()
        {
            SWConsole.Info($"Engine: ================RunningOnline {_currentInputFrameNumber + 1}=================");
            
            if (CanSimulateInputFrame(_currentInputFrameNumber + 1))
            {
                SimulateInputFrame(_currentInputFrameNumber + 1);
                ExportSimulationResult();
            }

            SWConsole.Info("Engine: ================end=================");
        }

        int _nextPlayerFrameNumberToConfirm = 0;

        void RunningOnlineWithPrediction()
        {
            SWConsole.Crit("Engine: ================RunningOnlineWithPrediction=================");
            //FlushInputOnlinePrediction();

            // check if we got new server frame to simulate
            int nextServerFrame = _currentInputFrameNumber + 1;

            if ( true )//CanSimulateInputFrame(nextServerFrame))
            {
                // restore the last simulated server frame before simulate any new server frame
                RestoreToConfirmedFrame();
            }

            int lastSimulatedPlayerFrameNumber = 0;

            // simulate all server frames first
            for (; nextServerFrame <= _lastReceivedInputFrameDeltaNumber + 1; nextServerFrame++)
            {
                if (CanSimulateInputFrame(nextServerFrame))
                {
                    lastSimulatedPlayerFrameNumber = SimulateInputFrame(nextServerFrame);
                    SWConsole.Crit($"lastSimulatedPlayerFrameNumber={lastSimulatedPlayerFrameNumber}");
                    if (lastSimulatedPlayerFrameNumber == 0)
                    {
                        // local player's input frame missing for this frame
                        if(_nextPlayerFrameNumberToConfirm > 1)
                        {
                            SWConsole.Warn("wtf");
                        }
                    }
                    else
                    {
                        _nextPlayerFrameNumberToConfirm = lastSimulatedPlayerFrameNumber + 1;
                        SWConsole.Crit($"nextPlayerFrameNumberToConfirm={_nextPlayerFrameNumberToConfirm}");
                    }

                    ExportSimulationResult();
                }
                else
                {
                    break;
                }
            }

            // if last simulated server frame has local player's input
            // we should simulate all player's input frames after it
            if ( true )// lastSimulatedPlayerFrameNumber > 0)
            {
                InputFrame lastInputFrame = inputFrames[nextServerFrame - 1];
                lastInputFrame.Copy(_lastInputFrameForPrediction);
                int startPlayerFrameNumber = _nextPlayerFrameNumberToConfirm;

                int endPlayerFrameNumber = _currentLocalInputFrameDeltaNumber - FrameSyncConstant.PREDICTION_GLOBAL_DEBAY_FRAMES;

                int predictFrameNumber = nextServerFrame;

                SWConsole.Crit($"startPlayerFrameNumber={startPlayerFrameNumber}");
                SWConsole.Crit($"endPlayerFrameNumber={endPlayerFrameNumber}");
                // endPlayerFrameNumber + 1 to include the endPlayerFrameNumber
                for (int i = startPlayerFrameNumber; i < endPlayerFrameNumber + 1; i++)
                {

                    Predict(i, predictFrameNumber);
                    predictFrameNumber++;
                    
                    //swap prediction InputFrames for the next prediction
                    InputFrame temp = _lastInputFrameForPrediction;
                    _lastInputFrameForPrediction = _inputFrameForPrediction;
                    _inputFrameForPrediction = temp;
                }
            }

            //reset game.frameNumber to the last confirmed frame
            //this is for debug server
            _game.frameNumber = _currentInputFrameNumber;

            SWConsole.Crit("Engine: ================end=================");
        }

        bool CanSimulateInputFrame(int frameNumber)
        {
            if (frameNumber > _lastReceivedInputFrameDeltaNumber + 1)
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        void RestoreToConfirmedFrame()
        {
            //skip the first frame because there is no systemData to restore to
            if(_currentInputFrameNumber > 1)
            {
                SWConsole.Crit($"Engine: RestoreToConfirmedFrame {_currentInputFrameNumber}");
                SWSystemDataFrame systemDataFrame = systemDataFrames[_currentInputFrameNumber];

                IRestorable restorable = systemDataFrame.GetUserRestorable();
                if(restorable != null)
                {
                    restorable.Restore();
                }

                ReloadSystemDataSnapshot(systemDataFrame.bytes);
                systemDataFrame.bytes.SetReadIndex(0);
            }
        }

        void FlushInputOffline()
        {
            //write directly to inputFrameDeltas
            InputFrameDelta inputFrameDelta = inputFrameDeltas[_currentInputFrameNumber];
            if (inputFrameDelta == null)
            {
                inputFrameDelta = new InputFrameDelta(_currentInputFrameNumber);
                inputFrameDeltas[_currentInputFrameNumber] = inputFrameDelta;
            }
            inputFrameDelta.frameNumber = _currentInputFrameNumber;

            inputFrameDelta.ResetBytes();
            _input.ExportInput(inputFrameDelta.bytes);
        }

        public void FlushInputOnline()
        {
            InputFrameDelta previousInputDelta = localInputFrameDeltas[_currentLocalInputFrameDeltaNumber];

            _currentLocalInputFrameDeltaNumber++;

            InputFrameDelta inputFrameDelta = localInputFrameDeltas[_currentLocalInputFrameDeltaNumber];
            if (inputFrameDelta == null)
            {
                inputFrameDelta = new InputFrameDelta(_currentLocalInputFrameDeltaNumber);
                localInputFrameDeltas[_currentLocalInputFrameDeltaNumber] = inputFrameDelta;
            }

            inputFrameDelta.frameNumber = _currentLocalInputFrameDeltaNumber;
            inputFrameDelta.resend = FrameSyncConstant.LOCAL_INPUT_FRAME_RESEND_COUNT;
            inputFrameDelta.ResetBytes();
            _input.ExportInput(inputFrameDelta.bytes);

            bool inputChanged = false;
            if (previousInputDelta == null)
            {
                inputChanged = true;
            }
            else
            {
                bool sameInput = previousInputDelta.IsSameInput(inputFrameDelta);
                inputChanged = !sameInput;
            }

            if (!inputChanged)
            {
                SWConsole.Crit("Engine: Input did NOT Change");
                _currentLocalInputFrameDeltaNumber--;
            }
            else
            {
                SWConsole.Crit("Engine: Input Changed");
            }

            SendLocalInputs();
        }

        InputFrameDelta _EMPTY_INPUT_FRAME_DELTA = new InputFrameDelta();
        void FlushInputOnlinePrediction()
        {
            InputFrameDelta previousInputDelta = localInputFrameDeltas[_currentLocalInputFrameDeltaNumber];

            _currentLocalInputFrameDeltaNumber++;

            if(_nextPlayerFrameNumberToConfirm == 0)
            {
                _nextPlayerFrameNumberToConfirm = _currentLocalInputFrameDeltaNumber;
            }

            InputFrameDelta inputFrameDelta = localInputFrameDeltas[_currentLocalInputFrameDeltaNumber];

            if (inputFrameDelta == null)
            {
                inputFrameDelta = new InputFrameDelta(_currentLocalInputFrameDeltaNumber);
                localInputFrameDeltas[_currentLocalInputFrameDeltaNumber] = inputFrameDelta;
            }

            inputFrameDelta.frameNumber = _currentLocalInputFrameDeltaNumber;
            inputFrameDelta.resend = FrameSyncConstant.LOCAL_INPUT_FRAME_RESEND_COUNT;
            inputFrameDelta.ResetBytes();
            _input.ExportInput(inputFrameDelta.bytes);

            bool inputChanged = false;
            if (previousInputDelta == null)
            {
                inputChanged = true;
            }
            else
            {
                bool sameInput = previousInputDelta.IsSameInput(inputFrameDelta);
                inputChanged = !sameInput;
            }

            if (!inputChanged)
            {
                SWConsole.Crit($"Engine: FlushInputOnlinePrediction Input did NOT Change: localFN={_currentLocalInputFrameDeltaNumber}");
                //_currentLocalInputFrameDeltaNumber--;
                //send an empty frame to keep the fixed delta time adjustment running
                inputFrameDelta.ResetBytes();
            }
            else
            {
                SWConsole.Crit($"Engine: FlushInputOnlinePrediction Input changed: localFN={_currentLocalInputFrameDeltaNumber}");
            }

            SendLocalInputs();
        }

        SWBytes _sendLocalInputDeltaBuffer = new SWBytes(512);
        void SendLocalInputs()
        {
            if(_localInputFrameDeltaNumberToSend == 0)
            {
                _localInputFrameDeltaNumberToSend = _currentLocalInputFrameDeltaNumber;
            }

            _sendLocalInputDeltaBuffer.Reset();

            int end = _localInputFrameDeltaNumberToSend + FrameSyncConstant.LOCAL_INPUT_FRAME_RESEND_COUNT;
            if(end > _currentLocalInputFrameDeltaNumber)
            {
                end = _currentLocalInputFrameDeltaNumber;
            }

            int count = 0;
            for(int i = _localInputFrameDeltaNumberToSend; i <= end; i++)
            {
                InputFrameDelta inputFrameDelta = localInputFrameDeltas[i];
                _sendLocalInputDeltaBuffer.Push(inputFrameDelta.frameNumber);

                byte length = (byte)inputFrameDelta.bytes.DataLength;
                _sendLocalInputDeltaBuffer.Push(length);
                _sendLocalInputDeltaBuffer.PushAll(inputFrameDelta.bytes);

                count++;
                inputFrameDelta.resend = inputFrameDelta.resend - 1;
                if(inputFrameDelta.resend == 0)
                {
                    _localInputFrameDeltaNumberToSend++;
                }
            }

            if(count > 0)
            {
                _io.SendInputFrameDeltas(_sendLocalInputDeltaBuffer, count, _input.Size);
            }
        }

        //must call CanSimulateInputFrame before calling simulate
        int SimulateInputFrame(int frameNumber)
        {
            int playerFrameNumber = Simulate(frameNumber);
            _currentInputFrameNumber = frameNumber;
            return playerFrameNumber;
        }

        InputFrame _lastInputFrameForPrediction;
        InputFrame _inputFrameForPrediction;

        bool Predict(int localFrameDeltaNumber, int frameNumber)
        {
            SWConsole.Crit($"Engine: Predict localFrameDeltaNumber={localFrameDeltaNumber} frameNumber={frameNumber}");

            InputFrameDelta inputFrameDelta = localInputFrameDeltas[localFrameDeltaNumber];

            _inputFrameForPrediction.FrameNumber = frameNumber;
            _inputFrameForPrediction.ResetBytes();

            inputFrameDelta.Apply(_input, _lastInputFrameForPrediction, _inputFrameForPrediction);

            _input.ApplyPredictionModifier(_inputFrameForPrediction.bytes);

            FrameSyncUpdateType updateType = FrameSyncUpdateType.Prediction;

            DoSimulate(updateType, _inputFrameForPrediction, frameNumber);

            return true;
        }

        int Simulate(int frameNumber)
        {
            SWConsole.Crit($"Engine: Simulate frameNumber={frameNumber}");

            InputFrame lastInputFrame = inputFrames[frameNumber - 1];

            InputFrameDelta lastInputFrameDelta = inputFrameDeltas[frameNumber - 1];

            int playerFrameNumber = lastInputFrameDelta.playerFrameNumber;

            InputFrame inputFrame = inputFrames[frameNumber];

            if(inputFrame == null)
            {
                inputFrame = new InputFrame(frameNumber);
                inputFrames[frameNumber] = inputFrame;
            }

            inputFrame.FrameNumber = frameNumber;
            inputFrame.ResetBytes();

            if(lastInputFrame == null || _input == null || inputFrame == null || lastInputFrameDelta == null)
            {
                SWConsole.Error($"Engine: Simulate input data is nil {lastInputFrame} {_input} {inputFrame} {lastInputFrameDelta}");
            }

            lastInputFrameDelta.Apply(_input, lastInputFrame, inputFrame);

            FrameSyncUpdateType updateType = FrameSyncUpdateType.Normal;

            DoSimulate(updateType, inputFrame, frameNumber);

            return playerFrameNumber;
        }  

        void DoSimulate(FrameSyncUpdateType updateType, InputFrame inputFrame, int frameNumber)
        {
            //Input manager facing frame data
            _currentInputFrame = inputFrame;
            //user facing frame number
            _game.frameNumber = frameNumber;

            //hook for other external systems
            //For example, physics engine
            if (OnEngineWillSimulateEvent != null)
            {
                OnEngineWillSimulateEvent();
            }

            //seed the random number generator
            FrameSyncRandom._internal_seed((UInt32)frameNumber);

            foreach (SWFrameSyncSystem system in _systems)
            {
                system.WillUpdate();
            }

            foreach (SWFrameSyncSystem system in _systems)
            {
                system.Update(_game, _input, updateType);
            }
        }

        void ExportSimulationResult()
        {
            SWSystemDataFrame systemDataFrame = systemDataFrames[_currentInputFrameNumber];
            if (systemDataFrame == null)
            {
                systemDataFrame = new SWSystemDataFrame(_currentInputFrameNumber);
                systemDataFrames[_currentInputFrameNumber] = systemDataFrame;
            }

            systemDataFrame.FrameNumber = _currentInputFrameNumber;
            systemDataFrame.Reset();

            if (OnEngineWillExportEvent != null)
            {
                IRestorable restorable = OnEngineWillExportEvent(_currentInputFrameNumber);
                systemDataFrame.SetUserRestorable(restorable);
            }

            TakeSystemDataSnapshot(systemDataFrame.bytes);
        }

        //system data
        PersistentArray<SWSystemDataFrame> systemDataFrames; 
        void ReloadSystemDataSnapshot(SWBytes buffer)
        {
            foreach (SWFrameSyncSystem system in _systems)
            {
                system.Import(buffer);
            }
        }

        void TakeSystemDataSnapshot(SWBytes buffer)
        {
            foreach (SWFrameSyncSystem system in _systems)
            {
                system.Export(buffer);
            }
        }

        DateTime _timeStamp;
        void ResetTimeStamp()
        {
            _timeStamp = DateTime.Now;
            _timeStamp = _timeStamp.AddHours(-1);
        }

        bool CheckInterval(int milliseconds)
        {
            if (_timeStamp == null)
            {
                _timeStamp = DateTime.Now;
                return true;
            }
            else
            {
                TimeSpan diff = DateTime.Now - _timeStamp;
                _timeStamp = DateTime.Now;
                return diff.TotalMilliseconds > milliseconds;
            }
        }

        //ISWFrameSyncHandler
        int _playerFrameCountOnServer = 0;
        int _firstFrameReceived = 0;

        public void HandleInputFrameInBackground(SWBytes inputFrame, int playerFrameCountOnServer, int roomStep, int playerFrameNumber)
        {
            lock (FRAME_SYNC_LOCK)
            {
                SWConsole.Crit($"<<<======Engine: HandleInputFrameInBackground roomStep={roomStep} playerFrameCountOnServer={playerFrameCountOnServer} playerFrameNumber={playerFrameNumber}");

                if (_game.gameState == FrameSyncGameState.Stopped)
                {
                    SWConsole.Crit($"Engine: HandleInputFrameInBackground game stopped");
                    return;
                }

                _playerFrameCountOnServer = playerFrameCountOnServer;

                if (_lastReceivedInputFrameDeltaNumber == 0)
                {
                    int startIndex = roomStep - 10;
                    if (startIndex < 0)
                    {
                        startIndex = 0;
                    }

                    InitializeFrames(startIndex);
                    _lastReceivedInputFrameDeltaNumber = roomStep;

                    InputFrameDelta firstDelta = new InputFrameDelta(roomStep);
                    firstDelta.playerFrameNumber = playerFrameNumber;
                    byte length = inputFrame.PopByte();
                    SWBytes.Copy(inputFrame, firstDelta.bytes, length);
                    inputFrameDeltas[roomStep] = firstDelta;
                    _currentInputFrameNumber = 0; //will be updated in the waiting for room frame state
                    _currentLocalInputFrameDeltaNumber = 0;
                    SWConsole.Crit($"Engine: HandleInputFrameInBackground startIndex={startIndex}");
                    return;
                }

                InputFrameDelta delta = inputFrameDeltas[roomStep];

                if (delta == null)
                {
                    delta = new InputFrameDelta();
                    inputFrameDeltas[roomStep] = delta;
                }

                if (delta.frameNumber == roomStep)
                {
                    SWConsole.Crit($"HandleInputFrameInBackground already has {roomStep}");
                }
                else
                {
                    delta.frameNumber = roomStep;
                    delta.playerFrameNumber = playerFrameNumber;
                    SWConsole.Crit($"HandleInputFrameInBackground copy roomStep={roomStep}");// bytes={inputFrame.FullString()}");
                    byte length = inputFrame.PopByte();

                    SWBytes.Copy(inputFrame, delta.bytes, length);
                }

                //SWConsole.Crit($"Engine: HandleInputFrameInBackground roomStep={roomStep} _lastReceivedInputFrameDeltaNumber={_lastReceivedInputFrameDeltaNumber}");

                if (roomStep == _lastReceivedInputFrameDeltaNumber + 1)
                {
                    if(_firstFrameReceived == 0)
                    {   //set firstFrameReceived when we have subsequence room steps
                        _firstFrameReceived = _lastReceivedInputFrameDeltaNumber;
                    }

                    _lastReceivedInputFrameDeltaNumber = roomStep;

                    //check if there is any more received frames
                    bool shouldContinue = true;
                    int nextFrameNumber = roomStep + 1;
                    while (shouldContinue)
                    {
                        InputFrameDelta nextDelta = inputFrameDeltas[nextFrameNumber];

                        if (nextDelta == null)
                        {
                            break;
                        }

                        if (nextDelta.frameNumber != nextFrameNumber)
                        {
                            break;
                        }

                        _lastReceivedInputFrameDeltaNumber = nextFrameNumber;

                        nextFrameNumber++;
                    }
                }
            }
        }

        //reload
        List<InputFrameDelta> _initialInputFrameDeltas = new List<InputFrameDelta>();
        int _startFrameNumber = 0;
        int _endFrameNumber = 0;
        SWBytes _initialInputFramesData;

        void PrepareToReceiveInitialInputFrameDeltas()
        {
            _startFrameNumber = 0;
            _endFrameNumber = 0;
        }

        bool HasNewInitialInputFrameDeltas()
        {
            if(_startFrameNumber != 0)
            {
                return true;
            }

            return false;
        }

        //should include startFrame, include endframe
        public void HandleInputFramesInBackground(SWBytes initialInputFramesData, int startFrameNumber, int endFrameNumber)
        {
            lock (FRAME_SYNC_LOCK)
            {
                if (_game.gameState == FrameSyncGameState.Stopped)
                {
                    return;
                }
                SWConsole.Info($"HandleInputFramesInBackground startFrameNumber={startFrameNumber} endFrameNumber={endFrameNumber}");
                _startFrameNumber = startFrameNumber;
                _endFrameNumber = endFrameNumber;
                _initialInputFrameDeltas.Clear();
                _initialInputFramesData = initialInputFramesData;
                for (int i = startFrameNumber; i < endFrameNumber; i++)
                {
                    InputFrameDelta delta = new InputFrameDelta();
                    byte length = initialInputFramesData.PopByte();
                    initialInputFramesData.PopByteBuffer(delta.bytes, 0, length);
                    _initialInputFrameDeltas.Add(delta);
                }

                int expected = endFrameNumber - startFrameNumber;
                int got = _initialInputFrameDeltas.Count;
                //reset read index, we will save the data to disk later
                _initialInputFramesData.SetReadIndex(0);
                if (expected != got)
                {
                    SWConsole.Error($"HandleInputFramesInBackground got={got} expected={expected}");
                }
            }
        }

        //Input Frame
        PersistentArray<InputFrame> inputFrames;
        PersistentArray<InputFrameDelta> inputFrameDeltas;
        PersistentArray<InputFrameDelta> localInputFrameDeltas;

        //
        int _lastReceivedInputFrameDeltaNumber = 0;

        //
        int _currentInputFrameNumber = 0;
        int _currentLocalInputFrameDeltaNumber = 0;
        int _localInputFrameDeltaNumberToSend = 0;

        //
        //int _confirmedInputFrameDeltaNumber = 0;

        //IFrameSyncInputProvider
        InputFrame _currentInputFrame;
        SWBytes _debugInputFrame;
        public SWBytes CurrentInputFrame
        {
            get
            {
                if(_debugInputFrame != null)
                {
                    return _debugInputFrame;
                }

                return _currentInputFrame.bytes;
            }
        }

        //Debug
        public InputFrame GetInputFrame(int frameNumber)
        {
            InputFrame inputFrame = inputFrames[frameNumber];
            return inputFrame;
        }

        public SWSystemDataFrame GetSystemDataFrame(int frameNumber)
        {
            SWSystemDataFrame systemDataFrame = systemDataFrames[frameNumber];
            return systemDataFrame;
        }

        public void SetSystemData(SWBytes buffer)
        {
            ReloadSystemDataSnapshot(buffer);
        }

        public StaticFrameSyncBehaviourManager GetStaticBehaviourManager()
        {
            return _staticFrameSyncBehaviourManager;
        }

        public DynamicFrameSyncBehaviourManager GetDynamicBehaviourManager()
        {
            return _dynamicFrameSyncBehaviourManager;
        }

        public void DebugStep(SWBytes bytes, int frameNumber)
        {
            _debugInputFrame = bytes;

            FrameSyncUpdateType updateType = FrameSyncUpdateType.Normal;

            foreach (SWFrameSyncSystem system in _systems)
            {
                system.WillUpdate();
            }

            foreach (SWFrameSyncSystem system in _systems)
            {
                system.Update(_game, _input, updateType);
            }

            _debugInputFrame = null;
        }

        public void DebugExport(SWBytes bytes)
        {
            foreach (SWFrameSyncSystem system in _systems)
            {
                system.Export(bytes);
            }
        }

    }
}
