using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using UnityEngine;

#if !UNITY_EDITOR && UNITY_WSA
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using System.Threading.Tasks;
#else
using System.Net.Sockets;
using System.Threading;
using System.IO;
#endif

public class TcpNetworkClient : MonoBehaviour
{
    public static TcpNetworkClient Instance { get; private set; }

    [Header("Network Settings")]
    [Tooltip("IP Address of the Flutter Server")]
    public string serverIp = "127.0.0.1";
    [Tooltip("Port of the Flutter Server")]
    public int serverPort = 6000;
    public bool autoConnect = true;

    [Header("Status")]
    public bool isConnected = false;
    public string lastError = "";

    [Header("Reconnect")]
    public float reconnectInterval = 3.0f;
    private float _retryTimer = 0f;
    private bool _isConnecting = false;

    private ConcurrentQueue<string> _sendQueue = new ConcurrentQueue<string>();
    private bool _isSenderRunning = false;

#if !UNITY_EDITOR && UNITY_WSA
    private StreamSocket _socket;
    private DataWriter _writer;
#else
    private TcpClient _client;
    private NetworkStream _stream;
    private Thread _clientThread;
#endif

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    private void Start()
    {
        if (autoConnect) Connect();
    }

    private void Update()
    {
        // Auto Reconnect Logic
        if (autoConnect && !isConnected && !_isConnecting)
        {
            _retryTimer += Time.deltaTime;
            if (_retryTimer >= reconnectInterval)
            {
                _retryTimer = 0f;
                Connect();
            }
        }
    }

    private void OnDestroy()
    {
        Disconnect();
    }

    public void Connect()
    {
        if (isConnected || _isConnecting) return;
        
        _isConnecting = true;
        Debug.Log($"[TCP] Checking Connection... Connecting to {serverIp}:{serverPort}...");

#if !UNITY_EDITOR && UNITY_WSA
        ConnectUWP();
#else
        ConnectEditor();
#endif
    }

    public void Disconnect()
    {
        isConnected = false;
        _isSenderRunning = false;
        _isConnecting = false;

        Debug.Log("[TCP] Disconnecting...");

#if !UNITY_EDITOR && UNITY_WSA
        try
        {
            lock (_uwpLock)
            {
                if (_writer != null) { _writer.DetachStream(); _writer.Dispose(); _writer = null; }
                if (_socket != null) { _socket.Dispose(); _socket = null; }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[TCP] Error during UWP disconnect: {e.Message}");
        }
#else
        try 
        {
            if (_clientThread != null && _clientThread.IsAlive) _clientThread.Abort(); 
        } 
        catch {} // Ignore thread abort errors
        
        try
        {
            if (_stream != null) { _stream.Close(); _stream = null; }
            if (_client != null) { _client.Close(); _client = null; }
        }
        catch (Exception e)
        {
            Debug.LogError($"[TCP] Error during Editor disconnect: {e.Message}");
        }
#endif
        Debug.Log("[TCP] Disconnected");
    }

    /// <summary>
    /// Queues a message to be sent. Auto-appends newline if missing.
    /// </summary>
    public void Send(string message)
    {
        if (!isConnected) return;
        if (!message.EndsWith("\n")) message += "\n";
        _sendQueue.Enqueue(message);
    }

    #region Editor Implementation
#if UNITY_EDITOR || !UNITY_WSA
    private void ConnectEditor()
    {
        _clientThread = new Thread(() =>
        {
            try
            {
                _client = new TcpClient();
                var result = _client.BeginConnect(serverIp, serverPort, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3));

                if (!success)
                {
                    throw new Exception("Connection timed out");
                }

                _client.EndConnect(result);
                _stream = _client.GetStream();
                isConnected = true;
                _isSenderRunning = true;
                _isConnecting = false; // Finished connecting
                
                Debug.Log("[TCP] Connected (Editor)");

                // Sender Loop
                while (isConnected && _client != null && _client.Connected)
                {
                    if (_sendQueue.TryDequeue(out string msg))
                    {
                        byte[] data = Encoding.UTF8.GetBytes(msg);
                        _stream.Write(data, 0, data.Length);
                        _stream.Flush();
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }
                }
            }
            catch (Exception e)
            {
                lastError = e.Message;
                Debug.LogError($"[TCP] Error: {e.Message}");
                isConnected = false;
                _isConnecting = false; // Reset connecting state on failure
            }
        });
        _clientThread.IsBackground = true;
        _clientThread.Start();
    }
#endif
    #endregion

    #region UWP Implementation
#if !UNITY_EDITOR && UNITY_WSA
    private object _uwpLock = new object();

    private async void ConnectUWP()
    {
        try
        {
            _socket = new StreamSocket();
            var host = new Windows.Networking.HostName(serverIp);
            await _socket.ConnectAsync(host, serverPort.ToString());

            lock (_uwpLock)
            {
                _writer = new DataWriter(_socket.OutputStream);
            }
            
            isConnected = true;
            _isSenderRunning = true;
            _isConnecting = false;

            Debug.Log("[TCP] Connected (UWP)");

            // Start Sender Task
            Task.Run(SenderLoopUWP);
        }
        catch (Exception e)
        {
            lastError = e.Message;
            Debug.LogError($"[TCP] UWP Error: {e.Message}");
            isConnected = false;
            _isConnecting = false;
        }
    }

    private async Task SenderLoopUWP()
    {
        while (isConnected && _isSenderRunning)
        {
            if (_sendQueue.TryDequeue(out string msg))
            {
                // We need to be careful not to hold the lock during the async await if possible, 
                // but DataWriter methods are async.
                // However, we MUST NOT dispose while writing.
                
                // Strategy: Check existence under lock, then perform operation? 
                // No, Dispose removes the underlying stream. 
                // We can catch the ObjectDisposedException, but a raw crash (Access Violation) 
                // implies race at native level.
                
                // Better strategy: Simple flag + try-catch inside the loop might not be enough.
                // Let's use flexible locking or just check nulls aggressively.
                // AND IMPORTANTLY: Debug.Log on background thread in UWP can sometimes crash Unity. 
                // Let's remove Debug.Log from the tight loop or minimize it.
                
                try
                {
                    // Accessing _writer must be safe
                    DataWriter localWriter = null;
                    lock(_uwpLock)
                    {
                        localWriter = _writer;
                    }

                    if (localWriter != null)
                    {
                        localWriter.WriteString(msg);
                        await localWriter.StoreAsync(); 
                        await localWriter.FlushAsync();
                    }
                }
                catch (Exception e)
                {
                    // Debug.LogError($"[TCP] Send Error: {e.Message}"); // Risky on background thread?
                    Disconnect(); 
                    break;
                }
            }
            else
            {
                await Task.Delay(10);
            }
        }
    }
#endif
    #endregion
}
