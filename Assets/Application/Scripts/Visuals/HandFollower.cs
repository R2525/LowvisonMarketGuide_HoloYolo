using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Application.Scripts.Visuals
{
    public class HandFollower : MonoBehaviour
    {
        [Header("Settings")]
        [Tooltip("The prefab to instantiate when the hand is detected.")]
        public GameObject followerPrefab;

        [Tooltip("Position offset from the hand center.")]
        public Vector3 positionOffset = new Vector3(0, 0.2f, 0);

        [Tooltip("Color to fade to when colliding with a FoundObject.")]
        public Color collisionFeedbackColor = Color.red;

        [Tooltip("Material to fade to when colliding. Overrides Color if set.")]
        public Material feedbackMaterial;

        [Header("Hand Settings")]
        public InputDeviceCharacteristics handCharacteristics = InputDeviceCharacteristics.Right | InputDeviceCharacteristics.HandTracking;

        private InputDevice _targetDevice;
        private GameObject _spawnedObject;

        // Editor Simulation
        private bool _isSimulating = false;

        void Update()
        {
            Vector3 handPosition = Vector3.zero;
            bool isHandFound = false;

            // 1. Try to get real device data
            InitializeDevice();
            
            if (_targetDevice.isValid && _targetDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked) && isTracked)
            {
                if (_targetDevice.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 devicePos))
                {
                    // Convert usage to World Space if needed (OpenXR usually gives world space relative to XR rig, 
                    // but depending on setup, we might need to transform it. 
                    // Assuming Camera parent is the rig origin, or using direct world coordinates if valid).
                    // In many simple XR setups, devicePos is local to the XR Rig.
                    
                    Camera mainCam = Camera.main;
                    if (mainCam != null && mainCam.transform.parent != null)
                    {
                        handPosition = mainCam.transform.parent.TransformPoint(devicePos);
                    }
                    else
                    {
                        handPosition = devicePos;
                    }
                    
                    isHandFound = true;
                }
            }

            // 2. Editor Simulation Override
#if UNITY_EDITOR
            if (Input.GetKeyDown(KeyCode.H))
            {
                _isSimulating = !_isSimulating;
                Debug.Log($"[HandFollower] Simulation Mode: {_isSimulating}");
            }

            if (_isSimulating)
            {
                isHandFound = true;
                // Simulate hand 50cm in front of camera, slightly down
                if (Camera.main != null)
                {
                    handPosition = Camera.main.transform.position + Camera.main.transform.forward * 0.5f - Vector3.up * 0.2f;
                }
            }
#endif

            // 3. Handle Object Spawning/Movement
            if (isHandFound)
            {
                if (_spawnedObject == null && followerPrefab != null)
                {
                    _spawnedObject = Instantiate(followerPrefab);
                    
                    // Attach collision observer if not present
                    if (_spawnedObject.GetComponent<HandCollisionObserver>() == null)
                    {
                        var observer = _spawnedObject.AddComponent<HandCollisionObserver>();
                        observer.targetFadeColor = collisionFeedbackColor;
                        observer.targetMaterial = feedbackMaterial;
                        
                        // Ensure it has a RigidBody for physics events if needed (optional but good for collision)
                        if (_spawnedObject.GetComponent<Rigidbody>() == null)
                        {
                            var rb = _spawnedObject.AddComponent<Rigidbody>();
                            rb.isKinematic = true; // Follow hand directly, don't fallback to physics
                            rb.useGravity = false;
                        }
                    }
                }

                if (_spawnedObject != null)
                {
                    _spawnedObject.transform.position = handPosition + positionOffset;
                    
                    // Optional: Make it look at the camera?
                    if (Camera.main != null)
                    {
                        _spawnedObject.transform.LookAt(Camera.main.transform);
                    }
                }
            }
            else
            {
                if (_spawnedObject != null)
                {
                    Destroy(_spawnedObject);
                    _spawnedObject = null;
                }
            }
        }

        private float _deviceSearchTimer = 0f;
        private const float DeviceSearchInterval = 1.0f;

        private void InitializeDevice()
        {
            if (_targetDevice.isValid) return;

            _deviceSearchTimer += Time.deltaTime;
            if (_deviceSearchTimer < DeviceSearchInterval) return;
            _deviceSearchTimer = 0f;

            List<InputDevice> devices = new List<InputDevice>();
            InputDevices.GetDevicesWithCharacteristics(handCharacteristics, devices);

            if (devices.Count > 0)
            {
                _targetDevice = devices[0];
            }
        }
    }

    /// <summary>
    /// Observer to log collisions and handle interactions with "FoundObject".
    /// </summary>
    public class HandCollisionObserver : MonoBehaviour
    {
        // ... (Keep existing inner classes and SendJson)
        [System.Serializable]
        public class EventMessage
        {
            public string type;
            public long timestamp;
            public string message;
        }

        private void SendJson(object data)
        {
            if (TcpNetworkClient.Instance != null && TcpNetworkClient.Instance.isConnected)
            {
                string json = JsonUtility.ToJson(data);
                TcpNetworkClient.Instance.Send(json);
                Debug.Log($"[HandFollower-JSON] Sent: {json}");
            }
            else
            {
                string status = (TcpNetworkClient.Instance == null) ? "Instance is NULL" : $"Connected: {TcpNetworkClient.Instance.isConnected}";
                Debug.LogWarning($"[HandFollower] Failed to send JSON. TCP Status: {status}");
            }
        }

        private void SendFoundEvent()
        {
            EventMessage e = new EventMessage
            {
                type = "grabbed",
                timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                message = "Hand Collision Grabbed"
            };
            SendJson(e);
      
        }

        // Dictionary to store running coroutines and original state
        private Dictionary<Renderer, Coroutine> _activeCoroutines = new Dictionary<Renderer, Coroutine>();
        private Dictionary<GameObject, Coroutine> _activeSendingCoroutines = new Dictionary<GameObject, Coroutine>(); // New: Track sending coroutines
        private Dictionary<Renderer, Color> _originalColors = new Dictionary<Renderer, Color>();
        private Dictionary<Renderer, Material> _originalMaterials = new Dictionary<Renderer, Material>();

        public float fadeDuration = 0.5f;
        public Color targetFadeColor = Color.red;
        public Material targetMaterial;

        // ... (Keep Enter/Exit handlers)
        private void OnCollisionEnter(Collision collision) { HandleEnter(collision.gameObject); }
        private void OnCollisionExit(Collision collision) { HandleExit(collision.gameObject); }
        private void OnTriggerEnter(Collider other) { HandleEnter(other.gameObject); }
        private void OnTriggerExit(Collider other) { HandleExit(other.gameObject); }

        private void HandleEnter(GameObject obj)
        {
            if (obj.CompareTag("FoundObject"))
            {
                Renderer r = obj.GetComponent<Renderer>();
                if (r != null)
                {
                    // Fix: Check for _BaseColor for URP shaders
                    if (!_originalColors.ContainsKey(r)) 
                    {
                        _originalColors[r] = r.material.HasProperty("_BaseColor") ? r.material.GetColor("_BaseColor") : r.material.color;
                    }
                    if (targetMaterial != null && !_originalMaterials.ContainsKey(r)) _originalMaterials[r] = new Material(r.material);

                    if (_activeCoroutines.ContainsKey(r) && _activeCoroutines[r] != null) StopCoroutine(_activeCoroutines[r]);
                    _activeCoroutines[r] = StartCoroutine(FadeToTarget(r));
                }
                
                // Start continuous sending if not already running for this object
                if (!_activeSendingCoroutines.ContainsKey(obj))
                {
                     _activeSendingCoroutines[obj] = StartCoroutine(SendFoundEventRepeatedly(obj));
                }
            }
        }

        private void HandleExit(GameObject obj)
        {
            if (obj.CompareTag("FoundObject"))
            {
                Renderer r = obj.GetComponent<Renderer>();
                if (r != null)
                {
                    if (_activeCoroutines.ContainsKey(r) && _activeCoroutines[r] != null) StopCoroutine(_activeCoroutines[r]);
                    _activeCoroutines[r] = StartCoroutine(FadeToOriginal(r));
                }

                // Stop continuous sending
                if (_activeSendingCoroutines.ContainsKey(obj))
                {
                    if (_activeSendingCoroutines[obj] != null) StopCoroutine(_activeSendingCoroutines[obj]);
                    _activeSendingCoroutines.Remove(obj);
                }
            }
        }

        private IEnumerator SendFoundEventRepeatedly(GameObject target)
        {
            while (target != null)
            {
                SendFoundEvent();
                yield return new WaitForSeconds(0.5f);
            }
            
            // If we exit loop because target is null, cleanup
            if (target == null && _activeSendingCoroutines.ContainsKey(target))
            {
                _activeSendingCoroutines.Remove(target);
            }
        }

        private IEnumerator FadeToTarget(Renderer r)
        {
             // ... existing implementation ...
             if (r == null) yield break;
             Material startMatSnap = new Material(r.material);
             Color startColor = r.material.HasProperty("_BaseColor") ? r.material.GetColor("_BaseColor") : r.material.color;
             // Debug.Log($"[HandFollower] Fading {r.name} to Target Color/Material"); 
             
             float elapsed = 0f;
             while (elapsed < fadeDuration)
             {
                if (r == null) { Destroy(startMatSnap); yield break; }

                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / fadeDuration);
                
                if (targetMaterial != null)
                {
                    r.material.Lerp(startMatSnap, targetMaterial, t);
                }
                else
                {
                    Color lerpedColor = Color.Lerp(startColor, targetFadeColor, t);
                    if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", lerpedColor);
                    r.material.color = lerpedColor; // Fallback for Standard
                }
                yield return null;
             }
             // ... existing end ...
             if (r != null)
             {
                  if (targetMaterial != null) r.material.CopyPropertiesFromMaterial(targetMaterial);
                  else 
                  {
                      if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", targetFadeColor);
                      r.material.color = targetFadeColor;
                  }
             }
             Destroy(startMatSnap);
        }

        private IEnumerator FadeToOriginal(Renderer r)
        {
            float elapsed = 0f;
            
            if (r == null) yield break;
            Material startMatSnap = new Material(r.material);
            Color startColor = r.material.HasProperty("_BaseColor") ? r.material.GetColor("_BaseColor") : r.material.color;

            // Determine target for restore
            Material endMat = (_originalMaterials.ContainsKey(r)) ? _originalMaterials[r] : null;
            Color endColor = (_originalColors.ContainsKey(r)) ? _originalColors[r] : Color.white;

            while (elapsed < fadeDuration)
            {
                if (r == null) { Destroy(startMatSnap); yield break; }

                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / fadeDuration);

                if (targetMaterial != null && endMat != null)
                {
                     r.material.Lerp(startMatSnap, endMat, t);
                }
                else
                {
                     Color lerpedColor = Color.Lerp(startColor, endColor, t);
                     if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", lerpedColor);
                     r.material.color = lerpedColor;
                }
                yield return null;
            }

            if (r != null)
            {
                if (targetMaterial != null && endMat != null) r.material.CopyPropertiesFromMaterial(endMat);
                else 
                {
                    if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", endColor);
                    r.material.color = endColor;
                }
            }
            Destroy(startMatSnap);
        }

        private void OnDestroy()
        {
            // 1. Restore all Modified Objects immediately (in case hand disappears while touching)
            if (_originalMaterials != null)
            {
                foreach(var kvp in _originalMaterials)
                {
                    Renderer r = kvp.Key;
                    Material mat = kvp.Value;
                    if (r != null && mat != null)
                    {
                        r.material.CopyPropertiesFromMaterial(mat);
                    }
                    if(mat != null) Destroy(mat);
                }
                _originalMaterials.Clear();
            }

            if (_originalColors != null)
            {
                foreach(var kvp in _originalColors)
                {
                    Renderer r = kvp.Key;
                    Color col = kvp.Value;
                    if (r != null)
                    {
                        if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", col);
                        r.material.color = col;
                    }
                }
                 _originalColors.Clear();
            }

            // 2. Stop all Coroutines (Unity does this automatically on Destroy, but good to be explicit for logic flow)
            StopAllCoroutines();
            _activeCoroutines.Clear();
            _activeSendingCoroutines.Clear();
        }
    }
}
