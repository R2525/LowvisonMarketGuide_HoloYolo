using UnityEngine;

public class MainAppFlow : MonoBehaviour
{
    [Header("Configuration")]
    [Tooltip("IP Address of the Flutter Server (Android Device)")]
    public string flutterServerIp = "192.168.0.100"; 
    public int flutterServerPort = 6000; 

    [Header("References (Auto-filled)")]
    public TcpNetworkClient networkClient;
    public HandTargetDistanceLogger distanceLogger;

    private void Start()
    {
        InitializeNetwork();
        InitializeLogger();
        
        Debug.Log("[MainAppFlow] Initialization Complete. Waiting for TargetObject...");
    }

    private void InitializeNetwork()
    {
        // 1. Find or Create TcpNetworkClient
        if (networkClient == null)
        {
            networkClient = FindObjectOfType<TcpNetworkClient>();
            if (networkClient == null)
            {
                GameObject networkObj = new GameObject("NetworkManager");
                networkClient = networkObj.AddComponent<TcpNetworkClient>();
                Debug.Log("[MainAppFlow] Created NetworkManager.");
            }
        }

        // 2. Configure
        networkClient.serverIp = flutterServerIp;
        networkClient.serverPort = flutterServerPort;
        networkClient.autoConnect = true;

        // 3. Connect manually if autoConnect didn't run yet (e.g. if we just added it)
        if (!networkClient.isConnected)
        {
            networkClient.Connect();
        }
    }

    private void InitializeLogger()
    {
        // 1. Find or Create Logger
        if (distanceLogger == null)
        {
            distanceLogger = FindObjectOfType<HandTargetDistanceLogger>();
            if (distanceLogger == null)
            {
                // Attach to Main Camera usually good for Head reference logic
                if (Camera.main != null)
                {
                    distanceLogger = Camera.main.gameObject.AddComponent<HandTargetDistanceLogger>();
                    Debug.Log("[MainAppFlow] Attached Logger to Main Camera.");
                }
                else
                {
                    GameObject loggerObj = new GameObject("DistanceLogger");
                    distanceLogger = loggerObj.AddComponent<HandTargetDistanceLogger>();
                    Debug.Log("[MainAppFlow] Created DistanceLogger object.");
                }
            }
        }

        // 2. Link Client
        distanceLogger.tcpClient = networkClient;
        
        // 3. Set Defaults
        distanceLogger.referenceFrame = HandTargetDistanceLogger.ReferenceFrame.Both; // Default to Both as per interest
    }
}
