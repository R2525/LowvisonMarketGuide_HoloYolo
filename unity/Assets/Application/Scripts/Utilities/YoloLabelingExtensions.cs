// using UnityEngine;
// using System.Collections.Generic;
// using System.Linq;

// namespace YoloHolo.Utilities
// {
//     public static class YoloLabelingExtensions
//     {
//         /// <summary>
//         /// Creates a copy of the camera transform for async processing
//         /// </summary>
//         public static Transform CopyCameraTransform(this Camera camera)
//         {
//             GameObject tempGO = new GameObject("TempCameraTransform");
//             tempGO.transform.position = camera.transform.position;
//             tempGO.transform.rotation = camera.transform.rotation;
//             tempGO.transform.localScale = camera.transform.localScale;
//             return tempGO.transform;
//         }

//         /// <summary>
//         /// Converts RenderTexture to Texture2D for processing
//         /// </summary>
//         public static Texture2D ToTexture2D(this RenderTexture renderTexture)
//         {
//             Texture2D texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, false);
//             RenderTexture currentActive = RenderTexture.active;
//             RenderTexture.active = renderTexture;
//             texture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
//             texture.Apply();
//             RenderTexture.active = currentActive;
//             return texture;
//         }

//         /// <summary>
//         /// RANSAC line fitting implementation as fallback
//         /// </summary>
//         public static (float a, float b, bool success) FitLineRANSAC(
//             List<Vector2> points,
//             int maxIterations = 200,
//             float inlierThreshold = 10f,
//             float minInlierRatio = 0.5f)
//         {
//             // Safety checks for null or empty collections
//             if (points == null || points.Count == 0)
//                 return (0f, 0f, false);

//             if (points.Count == 1)
//                 return (0f, points[0].y, false);

//             if (points.Count < 2)
//                 return (0f, 0f, false);

//             float bestA = 0, bestB = 0;
//             int bestInliers = 0;
//             float bestError = float.MaxValue;
//             System.Random rand = new System.Random();

//             for (int iter = 0; iter < maxIterations; iter++)
//             {
//                 // Select two random points
//                 int idx1 = rand.Next(points.Count);
//                 int idx2 = rand.Next(points.Count);

//                 if (idx1 == idx2) continue;

//                 Vector2 p1 = points[idx1];
//                 Vector2 p2 = points[idx2];

//                 // Calculate line parameters
//                 if (Mathf.Abs(p2.x - p1.x) < 0.001f) continue;

//                 float a = (p2.y - p1.y) / (p2.x - p1.x);
//                 float b = p1.y - a * p1.x;

//                 // Count inliers
//                 int inliers = 0;
//                 float totalError = 0;

//                 foreach (var point in points)
//                 {
//                     float yPred = a * point.x + b;
//                     float error = Mathf.Abs(point.y - yPred);

//                     if (error <= inlierThreshold)
//                     {
//                         inliers++;
//                         totalError += error;
//                     }
//                 }

//                 // Check if this is the best model
//                 if (inliers > bestInliers || (inliers == bestInliers && totalError < bestError))
//                 {
//                     bestInliers = inliers;
//                     bestError = totalError;
//                     bestA = a;
//                     bestB = b;
//                 }
//             }

//             // Check if we have enough inliers
//             float inlierRatio = (float)bestInliers / points.Count;
//             bool success = inlierRatio >= minInlierRatio;

//             // Refine with inliers using least squares
//             if (success && bestInliers > 2)
//             {
//                 var inlierPoints = points.Where(p =>
//                     Mathf.Abs(p.y - (bestA * p.x + bestB)) <= inlierThreshold
//                 ).ToList();

//                 if (inlierPoints.Count > 2)
//                 {
//                     (bestA, bestB) = FitLineLeastSquares(inlierPoints);
//                 }
//             }

//             return (bestA, bestB, success);
//         }

//         /// <summary>
//         /// Least squares line fitting for refinement
//         /// </summary>
//         private static (float a, float b) FitLineLeastSquares(List<Vector2> points)
//         {
//             float sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
//             int n = points.Count;

//             foreach (var p in points)
//             {
//                 sumX += p.x;
//                 sumY += p.y;
//                 sumXX += p.x * p.x;
//                 sumXY += p.x * p.y;
//             }

//             float denom = n * sumXX - sumX * sumX;
//             if (Mathf.Abs(denom) < 0.001f)
//             {
//                 // Vertical line
//                 return (0, sumY / n);
//             }

//             float a = (n * sumXY - sumX * sumY) / denom;
//             float b = (sumY - a * sumX) / n;

//             return (a, b);
//         }

//         /// <summary>
//         /// Calculate dynamic eps_y based on detected layer spacing
//         /// </summary>
//         public static float CalculateDynamicEpsY(List<float> yPositions, float defaultEps)
//         {
//             if (yPositions.Count < 2)
//                 return defaultEps;

//             var sorted = yPositions.OrderBy(y => y).ToList();
//             var gaps = new List<float>();

//             for (int i = 1; i < sorted.Count; i++)
//             {
//                 float gap = sorted[i] - sorted[i - 1];
//                 if (gap > defaultEps * 0.5f) // Filter out very small gaps
//                 {
//                     gaps.Add(gap);
//                 }
//             }

//             if (gaps.Count == 0)
//                 return defaultEps;

//             // Use median gap
//             gaps.Sort();
//             float medianGap = gaps[gaps.Count / 2];

//             // eps_y should be 40-60% of typical layer spacing
//             return Mathf.Clamp(medianGap * 0.5f, defaultEps * 0.5f, defaultEps * 2f);
//         }

//         /// <summary>
//         /// Project 3D world points to screen space for 3D mode
//         /// </summary>
//         public static Vector2 WorldToScreenPoint(Vector3 worldPoint, Camera camera)
//         {
//             Vector3 screenPoint = camera.WorldToScreenPoint(worldPoint);
//             return new Vector2(screenPoint.x, screenPoint.y);
//         }

//         /// <summary>
//         /// Calculate line from two 3D points projected to screen
//         /// </summary>
//         public static (float a, float b) CalculateLineFrom3DPoints(
//             Vector3 worldPoint1,
//             Vector3 worldPoint2,
//             Camera camera)
//         {
//             Vector2 screen1 = WorldToScreenPoint(worldPoint1, camera);
//             Vector2 screen2 = WorldToScreenPoint(worldPoint2, camera);

//             if (Mathf.Abs(screen2.x - screen1.x) < 0.001f)
//             {
//                 // Vertical line
//                 return (0, (screen1.y + screen2.y) / 2f);
//             }

//             float a = (screen2.y - screen1.y) / (screen2.x - screen1.x);
//             float b = screen1.y - a * screen1.x;

//             return (a, b);
//         }
//     }

//     /// <summary>
//     /// Configuration holder for layer detection parameters
//     /// </summary>
//     [System.Serializable]
//     public class LayerDetectionConfig
//     {
//         public float eps_y = 28f;
//         public float band = 12f;
//         public float alpha_line = 0.4f;
//         public float alpha_obj = 0.7f;
//         public int minPts = 2;
//         public int maxConsecutiveFailures = 10;
//         public float trackingGate = 50f;
//         public int votingM = 5;
//         public int votingN = 3;
//         public int keepFrames = 12;

//         // Adjust parameters based on resolution
//         public void AdjustForResolution(int height)
//         {
//             if (height < 800)
//             {
//                 eps_y = 20f;
//                 band = 10f;
//                 trackingGate = 40f;
//             }
//             else if (height > 1400)
//             {
//                 eps_y = 35f;
//                 band = 15f;
//                 trackingGate = 60f;
//             }
//             else
//             {
//                 eps_y = 28f;
//                 band = 12f;
//                 trackingGate = 50f;
//             }
//         }

//         // Get adaptive RANSAC inlier threshold based on band
//         public float GetRANSACInlierThreshold()
//         {
//             // Use band size as base for inlier threshold
//             return band * 0.8f;
//         }

//         // Get adaptive dynamic eps_y based on detected layer spacing
//         public float GetDynamicEpsY(float detectedLayerSpacing)
//         {
//             // eps_y should be 40-60% of typical layer spacing
//             return Mathf.Clamp(detectedLayerSpacing * 0.5f, eps_y * 0.5f, eps_y * 2f);
//         }
//     }
// }

