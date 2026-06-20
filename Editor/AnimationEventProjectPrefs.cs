using System.Collections.Generic;
using UnityEditor;
using UnityEditor.VersionControl;
using UnityEngine;

namespace AnimationEventEditor
{
    [FilePath("ProjectSettings/AnimationEventEditor.asset", FilePathAttribute.Location.ProjectFolder)]
    public class AnimationEventProjectPrefs : ScriptableSingleton<AnimationEventProjectPrefs>
    {
        public List<GameObject> favoriteModels = new();
        public GameObject referenceAnimatorOwner;

        public void SavePrefs()
        {
            Save(true);
            if(Provider.enabled)
                Provider.Checkout(this, CheckoutMode.Asset);
        }
    }
}