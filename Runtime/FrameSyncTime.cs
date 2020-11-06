using Parallel;
using SWNetwork.Core;
using System;
using UnityEditor;

namespace SWNetwork.FrameSync
{
    public static class FrameSyncTime
    {
        //user facting deltaTime
        public static Fix64 fixedDeltaTime { get; private set; }

        //internal deltaTime
        //this is the deltaTime we want to use for the FrameSyncEngine
        internal static float internalFixedDeltaTime { get; private set; }
        internal static float internalInputSampleInterval { get; private set; }

        static float _adjustInterval;
        static float _adjustTimer;
        static bool _prediction;
        static int _avgCount;
        static float _avgA;
        static float _avgB;

        static float _avgServerPlayerFrameCount;
        static float _previousAvgServerPlayerFrameCount;
        static float _avgLocalServerFrameCount;
        static float _previousAvgLocalServerFrameCount;
        static float _avgPredictionError;

        static float _minDeltaTime;
        static float _maxDeltaTime;

        internal static void Initialize(Fix64 deltaTimeInSeconds, bool prediction, float adjustInterval, int avgCount)
        {
            fixedDeltaTime = deltaTimeInSeconds;
            internalFixedDeltaTime = (float)deltaTimeInSeconds;
            internalInputSampleInterval = (float)deltaTimeInSeconds;

            _prediction = prediction;
            _adjustInterval = adjustInterval;
            _adjustTimer = 0;
            _avgCount = avgCount;

            _maxDeltaTime = (float)deltaTimeInSeconds * (1 + FrameSyncConstant.DYNAMIC_ADJUST_MAX);
            _minDeltaTime = (float)deltaTimeInSeconds * (1 - FrameSyncConstant.DYNAMIC_ADJUST_MAX);

            if (_avgCount < 1)
            {
                _avgA = 0;
                _avgB = 1;
            }
            else
            {
                _avgA = ((float)_avgCount - 1) / (float)_avgCount;
                _avgB = 1 / (float)_avgCount;
            }
        }

        static float _loopTime = 0;
        static float _lastLoopStepTime = 0;
        static float _stepInterval = 0;
        static int _currentLoopStepCount;
        static int _previousRemainingStepCount;

        public static void DoFixedTickIfNecessary(float deltaTime, int serverPlayerFrameCount, Action fixedTickHandler)
        {
            _loopTime += deltaTime;
            float loopTimeElapsed = _loopTime - _lastLoopStepTime;

            int loopStepCount = (int)FrameSyncConstant.OPTIMIZED_SERVER_PREDICTION_PLAYER_FRAME_COUNT - serverPlayerFrameCount + FrameSyncConstant.FIXED_TICKRATE_LOOP_STEP_COUNT + _previousRemainingStepCount;

            if(loopTimeElapsed > _stepInterval)
            {
                _currentLoopStepCount++;
                _lastLoopStepTime = _loopTime;

                fixedTickHandler();
            }
            
            if(_currentLoopStepCount >= loopStepCount)
            {
                loopStepCount = (int)FrameSyncConstant.OPTIMIZED_SERVER_PREDICTION_PLAYER_FRAME_COUNT - serverPlayerFrameCount + FrameSyncConstant.FIXED_TICKRATE_LOOP_STEP_COUNT;
                float newIntervale = FrameSyncConstant.FIXED_TICKRATE_LOOP_INTERVAL / (float)loopStepCount;
                _previousRemainingStepCount = 0;
                ResetFixedTickValues(newIntervale);
            }
            else
            {
                float remainTime = FrameSyncConstant.FIXED_TICKRATE_LOOP_INTERVAL - _loopTime;
                int remainSteps = loopStepCount - _currentLoopStepCount;

                if (remainTime < 0)
                {
                    //call all remaining steps and reset
                    //failed to completed all the steps in the loop
                    //add the remaining step to the next loop
                    _previousRemainingStepCount = remainSteps;
                    loopStepCount = (int)FrameSyncConstant.OPTIMIZED_SERVER_PREDICTION_PLAYER_FRAME_COUNT - serverPlayerFrameCount + FrameSyncConstant.FIXED_TICKRATE_LOOP_STEP_COUNT + _previousRemainingStepCount;
                    float newIntervale = FrameSyncConstant.FIXED_TICKRATE_LOOP_INTERVAL / (float)loopStepCount;
                    ResetFixedTickValues(newIntervale);
                }
                else
                {
                    _stepInterval = remainTime / (int)remainSteps;
                }
            }
        }

        static void ResetFixedTickValues(float stepInterval)
        {
            _currentLoopStepCount = 0;
            _loopTime = 0;
            _lastLoopStepTime = 0;
            _stepInterval = stepInterval;
        }

        public static bool Adjust(int serverPlayerFrameCount, int localServerFrameCount, float deltaTime)
        {
            _adjustTimer += deltaTime;

            if(_adjustTimer > _adjustInterval)
            {
                _adjustTimer = 0;
                //SWConsole.Warn($"======================Adjust=======================");
                //SWConsole.Warn($"Adjust serverPlayerFrameCount={serverPlayerFrameCount} localServerFrameCount={localServerFrameCount}");
                UpdateLocalServerFrameCount(localServerFrameCount);
                //UpdateServerPlayerFrameCount(serverPlayerFrameCount);
                //SWConsole.Warn($"Adjust AVG _avgServerPlayerFrameCount={_avgServerPlayerFrameCount} _avgLocalServerFrameCount={_avgLocalServerFrameCount}");

                internalFixedDeltaTime = Optimize(
                    internalFixedDeltaTime,
                    _avgLocalServerFrameCount,
                    _previousAvgLocalServerFrameCount, 
                    FrameSyncConstant.OPTIMIZED_LOCAL_SERVER_FRAME_COUNT, 
                    _maxDeltaTime, 
                    _minDeltaTime, 
                    0.1f, 
                    0.1f, 
                    FrameSyncConstant.DYNAMIC_ADJUST_SMALL_STEP, 
                    FrameSyncConstant.DYNAMIC_ADJUST_STEP,
                    false);

                _previousAvgLocalServerFrameCount = _avgLocalServerFrameCount;
                //DoAdjustment();

                return true;
            }

            return false;
        }

        public static bool AdjustPrediction(int serverPlayerFrameCount, int localServerFrameCount, float deltaTime)
        {
            _adjustTimer += deltaTime;

            if (_adjustTimer > _adjustInterval)
            {
                _adjustTimer = 0;

                UpdateLocalServerFrameCount(localServerFrameCount);
                UpdateServerPlayerFrameCount(serverPlayerFrameCount);

                SWConsole.Debug($"[internalInputSampleInterval]");
                internalInputSampleInterval = Optimize(
                    internalInputSampleInterval,
                    _avgServerPlayerFrameCount,
                    _previousAvgServerPlayerFrameCount,
                    FrameSyncConstant.OPTIMIZED_SERVER_PREDICTION_PLAYER_FRAME_COUNT,
                    _maxDeltaTime,
                    _minDeltaTime,
                    0.05f,
                    0.2f,
                    FrameSyncConstant.DYNAMIC_ADJUST_SMALL_STEP,
                    FrameSyncConstant.DYNAMIC_ADJUST_STEP,
                    true);

                _previousAvgServerPlayerFrameCount = _avgServerPlayerFrameCount;

                //SWConsole.Debug($"[internalFixedDeltaTime]");
                //internalFixedDeltaTime = Optimize(
                //    internalFixedDeltaTime,
                //    _avgLocalServerFrameCount,
                //    _previousAvgLocalServerFrameCount,
                //    FrameSyncConstant.OPTIMIZED_LOCAL_PREDICTION_FRAME_COUNT,
                //    _maxDeltaTime,
                //    _minDeltaTime,
                //    0.1f,
                //    0.1f,
                //    FrameSyncConstant.DYNAMIC_ADJUST_SMALL_STEP,
                //    FrameSyncConstant.DYNAMIC_ADJUST_STEP,
                //    false);

                //_previousAvgLocalServerFrameCount = _avgLocalServerFrameCount;

                return false;
            }

            return false;
        }

        public static bool Adjust(int predictionError, float deltaTime)
        {
            _adjustTimer += deltaTime;

            if (_adjustTimer > _adjustInterval)
            {
                _adjustTimer = 0;
                SWConsole.Warn($"======================Adjust=======================");
                //SWConsole.Warn($"Adjust serverPlayerFrameCount={serverPlayerFrameCount} localServerFrameCount={localServerFrameCount}");
                UpdatePredictionError(predictionError);
                //SWConsole.Warn($"Adjust AVG _avgServerPlayerFrameCount={_avgServerPlayerFrameCount} _avgLocalServerFrameCount={_avgLocalServerFrameCount}");
                DoAdjustmentForPrediction();

                return true;
            }

            return false;
        }

        public static void UpdateServerPlayerFrameCount(int serverPlayerFrameCount)
        {
            if(_avgServerPlayerFrameCount == 0)
            {
                _avgServerPlayerFrameCount = serverPlayerFrameCount;
            }
            _avgServerPlayerFrameCount = _avgServerPlayerFrameCount * _avgA + (float)serverPlayerFrameCount * _avgB;
        }

        public static void UpdateLocalServerFrameCount(int localServerFrameCount)
        {
            if (_avgLocalServerFrameCount == 0)
            {
                _avgLocalServerFrameCount = localServerFrameCount;
                _previousAvgLocalServerFrameCount = localServerFrameCount;
            }

            _avgLocalServerFrameCount = _avgLocalServerFrameCount * _avgA + (float)localServerFrameCount * _avgB;
        }

        public static void UpdatePredictionError(int predictionError)
        {
            if (_avgPredictionError == 0)
            {
                _avgPredictionError = predictionError;
            }

            _avgPredictionError = _avgPredictionError * _avgA + (float)predictionError * _avgB;
        }

        static void DoAdjustmentForPrediction()
        {
            //if client prediction is enabled, input is sampled in fixed updated
            //so we only adjust fixed update delta time

            //error = actual server frame number - predicted server frame number
            if (_avgPredictionError > 1)
            {
                //predicted is less than actual
                //local should run faster so local can predicter a larger frame numbers
                internalFixedDeltaTime = internalFixedDeltaTime * FrameSyncConstant.DYNAMIC_ADJUST_STEP;
                if (internalFixedDeltaTime < _minDeltaTime)
                {
                    internalFixedDeltaTime = _minDeltaTime;
                }

                SWConsole.Warn($"Adjust FASTER internalFixedDeltaTime={internalFixedDeltaTime}");
            }
            else if (_avgPredictionError < -1)
            {
                //predicted is greater than actual
                //local should run slower so local can predict smaller frame numbers
                internalFixedDeltaTime = internalFixedDeltaTime / FrameSyncConstant.DYNAMIC_ADJUST_STEP;
                if (internalFixedDeltaTime > _maxDeltaTime)
                {
                    internalFixedDeltaTime = _maxDeltaTime;
                }

                SWConsole.Warn($"Adjust SLOWER internalFixedDeltaTime={internalFixedDeltaTime}");
            }
        }

        enum OptimizerState
        {
            Constant,
            Decreasing,
            Increasing,
        }

        enum OptimizerCountState
        {
            Optimized,
            NotEnough,
            TooMany,
        }

        static void DoAdjustment()
        {

            //if(_avgServerPlayerFrameCount > FrameSyncConstant.EXPECTED_SERVER_PLAYER_FRAME_COUNT_MAX)
            //{
            //    //input sampling is running faster than server, server queued more player frames than expected
            //    //make input sample interval longer so less player frames are generated
            //    internalInputSampleInterval = internalInputSampleInterval / FrameSyncConstant.DYNAMIC_ADJUST_STEP;
            //    if(internalInputSampleInterval > _maxDeltaTime)
            //    {
            //        internalInputSampleInterval = _maxDeltaTime;
            //    }
            //    //SWConsole.Warn($"DoAdjustment Input SLOWER internalInputSampleInterval={internalInputSampleInterval}");
            //}
            //else if(_avgServerPlayerFrameCount < FrameSyncConstant.EXPECTED_SERVER_PLAYER_FRAME_COUNT_MIN)
            //{
            //    //maybe local is running too slow
            //    //make input sample interval slightly shorter
            //    internalInputSampleInterval = internalInputSampleInterval * FrameSyncConstant.DYNAMIC_ADJUST_SMALL_STEP;
            //    if (internalInputSampleInterval < _minDeltaTime)
            //    {
            //        internalInputSampleInterval = _minDeltaTime;
            //    }
            //    //SWConsole.Warn($"DoAdjustment Input FASTER internalInputSampleInterval={internalInputSampleInterval}");
            //}

            //float localServerFrameCountDelta = _previousAvgLocalServerFrameCount - _avgLocalServerFrameCount;
            //SWConsole.Debug($"=========================ADJUST=================================");
            //SWConsole.Debug($"_previousAvgLocalServerFrameCount={_previousAvgLocalServerFrameCount}");
            //SWConsole.Debug($"_avgLocalServerFrameCount={_avgLocalServerFrameCount}");
            //SWConsole.Debug($"localServerFrameCountDelta={localServerFrameCountDelta}");
            //ServerFrameBufferState bufferState = OptimizerState.Constant;

            //if(localServerFrameCountDelta > 0.1f)
            //{
            //    bufferState = ServerFrameBufferState.Decreasing;
            //}
            //else if (localServerFrameCountDelta < -0.1f)
            //{
            //    bufferState = ServerFrameBufferState.Increasing;
            //}

            //float localServerFrameCountDeltaToOptimized = FrameSyncConstant.OPTIMIZED_LOCAL_SERVER_FRAME_COUNT - _avgLocalServerFrameCount;

            //ServerFrameBufferOptimizedState optimizedState = ServerFrameBufferOptimizedState.Optimized;

            //if(localServerFrameCountDeltaToOptimized > 0.1f)
            //{
            //    optimizedState = ServerFrameBufferOptimizedState.NotEnough;
            //}
            //else if (localServerFrameCountDeltaToOptimized < -0.1f)
            //{
            //    optimizedState = ServerFrameBufferOptimizedState.TooMany;
            //}

            //bool shouldSlowDown = (optimizedState == ServerFrameBufferOptimizedState.NotEnough && bufferState == ServerFrameBufferState.Decreasing);
            //shouldSlowDown |= (optimizedState == ServerFrameBufferOptimizedState.Optimized && bufferState == ServerFrameBufferState.Decreasing);


            //bool shouldSpeedUp = (optimizedState == ServerFrameBufferOptimizedState.TooMany && bufferState == ServerFrameBufferState.Increasing);
            //shouldSpeedUp |= (optimizedState == ServerFrameBufferOptimizedState.Optimized && bufferState == ServerFrameBufferState.Increasing);

            //bool shouldSlowDownSmall = (bufferState == ServerFrameBufferState.Constant && optimizedState == ServerFrameBufferOptimizedState.NotEnough);
            //bool shouldSpeedUpSmall = (bufferState == ServerFrameBufferState.Constant && optimizedState == ServerFrameBufferOptimizedState.TooMany);

            //SWConsole.Debug($"optimizedState={optimizedState}");
            //SWConsole.Debug($"bufferState={bufferState}");
            //if (shouldSpeedUp)
            //{
            //    internalFixedDeltaTime = internalFixedDeltaTime * FrameSyncConstant.DYNAMIC_ADJUST_STEP;
            //    if (internalFixedDeltaTime < _minDeltaTime)
            //    {
            //        internalFixedDeltaTime = _minDeltaTime;
            //    }
            //    SWConsole.Debug($"shouldSpeedUp");
            //}
            //else if (shouldSpeedUpSmall)
            //{
            //    internalFixedDeltaTime = internalFixedDeltaTime * FrameSyncConstant.DYNAMIC_ADJUST_SMALL_STEP;
            //    if (internalFixedDeltaTime < _minDeltaTime)
            //    {
            //        internalFixedDeltaTime = _minDeltaTime;
            //    }
            //    SWConsole.Debug($"shouldSpeedUpSmall");
            //}
            //else if(shouldSlowDown)
            //{
            //    internalFixedDeltaTime = internalFixedDeltaTime / FrameSyncConstant.DYNAMIC_ADJUST_STEP;
            //    if (internalFixedDeltaTime > _maxDeltaTime)
            //    {
            //        internalFixedDeltaTime = _maxDeltaTime;
            //    }
            //    SWConsole.Debug($"shouldSlowDown");
            //}
            //else if(shouldSlowDownSmall)
            //{
            //    internalFixedDeltaTime = internalFixedDeltaTime / FrameSyncConstant.DYNAMIC_ADJUST_SMALL_STEP;
            //    if (internalFixedDeltaTime > _maxDeltaTime)
            //    {
            //        internalFixedDeltaTime = _maxDeltaTime;
            //    }
            //    SWConsole.Debug($"shouldSlowDownSmall");
            //}

            //_previousAvgLocalServerFrameCount = _avgLocalServerFrameCount;
            //SWConsole.Debug($"internalFixedDeltaTime={internalFixedDeltaTime}");
        }

        static float Optimize(
            float currentTime,
            float value, 
            float previousValue, 
            float optimizedValue,
            float maxValue,
            float minValue,
            float optimizerDelta,
            float countDelta,
            float smallStep,
            float largeStep,
            bool reversed)
        {
            float delta = previousValue - value;
            SWConsole.Debug($"=========================Optimize=================================");
            SWConsole.Debug($"previousValue={previousValue}");
            SWConsole.Debug($"value={value}");
            SWConsole.Debug($"delta={delta}");
            OptimizerState optimizerState = OptimizerState.Constant;

            if (delta > optimizerDelta)
            {
                optimizerState = OptimizerState.Decreasing;
            }
            else if (delta < -optimizerDelta)
            {
                optimizerState = OptimizerState.Increasing;
            }

            float deltaToOptimized = optimizedValue - value;

            OptimizerCountState countState = OptimizerCountState.Optimized;

            if (deltaToOptimized > countDelta)
            {
                countState = OptimizerCountState.NotEnough;
            }
            else if (deltaToOptimized < -countDelta)
            {
                countState = OptimizerCountState.TooMany;
            }

            bool shouldSlowDown = false;
            bool shouldSlowDownSmall = false;
            bool shouldSpeedUpSmall = false;
            bool shouldSpeedUp = false;

            if(reversed)
            {
                shouldSpeedUp = (countState == OptimizerCountState.NotEnough && optimizerState == OptimizerState.Decreasing);
                shouldSpeedUp |= (countState == OptimizerCountState.Optimized && optimizerState == OptimizerState.Decreasing);

                shouldSlowDown = (countState == OptimizerCountState.TooMany && optimizerState == OptimizerState.Increasing);
                shouldSlowDown |= (countState == OptimizerCountState.Optimized && optimizerState == OptimizerState.Increasing);

                shouldSpeedUpSmall = (optimizerState == OptimizerState.Constant && countState == OptimizerCountState.NotEnough);
                shouldSlowDownSmall = (optimizerState == OptimizerState.Constant && countState == OptimizerCountState.TooMany);
            }
            else
            {
                shouldSlowDown = (countState == OptimizerCountState.NotEnough && optimizerState == OptimizerState.Decreasing);
                shouldSlowDown |= (countState == OptimizerCountState.Optimized && optimizerState == OptimizerState.Decreasing);

                shouldSpeedUp = (countState == OptimizerCountState.TooMany && optimizerState == OptimizerState.Increasing);
                shouldSpeedUp |= (countState == OptimizerCountState.Optimized && optimizerState == OptimizerState.Increasing);

                shouldSlowDownSmall = (optimizerState == OptimizerState.Constant && countState == OptimizerCountState.NotEnough);
                shouldSpeedUpSmall = (optimizerState == OptimizerState.Constant && countState == OptimizerCountState.TooMany);
            }

            SWConsole.Debug($"optimizerState={optimizerState}");
            SWConsole.Debug($"countState={countState}");

            float result = currentTime;

            if (shouldSpeedUp)
            {
                result = currentTime * largeStep;
                if (result < minValue)
                {
                    result = minValue;
                }
                SWConsole.Debug($"shouldSpeedUp");
            }
            else if (shouldSpeedUpSmall)
            {
                result = currentTime * smallStep;
                if (result < minValue)
                {
                    result = minValue;
                }
                SWConsole.Debug($"shouldSpeedUpSmall");
            }
            else if (shouldSlowDown)
            {
                result = currentTime / largeStep;
                if (result > maxValue)
                {
                    result = maxValue;
                }
                SWConsole.Debug($"shouldSlowDown");
            }
            else if (shouldSlowDownSmall)
            {
                result = currentTime / smallStep;
                if (result > maxValue)
                {
                    result = maxValue;
                }
                SWConsole.Debug($"shouldSlowDownSmall");
            }

            SWConsole.Debug($"result={result}");

            return result;
        }
    }
}