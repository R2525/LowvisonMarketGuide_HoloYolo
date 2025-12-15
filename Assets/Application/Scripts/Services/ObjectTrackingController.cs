// ObjectTrackingController.cs (Modified)

using UnityEngine;
using System.Collections.Generic;

public class ObjectTrackingController : MonoBehaviour
{
    private Camera mainCamera;
    
    // --- ▼▼▼ 추가된 부분 ▼▼▼ ---
    // 할당된 고유 ID의 카운터
    private static int _nextUniqueID = 1;
    private Dictionary<GameObject, TargetObjectID> _trackedObjects = new Dictionary<GameObject, TargetObjectID>();
    public static void ResetIDCounter()
    {
        _nextUniqueID = 1;
        Debug.LogWarning("[ObjectTracker] 고유 ID 카운터가 1로 초기화되었습니다.");
    }
    void Start()
    {
        mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogError("[ObjectTracker] 메인 카메라를 찾을 수 없습니다!");
        }
    }

    public List<YoloObjectInfo> FindAndProjectObjects(string tag)
    {
        if (mainCamera != null)
        {
            // Debug.Log($"Current Camera Resolution: {mainCamera.pixelWidth} x {mainCamera.pixelHeight}");
        }
        var screenObjects = new List<YoloObjectInfo>();
        if (mainCamera == null) return screenObjects;

        var targetObjects = GameObject.FindGameObjectsWithTag(tag);

        foreach (var obj in targetObjects)
        {

            // 1. 오브젝트에 ID가 이미 할당되었는지 확인
            if (!_trackedObjects.ContainsKey(obj))
            {
                // ID가 없으면, TargetObjectID 컴포넌트를 가져오거나 추가
                TargetObjectID idComponent = obj.GetComponent<TargetObjectID>();
                if (idComponent == null)
                {
                    idComponent = obj.AddComponent<TargetObjectID>();
                }
                
                // 아직 고유 ID가 할당되지 않았다면 새로 할당
                if (idComponent.UniqueID == -1)
                {
                    idComponent.UniqueID = _nextUniqueID++;
                    
                    // 게임 오브젝트 자체의 이름을 고유한 이름으로 변경 (예: "Sphere_1")
                    // obj.name = $"{obj.name}_{idComponent.UniqueID}";
                    _trackedObjects.Add(obj, idComponent);

                    Vector3 worldPos = obj.transform.position;
                    Vector2 screenPos = mainCamera.WorldToScreenPoint(worldPos);
                    
                    Debug.Log($"✅ [ObjectTracker] 새로운 타겟 '{obj.name}' 발견 | 3D:({worldPos.x:F2}, {worldPos.y:F2}, {worldPos.z:F2}), 2D:({screenPos.x:F1}, {screenPos.y:F1})");
                    
                }
            }
            
            // 2. 3D 좌표를 2D 화면 좌표로 변환
            Vector3 screenPoint = mainCamera.WorldToScreenPoint(obj.transform.position);
            float yOffset = 4.0f; // 15픽셀만큼 아래로. 
            screenPoint.y += yOffset;
            if (screenPoint.z > 0 &&
                screenPoint.x >= 0 && screenPoint.x <= mainCamera.pixelWidth &&
                screenPoint.y >= 0 && screenPoint.y <= mainCamera.pixelHeight)
            {
                // 조건 만족 시에만 리스트에 추가
                screenObjects.Add(new YoloObjectInfo
                {
                    object_id = obj.name,
                    x = screenPoint.x,
                    // Y좌표계는 서버에서 변환할 것이므로 Unity 좌표 그대로 전송
                    y = screenPoint.y 
                });
            }
        }
        return screenObjects;
    }
}