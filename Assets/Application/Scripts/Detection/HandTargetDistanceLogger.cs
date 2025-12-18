using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class HandTargetDistanceLogger : MonoBehaviour
{
    [Header("Network")]
    public TcpNetworkClient tcpClient;

    private GameObject targetObject;
    private const string TargetTag = "FoundObject";
    
    // Cache the input devices
    private InputDevice rightHandDevice;

    // Logging & Visualization Settings
    private float lastLogTime;
    private const float LogInterval = 0.2f; // Slightly faster for guidance updates

    public enum ReferenceFrame { Head, Hand, Both }
    public ReferenceFrame referenceFrame = ReferenceFrame.Both;

    private bool _hasFoundTarget = false;

    [Header("Grab Detection")]
    public float grabDistanceThreshold = 0.15f;
    public float requiredGrabTime = 1.0f;
    private float grabTimer = 0f;
    private bool isGrabSent = false;

    private Collider targetCollider;
    private Renderer targetRenderer;

    void Start()
    {
        if (tcpClient == null) tcpClient = TcpNetworkClient.Instance;
    }

    void Update()
    {
        // --- Simulation Mode for Editor Testing ---
#if UNITY_EDITOR
        UpdateSimulation();
#endif

        if (Time.time - lastLogTime < LogInterval) {
            // Grab check needs to run every frame, independent of LogInterval
            if(targetObject != null) CheckGrabCondition(targetObject.transform.position);
            return; 
        }
        lastLogTime = Time.time;

        // Find Target Logic
        if (targetObject == null)
        {
            GameObject foundObj = GameObject.FindGameObjectWithTag(TargetTag);
            if (foundObj != null) 
            {
                targetObject = foundObj;
                Debug.Log($"[Logger] FOUND Target! Name: {targetObject.name}, Pos: {targetObject.transform.position}");
                
                // RECONNECT FLUTTER (User Requirement) - REMOVED due to connection instability
                // Debug.Log("[Logger] Target Found! Restarting Connection...");
                // if (tcpClient != null)
                // {
                //     tcpClient.Disconnect();
                //     tcpClient.Connect();
                // }
                // else if (TcpNetworkClient.Instance != null)
                // {
                //      // Fallback if reference missing
                //      TcpNetworkClient.Instance.Disconnect();
                //      TcpNetworkClient.Instance.Connect();
                // }

                // Cache Collider/Renderer for Grab Logic
                targetCollider = targetObject.GetComponent<Collider>();
                targetRenderer = targetObject.GetComponent<Renderer>();

                // First time found event
                if (!_hasFoundTarget)
                {
                    _hasFoundTarget = true;
                    // Wait a brief moment for connection? 
                    // SendEvent might need to retry if connection isn't instant.
                    // For now, we assume async connect is fast enough or use Invoke.
                    Invoke(nameof(SendFoundEventDelayed), 0.5f); 
                }
            }
            else
            {
                return; // Keep searching
            }
        }
        else 
        {
           // Object was found previously, verify it still exists
           if (targetObject == null) 
           {
               _hasFoundTarget = false;
               return; 
           }
        }

        Vector3 targetPos = targetObject.transform.position;
        Transform camTransform = Camera.main != null ? Camera.main.transform : null;
        if (camTransform == null) return;

        // --- 1. Guidance Logic (Hand Based with Head Fallback) ---
        Vector3 originPos = camTransform.position; // Default: Head
        originPos.y -= 0.1f; // Lower Head origin by 10cm (User Request)
        
        InitializeRightHand();
        if (IsHandTracked(out Vector3 handPos))
        {
            originPos = handPos; // Use Hand if valid (Overrides Head)
        }

        // Calculate direction relative to Camera (User's View)
        // This tells the user: "Move your hand [Right] to reach the target"
        Vector3 worldDir = targetPos - originPos;
        
        // Visuals
        Debug.DrawLine(originPos, targetPos, Color.yellow, LogInterval);
        
        // Send Data (Guidance Strings)
        // Flutter expects: "direction(value)" e.g. "left(0.5)"
        SendGuidanceStrings(camTransform, worldDir);

        // --- 2. Grab Logic (Hand based) ---
        CheckGrabCondition(targetPos);
    }

    private void CheckGrabCondition(Vector3 targetPos)
    {
        InitializeRightHand(); // Ensure device is found
        
        // 1. Check Hand Tracking & Position
        if (!IsHandTracked(out Vector3 handPos))
        {
             ResetGrab();
             return;
        }

        // 2. Check Overlap (Bounds) OR Distance Fallback
        bool isOverlapping = false;

        if (targetCollider != null)
        {
            // Best: Use Collider Bounds
            if (targetCollider.bounds.Contains(handPos)) isOverlapping = true;
        }
        else if (targetRenderer != null)
        {
            // Good: Use Renderer Bounds
            if (targetRenderer.bounds.Contains(handPos)) isOverlapping = true;
        }
        else
        {
            // Fallback: Distance check if no bounds available
            float dist = Vector3.Distance(handPos, targetPos);
            if (dist <= 0.1f) isOverlapping = true; // Strict 10cm fallback
        }

        if (!isOverlapping)
        {
            ResetGrab();
            return;
        }

        // 3. Check Input (Grip or Trigger)
        bool isGripping = false;
        if (rightHandDevice.isValid)
        {
            if (rightHandDevice.TryGetFeatureValue(CommonUsages.gripButton, out bool grip) && grip) isGripping = true;
            if (rightHandDevice.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger) && trigger) isGripping = true;
        }

        if (isGripping)
        {
            grabTimer += Time.deltaTime;
            if (grabTimer >= requiredGrabTime && !isGrabSent)
            {
                SendGrabSuccess();
                isGrabSent = true;
                Debug.Log($"[Logger] Grab Detected (Overlap)! Time: {grabTimer:F2}s");
            }
        }
        else
        {
            ResetGrab();
        }
    }

    private void ResetGrab()
    {
        grabTimer = 0f;
        isGrabSent = false;
    }
    
    // Data definitions for JSON serialization
    [System.Serializable]
    public class NetworkMessage
    {
        public string type;
        public long timestamp;
        public string message;
    }

    // EventMessage is effectively just NetworkMessage now
    [System.Serializable]
    public class EventMessage : NetworkMessage
    {
    }

    private void SendJson(object data)
    {
        string json = JsonUtility.ToJson(data);
        if (tcpClient != null) tcpClient.Send(json);
        else if (TcpNetworkClient.Instance != null) TcpNetworkClient.Instance.Send(json);
#if UNITY_EDITOR
        Debug.Log($"[Logger-JSON] {json}");
#endif
    }

    /// <summary>
    /// Call this publicly when the user grabs the object
    /// </summary>
    public void SendGrabSuccess()
    {
        SendEvent("grabbed", "Object Grabbed");
    }

    private void SendFoundEventDelayed()
    {
        SendEvent("found", "Target Found");
    }

    private void SendEvent(string eventType, string msg = "")
    {
        EventMessage e = new EventMessage
        {
            type = eventType,
            timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            message = msg
        };
        SendJson(e);
    }
    
    /// <summary>
    /// User requested: When 'Find' command is issued, clear all existing FoundObjects
    /// to prevent ghost tracking.
    /// Call this method when the Voice Command "Find" is recognized.
    /// </summary>
    public void ClearAllTargets()
    {
        targetObject = null;
        targetCollider = null;
        targetRenderer = null;
        _hasFoundTarget = false;
        isGrabSent = false;
        grabTimer = 0f;

        GameObject[] targets = GameObject.FindGameObjectsWithTag("FoundObject");
        foreach (var t in targets)
        {
            Destroy(t);
        }
        
        Debug.Log($"[Logger] Cleared {targets.Length} old FoundObjects.");
    }
    
    // Deprecated direct string sender...
    private void SendString(string message) 
    {
        // Legacy support or direct debug
        if (message == "found") SendEvent("found", "Target Found");
        else if (message == "grabbed") SendEvent("grabbed", "Object Grabbed");
        else 
        {
             // Fallback for unknown strings
             Debug.Log($"[Logger] Raw: {message}");
        }
    }

    private void SendGuidanceStrings(Transform relativeTo, Vector3 worldDir)
    {
        // Project world direction into Camera's local space
        Vector3 localDir = relativeTo.InverseTransformDirection(worldDir);
        
        float x = localDir.x; // Right+, Left-
        float y = localDir.y; // Up+, Down-
        float z = localDir.z; // Forward+, Back- (Depth)
        
        float threshold = 0.10f; // 10cm threshold

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("{");
        sb.Append($"\"type\":\"guidance\",");
        sb.Append($"\"timestamp\":{System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},");

        // Horizontal
        if (x > threshold) sb.Append($"\"right\":{x:F2},");
        else if (x < -threshold) sb.Append($"\"left\":{Mathf.Abs(x):F2},");

        // Vertical (User requested top/bottom)
        if (y > threshold) sb.Append($"\"top\":{y:F2},");
        else if (y < -threshold) sb.Append($"\"bottom\":{Mathf.Abs(y):F2},");
        
        // Depth
        sb.Append($"\"depth\":{z:F2}"); // Always send signed depth

        sb.Append("}");

        string json = sb.ToString();
        
        if (tcpClient != null)
        {
            if (tcpClient.isConnected) tcpClient.Send(json);
            else Debug.LogWarning("[Logger] Cannot send guidance - Client Disconnected");
        }
        else if (TcpNetworkClient.Instance != null && TcpNetworkClient.Instance.isConnected) 
        {
             TcpNetworkClient.Instance.Send(json);
        }
        
#if UNITY_EDITOR
        Debug.Log($"[Logger-JSON-Guidance] {json}");
#endif
    }

    private string GetDirectionString(float x, float y, float z)
    {
        float threshold = 0.10f;
        string msg = "";
        
        if (x > threshold) msg += "Right "; else if (x < -threshold) msg += "Left ";
        if (y > threshold) msg += "Up "; else if (y < -threshold) msg += "Down ";
        if (z > threshold) msg += "Forward "; else if (z < -threshold) msg += "Backward ";
        
        return msg.Trim();
    }

#if UNITY_EDITOR
    private void UpdateSimulation()
    {
        // 'F' key to simulate "Found Finding"
        if (Input.GetKeyDown(KeyCode.F))
        {
            Debug.Log("[Sim] Simulating Found Event");
            SendString("found");
        }

        // 'G' key to simulate "Grabbed Success"
        if (Input.GetKeyDown(KeyCode.G))
        {
            Debug.Log("[Sim] Simulating Grabbed Event");
            SendString("grabbed");
        }

        // 'C' key to simulate "Cleanup Targets"
        if (Input.GetKeyDown(KeyCode.C))
        {
             ClearAllTargets(); 
        }

        // Arrow Keys to simulate hand guidance
        if (Input.GetKeyDown(KeyCode.LeftArrow)) SendString("left(0.5)");
        if (Input.GetKeyDown(KeyCode.RightArrow)) SendString("right(0.5)");
        if (Input.GetKeyDown(KeyCode.UpArrow)) SendString("up(0.5)");
        if (Input.GetKeyDown(KeyCode.DownArrow)) SendString("down(0.5)");
        
        // W/S for Depth
        if (Input.GetKeyDown(KeyCode.W)) SendString("forward(0.5)");
        if (Input.GetKeyDown(KeyCode.S)) SendString("backward(0.5)");
    }
#endif

    private void InitializeRightHand()
    {
        if (rightHandDevice.isValid) return;
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Right | InputDeviceCharacteristics.HandTracking | InputDeviceCharacteristics.HeldInHand, devices);
        if (devices.Count == 0) InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller, devices);
        if (devices.Count > 0) rightHandDevice = devices[0];
    }

    private bool IsHandTracked(out Vector3 position)
    {
        position = Vector3.zero;
        if (rightHandDevice.isValid && rightHandDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked) && isTracked)
        {
            if (rightHandDevice.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 p))
            {
                 Transform camTransform = Camera.main.transform;
                 if (camTransform.parent != null) position = camTransform.parent.TransformPoint(p);
                 else position = p;
                 return true;
            }
        }
        return false;
    }
}
