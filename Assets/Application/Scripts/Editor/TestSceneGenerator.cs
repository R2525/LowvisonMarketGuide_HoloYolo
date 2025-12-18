using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public class TestSceneGenerator : Editor
{
    [MenuItem("Tools/Create Python Test Scene")]
    public static void CreateTestScene()
    {
        string originalScenePath = "Assets/Application/Scenes/Main.unity";
        string newScenePath = "Assets/Application/Scenes/PythonTestScene.unity";

        // 1. Open Main Scene
        Scene mainScene = EditorSceneManager.OpenScene(originalScenePath, OpenSceneMode.Single);
        
        // 2. Save as new Test Scene (Clone)
        EditorSceneManager.SaveScene(mainScene, newScenePath);
        
        // 2. Save as new Test Scene (Clone)
        EditorSceneManager.SaveScene(mainScene, newScenePath);
        
        // 3. CLEANUP: Aggressively Remove Unwanted Objects
        // Use GetRootGameObjects to find them even if inactive or distinct roots
        GameObject[] roots = mainScene.GetRootGameObjects();
        foreach (var root in roots)
        {
            if (root.name.Contains("MainController") || 
                root.name.Contains("ServiceManagerInstance") || 
                root.name.Contains("HologramCollection") ||
                root.name.Contains("Yolo"))
            {
                Object.DestroyImmediate(root);
            }
        }

        // Removed direct type reference to YoloObjectLabeler to fix compilation error.
        // The string-based check above covers objects named "Yolo...".

        // 4. Configure AppManager (The ONE we want)
        GameObject appManager = GameObject.Find("AppManager");
        if (appManager == null) 
        {
             appManager = new GameObject("AppManager");
             appManager.AddComponent<MainAppFlow>();
             appManager.AddComponent<TcpNetworkClient>();
        }

        // Ensure Components exist on AppManager
        MainAppFlow mainAppFlow = appManager.GetComponent<MainAppFlow>();
        if (mainAppFlow == null) mainAppFlow = appManager.AddComponent<MainAppFlow>();
        
        TcpNetworkClient tcpClient = appManager.GetComponent<TcpNetworkClient>();
        if (tcpClient == null) tcpClient = appManager.AddComponent<TcpNetworkClient>();

        // Configure Local Test IP
        mainAppFlow.flutterServerIp = "127.0.0.1"; 
        mainAppFlow.flutterServerPort = 6000;
        mainAppFlow.networkClient = tcpClient;

        // 5. Find Camera & Logger
        GameObject camObj = GameObject.FindGameObjectWithTag("MainCamera");
        if (camObj != null)
        {
            HandTargetDistanceLogger logger = camObj.GetComponent<HandTargetDistanceLogger>();
            if (logger == null) logger = camObj.AddComponent<HandTargetDistanceLogger>();
            
            logger.tcpClient = tcpClient;
            logger.referenceFrame = HandTargetDistanceLogger.ReferenceFrame.Both;
            mainAppFlow.distanceLogger = logger;
        }

        // 6. Create Test Target if missing
        GameObject targetObj = GameObject.FindGameObjectWithTag("TargetObject");
        if (targetObj == null)
        {
            targetObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            targetObj.name = "TestTarget_Cube";
            targetObj.tag = "TargetObject"; 
            targetObj.transform.position = new Vector3(0, 0f, 1.0f); 
            targetObj.transform.localScale = Vector3.one * 0.2f; 
            
            Material redMat = new Material(Shader.Find("Standard"));
            redMat.color = Color.red;
            targetObj.GetComponent<Renderer>().material = redMat;
        }

        // 7. Save modifications
        EditorSceneManager.SaveScene(mainScene);
        
        Debug.Log($"[TestSceneGenerator] Success! Cloned 'Main' to '{newScenePath}' with test config.");
        Debug.Log("Please press Play to test with 'python test_server.py'.");
    }
}
