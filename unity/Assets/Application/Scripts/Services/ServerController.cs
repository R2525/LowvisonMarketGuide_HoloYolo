// ServerController.cs

using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

#if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using WinUnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding;
#endif

public class ServerController : MonoBehaviour
{
    [Header("Server Settings")]
    public string commandWsUrl = "ws://192.168.0.110:5000";
    public string dataServerBase = "http://192.168.0.110:8008";
    public string ocrEndpoint = "/ocr";
    
    private string _uploadUrl;

#if ENABLE_WINMD_SUPPORT && WINDOWS_UWP
    // WebSocket 관련 필드들...
#endif

    void Start()
    {
        _uploadUrl = $"{dataServerBase.TrimEnd('/')}/{ocrEndpoint.TrimStart('/')}";
        Debug.Log($"[ServerController] 서버 URL 설정: {_uploadUrl}");
        
        // WebSocket 연결 시작
        StartCoroutine(ConnectWebSocket());
    }

    /// <summary>
    /// 프레임 데이터(이미지, 객체 정보)를 JSON으로 구성하여 서버에 전송합니다.
    /// </summary>
    public IEnumerator SendFrameJsonCoroutine(long frameId, byte[] jpgData, List<YoloObjectInfo> detectedObjects)
    {
        Debug.Log("0");
        if (jpgData == null || jpgData.Length == 0)
        {
            Debug.LogError("[ServerController] 전송할 이미지 데이터가 없습니다.");
            yield break;
        }
        Debug.Log("1");
        string b64Image = Convert.ToBase64String(jpgData);

        var payload = new Dictionary<string, object>
        {
            { "frame_id", frameId },
            { "image", b64Image },
            { "detected_objects", detectedObjects }
        };
                Debug.Log("2");
        string json = JsonConvert.SerializeObject(payload, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        Debug.Log("3");
        using (var req = new UnityWebRequest(_uploadUrl, "POST"))
        {
                    Debug.Log("4");
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = 10;
            req.SetRequestHeader("Content-Type", "application/json");
            Debug.Log("5");
            yield return req.SendWebRequest();
                    Debug.Log("6");
            if (req.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"✅ [HTTP] 프레임 전송 성공: ID={frameId}, Size={jpgData.Length}B");
            }
            else
            {
                Debug.LogError($"❌ [HTTP] 프레임 전송 실패: {req.responseCode} {req.error}");
            }
        }
    }

    #region WebSocket Methods
    // 기존 MainController_Base64에 있던 WebSocket 관련 메서드들을 모두 이곳으로 옮깁니다.
    // (ConnectWebSocket, Heartbeat, SendWsJson, HandleCommand 등)
    private IEnumerator ConnectWebSocket()
    {
        #if !ENABLE_WINMD_SUPPORT || !WINDOWS_UWP
        Debug.Log("[ServerController] WebSocket은 UWP 빌드에서만 동작합니다.");
        yield break;
        #else
        // 여기에 UWP WebSocket 연결 로직 전체를 붙여넣으세요.
        yield break;
        #endif
    }
    // ... 기타 WS 메서드들
    #endregion
}