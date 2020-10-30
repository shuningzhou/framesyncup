using UnityEngine;
using SWNetwork.Core;
using System.Collections.Generic;
using SWNetwork.Core.DataStructure;
using System;

namespace SWNetwork.FrameSync
{
    public class FrameSyncClient : IFrameSyncPlayerDataProvider
    {
        public static FrameSyncClient Instance = null;
        private SWGameServerClient _client;

        static string _debugName = "SWFrameSyncClient";
        static bool _debugMode = false;
        static int _debugPlayerCount = 0;

        static string _playerUID;
        
        static Action<bool> _clientReadyHandler;

        public static void Init(string playerUID)
        {
            if (Instance != null)
            {
                return;
            }

            _debugMode = false;
            Instance = new FrameSyncClient(playerUID);
            UnityThread.initUnityThread();

            if (UnityEngine.Application.isPlaying)
            {
                GameObject obj = new GameObject("_SWFrameSyncClientBehaviour");
                //obj.hideFlags = HideFlags.HideAndDontSave;
                SWFrameSyncClientBehaviour cb = obj.AddComponent<SWFrameSyncClientBehaviour>();
                cb.SetClient(Instance);
            }
        }

        public static void InitDebugMode(string playerUID, int playerCount)
        {
            if (Instance != null)
            {
                return;
            }

            if(playerCount < 1 || playerCount > 8)
            {
                Debug.LogError($"{_debugName}: invalid player count. (Max=8)");
                return;
            }

            _debugMode = true;
            _debugPlayerCount = playerCount;
            Instance = new FrameSyncClient(playerUID);
            UnityThread.initUnityThread();

            if (UnityEngine.Application.isPlaying)
            {
                GameObject obj = new GameObject("_SWFrameSyncClientBehaviour");
                //obj.hideFlags = HideFlags.HideAndDontSave;
                SWFrameSyncClientBehaviour cb = obj.AddComponent<SWFrameSyncClientBehaviour>();
                cb.SetClient(Instance);
            }
        }

        FrameSyncClient(string playerUID)
        {
            _playerUID = playerUID;
            _playerDataBiMap.Clear();

            //todo: this should be create in the match making phase 
            if (_debugMode)
            {
                for(int i = 1; i <= _debugPlayerCount; i++)
                {
                    _playerDataBiMap.Add($"{i}", (byte)i);
                }
            }
        }

        public IFrameSyncIO frameSyncIO
        {
            get
            {
                if (Instance != null && Instance._client != null)
                {
                    return Instance._client;
                }
                return null;
            }
        }

        public IFrameSyncPlayerDataProvider playerDataProvider
        {
            get
            {
                if (Instance != null && Instance._client != null)
                {
                    return Instance;
                }
                return null;
            }
        }

        public static int ServerPing
        {
            get
            {
                if(Instance != null && Instance._client != null)
                {
                    return Instance._client.Ping;
                }
                return 0;
            }
        }

        public static void Connect(Action<bool> completionHandler)
        {
            if (Instance != null)
            {
                if (_debugMode)
                {
                    Debug.LogError($"{_debugName}: Please specify debug server ip and port");
                    return;
                }

                if(Instance._client != null)
                {
                    Debug.LogError($"{_debugName}: You are already connected. Please call Disconnect() to stop the current connection.");
                    return;
                }

                _clientReadyHandler = completionHandler;

                //todo connect to production game servers
                Instance._client = new SWGameServerClient("127.0.0.1", _playerUID, "roomKey");
                Instance._client.OnGameServerConnectionReadyEvent += OnGameServerConnectionReady;

                
                Instance._client.Connect();
            }
        }

        public static void ConnectDebugServer(string host, Action<bool> completionHandler)
        {
            if (Instance != null)
            {
                if (Instance._client != null)
                {
                    Debug.LogError($"{_debugName}: You are already connected. Please call Disconnect() to stop the current connection.");
                    return;
                }

                _clientReadyHandler = completionHandler;
                Instance._client = new SWGameServerClient(host, _playerUID, "roomKey");
                Instance._client.OnGameServerConnectionReadyEvent += OnGameServerConnectionReady;

                Instance._client.Connect();
            }
        }

        public static void Disconnect()
        {
            if (Instance != null)
            {
                _clientReadyHandler = null;
                Instance._client.OnGameServerConnectionReadyEvent -= OnGameServerConnectionReady;
                Instance._client.Stop();
                Instance._client = null;
            }
        }

        /* Private */
        private static void OnGameServerConnectionReady(bool ready)
        {
            UnityThread.executeInUpdate(() =>
            {   
                if(_clientReadyHandler != null)
                {
                    _clientReadyHandler(ready);
                    _clientReadyHandler = null;
                }
            });
        }

        internal void OnUnityApplicationQuit()
        {
            Disconnect();
            SWConsole.Info($"{_debugName} Application ending after " + Time.time + " seconds");
        }

        //playerDataProvider
        BiMap<string, byte> _playerDataBiMap= new BiMap<string, byte>();

        public byte localPlayerRoomID
        {
            get
            {
                return _client.PlayerRoomID;
            }
        }

        public IEnumerable<byte> playerRoomIDs
        {
            get
            {
                foreach(var item in _playerDataBiMap.values)
                {
                    yield return item;
                }
            }
        }

        public IEnumerable<string> playerUIDs
        {
            get
            {
                foreach (var item in _playerDataBiMap.keys)
                {
                    yield return item;
                }
            }
        }

        public string GetPlayerUID(byte playerRoomID)
        {
            return _playerDataBiMap.GetKey(playerRoomID);
        }

        public byte GetPlayerRoomID(string playerUID)
        {
            return _playerDataBiMap.GetValue(playerUID);
        }

        public T GetUserData<T>()
        {
            //todo
            //should be similar to room customData
            return default(T);
        }
    }
}
