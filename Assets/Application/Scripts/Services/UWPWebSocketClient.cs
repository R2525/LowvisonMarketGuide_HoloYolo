// #if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
// using System;
// using System.Text;
// using System.Threading.Tasks;
// using Windows.Networking.Sockets;
// using Windows.Storage.Streams;
// using LowVison.Networking;
// using System.Collections.Concurrent;
// using System.Threading;



// #endif

// using UnityEngine;


// public class UWPWebSocketClient
// {
// #if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
//     private MessageWebSocket webSocket;
//     private DataWriter messageWriter;
//     private Action<string> onMessage;
//     // public enum SocketState { Closed, Open };
//     private ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();
//     private bool isListening = false;

//     public SocketState State { get; private set; } = SocketState.Closed;
//     private string lastConnectedUri;
//     private Action<string> lastMessageCallback;




//     public async Task Connect(string uri, Action<string> onMessageCallback = null)
//     {
//         try
//         {
//             lastConnectedUri = uri;
//             lastMessageCallback = onMessageCallback;    
//             onMessage = onMessageCallback;

//             webSocket = new MessageWebSocket();
//             webSocket.Control.MessageType = SocketMessageType.Utf8;

//             webSocket.MessageReceived += WebSocket_MessageReceived;
//             webSocket.Closed += WebSocket_Closed;

//             await webSocket.ConnectAsync(new Uri(uri)).AsTask().ConfigureAwait(false);
//             messageWriter = new DataWriter(webSocket.OutputStream);

//             this.lastConnectedUri = uri;

//             State = SocketState.Open;
//             isListening = true;
//             Debug.Log("✅ WebSocket 연결됨: " + uri);
//         }
//         catch (Exception ex)
//         {
//             Debug.LogError("❌ WebSocket 연결 실패: " + ex.Message);
//         }

//     }

//     private async void WebSocket_Closed(IWebSocket sender, WebSocketClosedEventArgs args)
//     {
//         State = SocketState.Closed;
//         Debug.Log("🛑 WebSocket 연결 종료");

//         if (webSocket != null)
//         {
//             webSocket.Dispose();
//             webSocket = null;
//         }

//         // 자동 재연결 시도 (최대 5회)
//         for (int i = 1; i <= 3; i++)
//         {
//             Debug.Log($"🔄 WebSocket 재연결 시도 {i}/3...");
//             await Task.Delay(100 * i);  // 점진적 지연

//             if (!string.IsNullOrEmpty(lastConnectedUri))
//             {
//                 await Connect(lastConnectedUri, onMessage);
//                 if (State == SocketState.Open)
//                 {
//                     Debug.Log("✅ WebSocket 재연결 성공");
//                     break;
//                 }
//             }
//         }

//         if (State == SocketState.Closed)
//             Debug.LogError("❌ WebSocket 재연결 실패 - 수동 개입 필요");
//     }

//     private void WebSocket_MessageReceived(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
//     {
//         using (DataReader reader = args.GetDataReader())
//         {
//             reader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
//             string message = reader.ReadString(reader.UnconsumedBufferLength);
//             Debug.Log("📨 수신 메시지: " + message);
//             // onMessage?.Invoke(message);
//             messageQueue.Enqueue(message);
//         }
//     }

//     public async Task ListenMessagesLoop(Func<string, Task> onMessage, CancellationToken cancellationToken = default)
//     {
//         while (!cancellationToken.IsCancellationRequested)
//         {
//             if (messageQueue.TryDequeue(out var msg))
//             {
//                 await onMessage(msg);
//             }
//             else
//             {
//                 await Task.Delay(50, cancellationToken);
//             }
//         }
//     }

//     public async Task SendText(string message)
//     {
//         if (webSocket == null || State != SocketState.Open)
//         {
//             Debug.LogWarning("⚠ WebSocket 연결 안됨");
//             return;
//         }

//         try
//         {
//             messageWriter.WriteString(message);
//             await messageWriter.StoreAsync();
//             Debug.Log("📤 텍스트 전송: " + message);
//         }
//         catch (Exception ex)
//         {
//             Debug.LogError("❌ 텍스트 전송 실패: " + ex.Message);
//         }
//     }

//     public async Task<bool> SendBinary(byte[] data)
//     {
//         Debug.Log("b0");
//     #if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
//         if (webSocket == null || State != SocketState.Open)
//         {
//             Debug.Log("b1");
//             Debug.LogWarning("⚠ WebSocket 연결이 유효하지 않음. 전송 생략");
//             return false;
//         }

//         int maxRetry = 3;
//         for (int attempt = 1; attempt <= maxRetry; attempt++)
//         {
//             try
//             {
//                 Debug.Log("b2");

//                 // Check if output stream is still valid before creating writer
//                 if (webSocket.OutputStream == null)
//                 {
//                     Debug.LogWarning("⚠ WebSocket OutputStream이 null입니다. 재연결 시도...");
//                     await ReconnectAsync();
//                     continue;
//                 }

//                 DataWriter writer = null;
//                 try
//                 {
//                     // writer = new DataWriter(webSocket.OutputStream);
//                     Debug.Log("b2_1");    
//                     Debug.Log($"데이터 크기: {data.Length} bytes");

//                     writer.WriteBytes(data);
//                     Debug.Log("b2_1_1");    

//                     // Remove .AsTask() as it might be causing issues
//                     await writer.StoreAsync();
//                     Debug.Log("b2_1_2");

//                     await writer.FlushAsync();
//                     Debug.Log("b2_2");
//                 }
//                 finally
//                 {
//                     // Properly dispose writer
//                     writer?.Dispose();
//                 }

//                 Debug.Log($"📤 바이너리 전송 완료 ({data.Length} bytes)");
//                 Debug.Log("b3");
//                 return true;
//             }
//             catch (Exception ex)
//             {
//                 Debug.Log("b4");
//                 Debug.LogError(
//                 $"❌ 바이너리 전송 실패 (시도 {attempt}/{maxRetry})\n" +
//                 $"- 예외 타입: {ex.GetType().Name}\n" +
//                 $"- 메시지: {ex.Message}\n" +
//                 $"- StackTrace:\n{ex.StackTrace}"
//                 );  

//                 if (attempt < maxRetry)
//                 {
//                     Debug.Log($"🔄 WebSocket 재연결 시도 {attempt}/{maxRetry}...");
//                     await ReconnectAsync();
//                     await Task.Delay(500 * attempt); // Exponential backoff
//                 }
//             }
//         }

//         return false;
//     #else
//         return false;
//     #endif
//     }

//     // 재연결 메서드
//     // 이 메서드는 연결이 끊어진 후 자동으로 호출되어 재연결
//     public async Task ReconnectAsync()
//     {
//         try
//         {
//             Debug.Log("🔁 WebSocket 재연결 시도 중...");
//             Close();

//             await Task.Delay(500);  // 잠시 대기 후 재연결

//             if (!string.IsNullOrEmpty(lastConnectedUri))
//             {
//                 await Connect(lastConnectedUri, lastMessageCallback); // 마지막 연결 정보 재사용
//             }
//         }
//         catch (Exception ex)
//         {
//             Debug.LogError($"❌ WebSocket 재연결 실패: {ex.Message}");
//         }
//     }



//     public async void Close()
//     {
//         if (webSocket != null)
//         {
//             try
//             {
//                 webSocket.Close(1000, "Client close");
//                 isListening = false;
//                 webSocket?.Dispose();
//                 webSocket = null;
//             }
//             catch (Exception ex)
//             {
//                 Debug.LogError("❌ WebSocket 닫기 실패: " + ex.Message);
//             }
//         }

//         State = SocketState.Closed;
//         lastConnectedUri = null;
//     }


// #endif
// }

// using 구문들은 파일 최상단에 있어야 합니다.
using System;
using System.Threading;
using System.Threading.Tasks;

// UWP 전용 using 구문들은 #if 블록 안에 둡니다.
#if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using System.Collections.Concurrent;
#endif

using UnityEngine;
using LowVison.Networking; // SocketState 열거형을 사용하기 위해 추가

namespace LowVison.Networking
{
    // 네임스페이스 안으로 이동하여 코드 구조를 정리합니다.
    public enum SocketState { Closed, Connecting, Open }
    
    public class UWPWebSocketClient
    {
    #if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
        private MessageWebSocket webSocket;
        private string lastConnectedUri;
        private ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();
        
        public SocketState State { get; private set; } = SocketState.Closed;

        // 재연결 로직을 위한 상태 변수들
        private int reconnectAttempts = 0;
        private bool isReconnecting = false;
        private bool intentionalClose = false;
        private const int maxReconnectDelaySeconds = 30;

        public async Task Connect(string uri)
        {
            if (State == SocketState.Open || State == SocketState.Connecting) return;

            State = SocketState.Connecting;
            lastConnectedUri = uri;
            intentionalClose = false;

            try
            {
                // 재연결 시 기존 객체가 남아있을 수 있으므로 확실히 정리
                webSocket?.Dispose();
                
                webSocket = new MessageWebSocket();
                webSocket.Control.MessageType = SocketMessageType.Utf8;
                webSocket.MessageReceived += OnMessageReceived;
                webSocket.Closed += OnWebSocketClosed;

                await webSocket.ConnectAsync(new Uri(uri));

                State = SocketState.Open;
                isReconnecting = false; 
                reconnectAttempts = 0;
                Debug.Log($"✅ WebSocket 연결 성공: {uri}");
            }
            catch (Exception ex)
            {
                var socketErrorStatus = SocketError.GetStatus(ex.HResult);
                string detailedError = $"[SocketError: {socketErrorStatus}] {ex.Message}";
                
                State = SocketState.Closed;
                webSocket?.Dispose();
                webSocket = null;
                Debug.LogError($"❌ WebSocket 연결 실패: {detailedError}");
                // 연결 실패 시 OnWebSocketClosed가 자동으로 호출되어 재연결 로직이 시작됨
            }
        }

        private void OnMessageReceived(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            try
            {
                using (DataReader reader = args.GetDataReader())
                {
                    reader.UnicodeEncoding = UnicodeEncoding.Utf8;
                    string message = reader.ReadString(reader.UnconsumedBufferLength);
                    messageQueue.Enqueue(message);
                }
            }
            catch (Exception ex) { Debug.LogError($"❌ 메시지 수신 오류: {ex.Message}"); }
        }
        
        public async Task ListenMessagesLoop(Func<string, Task> onMessage, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (messageQueue.TryDequeue(out var msg))
                {
                    await onMessage(msg);
                }
                else
                {
                    await Task.Delay(50, cancellationToken);
                }
            }
        }

        public async Task SendText(string message)
        {
            if (State != SocketState.Open) return;
            try
            {
                using (var writer = new DataWriter(webSocket.OutputStream))
                {
                    writer.WriteString(message);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                }
            }
            catch (Exception ex) { Debug.LogError($"❌ 텍스트 전송 실패: {ex.Message}"); }
        }
        
        // 이전 코드의 바이너리 전송 오류를 수정한 버전
        public async Task<bool> SendBinary(byte[] data)
        {
            if (State != SocketState.Open) return false;
            try
            {
                using (var writer = new DataWriter(webSocket.OutputStream))
                {
                    writer.WriteBytes(data);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 바이너리 전송 실패: {ex.Message}");
                return false;
            }
        }

        private void OnWebSocketClosed(IWebSocket sender, WebSocketClosedEventArgs args)
        {
            State = SocketState.Closed;
            if (intentionalClose || isReconnecting) return;
            
            Debug.LogWarning($"🛑 WebSocket 연결 끊김 (Code: {args.Code}) - 재연결을 시작합니다.");
            _ = TryReconnectAsync();
        }
        
        private async Task TryReconnectAsync()
        {
            isReconnecting = true;
            
            int delaySeconds = (int)Math.Pow(2, reconnectAttempts);
            int delay = Math.Min(delaySeconds, maxReconnectDelaySeconds) * 1000;
            delay += UnityEngine.Random.Range(500, 1500);

            reconnectAttempts++;
            
            Debug.Log($"🔄 WebSocket 재연결 시도 ({reconnectAttempts}회차)... {delay / 1000.0f}초 후");

            await Task.Delay(delay);

            isReconnecting = false;
            if (!string.IsNullOrEmpty(lastConnectedUri) && !intentionalClose)
            {
                await Connect(lastConnectedUri);
            }
        }

        public void Close()
        {
            intentionalClose = true;
            State = SocketState.Closed;
            webSocket?.Close(1000, "Client requested close.");
            webSocket?.Dispose();
            webSocket = null;
        }

    #else
        // UWP 환경이 아닐 경우를 위한 빈 클래스
        public SocketState State { get; private set; } = SocketState.Closed;
        public async Task Connect(string uri) { await Task.CompletedTask; }
        public async Task ListenMessagesLoop(Func<string, Task> onMessage, CancellationToken cancellationToken) { await Task.CompletedTask; }
        public async Task SendText(string message) { await Task.CompletedTask; }
        public async Task<bool> SendBinary(byte[] data) { return await Task.FromResult(false); }
        public void Close() { }
    #endif
    }
}