// using System;
// using System.Collections.Generic;
// using System.Linq;
// using System.Threading.Tasks;
// using RealityCollective.ServiceFramework.Services;
// using UnityEngine;
// using YoloHolo.Services;
// using YoloHolo.Utilities;

// namespace YoloHolo.YoloLabeling
// {
//     public class YoloObjectLabeler : MonoBehaviour
//     {
//         [Header("Layer Settings")]
//         [Tooltip("라벨을 정렬할 총 층의 개수")]
//         [SerializeField]
//         private int layerCount = 4;
//         [Tooltip("각 층의 높이 (미터 단위)")]
//         [SerializeField]
//         private float layerHeight = 0.2f; // 20cm
//         [SerializeField]
//         private GameObject labelObject;

//         [SerializeField]
//         private int cameraFPS = 4;

//         [SerializeField]
//         private Vector2Int requestedCameraSize = new(896, 504);

//         private Vector2Int actualCameraSize;

//         [SerializeField]
//         private Vector2Int yoloImageSize = new(320, 256);

//         [SerializeField]
//         private float virtualProjectionPlaneWidth = 1.356f;

//         [SerializeField]
//         private float minIdenticalLabelDistance = 0.3f;

//         [SerializeField]
//         private float labelNotSeenTimeOut = 5f;

//         [SerializeField]
//         private Renderer debugRenderer;

//         private WebCamTexture webCamTexture;

//         private IYoloProcessor yoloProcessor;

//         private readonly List<YoloGameObject> yoloGameObjects = new();

//         public RenderTexture YoloInputTexture { get; private set; }
//         private int yoloObjectCounter = 1;
//         private void Start()
//         {
//             yoloProcessor = ServiceManager.Instance.GetService<IYoloProcessor>();
//             webCamTexture = new WebCamTexture(requestedCameraSize.x, requestedCameraSize.y, cameraFPS);
//             webCamTexture.Play();
//             StartRecognizingAsync();
//         }

//         private async Task StartRecognizingAsync()
//         {
//             await Task.Delay(1000);

//             actualCameraSize = new Vector2Int(webCamTexture.width, webCamTexture.height);
//             // var renderTexture = new RenderTexture(yoloImageSize.x, yoloImageSize.y, 24);
//             YoloInputTexture = new RenderTexture(yoloImageSize.x, yoloImageSize.y, 24); 

//             if (debugRenderer != null && debugRenderer.gameObject.activeInHierarchy)
//             {
//                 // debugRenderer.material.mainTexture = renderTexture;
//                 debugRenderer.material.mainTexture = YoloInputTexture;

//             }

//             while (true)
//             {
//                 var cameraTransform = Camera.main.CopyCameraTransForm();
//                 // Graphics.Blit(webCamTexture, renderTexture);
//                 Graphics.Blit(webCamTexture, YoloInputTexture);

//                 await Task.Delay(32);

//                 // var texture = renderTexture.ToTexture2D();
//                 var texture = YoloInputTexture.ToTexture2D();

//                 await Task.Delay(32);

//                 var foundObjects = await yoloProcessor.RecognizeObjects(texture);

//                 ShowRecognitions(foundObjects, cameraTransform);
//                 Destroy(texture);
//                 Destroy(cameraTransform.gameObject);
//             }
//         }


//         private void ShowRecognitions(List<YoloItem> recognitions, Transform cameraTransform)
//         {
//             foreach (var recognition in recognitions)
//             {
//             var newObj = new YoloGameObject(recognition, cameraTransform,
//                         actualCameraSize, yoloImageSize, virtualProjectionPlaneWidth, yoloObjectCounter);
//                 if (newObj.PositionInSpace != null && !HasBeenSeenBefore(newObj))
//                 {
//                     yoloObjectCounter++;
//                     yoloGameObjects.Add(newObj);
//                     newObj.DisplayObject = Instantiate(labelObject,
//                         newObj.PositionInSpace.Value, Quaternion.identity);
//                     newObj.DisplayObject.transform.parent = transform;
//                     var labelController = newObj.DisplayObject.GetComponent<ObjectLabelController>();
//                     labelController.SetText(newObj.Name);
//                 }
//             }

//             for (var i = yoloGameObjects.Count - 1; i >= 0; i--)
//             {
//                 if (Time.time - yoloGameObjects[i].TimeLastSeen > labelNotSeenTimeOut)
//                 {
//                     Destroy(yoloGameObjects[i].DisplayObject);
//                     yoloGameObjects.RemoveAt(i);
//                 }
//             }
//         }

//         private bool HasBeenSeenBefore(YoloGameObject obj)
//         {
//             var objClassName = obj.Name.Split('_').LastOrDefault();
//             if (string.IsNullOrEmpty(objClassName)) return true;

//             var seenBefore = yoloGameObjects.FirstOrDefault(
//                 ylo => ylo.Name.EndsWith(objClassName) &&
//                 Vector3.Distance(obj.PositionInSpace.Value, ylo.PositionInSpace.Value) < minIdenticalLabelDistance);

//             if (seenBefore != null)
//             {
//                 seenBefore.TimeLastSeen = Time.time;
//             }
//             return seenBefore != null;
//         }
//     }
// }
// YoloObjectLabeler.cs (Advanced Dynamic Shelving Logic)
//----------------------------------------------------------------------------------------------------------
// using System;
// using System.Collections.Generic;
// using System.Linq;
// using System.Threading.Tasks;
// using RealityCollective.ServiceFramework.Services;
// using UnityEngine;
// using YoloHolo.Services;
// using YoloHolo.Utilities;

// namespace YoloHolo.YoloLabeling
// {
//     public class YoloObjectLabeler : MonoBehaviour
//     {
//         [Header("Label Settings")]
//         [SerializeField] private GameObject labelObject;
//         [SerializeField] private float minIdenticalLabelDistance = 0.3f;
//         [SerializeField] private float labelNotSeenTimeOut = 5f;

//         [Header("Dynamic Shelving Settings")]
//         [Tooltip("선반(마스크)을 시각적으로 표시할 프리팹")]
//         [SerializeField] private GameObject shelfVisualPrefab;

//         [Header("Fixed Layer Settings")]
//         [Tooltip("라벨을 정렬할 총 층의 개수")]
//         [SerializeField] private int layerCount = 4;

//         [Tooltip("각 층의 높이 (미터 단위)")]
//         [SerializeField] private float layerHeight = 0.2f; // 20cm

//         [Tooltip("새로운 선반으로 인식될 최소 거리 (미터)")]
//         [SerializeField] private float newShelfThreshold = 0.33f; // 33cm

//         [Tooltip("가장 낮은 선반과 높은 선반의 최대 높이 차이 (미터)")]
//         [SerializeField] private float maxTotalShelfHeight = 2.3f;

//         [Tooltip("기존 선반과 이 거리(미터) 이상 차이나는 새 객체는 생성하지 않습니다.")]
//         [SerializeField] private float maxNewObjectDistance = 0.2f;

//         [Header("YOLO & Camera Settings")]
//         [SerializeField] private int cameraFPS = 4;
//         [SerializeField] private Vector2Int requestedCameraSize = new(896, 504);
//         [SerializeField] private Vector2Int yoloImageSize = new(320, 256);
//         [SerializeField] private float virtualProjectionPlaneWidth = 1.356f;
//         [SerializeField] private Renderer debugRenderer;

//         // --- 내부 관리 변수 ---
//         private WebCamTexture webCamTexture;
//         private IYoloProcessor yoloProcessor;
//         private readonly List<YoloGameObject> yoloGameObjects = new();
//         private List<float> shelfHeights = new List<float>();
//         private Dictionary<float, GameObject> shelfVisuals = new Dictionary<float, GameObject>();
//         private int yoloObjectCounter = 1;
//         public RenderTexture YoloInputTexture { get; private set; }

//         private void Start()
//         {
//             yoloProcessor = ServiceManager.Instance.GetService<IYoloProcessor>();
//             webCamTexture = new WebCamTexture(requestedCameraSize.x, requestedCameraSize.y, cameraFPS);
//             webCamTexture.Play();
//             StartRecognizingAsync();
//         }

//         private async Task StartRecognizingAsync()
//         {
//             await Task.Delay(1000);
//             YoloInputTexture = new RenderTexture(yoloImageSize.x, yoloImageSize.y, 24);
//             if (debugRenderer != null) debugRenderer.material.mainTexture = YoloInputTexture;

//             while (true)
//             {
//                 var cameraTransform = Camera.main.CopyCameraTransform();
//                 Graphics.Blit(webCamTexture, YoloInputTexture);
//                 await Task.Delay(32);
//                 var texture = YoloInputTexture.ToTexture2D();
//                 await Task.Delay(32);
//                 var foundObjects = await yoloProcessor.RecognizeObjects(texture);
//                 ShowRecognitions(foundObjects, cameraTransform);
//                 Destroy(texture);
//                 Destroy(cameraTransform.gameObject);
//             }
//         }

//         private void ShowRecognitions(List<YoloItem> recognitions, Transform cameraTransform)
//         {
//             // 1. 새 객체 탐지 및 목록 관리
//             foreach (var recognition in recognitions)
//             {
//                 var newObj = new YoloGameObject(recognition, cameraTransform,
//                     new Vector2Int(webCamTexture.width, webCamTexture.height), yoloImageSize, virtualProjectionPlaneWidth, yoloObjectCounter);

//                 if (newObj.PositionInSpace == null || HasBeenSeenBefore(newObj)) continue;
                
//                 if (shelfHeights.Count > 0)
//                 {
//                     float objY = newObj.PositionInSpace.Value.y;
//                     // 가장 가까운 선반까지의 최소 거리를 계산
//                     float minDistanceToShelf = shelfHeights.Min(shelfY => Mathf.Abs(shelfY - objY));

//                     // 그 거리가 설정된 값(20cm)보다 크면, 이 객체를 무시하고 다음 객체로 넘어감
//                     if (minDistanceToShelf > maxNewObjectDistance)
//                     {
//                         // Debug.Log($"[Filtering] 객체({recognition.Label.Name}) 생성 취소. 가장 가까운 선반과의 거리({minDistanceToShelf}m)가 임계값({maxNewObjectDistance}m)을 초과했습니다.");
//                         continue;
//                     }
//                 }
//                 yoloObjectCounter++;
//                 yoloGameObjects.Add(newObj);
//                 newObj.DisplayObject = Instantiate(labelObject, newObj.PositionInSpace.Value, Quaternion.identity);
//                 newObj.DisplayObject.name = newObj.Name;
//                 newObj.DisplayObject.transform.parent = transform;
//                 newObj.DisplayObject.GetComponent<ObjectLabelController>().SetText(newObj.Name);
//             }

//             // 타임아웃 객체 제거
//             for (var i = yoloGameObjects.Count - 1; i >= 0; i--)
//             {
//                 if (Time.time - yoloGameObjects[i].TimeLastSeen > labelNotSeenTimeOut)
//                 {
//                     Destroy(yoloGameObjects[i].DisplayObject);
//                     yoloGameObjects.RemoveAt(i);
//                 }
//             }

//             if (yoloGameObjects.Count == 0)
//             {
//                 // 모든 객체가 사라지면 선반 정보도 초기화
//                 foreach (var visual in shelfVisuals.Values) Destroy(visual);
//                 shelfVisuals.Clear();
//                 shelfHeights.Clear();
//                 return;
//             }

//             // 2. 동적 선반(마스크) 계산 및 보정
//             // 2-1. 선반 초기화: 활성 객체는 있는데 선반이 없으면, 첫 객체 높이로 첫 선반 생성
//             if (shelfHeights.Count == 0 && yoloGameObjects.Count > 0)
//             {
//                 shelfHeights.Add(yoloGameObjects.Average(o => o.PositionInSpace.Value.y));
//             }

//             // 2-2. 객체를 가장 가까운 선반에 할당 (또는 새 선반 생성)
//             var shelfGroups = new Dictionary<int, List<YoloGameObject>>();
//             foreach (var obj in yoloGameObjects)
//             {
//                 float objY = obj.PositionInSpace.Value.y;
//                 int closestShelfIndex = -1;
//                 float minDistance = float.MaxValue;

//                 for (int i = 0; i < shelfHeights.Count; i++)
//                 {
//                     float distance = Mathf.Abs(shelfHeights[i] - objY);
//                     if (distance < minDistance)
//                     {
//                         minDistance = distance;
//                         closestShelfIndex = i;
//                     }
//                 }

//                 if (minDistance > newShelfThreshold) // 어떤 선반과도 가깝지 않으면 새 선반 생성
//                 {
//                     shelfHeights.Add(objY);
//                     closestShelfIndex = shelfHeights.Count - 1;
//                 }

//                 if (!shelfGroups.ContainsKey(closestShelfIndex))
//                     shelfGroups[closestShelfIndex] = new List<YoloGameObject>();
//                 shelfGroups[closestShelfIndex].Add(obj);
//             }

//             // 2-3. 각 선반의 높이를 해당 그룹의 평균 높이로 보정
//             var updatedShelfHeights = new List<float>();
//             foreach (var group in shelfGroups)
//             {
//                 if (group.Value.Count > 0)
//                 {
//                     updatedShelfHeights.Add(group.Value.Average(obj => obj.PositionInSpace.Value.y));
//                 }
//             }
//             shelfHeights = updatedShelfHeights;
//             shelfHeights.Sort();

//             // 2-4. 최대 높이(2.3m) 제한 적용 (압축 보정)
//             if (shelfHeights.Count > 1)
//             {
//                 float span = shelfHeights.Last() - shelfHeights.First();
//                 if (span > maxTotalShelfHeight)
//                 {
//                     float compressionFactor = maxTotalShelfHeight / span;
//                     float averageOfAllShelves = shelfHeights.Average();
//                     for (int i = 0; i < shelfHeights.Count; i++)
//                     {
//                         shelfHeights[i] = averageOfAllShelves + (shelfHeights[i] - averageOfAllShelves) * compressionFactor;
//                     }
//                 }
//             }

//             // 3. 라벨 및 마스크 시각적 업데이트
//             // 3-1. 라벨 위치를 최종 보정된 선반 높이로 업데이트
//             foreach (var group in shelfGroups)
//             {
//                 int shelfIndex = group.Key;
//                 // 그룹의 인덱스가 업데이트된 선반 높이 리스트 범위 내에 있는지 확인
//                 if (shelfIndex < shelfHeights.Count)
//                 {
//                     float finalShelfHeight = shelfHeights[shelfIndex];
//                     foreach (var obj in group.Value)
//                     {
//                         var currentPos = obj.DisplayObject.transform.position;
//                         obj.DisplayObject.transform.position = new Vector3(currentPos.x, finalShelfHeight, currentPos.z);
//                     }
//                 }
//             }

//             // 3-2. 마스크(선반 시각화 오브젝트) 업데이트
//             UpdateShelfVisuals(shelfGroups);
//         }

//         private void UpdateShelfVisuals(Dictionary<int, List<YoloGameObject>> shelfGroups)
//         {
//             if (shelfVisualPrefab == null) return;

//             var activeKeys = new HashSet<int>(shelfGroups.Keys);
//             var visualKeys = new List<int>(shelfVisuals.Keys.Cast<int>()); // Dictionary keys might not be float anymore if we use index

//             // 불필요한 마스크 제거
//             foreach (var key in visualKeys)
//             {
//                 if (!activeKeys.Contains(key))
//                 {
//                     Destroy(shelfVisuals[(float)key]);
//                     shelfVisuals.Remove((float)key);
//                 }
//             }

//             // 마스크 생성 및 위치 업데이트
//             foreach (var group in shelfGroups)
//             {
//                 int shelfIndex = group.Key;
//                 if (shelfIndex >= shelfHeights.Count) continue;

//                 float shelfY = shelfHeights[shelfIndex];

//                 // 그룹 내 객체들의 평균 3D 위치 계산
//                 float avgX = group.Value.Average(o => o.PositionInSpace.Value.x);
//                 float avgZ = group.Value.Average(o => o.PositionInSpace.Value.z);

//                 if (!shelfVisuals.ContainsKey(shelfIndex))
//                 {
//                     shelfVisuals[shelfIndex] = Instantiate(shelfVisualPrefab);
//                     shelfVisuals[shelfIndex].transform.parent = transform;
//                 }

//                 var visual = shelfVisuals[shelfIndex];
//                 visual.name = $"Shelf_Visual_{shelfIndex}";
//                 visual.transform.position = new Vector3(avgX, shelfY, avgZ);
//                 visual.transform.rotation = Quaternion.identity; // 수평 유지
//             }
//         }

//         private bool HasBeenSeenBefore(YoloGameObject obj)
//         {
//             var objClassName = obj.Name.Split('_').LastOrDefault();
//             if (string.IsNullOrEmpty(objClassName)) return true;
//             var seenBefore = yoloGameObjects.FirstOrDefault(
//                 ylo => ylo.Name.EndsWith(objClassName) &&
//                 Vector3.Distance(obj.PositionInSpace.Value, ylo.PositionInSpace.Value) < minIdenticalLabelDistance);
//             if (seenBefore != null)
//             {
//                 seenBefore.TimeLastSeen = Time.time;
//             }
//             return seenBefore != null;
//         }
//     }
// }
// YoloObjectLabeler.cs (Modified for Fixed Layer-Based Sorting)
//----------------------------------------------------------------------------------------------------------
// YoloObjectLabeler.cs (Modified for Dynamic Spacing with Min/Max Constraints)
//----------------------------------------------------------------------------------------------------------
// YoloObjectLabeler.cs (Added Max Height Limit Filter)
//----------------------------------------------------------------------------------------------------------
// using System;
// using System.Collections.Generic;
// using System.Linq;
// using System.Threading.Tasks;
// using RealityCollective.ServiceFramework.Services;
// using UnityEngine;
// using YoloHolo.Services;
// using YoloHolo.Utilities;

// namespace YoloHolo.YoloLabeling
// {
//     public class YoloObjectLabeler : MonoBehaviour
//     {
//         [Header("Label Settings")]
//         [SerializeField] private GameObject labelObject;
//         [SerializeField] private float minIdenticalLabelDistance = 0.3f;
//         [SerializeField] private float labelNotSeenTimeOut = 5f;

//         [Header("Dynamic Spacing Settings")]
//         [Tooltip("객체들을 하나의 그룹으로 묶을 최대 거리")]
//         [SerializeField] private float initialGroupingDistance = 0.2f;

//         [Tooltip("선반과 선반 사이의 최소 간격 (미터)")]
//         [SerializeField] private float minShelfSpacing = 0.28f;

//         [Tooltip("선반과 선반 사이의 최대 간격 (미터)")]
//         [SerializeField] private float maxShelfSpacing = 0.36f;
        

//         [Header("Height Limit Settings")]
//         [Tooltip("인식 시작 시점의 카메라 높이로부터 허용할 최대 높이(미터)")]
//         [SerializeField] private float maxRelativeHeight = 1.0f;
//         [Header("Virtual Plane Settings")]


//         [Header("YOLO & Camera Settings")]
//         [SerializeField] private int cameraFPS = 4;
//         [SerializeField] private Vector2Int requestedCameraSize = new(896, 504);
//         [SerializeField] private Vector2Int yoloImageSize = new(320, 256);
//         [SerializeField] private float virtualProjectionPlaneWidth = 1.356f;
//         [SerializeField] private Renderer debugRenderer;

//         // --- 내부 관리 변수 ---
//         private WebCamTexture webCamTexture;
//         private IYoloProcessor yoloProcessor;
//         private readonly List<YoloGameObject> yoloGameObjects = new();
//         private int yoloObjectCounter = 1;
//         private float startingCameraY; // 시작 시점 카메라 높이를 저장할 변수
//         private bool isInitialized = false;
//         public RenderTexture YoloInputTexture { get; private set; }

//         private void Start()
//         {
//             yoloProcessor = ServiceManager.Instance.GetService<IYoloProcessor>();
//             webCamTexture = new WebCamTexture(requestedCameraSize.x, requestedCameraSize.y, cameraFPS);
//             webCamTexture.Play();
//             StartRecognizingAsync();
//         }

//         private async Task StartRecognizingAsync()
//         {
//             await Task.Delay(1000);
//             YoloInputTexture = new RenderTexture(yoloImageSize.x, yoloImageSize.y, 24);
//             if (debugRenderer != null) debugRenderer.material.mainTexture = YoloInputTexture;

//             while (true)
//             {
//                 var cameraTransform = Camera.main.CopyCameraTransform();
                
//                 // 최초 한 번만 시작 높이를 기록
//                 if (!isInitialized)
//                 {
//                     startingCameraY = cameraTransform.position.y;
//                     isInitialized = true;
//                     Debug.Log($"[Height Limit] 기준 높이 설정 완료: {startingCameraY}m");
//                 }

//                 Graphics.Blit(webCamTexture, YoloInputTexture);
//                 await Task.Delay(32);
//                 var texture = YoloInputTexture.ToTexture2D();
//                 await Task.Delay(32);
//                 var foundObjects = await yoloProcessor.RecognizeObjects(texture);
//                 ShowRecognitions(foundObjects, cameraTransform);
//                 Destroy(texture);
//                 Destroy(cameraTransform.gameObject);
//             }
//         }

//         private void ShowRecognitions(List<YoloItem> recognitions, Transform cameraTransform)
//         {
//             // 1. 새 객체 탐지 및 목록 관리
//             foreach (var recognition in recognitions)
//             {
//                 var newObj = new YoloGameObject(recognition, cameraTransform,
//                     new Vector2Int(webCamTexture.width, webCamTexture.height), yoloImageSize, virtualProjectionPlaneWidth, yoloObjectCounter);

//                 if (newObj.PositionInSpace == null || HasBeenSeenBefore(newObj))
//                 {
//                     continue;
//                 }


//                 // 객체의 높이가 (시작 높이 + 최대 허용 높이)를 초과하면 생성하지 않고 건너뜀
//                 if (newObj.PositionInSpace.Value.y > (startingCameraY + maxRelativeHeight))
//                 {
//                     // Debug.Log($"객체 생성 취소: 높이 제한({startingCameraY + maxRelativeHeight}m)을 초과했습니다. (객체 높이: {newObj.PositionInSpace.Value.y}m)");
//                     continue;
//                 }


//                 yoloObjectCounter++;
//                 yoloGameObjects.Add(newObj);
//                 newObj.DisplayObject = Instantiate(labelObject, newObj.PositionInSpace.Value, Quaternion.identity);
//                 newObj.DisplayObject.name = newObj.Name;
//                 newObj.DisplayObject.transform.parent = transform;
//                 newObj.DisplayObject.GetComponent<ObjectLabelController>().SetText(newObj.Name);
//             }

//             // (이하 로직은 이전과 동일합니다)
//             // 2. 타임아웃 객체 제거
//             for (var i = yoloGameObjects.Count - 1; i >= 0; i--)
//             {
//                 if (Time.time - yoloGameObjects[i].TimeLastSeen > labelNotSeenTimeOut)
//                 {
//                     Destroy(yoloGameObjects[i].DisplayObject);
//                     yoloGameObjects.RemoveAt(i);
//                 }
//             }

//             if (yoloGameObjects.Count == 0) return;
//             // ▼▼▼▼▼ 3-A. 가상 평면 보정 기능 호출 (이 부분을 추가) ▼▼▼▼▼
//             UpdateAndCorrectWithVirtualPlane();
//             // 3. 객체들을 높이 순으로 정렬
//             var sortedObjects = yoloGameObjects.OrderBy(o => o.PositionInSpace.Value.y).ToList();

//             // 4. 높이 기반으로 객체 그룹화하여 초기 선반 위치 계산
//             var initialShelfHeights = new List<float>();
//             if (sortedObjects.Count > 0)
//             {
//                 List<YoloGameObject> currentGroup = new List<YoloGameObject> { sortedObjects[0] };

//                 for (int i = 1; i < sortedObjects.Count; i++)
//                 {
//                     float groupAverageY = currentGroup.Average(o => o.PositionInSpace.Value.y);
//                     if (Mathf.Abs(sortedObjects[i].PositionInSpace.Value.y - groupAverageY) > initialGroupingDistance)
//                     {
//                         initialShelfHeights.Add(groupAverageY);
//                         currentGroup.Clear();
//                     }
//                     currentGroup.Add(sortedObjects[i]);
//                 }
//                 initialShelfHeights.Add(currentGroup.Average(o => o.PositionInSpace.Value.y));
//             }

//             // 5. 계산된 선반들의 간격을 최소/최대 범위에 맞게 강제 조정
//             var finalShelfHeights = new List<float>();
//             if (initialShelfHeights.Count > 0)
//             {
//                 finalShelfHeights.Add(initialShelfHeights[0]);
//                 for (int i = 1; i < initialShelfHeights.Count; i++)
//                 {
//                     float previousFinalY = finalShelfHeights.Last();
//                     float currentInitialY = initialShelfHeights[i];
                    
//                     float spacing = currentInitialY - previousFinalY;
//                     float clampedSpacing = Mathf.Clamp(spacing, minShelfSpacing, maxShelfSpacing);
                    
//                     float newFinalY = previousFinalY + clampedSpacing;
//                     finalShelfHeights.Add(newFinalY);
//                 }
//             }
            
//             // 6. 모든 객체들을 조정이 완료된 최종 선반들 중 가장 가까운 곳에 정렬
//             foreach (var yoloObject in yoloGameObjects)
//             {
//                 if (yoloObject.PositionInSpace == null || finalShelfHeights.Count == 0) continue;

//                 var originalPosition = yoloObject.PositionInSpace.Value;
                
//                 float closestShelfY = finalShelfHeights.OrderBy(y => Mathf.Abs(y - originalPosition.y)).First();
                
//                 yoloObject.DisplayObject.transform.position = new Vector3(originalPosition.x, closestShelfY, originalPosition.z);
//             }
//         }


//         /// <summary>
//         /// 3개 이상의 객체로 가상의 수직 평면을 만들고, 모든 객체의 위치를 해당 평면에 맞게 보정합니다.
//         /// </summary>
//         private void UpdateAndCorrectWithVirtualPlane()
//         {
//             // 1. 평면을 계산하는 데 사용할 유효한 포인트 목록을 준비
//             var pointsForPlane = new List<Vector3>();

//             foreach (var obj in yoloGameObjects)
//             {
//                 // 평면이 이미 생성되었다면, 이상치(outlier)를 필터링
//                 if (hasPlane)
//                 {
//                     // 객체와 평면 사이의 거리를 계산
//                     float distanceToPlane = Mathf.Abs(virtualPlane.GetDistanceToPoint(obj.DisplayObject.transform.position));

//                     // 거리가 임계값보다 작을 경우에만 평면 계산에 포함
//                     if (distanceToPlane <= planeOutlierThreshold)
//                     {
//                         pointsForPlane.Add(obj.DisplayObject.transform.position);
//                     }
//                 }
//                 else
//                 {
//                     // 첫 평면 생성 시에는 모든 포인트를 사용
//                     pointsForPlane.Add(obj.DisplayObject.transform.position);
//                 }
//             }

//             // 2. 유효한 포인트가 최소 요구치보다 적으면 평면을 계산/업데이트하지 않음
//             if (pointsForPlane.Count < minPointsForPlane)
//             {
//                 return;
//             }

//             // 3. 새 가상 평면 계산
//             // 3-1. 모든 포인트의 평균 위치(중심점)를 계산
//             Vector3 centroid = Vector3.zero;
//             foreach (var p in pointsForPlane) centroid += p;
//             centroid /= pointsForPlane.Count;

//             // 3-2. 포인트들의 주 방향(왼쪽 끝 -> 오른쪽 끝)을 계산하여 평면의 방향으로 설정
//             var sortedPoints = pointsForPlane.OrderBy(p => p.x).ToList();
//             Vector3 planeDirection = (sortedPoints.Last() - sortedPoints.First()).normalized;
//             planeDirection.y = 0; // 바닥과 수직이 되도록 Y축 방향 성분 제거

//             // 3-3. 평면의 법선 벡터(Normal Vector) 계산 (방향벡터와 수직인 벡터)
//             Vector3 planeNormal = Vector3.Cross(planeDirection, Vector3.up).normalized;

//             // 3-4. 최종 평면 업데이트
//             virtualPlane.SetNormalAndPosition(planeNormal, centroid);
//             hasPlane = true;

//             // 4. 모든 객체를 계산된 평면에 투영하여 위치 보정
//             foreach (var yoloObject in yoloGameObjects)
//             {
//                 var currentPos = yoloObject.DisplayObject.transform.position;
//                 // 객체의 현재 위치에서 평면에 가장 가까운 점의 위치를 계산
//                 Vector3 projectedPos = virtualPlane.ClosestPointOnPlane(currentPos);
                
//                 // Y값(높이)은 이후에 실행될 높이 보정 로직을 위해 유지하고, X와 Z값만 보정된 값으로 변경
//                 yoloObject.DisplayObject.transform.position = new Vector3(projectedPos.x, currentPos.y, projectedPos.z);
//             }
//         }
//         private bool HasBeenSeenBefore(YoloGameObject obj)
//         {
//             var objClassName = obj.Name.Split('_').LastOrDefault();
//             if (string.IsNullOrEmpty(objClassName)) return true;
            
//             var seenBefore = yoloGameObjects.FirstOrDefault(
//                 ylo => ylo.Name.EndsWith(objClassName) &&
//                 Vector3.Distance(obj.PositionInSpace.Value, ylo.PositionInSpace.Value) < minIdenticalLabelDistance);
            
//             if (seenBefore != null)
//             {
//                 seenBefore.TimeLastSeen = Time.time;
//             }
//             return seenBefore != null;
//         }
//     }
// }// YoloObjectLabeler.cs (Plane Locking Logic Applied)
//========================================================================================================================
// using System;
// using System.Collections.Generic;
// using System.Linq;
// using System.Threading.Tasks;
// using RealityCollective.ServiceFramework.Services;
// using UnityEngine;
// using YoloHolo.Services;
// using YoloHolo.Utilities;

// namespace YoloHolo.YoloLabeling
// {
//     public class YoloObjectLabeler : MonoBehaviour
//     {
//         [Header("Label Settings")]
//         [SerializeField] private GameObject labelObject;
//         [SerializeField] private float minIdenticalLabelDistance = 0.3f;
//         [SerializeField] private float labelNotSeenTimeOut = 5f;

//         [Header("Dynamic Spacing Settings")]
//         [SerializeField] private float initialGroupingDistance = 0.2f;
//         [SerializeField] private float minShelfSpacing = 0.28f;
//         [SerializeField] private float maxShelfSpacing = 0.36f;
//         [SerializeField] private int maxShelfCount = 5;
//         [Header("Height Limit Settings")]
//         [SerializeField] private float maxRelativeHeight = 1.0f;
        
//         [Header("Creation Constraints")]
//         [Tooltip("앱 시작 위치로부터 라벨이 생성될 수 있는 최대 거리(미터)")]
//         [SerializeField] private float maxCreationDistance = 0.5f;
//         [Header("Virtual Plane Settings")]
//         [SerializeField] private int minPointsForPlane = 3;
//         [SerializeField] private float planeOutlierThreshold = 0.1f; // 10cm

//         [Header("YOLO & Camera Settings")]
//         [SerializeField] private int cameraFPS = 4;
//         [SerializeField] private Vector2Int requestedCameraSize = new(896, 504);
//         [SerializeField] private Vector2Int yoloImageSize = new(320, 256);
//         [SerializeField] private float virtualProjectionPlaneWidth = 1.356f;
//         [SerializeField] private Renderer debugRenderer;
//         [Tooltip("앱 시작 후 위치 보정을 활성화할 시간(초)")]
//         [SerializeField] private float correctionDuration = 5f;
//         // --- 내부 관리 변수 ---
//         private WebCamTexture webCamTexture;
//         private IYoloProcessor yoloProcessor;
//         private readonly List<YoloGameObject> yoloGameObjects = new();
//         private int yoloObjectCounter = 1;
//         private float startingCameraY;
//         private bool isInitialized = false;
//         public RenderTexture YoloInputTexture { get; private set; }
//         private bool allowPositionCorrection = true;

//         // ▼▼▼ 1. 평면 정보를 영구적으로 저장하기 위해 static으로 변경 ▼▼▼
//         // private static Plane virtualPlane;
//         // private static bool hasPlane = false;


//         // ▼▼▼ [수정] 외부에서 호출할 수 있는 static 초기화 메서드 추가 ▼▼▼
//         /// <summary>
//         /// 모든 static 변수를 초기 상태로 리셋합니다.
//         /// MainController가 시작될 때 호출하여 이전 실행 정보를 삭제합니다.
//         /// </summary>
//         /// 
//         private void Start()
//         {
//             yoloProcessor = ServiceManager.Instance.GetService<IYoloProcessor>();
//             webCamTexture = new WebCamTexture(requestedCameraSize.x, requestedCameraSize.y, cameraFPS);
//             webCamTexture.Play();
//             StartRecognizingAsync();
//             StartCoroutine(StopCorrectionAfterDelay(correctionDuration));
//         }

//         private async Task StartRecognizingAsync()
//         {
//             await Task.Delay(1000);
//             YoloInputTexture = new RenderTexture(yoloImageSize.x, yoloImageSize.y, 24);
//             if (debugRenderer != null) debugRenderer.material.mainTexture = YoloInputTexture;

//             while (true)
//             {
//                 if (!isInitialized)
//                 {
//                     startingCameraY = Camera.main.transform.position.y;
//                     isInitialized = true;
//                     Debug.Log($"[Height Limit] 기준 높이 설정 완료: {startingCameraY}m");
//                 }

//                 Graphics.Blit(webCamTexture, YoloInputTexture);
//                 await Task.Delay(32);
//                 var texture = YoloInputTexture.ToTexture2D();
//                 await Task.Delay(32);
//                 var foundObjects = await yoloProcessor.RecognizeObjects(texture);
//                 ShowRecognitions(foundObjects);
//                 Destroy(texture);
//             }
//         }

//         private void ShowRecognitions(List<YoloItem> recognitions)
//         {
//             var cameraTransform = Camera.main.transform;

//             // 1. 새 객체 탐지 및 필터링
//             foreach (var recognition in recognitions)
//             {
//                 var newObj = new YoloGameObject(recognition, cameraTransform,
//                     new Vector2Int(webCamTexture.width, webCamTexture.height), yoloImageSize, virtualProjectionPlaneWidth, yoloObjectCounter);

//                 // 1-1. SphereCast로 계산된 3D 위치가 없으면 건너뜀
//                 if (newObj.PositionInSpace == null) continue;

//                 var rawPosition = newObj.PositionInSpace.Value;

//                 // 1-2. 근처에 중복된 객체가 있으면 건너뜀
//                 if (HasBeenSeenBefore(newObj)) continue;
                
//                 // 1-3. 높이 제한 필터링
//                 if (newObj.PositionInSpace.Value.y > (startingCameraY + maxRelativeHeight)) continue;
                
//                 // 모든 필터를 통과하면 객체 생성
//                 yoloObjectCounter++;
//                 yoloGameObjects.Add(newObj);
//                 newObj.DisplayObject = Instantiate(labelObject, newObj.PositionInSpace.Value, Quaternion.identity);
//                 newObj.DisplayObject.name = newObj.Name;
//                 newObj.DisplayObject.transform.parent = transform;
//                 newObj.DisplayObject.GetComponent<ObjectLabelController>().SetText(newObj.Name);
//             }

//             // 2. 타임아웃 객체 제거
//             for (var i = yoloGameObjects.Count - 1; i >= 0; i--)
//             {
//                 if (Time.time - yoloGameObjects[i].TimeLastSeen > labelNotSeenTimeOut)
//                 {
//                     Destroy(yoloGameObjects[i].DisplayObject);
//                     yoloGameObjects.RemoveAt(i);
//                 }
//             }

//             if (yoloGameObjects.Count == 0) return;
//             if (allowPositionCorrection)
//             {
//                 UpdateAndCorrectPositions();
//             }

//             // 3. 평면 및 높이 보정
//             UpdateAndCorrectPositions();
//         }
        

//         private void UpdateAndCorrectPositions()
//         {
            
//             // 3-C. 높이(Y축) 보정 (기존 로직과 동일)
//             var sortedObjects = yoloGameObjects.OrderBy(o => o.DisplayObject.transform.position.y).ToList();
//             var initialShelfHeights = new List<float>();
//             if (sortedObjects.Count > 0)
//             {
//                 List<YoloGameObject> currentGroup = new List<YoloGameObject> { sortedObjects[0] };
//                 for (int i = 1; i < sortedObjects.Count; i++)
//                 {
//                     float groupAverageY = currentGroup.Average(o => o.DisplayObject.transform.position.y);
//                     if (Mathf.Abs(sortedObjects[i].DisplayObject.transform.position.y - groupAverageY) > initialGroupingDistance)
//                     {
//                         initialShelfHeights.Add(groupAverageY);
//                         currentGroup.Clear();
//                     }
//                     currentGroup.Add(sortedObjects[i]);
//                 }
//                 initialShelfHeights.Add(currentGroup.Average(o => o.DisplayObject.transform.position.y));
//             }
            
//             var finalShelfHeights = new List<float>();
//             if (initialShelfHeights.Count > 0)
//             {
//                 finalShelfHeights.Add(initialShelfHeights[0]);
//                 for (int i = 1; i < initialShelfHeights.Count; i++)
//                 {
//                     float clampedSpacing = Mathf.Clamp(initialShelfHeights[i] - finalShelfHeights.Last(), minShelfSpacing, maxShelfSpacing);
//                     finalShelfHeights.Add(finalShelfHeights.Last() + clampedSpacing);
//                 }
//             }

//             foreach (var yoloObject in yoloGameObjects)
//             {
//                 if (finalShelfHeights.Count == 0) continue;
//                 var currentPos = yoloObject.DisplayObject.transform.position;
//                 float closestShelfY = finalShelfHeights.OrderBy(y => Mathf.Abs(y - currentPos.y)).First();
//                 yoloObject.DisplayObject.transform.position = new Vector3(currentPos.x, closestShelfY, currentPos.z);
//             }
//         }

//         private bool HasBeenSeenBefore(YoloGameObject obj)
//         {
//             var objClassName = obj.Name.Split('_').LastOrDefault();
//             if (string.IsNullOrEmpty(objClassName)) return true;
            
//             var seenBefore = yoloGameObjects.FirstOrDefault(
//                 ylo => ylo.Name.EndsWith(objClassName) &&
//                 Vector3.Distance(obj.PositionInSpace.Value, ylo.PositionInSpace.Value) < minIdenticalLabelDistance);
            
//             if (seenBefore != null)
//             {
//                 seenBefore.TimeLastSeen = Time.time;
//             }
//             return seenBefore != null;
//         }
//     }
// }
// // YoloObjectLabeler.cs (Advanced Dynamic Shelving Logic)
// //----------------------------------------------------------------------------------------------------------
// // 파일: YoloObjectLabeler.cs (2025-09-15 최종 수정본)
// using System;
// using System.Collections.Generic;
// using System.Linq;
// using System.Threading.Tasks;
// using RealityCollective.ServiceFramework.Services;
// using UnityEngine;
// using YoloHolo.Services;
// using YoloHolo.Utilities;

// namespace YoloHolo.YoloLabeling
// {
//     public class YoloObjectLabeler : MonoBehaviour
//     {
//         [Header("Creation Constraints")]
//         [Tooltip("앱 시작 위치로부터 라벨이 생성될 수 있는 최대 거리(미터)")]
//         // [SerializeField] private float maxCreationDistance = 2.0f; // 2m로 기본값 설정

//         [Header("Label Settings")]
//         [SerializeField] private GameObject labelObject;
//         [SerializeField] private float minIdenticalLabelDistance = 0.3f;
//         [SerializeField] private float labelNotSeenTimeOut = 5f;

//         [Header("Dynamic Spacing Settings")]
//         [SerializeField] private float initialGroupingDistance = 0.2f;
//         [SerializeField] private float minShelfSpacing = 0.28f;
//         [SerializeField] private float maxShelfSpacing = 0.36f;

//         [Header("Height Limit Settings")]
//         [SerializeField] private float maxRelativeHeight = 1.0f;

//         [Header("Virtual Plane Settings")]
//         [SerializeField] private int minPointsForPlane = 3;
//         [SerializeField] private float planeOutlierThreshold = 0.1f; // 10cm

//         [Header("YOLO & Camera Settings")]
//         [SerializeField] private int cameraFPS = 4;
//         [SerializeField] private Vector2Int requestedCameraSize = new(896, 504);
//         [SerializeField] private Vector2Int yoloImageSize = new(320, 256);
//         [SerializeField] private float virtualProjectionPlaneWidth = 1.356f;
//         [SerializeField] private Renderer debugRenderer;

//         // --- 내부 관리 변수 ---
//         private WebCamTexture webCamTexture;
//         private IYoloProcessor yoloProcessor;
//         private readonly List<YoloGameObject> yoloGameObjects = new();
//         private int yoloObjectCounter = 1;
//         private float startingCameraY;
//         private bool isInitialized = false;
//         public RenderTexture YoloInputTexture { get; private set; }
//         private Vector3 sessionStartPosition; // 앱 시작 시점의 위치
//         private bool isFirstShelfLocked = false; // 첫 선반이 고정되었는지 여부
//         private float lockedShelfHeight = 0f; // 고정된 첫 선반의 높이
        
//         // ▼▼▼ 1. 평면 정보를 영구적으로 저장하기 위해 static으로 변경 ▼▼▼


//         // ▼▼▼ [수정] 외부에서 호출할 수 있는 static 초기화 메서드 추가 ▼▼▼
//         /// <summary>
//         /// 모든 static 변수를 초기 상태로 리셋합니다.
//         /// MainController가 시작될 때 호출하여 이전 실행 정보를 삭제합니다.
//         /// </summary>
//         private void Start()
//         {
//             yoloProcessor = ServiceManager.Instance.GetService<IYoloProcessor>();
//             webCamTexture = new WebCamTexture(requestedCameraSize.x, requestedCameraSize.y, cameraFPS);
//             webCamTexture.Play();
//             StartRecognizingAsync();
//         }

//         private async Task StartRecognizingAsync()
//         {
//             await Task.Delay(1000);
//             YoloInputTexture = new RenderTexture(yoloImageSize.x, yoloImageSize.y, 24);
//             if (debugRenderer != null) debugRenderer.material.mainTexture = YoloInputTexture;

//             while (true)
//             {
//                 if (!isInitialized)
//                 {
//                     startingCameraY = Camera.main.transform.position.y;
//                     // sessionStartPosition = Camera.main.transform.position;
//                     isInitialized = true;
//                     Debug.Log($"[Height Limit] 기준 높이 설정 완료: {startingCameraY}m");
//                 }

//                 Graphics.Blit(webCamTexture, YoloInputTexture);
//                 await Task.Delay(32);
//                 var texture = YoloInputTexture.ToTexture2D();
//                 await Task.Delay(32);
//                 var foundObjects = await yoloProcessor.RecognizeObjects(texture);
//                 ShowRecognitions(foundObjects);
//                 Destroy(texture);
//             }
//         }

//         private void ShowRecognitions(List<YoloItem> recognitions)
//         {
//             var cameraTransform = Camera.main.transform;

//             // 1. 새 객체 탐지 및 필터링
//             foreach (var recognition in recognitions)
//             {
//                 var newObj = new YoloGameObject(recognition, cameraTransform,
//                     new Vector2Int(webCamTexture.width, webCamTexture.height), yoloImageSize, virtualProjectionPlaneWidth, yoloObjectCounter);

//                 // 1-1. SphereCast로 계산된 3D 위치가 없으면 건너뜀
//                 if (newObj.PositionInSpace == null) continue;

//                 var rawPosition = newObj.PositionInSpace.Value;
//                 if (isFirstShelfLocked && rawPosition.y < lockedShelfHeight - initialGroupingDistance)
//                 {
//                     continue;
//                 }
//                 // 1-2. 근처에 중복된 객체가 있으면 건너뜀
//                 if (HasBeenSeenBefore(newObj)) continue;
                
//                 // 1-3. 높이 제한 필터링
//                 if (newObj.PositionInSpace.Value.y > (startingCameraY + maxRelativeHeight)) continue;
                
//                 // 모든 필터를 통과하면 객체 생성
//                 yoloObjectCounter++;
//                 yoloGameObjects.Add(newObj);
//                 newObj.DisplayObject = Instantiate(labelObject, newObj.PositionInSpace.Value, Quaternion.identity);
//                 newObj.DisplayObject.name = newObj.Name;
//                 newObj.DisplayObject.transform.parent = transform;
//                 newObj.DisplayObject.GetComponent<ObjectLabelController>().SetText(newObj.Name);
//             }

//             // 2. 타임아웃 객체 제거
//             for (var i = yoloGameObjects.Count - 1; i >= 0; i--)
//             {
//                 if (Time.time - yoloGameObjects[i].TimeLastSeen > labelNotSeenTimeOut)
//                 {
//                     Destroy(yoloGameObjects[i].DisplayObject);
//                     yoloGameObjects.RemoveAt(i);
//                 }
//             }

//             if (yoloGameObjects.Count == 0) return;

//             // 3. 평면 및 높이 보정
//             UpdateAndCorrectPositions();
//         }
        

//         private void UpdateAndCorrectPositions()
//         {
            
//             // 3-C. 높이(Y축) 보정 (기존 로직과 동일)
//             var sortedObjects = yoloGameObjects.OrderBy(o => o.DisplayObject.transform.position.y).ToList();
//             var initialShelfHeights = new List<float>();
//             if (sortedObjects.Count > 0)
//             {
//                 List<YoloGameObject> currentGroup = new List<YoloGameObject> { sortedObjects[0] };
//                 for (int i = 1; i < sortedObjects.Count; i++)
//                 {
//                     float groupAverageY = currentGroup.Average(o => o.DisplayObject.transform.position.y);
//                     if (Mathf.Abs(sortedObjects[i].DisplayObject.transform.position.y - groupAverageY) > initialGroupingDistance)
//                     {
//                         initialShelfHeights.Add(groupAverageY);
//                         currentGroup.Clear();
//                     }
//                     currentGroup.Add(sortedObjects[i]);
//                 }
//                 initialShelfHeights.Add(currentGroup.Average(o => o.DisplayObject.transform.position.y));
//             }
            
//             var finalShelfHeights = new List<float>();
//             if (initialShelfHeights.Count > 0)
//             {
//                 finalShelfHeights.Add(initialShelfHeights[0]);
//                 for (int i = 1; i < initialShelfHeights.Count; i++)
//                 {
//                     float clampedSpacing = Mathf.Clamp(initialShelfHeights[i] - finalShelfHeights.Last(), minShelfSpacing, maxShelfSpacing);
//                     finalShelfHeights.Add(finalShelfHeights.Last() + clampedSpacing);
//                 }
//             }
        
//             if (!isFirstShelfLocked && finalShelfHeights.Count > 0)
//             {
//                 lockedShelfHeight = finalShelfHeights.First(); // First()는 정렬된 리스트의 가장 낮은 값
//                 isFirstShelfLocked = true;
//                 Debug.Log($"[Shelving] 첫 번째 선반 위치를 {lockedShelfHeight}m로 고정합니다.");
//             }

//             // 2. 첫 선반이 고정된 이후라면, 매번 계산되는 선반들의 높이를 '고정 선반'에 맞춰 전체적으로 이동
//             if (!isFirstShelfLocked && finalShelfHeights.Count > 0)
//             {
//                 lockedShelfHeight = finalShelfHeights.First();
//                 isFirstShelfLocked = true;
//                 Debug.Log($"[Shelving] 첫 번째 선반 위치를 {lockedShelfHeight:F3}m로 고정합니다.");
//             }

//             // 4. 고정된 이후, 기준 선반에 맞춰 전체 높이 보정
//             if (isFirstShelfLocked && finalShelfHeights.Count > 0)
//             {
//                 // 현재 계산된 선반들 중, 고정된 높이와 가장 가까운 선반을 찾음
//                 float closestShelfToLock = finalShelfHeights.OrderBy(y => Mathf.Abs(y - lockedShelfHeight)).First();
                
//                 // 이 선반이 고정 높이에 위치하도록 전체를 이동시킬 오프셋 계산
//                 float offset = lockedShelfHeight - closestShelfToLock;

//                 // 모든 선반 높이에 오프셋을 적용
//                 for (int i = 0; i < finalShelfHeights.Count; i++)
//                 {
//                     finalShelfHeights[i] += offset;
//                 }
//             }
//             foreach (var yoloObject in yoloGameObjects)
//             {
//                 if (finalShelfHeights.Count == 0) continue;
//                 var currentPos = yoloObject.DisplayObject.transform.position;
//                 float closestShelfY = finalShelfHeights.OrderBy(y => Mathf.Abs(y - currentPos.y)).First();
//                 yoloObject.DisplayObject.transform.position = new Vector3(currentPos.x, closestShelfY, currentPos.z);
//             }
//         }

//         private bool HasBeenSeenBefore(YoloGameObject obj)
//         {
//             var objClassName = obj.Name.Split('_').LastOrDefault();
//             if (string.IsNullOrEmpty(objClassName)) return true;
            
//             var seenBefore = yoloGameObjects.FirstOrDefault(
//                 ylo => ylo.Name.EndsWith(objClassName) &&
//                 Vector3.Distance(obj.PositionInSpace.Value, ylo.PositionInSpace.Value) < minIdenticalLabelDistance);
            
//             if (seenBefore != null)
//             {
//                 seenBefore.TimeLastSeen = Time.time;
//             }
//             return seenBefore != null;
//         }
//     }
// }// 파일: YoloObjectLabeler.cs (보정 기능 제거, 거리/중복 필터만 적용)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RealityCollective.ServiceFramework.Services;
using UnityEngine;
using YoloHolo.Services;
using YoloHolo.Utilities;

namespace YoloHolo.YoloLabeling
{
    public class YoloObjectLabeler : MonoBehaviour
    {
        [Header("Label Settings")]
        [SerializeField] private GameObject labelObject;
        [Tooltip("이 거리(미터) 안에 같은 종류의 라벨이 이미 있으면 새로 생성하지 않습니다.")]
        [SerializeField] private float minIdenticalLabelDistance = 0.2f;
        [Tooltip("라벨이 보이지 않을 때 사라지기까지의 시간(초)")]
        [SerializeField] private float labelNotSeenTimeOut = 5f;

        [Header("Creation Constraints")]
        [Tooltip("인식 시작 시점의 카메라로부터 허용할 최대 상대 높이(미터)")]
        [SerializeField] private float maxRelativeHeight = 1.0f;
        [Tooltip("앱 시작 위치로부터 라벨이 생성될 수 있는 최대 거리(미터)")]
        [SerializeField] private float maxCreationDistance = 2.0f;

        [Header("YOLO & Camera Settings")]
        [SerializeField] private int cameraFPS = 4;
        [SerializeField] private Vector2Int requestedCameraSize = new(896, 504);
        [SerializeField] public Vector2Int yoloImageSize = new(320, 256);
        [SerializeField] private float virtualProjectionPlaneWidth = 1.356f;
        [SerializeField] private Renderer debugRenderer;

        // --- 내부 관리 변수 ---
        private WebCamTexture webCamTexture;
        private IYoloProcessor yoloProcessor;
        private readonly List<YoloGameObject> yoloGameObjects = new();
        private int yoloObjectCounter = 1;
        private float startingCameraY;
        private bool isInitialized = false;
        public RenderTexture YoloInputTexture { get; private set; }
        public List<YoloItem> LastDetections { get; private set; } = new List<YoloItem>();
        private Vector3 sessionStartPosition;
        public WebCamTexture OriginalWebCamTexture => webCamTexture;
        private void Start()
        {
            yoloProcessor = ServiceManager.Instance.GetService<IYoloProcessor>();
            if (yoloProcessor == null)
            {
                Debug.LogError("Failed to get IYoloProcessor service. Aborting YoloObjectLabeler start.");
                return;
            }

            webCamTexture = new WebCamTexture(requestedCameraSize.x, requestedCameraSize.y, cameraFPS);
            webCamTexture.Play();
            
            // Check if webcam started playing (might take a frame, but Play() is usually async start)
            // Ideally we check isPlaying in coroutine, but at least don't crash if texture is invalid?
            // Existing logic says 'Couldn't config the stream!', likely internal Unity error.
            
            _ = StartRecognizingAsync();
        }
        public GameObject FindTrackedObjectByClassName(string className)
        {
            var foundObject = yoloGameObjects.FirstOrDefault(obj => obj.Name.StartsWith(className));
            return foundObject?.DisplayObject;
        }
        private async Task StartRecognizingAsync()
        {
            await Task.Delay(1000);
            YoloInputTexture = new RenderTexture(yoloImageSize.x, yoloImageSize.y, 24);
            if (debugRenderer != null) debugRenderer.material.mainTexture = YoloInputTexture;

            while (true)
            {
                if (!isInitialized)
                {
                    startingCameraY = Camera.main.transform.position.y;
                    sessionStartPosition = Camera.main.transform.position; // 시작 위치 기록
                    isInitialized = true;
                    Debug.Log($"[Constraints] 기준 높이: {startingCameraY}m, 기준 위치: {sessionStartPosition}");
                }

                Graphics.Blit(webCamTexture, YoloInputTexture);
                await Task.Delay(32);
                var texture = YoloInputTexture.ToTexture2D();
                await Task.Delay(32);
                var foundObjects = await yoloProcessor.RecognizeObjects(texture);
                this.LastDetections = foundObjects;

                ShowRecognitions(foundObjects);
                Destroy(texture);
            }
        }

        private void ShowRecognitions(List<YoloItem> recognitions)
        {
            var cameraTransform = Camera.main.transform;

            // 1. 새 객체 탐지 및 필터링
            foreach (var recognition in recognitions)
            {
                var newObj = new YoloGameObject(recognition, cameraTransform,
                    new Vector2Int(webCamTexture.width, webCamTexture.height), yoloImageSize, virtualProjectionPlaneWidth, yoloObjectCounter);

                // 필터 1: 3D 위치를 찾지 못했으면 무시
                if (newObj.PositionInSpace == null) continue;

                // 필터 2: 시작 위치에서 너무 멀면 무시
                if (Vector3.Distance(newObj.PositionInSpace.Value, sessionStartPosition) > maxCreationDistance) continue;

                // 필터 3: 너무 가까운 곳에 중복된 객체가 있으면 무시
                if (HasBeenSeenBefore(newObj)) continue;

                // 필터 4: 너무 높은 곳에 있으면 무시
                if (newObj.PositionInSpace.Value.y > (startingCameraY + maxRelativeHeight)) continue;

                // 모든 필터를 통과하면 객체 생성
                
                yoloObjectCounter++;
                yoloGameObjects.Add(newObj);
                newObj.DisplayObject = Instantiate(labelObject, newObj.PositionInSpace.Value, Quaternion.identity);
                newObj.DisplayObject.name = newObj.Name;
                
                // [Candidates] Tag as "TargetObject" so MainController sends them to Python
                newObj.DisplayObject.tag = "TargetObject";
                
                newObj.DisplayObject.transform.parent = transform;
                newObj.DisplayObject.GetComponent<ObjectLabelController>().SetText(newObj.Name);
            }

            // 2. 타임아웃 객체 제거
            for (var i = yoloGameObjects.Count - 1; i >= 0; i--)
            {
                if (Time.time - yoloGameObjects[i].TimeLastSeen > labelNotSeenTimeOut)
                {
                    Destroy(yoloGameObjects[i].DisplayObject);
                    yoloGameObjects.RemoveAt(i);
                }
            }
        }

        // --- [삭제] UpdateAndCorrectPositions 메서드 전체를 삭제 ---

        private bool HasBeenSeenBefore(YoloGameObject obj)
        {
            var objClassName = obj.Name.Split('_').LastOrDefault();
            if (string.IsNullOrEmpty(objClassName)) return true;

            var seenBefore = yoloGameObjects.FirstOrDefault(
                ylo => ylo.Name.EndsWith(objClassName) &&
                Vector3.Distance(obj.PositionInSpace.Value, ylo.PositionInSpace.Value) < minIdenticalLabelDistance);

            if (seenBefore != null)
            {
                seenBefore.TimeLastSeen = Time.time;
            }
            return seenBefore != null;
        }

        /// PhotoCapture 사용을 위해 WebCamTexture를 일시적으로 중지합니다.
        public void StopWebCam()
        {
            if (webCamTexture != null && webCamTexture.isPlaying)
            {
                webCamTexture.Stop();
                Debug.Log("[YoloObjectLabeler] PhotoCapture를 위해 WebCam을 중지합니다.");
            }
        }

        /// 중지했던 WebCamTexture를 다시 시작합니다.
        public void StartWebCam()
        {
            if (webCamTexture != null && !webCamTexture.isPlaying)
            {
                webCamTexture.Play();
                Debug.Log("[YoloObjectLabeler] WebCam을 다시 시작합니다.");
            }
        }
    }
    
}