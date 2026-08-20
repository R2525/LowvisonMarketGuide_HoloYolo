using System;
using System.Collections.Generic;
using YoloHolo.Services;

namespace YoloHolo.YoloLabeling
{
    [Serializable]
    public class DemoModelTranslator : IYoloClassTranslator
    {
        public string GetName(int classIndex)
        {
            if (classIndex < 0 || classIndex >= detectableObjects.Count)
                return "Unknown";
            return detectableObjects[classIndex];
        }

        private static List<string> detectableObjects = new()
        {
            "box"
        };
    }
}
