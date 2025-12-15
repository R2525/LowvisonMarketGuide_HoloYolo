
using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using YoloHolo.YoloLabeling;
using YoloHolo.Utilities;
using System.Collections.Concurrent; 

#if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using WinUnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding;
#endif
public class ServerResponse
{
    public string object_id { get; set; }
    // 필요하다면 timestamp 등 다른 필드도 추가할 수 있습니다.
}
public class MainController_Base64 : MonoBehaviour
{
    public class WebSocketMessage
    {
        public string type { get; set; }
        public string target { get; set; }
        public string message { get; set; }
        public string new_state { get; set; } // state_changed 메시지용
    }

    [Header("Component Links")]
    [Tooltip("YoloObjectLabeler 컴포넌트가 있는 게임 오브젝트")]
    public YoloObjectLabeler yoloLabeler;
    [Tooltip("ObjectTrackingController 컴포넌트")]
    public ObjectTrackingController objectTracker;

    [Header("Server Settings")]
    public string commandWsUrl = "ws://192.168.0.110:5000";
    public string dataServerBase = "http://192.168.0.110:8008";
    public string ocrEndpoint = "/ocr";

    [Header("Highlight Settings")]
    [Tooltip("타겟을 강조 표시할 때 사용할 프리팹")]
    public GameObject highlightPrefab;

    [Header("Stability Settings")]
    [Tooltip("캡처를 위한 안정성 임계값 (프레임당 각도 변화). 낮을수록 엄격합니다.")]
    [SerializeField] private float stabilityThreshold = 0.1f;

    [Header("Capture & Encoding Settings")]
    public float captureInterval = 0.35f;
    public bool alwaysSend = true;
    public int resizedWidth = 896;
    public int resizedHeight = 504;
    [Range(1, 100)]
    public int jpgQuality = 100;


    // --- Internal State ---
    private string _uploadUrl;
    private bool _isProcessing = false;
    private long _frameSeq = 0;

    // Texture processing cache
    private Camera _mainCamera;
    private GameObject _currentHighlightObject; // 이름 변경: Cube -> Object
    private string _currentHighlightedObjectId = null;
    private string _currentSearchTarget = null;

#if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
    private MessageWebSocket messageWebSocket;
    private readonly ConcurrentQueue<string> wsMessages = new ConcurrentQueue<string>();
#endif
    //안정성 감지 변수
    private Quaternion _lastCameraRotation;
    private bool _isStable = false;   

    private int _highlightCubeCounter = 1;
    private RenderTexture _rt;
    private Texture2D _tmpJpgRead;

#if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
    // WebSocket 관련 필드들
#endif
    // --- 수정된 부분 1: _mainCamera 변수 선언 ---


    void Start()
    {
        ObjectTrackingController.ResetIDCounter();
        // 2. 혹시라도 씬에 남아있을 수 있는 이전 하이라이트 오브젝트를 제거합니다.
        ClearHighlight();

        // 3. 프레임 번호와 하이라이트 카운터를 초기값으로 명확하게 리셋합니다.
        _frameSeq = 0;
        _highlightCubeCounter = 1;
        _isProcessing = false; // 처리 플래그도 확실하게 초기화합니다.

        Debug.LogWarning("[MainController] 모든 상태 변수와 이전 데이터를 초기화했습니다.");
        // --- 수정된 부분 2: _mainCamera 변수 초기화 ---
        _mainCamera = Camera.main;
        //  회전 값 초기화 코드 추가 
        if (_mainCamera != null)
        {
            _lastCameraRotation = _mainCamera.transform.rotation;
        }   
        if (yoloLabeler == null || objectTracker == null || _mainCamera == null)
        {
            Debug.LogError("[MainController] 필수 컴포넌트(YoloLabeler, ObjectTracker, MainCamera)가 할당되지 않았습니다!");
            return;
        }

        StartCoroutine(DelayedStart());
    }
    void Update()
    {
#if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
        // 큐에 메시지가 있으면 하나씩 꺼내서 메인 스레드에서 처리
        while (wsMessages.TryDequeue(out string message))
        {
            ProcessWebSocketMessage(message);
        }
#endif
        if (_mainCamera != null)
        {
            Quaternion currentRotation = _mainCamera.transform.rotation;
            
            // 이전 프레임과 현재 프레임의 각도 차이를 계산
            float angleDifference = Quaternion.Angle(_lastCameraRotation, currentRotation);

            // 각도 차이가 임계값보다 작으면 '안정' 상태로 판단
            _isStable = (angleDifference < stabilityThreshold);

            // 다음 프레임을 위해 현재 회전 값을 저장
            _lastCameraRotation = currentRotation;
        }
    }
    private IEnumerator DelayedStart()
    {
        Debug.Log("[MainController] 초기화 완료. 1초 후 캡처 루프를 시작합니다.");
        yield return new WaitForSeconds(1.0f);

        _uploadUrl = $"{dataServerBase.TrimEnd('/')}/{ocrEndpoint.TrimStart('/')}";
        Debug.Log($"[MainController] 서버 URL 설정: {_uploadUrl}");
    #if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
        ConnectWebSocket(); 
    #endif
        StartCoroutine(CaptureLoop());
    }
    private IEnumerator CaptureLoop()
    {
        var waiter = new WaitForSecondsRealtime(captureInterval);
        Debug.Log("[MainController] 캡처 루프를 시작합니다.");

        while (true)
        {
            if (yoloLabeler.YoloInputTexture == null)
            {
                yield return waiter;
                continue;
            }

            if (!_isProcessing && !string.IsNullOrEmpty(_currentSearchTarget))
            {
                _isProcessing = true;
                yield return ProcessAndSendFrame();
                _isProcessing = false;
            }
            // 모든 과정(캡처-지연-전송)이 끝난 후 다음 캡처까지 captureInterval 만큼 대기합니다.
            yield return waiter;
        }
    }

    private IEnumerator ProcessAndSendFrame()
    {
        long frameId = ++_frameSeq;

        // 1. 캡처 및 데이터 처리 시작
        Debug.Log($"[MainController] Frame {frameId}: 데이터 처리 및 이미지 캡처 시작.");

        // ▼▼▼ 순서 변경 ▼▼▼
        // 1. 3D -> 2D 좌표 변환을 먼저 수행
        List<YoloObjectInfo> foundObjects = objectTracker.FindAndProjectObjects("TargetObject");

        // 2. 좌표 변환 직후, 시간차를 최소화하여 이미지 캡처
        // Texture2D sourceTexture = yoloLabeler.YoloInputTexture.ToTexture2D();
        Texture sourceTexture = yoloLabeler.OriginalWebCamTexture; // 원본 WebCamTexture를 직접 사용

        if (sourceTexture == null)
        {
            Debug.LogError("[MainController] YOLO 텍스처를 가져오지 못했습니다.");
            _isProcessing = false;
            yield break;
        }
        // ▲▲▲ 순서 변경 완료 ▲▲▲

        byte[] jpgData = EncodeResizedToJPG(sourceTexture, resizedWidth, resizedHeight, jpgQuality);
        // Destroy(sourceTexture);

        // 3. 요청하신 1초 지연
        Debug.Log($"[MainController] Frame {frameId}: 처리 완료. 1초 후 전송을 시작합니다.");
        yield return new WaitForSeconds(1.0f);

        // 4. 서버로 전송
        Debug.Log($"[MainController] Frame {frameId}: 데이터 전송 중...");
        yield return SendFrameJsonCoroutine(frameId, jpgData, foundObjects);
    }

    // --- 아래는 ServerController로부터 가져온 메서드들 ---
    IEnumerator TestPing()
    {
        var url = $"{dataServerBase.TrimEnd('/')}/ping";
        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 5;
            yield return req.SendWebRequest();
            Debug.Log($"[PING] url={url}, result={req.result}, code={req.responseCode}, err={req.error}, body={req.downloadHandler.text}");
        }
    }
    // Start() 里先跑
    // StartCoroutine(TestPing());
    /// <summary>
    /// 프레임 데이터(이미지, 객체 정보)를 JSON으로 구성하여 서버에 전송합니다.
    /// </summary>
    public IEnumerator SendFrameJsonCoroutine(long frameId, byte[] jpgData, List<YoloObjectInfo> Objects)
    {
        if (jpgData == null || jpgData.Length == 0)
        {
            Debug.LogError("[MainController] 전송할 이미지 데이터가 없습니다.");
            yield break;
        }

        string b64Image = Convert.ToBase64String(jpgData);

        // Get original YOLO texture dimensions
        // Texture2D yoloTexture = yoloLabeler.YoloInputTexture.ToTexture2D();
        
        // var originalTexture = yoloLabeler.OriginalWebCamTexture;
        // float originalWidth = originalTexture.width;
        // float originalHeight = originalTexture.height;

        float originalWidth = _mainCamera.pixelWidth;
        float originalHeight = _mainCamera.pixelHeight;

        // float originalWidth = yoloTexture.width;
        // float originalHeight = yoloTexture.height;
        // Destroy(yoloTexture); // Clean up temp texture

        // Calculate scaling factors
        float scaleX = resizedWidth / originalWidth;
        float scaleY = resizedHeight / originalHeight;
        bool mirrorX = false; // Set to true if your camera/display is mirrored

        // Convert coordinates to match the resized image
        var convertedObjects = new List<YoloObjectInfo>();
        foreach (var obj in Objects)
        {
            // // First, scale the coordinates to match resized image dimensions
            // float scaledX = obj.x * scaleX;
            // float scaledY = obj.y * scaleY;

            // // Then, convert Y from Unity's bottom-left to image's top-left origin

            // // float convertedY = resizedHeight - scaledY - 1;
            // float convertedY = (resizedHeight - 1) - scaledY;
            // if (mirrorX)
            // {
            //     scaledX = (resizedWidth - 1) - scaledX;
            // }
            // scaledX = Mathf.Clamp(scaledX, 0, resizedWidth - 1);
            // convertedY = Mathf.Clamp(convertedY, 0, resizedHeight - 1);
            // // float convertedY = 936 -  obj.y - 1;

            // convertedObjects.Add(new YoloObjectInfo
            // {
            //     object_id = obj.object_id,
            //     x = scaledX,
            //     y = convertedY
            //     // x = obj.x,
            //     // y = convertedY
            // });

            // // Debug log for verification
            // Debug.Log($"[Coord Transform] {obj.object_id}: Original({obj.x}, {obj.y}) → Scaled({scaledX}, {scaledY}) → Final({scaledX}, {convertedY})");
                        // --- ▼▼▼ [수정] 좌표 변환 로직 전체 수정 ▼▼▼ ---

            // 1. 원본 좌표를 리사이즈된 이미지 크기에 맞게 스케일링합니다.
            //    정확한 계산을 위해 모든 값을 float으로 처리합니다.
            float scaledX = obj.x * ((float)resizedWidth / originalWidth);
            float scaledY = obj.y * ((float)resizedHeight / originalHeight);

            // 2. Y 좌표를 뒤집습니다. (Unity 화면 좌표계 -> 이미지 좌표계)
            //    Unity는 아래가 0, 이미지는 위가 0이므로 (높이 - y) 계산이 필요합니다.
            float convertedY = resizedHeight - 1 - scaledY;
            float invertedY = 936 - 1 - obj.y;

            // 3. 최종 좌표가 이미지 크기(0 ~ resizedWidth/Height - 1)를 벗어나지 않도록 제한합니다.
            float finalX = Mathf.Clamp(scaledX, 0, resizedWidth - 1);
            float finalY = Mathf.Clamp(convertedY, 0, resizedHeight - 1);

            convertedObjects.Add(new YoloObjectInfo
            {
                object_id = obj.object_id,
                x = finalX, // obj.x -> finalX 로 수정
                y = finalY  // invertedY -> finalY 로 수정
            });

            // [수정] 디버그 로그를 더 상세하게 변경하여 문제 추적을 쉽게 만듭니다.
            Debug.Log($"[Coord Transform] ID: {obj.object_id} | Original: ({obj.x:F2}, {obj.y:F2}) | Scaled: ({scaledX:F2}, {scaledY:F2}) | FlippedY: {invertedY:F2} | Final: ({finalX:F2}, {finalY:F2})");
            // --- ▲▲▲ 수정 완료 ▲▲▲ ---
        }
        Debug.Log(convertedObjects);

        long tsMs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new Dictionary<string, object>
        {
            { "frame_id", frameId },
            { "image", b64Image },
            { "image_w", resizedWidth },  // Added for server validation
            { "image_h", resizedHeight },  // Added for server validation
            { "objects", convertedObjects },
            { "timestamp", tsMs },

        };

        string json = JsonConvert.SerializeObject(payload, new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        });

        // Debug log (truncated for large images)
        if (json.Length > 500)
        {
            Debug.Log($"[MainController] Sending JSON (truncated): {json.Substring(0, 500)}...");
        }
        else
        {
            Debug.Log($"[MainController] Sending JSON: {json}");
        }

        using (var req = new UnityWebRequest(_uploadUrl, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = 10;
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                // string response = req.downloadHandler.text;
                // Debug.Log($"✅ [HTTP] 프레임 전송 성공: ID={frameId}, Response={response}");

                // // --- ▼▼▼ [수정] 서버 응답에 따른 분기 처리 로직 변경 ▼▼▼ ---
                // if (!string.IsNullOrEmpty(response) && response != "not_found")
                // {
                //     // 타겟을 찾았을 때, 현재 하이라이트된 타겟과 다른 경우에만 업데이트
                //     if (response != _currentHighlightedObjectId)
                //     {
                //         Debug.Log($"🎯 [Highlight] 새로운 타겟 발견! 이전({_currentHighlightedObjectId ?? "없음"}) -> 신규({response})");
                //         HandleTargetFound(response);
                //         _currentHighlightedObjectId = response; // 현재 하이라이트 ID를 새 타겟으로 업데이트
                //     }
                //     // 같은 타겟일 경우 아무것도 하지 않고 현재 하이라이트를 유지
                // }
                // else
                // {
                //     // 'not_found' 응답을 받아도 아무것도 하지 않고 기존 하이라이트를 유지
                //     Debug.Log("🚫 [OCR] 타겟을 찾지 못함. 기존 하이라이트를 유지합니다.");
                // }
                    string responseText = req.downloadHandler.text;
                    Debug.Log($"✅ [HTTP] 프레임 전송 성공: ID={frameId}, Response={responseText}");

                    if (!string.IsNullOrEmpty(responseText) && responseText != "not_found")
                    {
                        try
                        {
                            // 1. 서버가 보낸 JSON 문자열을 ServerResponse 객체로 변환
                            ServerResponse serverResponse = JsonConvert.DeserializeObject<ServerResponse>(responseText);
                            string targetObjectId = serverResponse.object_id;

                            // 2. 파싱해서 얻은 순수한 object_id로 하이라이트 함수 호출
                            if (!string.IsNullOrEmpty(targetObjectId) && targetObjectId != _currentHighlightedObjectId)
                            {
                                Debug.Log($"🎯 [Highlight] 새로운 타겟 발견! 이전({_currentHighlightedObjectId ?? "없음"}) -> 신규({targetObjectId})");
                                HandleTargetFound(targetObjectId); // <--- 이제 "23_box" 같은 순수한 이름이 전달됩니다.
                                _currentHighlightedObjectId = targetObjectId;
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[JSON Parsing Error] 서버 응답을 파싱하는 데 실패했습니다: {responseText}, Error: {e.Message}");
                        }
                    }
                    else
                    {
                        Debug.Log("🚫 [OCR] 타겟을 찾지 못함. 기존 하이라이트를 유지합니다.");
                    }
                // --- ▲▲▲ 수정 완료 ▲▲▲ ---
            }
            else
            {
                Debug.LogError($"❌ [HTTP] 프레임 전송 실패: {req.responseCode} {req.error}");
                // 전송 실패 시에는 하이라이트를 유지할지, 지울지 결정할 수 있습니다. (현재는 유지)
            }
        }
    }
    // Optional: Add a method to handle when target is found
    private void HandleTargetFound(string objectId)
    {
        // --- ▼▼▼ 수정된 부분 3: 프리팹을 사용하도록 변경 ▼▼▼ ---

        // 1. 기존 하이라이트 오브젝트가 있으면 삭제합니다.
        ClearHighlight();

        // 2. 씬에서 타겟 오브젝트를 찾습니다.
        GameObject targetObject = GameObject.Find(objectId);

        if (targetObject != null)
        {
            Debug.Log($"[Highlight] 씬에서 {objectId} 오브젝트를 찾았습니다. 프리팹을 생성합니다.");

            // 3. 지정된 프리팹이 있는지 확인합니다.
            if (highlightPrefab != null)
            {
                // 4. 기본 큐브 대신 연결된 프리팹을 생성(Instantiate)합니다.
                GameObject newHighlight = Instantiate(highlightPrefab);
                newHighlight.name = $"Highlight_{_highlightCubeCounter++}";

                // 5. 프리팹의 위치와 크기를 설정합니다.
                newHighlight.transform.localScale = new Vector3(0.15f, 0.15f, 0.15f); // 필요에 따라 조절
                newHighlight.transform.position = targetObject.transform.position + new Vector3(0, 0, 0);

                // 6. 새로 만든 프리팹 인스턴스를 현재 하이라이트 오브젝트로 저장합니다.
                _currentHighlightObject = newHighlight;
            }
            else
            {
                Debug.LogError("[Highlight] Inspector에 `highlightPrefab`이 할당되지 않았습니다!");
            }
        }
        else
        {
            Debug.LogWarning($"[Highlight] 씬에서 {objectId} 라는 이름의 오브젝트를 찾지 못했습니다.");
        }
    }

    // --- ▼▼▼ 추가된 부분 4: 하이라이트를 깔끔하게 지우는 메서드 ▼▼▼ ---
    private void ClearHighlight()
    {
        if (_currentHighlightObject != null)
        {
            Debug.Log($"[Highlight] 기존 하이라이트 오브젝트({_currentHighlightObject.name})를 삭제합니다.");
            Destroy(_currentHighlightObject);
            _currentHighlightObject = null;
        }
        // ▼▼▼ [추가] 저장된 하이라이트 ID도 함께 초기화 ▼▼▼
        _currentHighlightedObjectId = null;
    }
    //     #region WebSocket Methods
    //     private IEnumerator ConnectWebSocket()
    //     {
    // #if !ENABLE_WINMD_SUPPORT || !WINDOWS_UWP
    //         Debug.Log("[MainController] WebSocket은 UWP 빌드에서만 동작합니다.");
    //         yield break;
    // #else
    //         // 여기에 UWP WebSocket 연결 로직 전체를 붙여넣으세요.
    //         yield break;
    // #endif
    //     }
    //     // ... 기타 WS 메서드들
    //     #endregion
#if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
    private async void ConnectWebSocket()
    {
#if !ENABLE_WINMD_SUPPORT || !WINDOWS_UWP
        Debug.Log("[MainController] WebSocket은 UWP 빌드에서만 동작합니다.");
        return; // Use return instead of yield break
#else
        Debug.Log($"[WebSocket] 서버에 연결 시도 중... {commandWsUrl}");
        messageWebSocket = new MessageWebSocket();
        messageWebSocket.Control.MessageType = SocketMessageType.Utf8;
        messageWebSocket.MessageReceived += OnWebSocketMessage;
        messageWebSocket.Closed += OnWebSocketClosed;

        try
        {
            // Now 'await' is valid in an async method
            await messageWebSocket.ConnectAsync(new Uri(commandWsUrl));
            Debug.Log("[WebSocket] 성공적으로 연결되었습니다.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WebSocket] 연결 실패: {ex.Message}");
            messageWebSocket.Dispose();
            messageWebSocket = null;
        }
        // No longer need 'yield return null'
#endif
    }
    private void OnWebSocketMessage(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
    {
        try
        {
            using (DataReader dataReader = args.GetDataReader())
            {
                dataReader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                string message = dataReader.ReadString(dataReader.UnconsumedBufferLength);
                Debug.Log($"[WebSocket] 메시지 수신: {message}");

                // Corrected: Use the existing ConcurrentQueue to pass the message to the main thread.
                // The Update() method will handle processing it.
                wsMessages.Enqueue(message);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WebSocket] 메시지 처리 오류: {ex.Message}");
        }
    }
    private void ProcessWebSocketMessage(string message)
    {
        try
        {
            WebSocketMessage wsMessage = JsonConvert.DeserializeObject<WebSocketMessage>(message);

            // "search_started" 타입일 경우 target을 설정
            if (wsMessage.type == "search_started")
            {
                Debug.Log($"[WebSocket] 새로운 검색 타겟 수신: {wsMessage.target}");
                _currentSearchTarget = wsMessage.target;
                ClearHighlight(); // 새로운 타겟을 찾기 전에 이전 하이라이트 제거
            }
            // "state_changed" 타입이고, 새 상태가 "active"일 경우 target을 설정
            else if (wsMessage.type == "state_changed" && wsMessage.new_state == "active")
            {
                // 서버가 target을 보내주는 경우에만 값을 가져옴
                if (!string.IsNullOrEmpty(wsMessage.target))
                {
                    Debug.Log($"[WebSocket] 상태 변경으로 검색 타겟 활성화: {wsMessage.target}");
                    _currentSearchTarget = wsMessage.target;
                    ClearHighlight();
                }
            }
            // 검색이 중지되는 조건
            else if (wsMessage.type == "search_paused" || (wsMessage.type == "state_changed" && wsMessage.new_state != "active"))
            {
                Debug.Log("[WebSocket] 검색이 중지되었습니다.");
                _currentSearchTarget = null;
                ClearHighlight();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[WebSocket] JSON 파싱 오류: {e.Message}");
        }
    }
    private void OnWebSocketClosed(IWebSocket sender, WebSocketClosedEventArgs args)
    {
        Debug.LogWarning($"[WebSocket] 연결이 끊어졌습니다. Code: {args.Code}, Reason: {args.Reason}");
        messageWebSocket?.Dispose();
        messageWebSocket = null;
        // 필요하다면 여기서 재연결 로직을 시작할 수 있습니다.
        // StartCoroutine(ConnectWebSocket());
    }
#endif

    private byte[] EncodeResizedToJPG(Texture src, int w, int h, int quality)
    {
        var prev = RenderTexture.active;
        try
        {
            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                if (_rt != null) RenderTexture.ReleaseTemporary(_rt);
                _rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            }

            Graphics.Blit(src, _rt);
            RenderTexture.active = _rt;

            if (_tmpJpgRead == null || _tmpJpgRead.width != w || _tmpJpgRead.height != h)
            {
                if (_tmpJpgRead != null) Destroy(_tmpJpgRead);
                _tmpJpgRead = new Texture2D(w, h, TextureFormat.RGB24, false);
            }

            _tmpJpgRead.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            _tmpJpgRead.Apply(false, false);
            return _tmpJpgRead.EncodeToJPG(Mathf.Clamp(quality, 1, 100));
        }
        finally
        {
            RenderTexture.active = prev;
        }
    }

    void OnDestroy()
    {
        if (_rt != null) RenderTexture.ReleaseTemporary(_rt);
        if (_tmpJpgRead != null) Destroy(_tmpJpgRead);
    }
}




//photocapture


// using UnityEngine;
// using UnityEngine.Networking;
// using System;
// using System.Collections;
// using System.Collections.Generic;
// using System.Text;
// using Newtonsoft.Json;
// using YoloHolo.YoloLabeling;
// using YoloHolo.Utilities;
// using UnityEngine.Windows.WebCam;
// using System.Linq;
// using System.Threading.Tasks;
// using System.IO;

// using System.Collections.Concurrent;
// #if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
// using Windows.Networking.Sockets;
// using Windows.Storage.Streams;
// using WinUnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding;
// using UnityEngine.WSA;
// #endif
// public class WebSocketMessage
// {
//     public string type { get; set; }
//     public string target { get; set; }
//     public string message { get; set; }
//     public string new_state { get; set; } // state_changed 메시지용
// }

// public class ServerResponse
// {
//     public string object_id { get; set; }
// }
// public class MainController_Base64 : MonoBehaviour
// {
//     [Header("Component Links")]
//     public YoloObjectLabeler yoloLabeler;
//     public ObjectTrackingController objectTracker;

//     [Header("Server Settings")]
//     public string dataServerBase = "http://192.168.0.110:8008";
//     public string commandWsUrl = "ws://192.168.0.110:5000/ws/command"; 
//     public string ocrEndpoint = "/ocr";

//     [Header("Highlight Settings")]
//     public GameObject highlightPrefab;

//     [Header("Capture & Encoding Settings")]
//     public float captureInterval = 0.5f;
//     public int resizedWidth = 896;//1280
//     public int resizedHeight = 504;//720
//     [Range(1, 100)]
//     public int jpgQuality = 75;

//     private string _uploadUrl;
//     private bool _isProcessing = false;
//     private long _frameSeq = 0;
//     private Camera _mainCamera;
//     private GameObject _currentHighlightObject;
//     private string _currentHighlightedObjectId = null;
//     private string _currentSearchTarget = null; 
//     private int _highlightCubeCounter = 1;
//     private RenderTexture _rt;
//     private Texture2D _tmpJpgRead;

//     // --- PhotoCapture 객체를 클래스 변수로 다시 선언 ---
//     private PhotoCapture photoCaptureObject = null;
//     private bool isCapturingPhoto = false;
//     private Texture2D capturedTexture = null;

// #if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
//     private MessageWebSocket messageWebSocket;
//     private readonly ConcurrentQueue<string> wsMessages = new ConcurrentQueue<string>();
// #endif

//     void Start()
//     {
//         ObjectTrackingController.ResetIDCounter();
//         ClearHighlight();
//         _frameSeq = 0;
//         _highlightCubeCounter = 1;
//         _isProcessing = false;
//         Debug.LogWarning("[MainController] 모든 상태 변수와 이전 데이터를 초기화했습니다.");
//         _mainCamera = Camera.main;
        
//         if (yoloLabeler == null || objectTracker == null || _mainCamera == null)
//         {
//             Debug.LogError("[MainController] 필수 컴포넌트가 할당되지 않았습니다!");
//             return;
//         }
//         StartCoroutine(DelayedStart());
//     }
//     void Update()
//     {
// #if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
//         // 큐에 메시지가 있으면 하나씩 꺼내서 메인 스레드에서 처리
//         while (wsMessages.TryDequeue(out string message))
//         {
//             ProcessWebSocketMessage(message);
//         }
// #endif
//     }
//     private IEnumerator DelayedStart()
//     {
//         Debug.Log("[MainController] 초기화 완료. 1초 후 캡처 루프를 시작합니다.");
//         yield return new WaitForSeconds(1.0f);

//         Debug.Log("[MainController] YOLO 카메라 준비 대기 중...");
//         yield return new WaitUntil(() => yoloLabeler.YoloInputTexture != null);
//         Debug.Log("[MainController] YOLO 카메라 준비 완료.");

//         _uploadUrl = $"{dataServerBase.TrimEnd('/')}/{ocrEndpoint.TrimStart('/')}";
//         Debug.Log($"[MainController] 서버 URL 설정: {_uploadUrl}");
        
//         // 앱 시작 시 한 번만 PhotoCapture를 초기화
//         yield return InitializePhotoCapture(); 
        
//         // Corrected: Call the async method directly, don't start it as a coroutine
//         ConnectWebSocket(); 
        
//         StartCoroutine(CaptureLoop());
//     }

// // --- ▼▼▼ 웹소켓 로직 전체 구현 ▼▼▼ ---
//     #region WebSocket Methods
//     private async void ConnectWebSocket()
//     {
// #if !ENABLE_WINMD_SUPPORT || !WINDOWS_UWP
//         Debug.Log("[MainController] WebSocket은 UWP 빌드에서만 동작합니다.");
//         return; // Use return instead of yield break
// #else
//         Debug.Log($"[WebSocket] 서버에 연결 시도 중... {commandWsUrl}");
//         messageWebSocket = new MessageWebSocket();
//         messageWebSocket.Control.MessageType = SocketMessageType.Utf8;
//         messageWebSocket.MessageReceived += OnWebSocketMessage;
//         messageWebSocket.Closed += OnWebSocketClosed;

//         try
//         {
//             // Now 'await' is valid in an async method
//             await messageWebSocket.ConnectAsync(new Uri(commandWsUrl));
//             Debug.Log("[WebSocket] 성공적으로 연결되었습니다.");
//         }
//         catch (Exception ex)
//         {
//             Debug.LogError($"[WebSocket] 연결 실패: {ex.Message}");
//             messageWebSocket.Dispose();
//             messageWebSocket = null;
//         }
//         // No longer need 'yield return null'
// #endif
//     }

// #if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
//     private void OnWebSocketMessage(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
//     {
//         try
//         {
//             using (DataReader dataReader = args.GetDataReader())
//             {
//                 dataReader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
//                 string message = dataReader.ReadString(dataReader.UnconsumedBufferLength);
//                 Debug.Log($"[WebSocket] 메시지 수신: {message}");

//                 // Corrected: Use the existing ConcurrentQueue to pass the message to the main thread.
//                 // The Update() method will handle processing it.
//                 wsMessages.Enqueue(message);
//             }
//         }
//         catch (Exception ex)
//         {
//             Debug.LogError($"[WebSocket] 메시지 처리 오류: {ex.Message}");
//         }
//     }


//     private void ProcessWebSocketMessage(string message)
//     {
//         try
//         {
//             WebSocketMessage wsMessage = JsonConvert.DeserializeObject<WebSocketMessage>(message);

//             // "search_started" 타입일 경우 target을 설정
//             if (wsMessage.type == "search_started")
//             {
//                 Debug.Log($"[WebSocket] 새로운 검색 타겟 수신: {wsMessage.target}");
//                 _currentSearchTarget = wsMessage.target;
//                 ClearHighlight(); // 새로운 타겟을 찾기 전에 이전 하이라이트 제거
//             }
//             // "state_changed" 타입이고, 새 상태가 "active"일 경우 target을 설정
//             else if (wsMessage.type == "state_changed" && wsMessage.new_state == "active")
//             {
//                 // 서버가 target을 보내주는 경우에만 값을 가져옴
//                 if (!string.IsNullOrEmpty(wsMessage.target))
//                 {
//                     Debug.Log($"[WebSocket] 상태 변경으로 검색 타겟 활성화: {wsMessage.target}");
//                     _currentSearchTarget = wsMessage.target;
//                     ClearHighlight();
//                 }
//             }
//             // 검색이 중지되는 조건
//             else if (wsMessage.type == "search_paused" || (wsMessage.type == "state_changed" && wsMessage.new_state != "active"))
//             {
//                 Debug.Log("[WebSocket] 검색이 중지되었습니다.");
//                 _currentSearchTarget = null;
//                 ClearHighlight();
//             }
//         }
//         catch (Exception e)
//         {
//             Debug.LogError($"[WebSocket] JSON 파싱 오류: {e.Message}");
//         }
//     }

//     private void OnWebSocketClosed(IWebSocket sender, WebSocketClosedEventArgs args)
//     {
//         Debug.LogWarning($"[WebSocket] 연결이 끊어졌습니다. Code: {args.Code}, Reason: {args.Reason}");
//         messageWebSocket?.Dispose();
//         messageWebSocket = null;
//         // 필요하다면 여기서 재연결 로직을 시작할 수 있습니다.
//         // StartCoroutine(ConnectWebSocket());
//     }
// #endif
//     #endregion

//     private IEnumerator InitializePhotoCapture()
//     {
//         Debug.Log("[PhotoCapture] 초기화 시작...");
//         yoloLabeler.StopWebCam();
//         yield return new WaitForSeconds(0.2f);

//         var createCompletionSource = new TaskCompletionSource<PhotoCapture>();
//         PhotoCapture.CreateAsync(false, captureObject =>
//         {
//             createCompletionSource.SetResult(captureObject);
//         });
//         yield return new WaitUntil(() => createCompletionSource.Task.IsCompleted);
//         photoCaptureObject = createCompletionSource.Task.Result;

//         if (photoCaptureObject == null)
//         {
//             Debug.LogError("[PhotoCapture] 카메라 객체 생성 실패!");
//             yoloLabeler.StartWebCam();
//             yield break;
//         }

//         // Resolution cameraResolution = PhotoCapture.SupportedResolutions.OrderByDescending((res) => res.width * res.height).First();
//         //가장 작은 해상도 선택
//         Resolution cameraResolution = PhotoCapture.SupportedResolutions
//         .OrderBy(res => res.width * res.height)
//         .First();
//         CameraParameters cameraParameters = new CameraParameters(WebCamMode.PhotoMode);
//         cameraParameters.hologramOpacity = 0.0f;
//         cameraParameters.cameraResolutionWidth = cameraResolution.width;
//         cameraParameters.cameraResolutionHeight = cameraResolution.height;
//         cameraParameters.pixelFormat = CapturePixelFormat.BGRA32;

//         var startCompletionSource = new TaskCompletionSource<bool>();
//         photoCaptureObject.StartPhotoModeAsync(cameraParameters, result =>
//         {
//             startCompletionSource.SetResult(result.success);
//         });
//         yield return new WaitUntil(() => startCompletionSource.Task.IsCompleted);

//         if (startCompletionSource.Task.Result)
//         {
//             Debug.Log($"[PhotoCapture] 고화질 사진 모드가 {cameraResolution.width}x{cameraResolution.height} 해상도로 시작되었습니다.");
//         }
//         else
//         {
//             Debug.LogError("[PhotoCapture] 고화질 사진 모드 시작 실패!");
//         }

//         yoloLabeler.StartWebCam();
//     }

//     private IEnumerator CaptureLoop()
//     {
//         var waiter = new WaitForSecondsRealtime(captureInterval);
//         Debug.Log("[MainController] 캡처 루프를 시작합니다.");
//         while (true)
//         {
//             if (yoloLabeler.YoloInputTexture == null)
//             {
//                 yield return waiter;
//                 continue;
//             }
//             if (!_isProcessing && !string.IsNullOrEmpty(_currentSearchTarget))
//             {
//                 _isProcessing = true;
//                 yield return ProcessAndSendFrame();
//                 _isProcessing = false;
//             }
//             yield return waiter;
//         }
//     }
    
//     // ProcessAndSendFrame을 다시 단순한 구조로 변경
//     private IEnumerator ProcessAndSendFrame()
//     {
//         long frameId = ++_frameSeq;
//         Debug.Log($"[MainController] Frame {frameId}: 데이터 처리 및 이미지 캡처 시작.");
//         List<YoloObjectInfo> foundObjects = objectTracker.FindAndProjectObjects("TargetObject");

//         if (photoCaptureObject == null || isCapturingPhoto)
//         {
//             Debug.LogWarning("[PhotoCapture] 이미 캡처 중이거나 초기화되지 않았습니다.");
//             _isProcessing = false;
//             yield break;
//         }

//         yoloLabeler.StopWebCam();
        
//         isCapturingPhoto = true;
//         photoCaptureObject.TakePhotoAsync(OnCapturedPhotoToMemory);
//         yield return new WaitUntil(() => !isCapturingPhoto);
        
//         yoloLabeler.StartWebCam();

//         if (capturedTexture == null)
//         {
//             Debug.LogError("[MainController] 사진 캡처에 실패하여 전송할 수 없습니다.");
//             _isProcessing = false;
//             yield break;
//         }
        
//         byte[] jpgData = EncodeResizedToJPG(capturedTexture, resizedWidth, resizedHeight, jpgQuality);
        
//         Debug.Log($"[MainController] Frame {frameId}: 처리 완료. 1초 후 전송을 시작합니다.");
//         yield return new WaitForSeconds(1.0f);
//         Debug.Log($"[MainController] Frame {frameId}: 데이터 전송 중...");
//         yield return SendFrameJsonCoroutine(frameId, jpgData, foundObjects);
//     }
    
//     void OnCapturedPhotoToMemory(PhotoCapture.PhotoCaptureResult result, PhotoCaptureFrame photoCaptureFrame)
//     {
//         if (result.success)
//         {
//             if (capturedTexture != null)
//             {
//                 Destroy(capturedTexture);
//             }
//             // capturedTexture = new Texture2D(2, 2);
//             capturedTexture = new Texture2D(resizedWidth, resizedHeight, TextureFormat.RGB24, false);
//             photoCaptureFrame.UploadImageDataToTexture(capturedTexture);
//             Debug.Log("[PhotoCapture] 사진 캡처 성공! Texture에 직접 업로드됨.");
//             try
//             {
//                 string filename = $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
//                 // 'Application'이 모호하다는 오류를 해결하기 위해 'UnityEngine.Application'으로 명시
//                 string filepath = Path.Combine(UnityEngine.Application.persistentDataPath, filename);

//                 // byte[] jpgBytes = capturedTexture.EncodeToJPG(100);
//                 byte[] jpgBytes = EncodeResizedToJPG(capturedTexture, resizedWidth, resizedHeight, 100);


//                 File.WriteAllBytes(filepath, jpgBytes);
//                 Debug.Log($"[PhotoCapture] 이미지를 다음 경로에 저장했습니다: {filepath}");
//             }
//             catch (Exception e)
//             {
//                 Debug.LogError($"[PhotoCapture] 이미지 저장 실패: {e.Message}");
//             }
//         }
//         else
//         {
//             Debug.LogError("[PhotoCapture] 메모리에 사진 저장 실패!");
//             if (capturedTexture != null)
//             {
//                 Destroy(capturedTexture);
//                 capturedTexture = null;
//             }
//         }
//         photoCaptureFrame.Dispose();
//         isCapturingPhoto = false;
//     }

//     public IEnumerator SendFrameJsonCoroutine(long frameId, byte[] jpgData, List<YoloObjectInfo> Objects)
//     {
//         if (jpgData == null || jpgData.Length == 0) yield break;
        
//         string b64Image = Convert.ToBase64String(jpgData);
//         // float originalHeight = yoloLabeler.YoloInputTexture.height;
//         float originalWidth = _mainCamera.pixelWidth;
//         float originalHeight = _mainCamera.pixelHeight;
//         var convertedObjects = new List<YoloObjectInfo>();
//         foreach (var obj in Objects)
//         // {
//         //     float invertedY = 936 - 1 - obj.y;
//         //     convertedObjects.Add(new YoloObjectInfo
//         //     {
//         //         object_id = obj.object_id,
//         //         // x = obj.x,
//         //         // y = invertedY
//         //         x = finalX,
//         //         y = finalY
//         //     });
//         //     Debug.Log($"🚀 [Coord Transform] 전송: {obj.object_id} | 원본:({obj.x:F1}, {obj.y:F1}) -> 최종:({finalX:F1}, {finalY:F1})");

//         // }
//             //896, 504
//         {
//         // 2. 원본 좌표를 리사이즈된 이미지 크기에 맞게 스케일링합니다.
//         float scaledX = obj.x * (resizedWidth / originalWidth);
//         float scaledY = obj.y * (resizedHeight / originalHeight);

//         // 3. 스케일링된 Y 좌표를 이미지 좌표계(상단 0)에 맞게 뒤집습니다.
//         float finalY = resizedHeight - 1 - scaledY;

//         // 4. 최종 좌표가 이미지 경계를 벗어나지 않도록 Clamp 처리합니다.
//         float finalX = Mathf.Clamp(scaledX, 0, resizedWidth - 1);
//         finalY = Mathf.Clamp(finalY, 0, resizedHeight - 1);

//         convertedObjects.Add(new YoloObjectInfo {
//             object_id = obj.object_id,
//             x = finalX,
//             y = finalY
//         });

//         // --- ▼▼▼ 전송 시 좌표 로그 ▼▼▼ ---
//         Debug.Log($"🚀 [Coord Transform] 전송: {obj.object_id} | 원본:({obj.x:F1}, {obj.y:F1}) -> 최종:({finalX:F1}, {finalY:F1})");
//         // --- ▲▲▲ 로그 추가 완료 ▲▲▲ ---
//     }
        
//         long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
//         var payload = new Dictionary<string, object>
//         {
//             { "frame_id", frameId },
//             { "image", b64Image },
//             { "image_w", resizedWidth },
//             { "image_h", resizedHeight },
//             { "objects", convertedObjects },
//             { "timestamp", ts },
//         };
//         string json = JsonConvert.SerializeObject(payload);

//         using (var req = new UnityWebRequest(_uploadUrl, "POST"))
//         {
//             byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
//             req.uploadHandler = new UploadHandlerRaw(bodyRaw);
//             req.downloadHandler = new DownloadHandlerBuffer();
//             req.timeout = 10;
//             req.SetRequestHeader("Content-Type", "application/json");
//             yield return req.SendWebRequest();
            
//             if (req.result == UnityWebRequest.Result.Success)
//             {
//                 string responseText = req.downloadHandler.text;
//                 Debug.Log($"✅ [HTTP] 프레임 전송 성공: ID={frameId}, Response={responseText}");
//                 if (!string.IsNullOrEmpty(responseText) && responseText != "not_found")
//                 {
//                     try
//                     {
//                         ServerResponse serverResponse = JsonConvert.DeserializeObject<ServerResponse>(responseText);
//                         string targetObjectId = serverResponse.object_id;
//                         if (!string.IsNullOrEmpty(targetObjectId) && targetObjectId != _currentHighlightedObjectId)
//                         {
//                             HandleTargetFound(targetObjectId);
//                             _currentHighlightedObjectId = targetObjectId;
//                         }
//                     }
//                     catch (Exception e) { Debug.LogError($"[JSON Parsing Error]: {responseText}, Error: {e.Message}"); }
//                 }
//             }
//             else { Debug.LogError($"❌ [HTTP] 프레임 전송 실패: {req.responseCode} {req.error}"); }
//         }
//     }

//     private void HandleTargetFound(string objectId)
//     {
//         ClearHighlight();
//         GameObject targetObject = GameObject.Find(objectId);
//         if (targetObject != null)
//         {
//             Debug.Log($"[Highlight] 씬에서 {objectId} 오브젝트를 찾았습니다.");
//             if (highlightPrefab != null)
//             {
//                 GameObject newHighlight = Instantiate(highlightPrefab);
//                 newHighlight.name = $"Highlight_{_highlightCubeCounter++}";
//                 newHighlight.transform.localScale = new Vector3(0.15f, 0.15f, 0.15f);
//                 newHighlight.transform.position = targetObject.transform.position;
//                 _currentHighlightObject = newHighlight;
//             }
//         }
//         else { Debug.LogWarning($"[Highlight] 씬에서 '{objectId}' 라는 이름의 오브젝트를 찾지 못했습니다."); }
//     }

//     private void ClearHighlight()
//     {
//         if (_currentHighlightObject != null)
//         {
//             Destroy(_currentHighlightObject);
//             _currentHighlightObject = null;
//         }
//         _currentHighlightedObjectId = null;
//     }

//     private byte[] EncodeResizedToJPG(Texture src, int w, int h, int quality)
//     {
//         var prev = RenderTexture.active;
//         try
//         {
//             if (_rt == null || _rt.width != w || _rt.height != h)
//             {
//                 if (_rt != null) RenderTexture.ReleaseTemporary(_rt);
//                 _rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
//             }
//             Graphics.Blit(src, _rt);
//             RenderTexture.active = _rt;
//             if (_tmpJpgRead == null || _tmpJpgRead.width != w || _tmpJpgRead.height != h)
//             {
//                 if (_tmpJpgRead != null) Destroy(_tmpJpgRead);
//                 _tmpJpgRead = new Texture2D(w, h, TextureFormat.RGB24, false);
//             }
//             _tmpJpgRead.ReadPixels(new Rect(0, 0, w, h), 0, 0);
//             _tmpJpgRead.Apply(false, false);
//             return _tmpJpgRead.EncodeToJPG(Mathf.Clamp(quality, 1, 100));
//         }
//         finally
//         {
//             RenderTexture.active = prev;
//         }
//     }

//     void OnDestroy()
//     {
//         if (photoCaptureObject != null)
//         {
//             photoCaptureObject.StopPhotoModeAsync(result =>
//             {
//                 if (result.success)
//                 {
//                     photoCaptureObject.Dispose();
//                     photoCaptureObject = null;
//                 }
//             });
//         }
//         if (_rt != null) RenderTexture.ReleaseTemporary(_rt);
//         if (_tmpJpgRead != null) Destroy(_tmpJpgRead);
//         if (capturedTexture != null) Destroy(capturedTexture);
// #if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
//         if (messageWebSocket != null)
//         {
//             messageWebSocket.Close(1000, "Client shutting down");
//             messageWebSocket.Dispose();
//             messageWebSocket = null;
//             Debug.Log("[WebSocket] 연결을 해제했습니다.");
//         }
// #endif
//     }
// }

















