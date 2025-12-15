// YoloHoloUtilities.cs (Unified Helper Script)

using UnityEngine;

namespace YoloHolo.Utilities
{
    public static class YoloHoloUtilities
    {
        /// <summary>
        /// Creates a copy of the camera transform for async processing
        /// </summary>
        public static Transform CopyCameraTransform(this Camera camera)
        {
            GameObject tempGO = new GameObject("TempCameraTransform");
            tempGO.transform.position = camera.transform.position;
            tempGO.transform.rotation = camera.transform.rotation;
            tempGO.transform.localScale = camera.transform.localScale;
            return tempGO.transform;
        }

        /// <summary>
        /// Converts RenderTexture to Texture2D for processing
        /// </summary>
        public static Texture2D ToTexture2D(this RenderTexture renderTexture)
        {
            Texture2D texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, false);
            RenderTexture currentActive = RenderTexture.active;
            RenderTexture.active = renderTexture;
            texture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
            texture.Apply();
            RenderTexture.active = currentActive;
            return texture;
        }
    }
}