// TargetObjectID.cs

using UnityEngine;

// "TargetObject" 태그가 붙은 게임 오브젝트에 이 스크립트를 추가하세요.
// 이 스크립트는 각 오브젝트에 고유한 ID를 부여하고 저장하는 역할을 합니다.
public class TargetObjectID : MonoBehaviour
{
    // 실행 시점에 ObjectTrackingController가 할당해 줄 고유한 ID
    // -1은 아직 ID가 할당되지 않았음을 의미합니다.
    public int UniqueID { get; set; } = -1; 
}