// using UnityEngine;
// using YoloHolo.Services;

// namespace YoloHolo.YoloLabeling
// {
//     public class YoloGameObject 
//     {
//         public Vector2 ImagePosition { get; }
//         public string Name {get; }
//         public GameObject DisplayObject { get; set; }
//         public Vector3? PositionInSpace { get; set; }
//         public float TimeLastSeen { get; set; }

//         private const int MaxLabelDistance = 10;
//         private const float SphereCastSize = 0.15f;
//         private const string SpatialMeshLayerName  = "Spatial Mesh";

//         public YoloGameObject(
//             YoloItem yoloItem, Transform cameraTransform, 
//             Vector2Int cameraSize, Vector2Int yoloImageSize,
//             float virtualProjectionPlaneWidth, int id)
//         {
//             ImagePosition = new Vector2(
//                 (yoloItem.Center.x / yoloImageSize.x * cameraSize.x - cameraSize.x / 2) / cameraSize.x,
//                 (yoloItem.Center.y / yoloImageSize.y * cameraSize.y - cameraSize.y / 2) / cameraSize.y);
//             Name = $"{id}_{yoloItem.MostLikelyObject}";

//             var virtualProjectionPlaneHeight = virtualProjectionPlaneWidth * cameraSize.y / cameraSize.x;
//             FindPositionInSpace(cameraTransform, virtualProjectionPlaneWidth, virtualProjectionPlaneHeight);
//             TimeLastSeen = Time.time;
//         }

//         private void FindPositionInSpace(Transform transform,
//             float width, float height)
//         {
//             var positionOnPlane = transform.position + transform.forward + 
//                 transform.right * (ImagePosition.x * width) - 
//                 transform.up * (ImagePosition.y * height);
//             PositionInSpace = CastOnSpatialMap(positionOnPlane, transform);
//         }

//         private Vector3? CastOnSpatialMap(Vector3 positionOnPlane, Transform transform)
//         {
//             if (Physics.SphereCast(transform.position, SphereCastSize,
//                     (positionOnPlane - transform.position),
//                     out var hitInfo, MaxLabelDistance, LayerMask.GetMask(SpatialMeshLayerName)))
//             {
//                 return hitInfo.point;
//             }
//             return null;
//         }

//     }
// }
using UnityEngine;
using YoloHolo.Services;

namespace YoloHolo.YoloLabeling
{
    public class YoloGameObject 
    {
        public Vector2 ImagePosition { get; }
        public string Name {get; }
        public GameObject DisplayObject { get; set; }
        public Vector3? PositionInSpace { get; set; }
        public float TimeLastSeen { get; set; }

        private const int MaxLabelDistance = 10;
        private const float SphereCastSize = 0.15f;
        private const string SpatialMeshLayerName  = "Spatial Mesh";

        public YoloGameObject(
            YoloItem yoloItem, Transform handTransform,
            Vector2Int cameraSize, Vector2Int yoloImageSize,
            float virtualProjectionPlaneWidth, int id)
        {
            // ========== FIX: Y-axis inversion for YOLO (top-left) to Unity (bottom-left) coordinate system ==========

            // X-axis: remains the same (both use left-to-right)
            float normalizedX = (yoloItem.Center.x / yoloImageSize.x * cameraSize.x - cameraSize.x / 2) / cameraSize.x;

            // Y-axis: CRITICAL FIX - flip Y coordinate
            // YOLO: Y=0 at TOP, Unity: Y=0 at BOTTOM
            float yoloY = yoloItem.Center.y / yoloImageSize.y * cameraSize.y;  // Convert to camera pixel space
            float unityY = cameraSize.y - yoloY;  // Flip: cameraSize.y - Y
            float normalizedY = (unityY - cameraSize.y / 2) / cameraSize.y;  // Normalize to [-0.5, 0.5]

            ImagePosition = new Vector2(normalizedX, normalizedY);

            // Debug logging for verification
            Debug.Log($"[YoloGameObject FIX] ID:{id} | " +
                      $"YOLO Center:({yoloItem.Center.x:F1}, {yoloItem.Center.y:F1}) | " +
                      $"YoloY:{yoloY:F1} → UnityY:{unityY:F1} | " +
                      $"ImagePosition:({ImagePosition.x:F3}, {ImagePosition.y:F3})");

            // ========== End of fix ==========

            Name = $"{id}_{yoloItem.MostLikelyObject}";

            var virtualProjectionPlaneHeight = virtualProjectionPlaneWidth * cameraSize.y / cameraSize.x;
            FindPositionInSpace(handTransform, virtualProjectionPlaneWidth, virtualProjectionPlaneHeight);
            TimeLastSeen = Time.time;
        }

        private void FindPositionInSpace(Transform transform,
            float width, float height)
        {
            // Now with corrected ImagePosition.y (after Y-axis flip), use + instead of -
            // Positive ImagePosition.y should move UP (towards +Y in Unity)
            // Negative ImagePosition.y should move DOWN (towards -Y in Unity)
            var positionOnPlane = transform.position + transform.forward +
                transform.right * (ImagePosition.x * width) +
                transform.up * (ImagePosition.y * height);
            PositionInSpace = CastOnSpatialMap(positionOnPlane, transform);
        }

        private Vector3? CastOnSpatialMap(Vector3 positionOnPlane, Transform transform)
        {
            if (Physics.SphereCast(transform.position, SphereCastSize,
                    (positionOnPlane - transform.position),
                    out var hitInfo, MaxLabelDistance, LayerMask.GetMask(SpatialMeshLayerName)))
            {
                return hitInfo.point;
            }
            return null;
        }

    }
}
