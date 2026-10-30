// KawaiiPhysicsChain 的 Inspector：預設欄位＋「新增外力」按鈕（外力清單的元素用 KPExternalForceDrawer 選型別）。
using UnityEditor;
using UnityEngine;

namespace KawaiiPhysicsUnity.Editor
{
    /// <summary>KawaiiPhysicsChain 的 Inspector：預設欄位＋「新增外力」按鈕。</summary>
    [CustomEditor(typeof(KawaiiPhysicsChain)), CanEditMultipleObjects]
    public class KawaiiPhysicsChainEditor : UnityEditor.Editor
    {
        /// <summary>畫 Inspector。</summary>
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            var rect = GUILayoutUtility.GetRect(new GUIContent("新增外力 ▾"), EditorStyles.miniPullDown);
            if (EditorGUI.DropdownButton(rect, new GUIContent("新增外力 ▾"), FocusType.Keyboard))
            {
                var menu = new GenericMenu();
                foreach (var t in KPExternalForceMenu.ForceTypes)
                {
                    var type = t;
                    menu.AddItem(new GUIContent(KPExternalForceMenu.DisplayName(type)), false, () => KPExternalForceMenu.Add(targets, type));
                }
                menu.DropDown(rect);
            }
        }
    }
}
