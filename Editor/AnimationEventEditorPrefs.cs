using UnityEditor;

namespace AnimationEventEditor
{
    [FilePath("AnimationEventEditorPrefs.asset", FilePathAttribute.Location.PreferencesFolder)]
    public class AnimationEventEditorPrefs : ScriptableSingleton<AnimationEventEditorPrefs>
    {
        // Panel state
        public bool showFavorites = true;
        public float favoritesPanelWidth = 150f;

        public void SavePrefs() => Save(true);
    }
}