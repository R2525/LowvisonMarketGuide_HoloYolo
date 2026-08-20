using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
#if WINDOWS_UWP
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
#endif
public class FlutterServerManager : MonoBehaviour
{
    [Tooltip("Port to listen on")]
    public int port = 8000;
    [Tooltip("Display connection status/messages in this TextMesh if assigned (Optional)")]
    public TextMesh debugTextMesh; 
    private ConcurrentQueue<string> _mainThreadQueue = new ConcurrentQueue<string>();
    #if WINDOWS_UWP
    private StreamSocketListener _listener;
    #else
    private TcpListener _legacyListener;
    private TcpClient _legacyClient;
    private NetworkStream _legacyStream;
    private bool _isRunning = false;
    #endif
    void Start()
    {
        StartServer();
    }
    void Update()
    {
        // Process messages on the main thread (e.g., for UI updates)
        while (_mainThreadQueue.TryDequeue(out string message))
        {
            Debug.Log($"[TCP] {message}");
            if (debugTextMesh != null)
            {
                debugTextMesh.text = message;
            }
        }
    }
    private void StartServer()
    {
        Log("Starting TCP Server on port " + port + "...");
        #if WINDOWS_UWP
        StartUwpServer();
        #else
        StartEditorServer();
        #endif
    }
    #if WINDOWS_UWP
    private async void StartUwpServer()
    {
        try
        {
            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnectionReceived;
            await _listener.BindServiceNameAsync(port.ToString());
            Log("UWP Server Listening on port " + port);
        }
        catch (Exception e)
        {
            Log("Error starting UWP server: " + e.Message);
        }
    }
    private async void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        Log("Client connected: " + args.Socket.Information.RemoteAddress.DisplayName);
        try
        {
            using (var reader = new DataReader(args.Socket.InputStream))
            {
                reader.InputStreamOptions = InputStreamOptions.Partial;
                while (true)
                {
                    uint size = await reader.LoadAsync(1024);
                    if (size == 0)
                    {
                        Log("Client disconnected.");
                        break;
                    }
                    string data = reader.ReadString(size);
                    Log("Received: " + data);
                    
                    // Respond?
                    // await SendMessageUwp(args.Socket, "Ack: " + data);
                }
            }
        }
        catch (Exception e)
        {
            Log("Connection error: " + e.Message);
        }
    }
    #else
    private void StartEditorServer()
    {
        // Fallback for Unity Editor testing
        _isRunning = true;
        System.Threading.Thread t = new System.Threading.Thread(() => 
        {
            try
            {
                _legacyListener = new TcpListener(IPAddress.Any, port);
                _legacyListener.Start();
                Log("Editor Server Listening on port " + port);
                while (_isRunning)
                {
                    TcpClient client = _legacyListener.AcceptTcpClient();
                    Log("Client connected.");
                    _legacyClient = client;
                    
                    using (NetworkStream stream = client.GetStream())
                    {
                        _legacyStream = stream;
                        byte[] buffer = new byte[1024];
                        int bytesRead;
                        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            string data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                            Log("Received: " + data);
                        }
                    }
                    client.Close();
                    Log("Client disconnected.");
                }
            }
            catch (Exception e)
            {
                Log("Server loop error: " + e.Message);
            }
        });
        t.IsBackground = true;
        t.Start();
    }
    #endif
    private void Log(string msg)
    {
        _mainThreadQueue.Enqueue(msg);
    }
    private void OnDestroy()
    {
        #if WINDOWS_UWP
        if (_listener != null)
        {
            _listener.Dispose();
            _listener = null;
        }
        #else
        _isRunning = false;
        if (_legacyListener != null) _legacyListener.Stop();
        if (_legacyClient != null) _legacyClient.Close();
        #endif
    }
}