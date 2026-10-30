// 外力（KawaiiPhysicsChain.externalForces 的 [SerializeReference] 元素）的 Inspector：
// 每個元素的標題列有型別選單（換型別、清空），展開後照一般欄位顯示。
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace KawaiiPhysicsUnity.Editor
{
    /// <summary>外力欄位的 Inspector：標題列的型別選單（換型別、清空）＋欄位。</summary>
    [CustomPropertyDrawer(typeof(KPExternalForce), true)]
    public class KPExternalForceDrawer : PropertyDrawer
    {
        /// <summary>欄位的高度。</summary>
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var height = EditorGUIUtility.singleLineHeight;
            if (property.managedReferenceValue == null || !property.isExpanded) return height;
            foreach (var child in KPExternalForceMenu.VisibleChildren(property))
                height += EditorGUI.GetPropertyHeight(child, true) + EditorGUIUtility.standardVerticalSpacing;
            return height;
        }

        /// <summary>畫欄位。</summary>
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            var current = property.managedReferenceValue?.GetType();
            var typeRect = new Rect(line.x + EditorGUIUtility.labelWidth, line.y, line.width - EditorGUIUtility.labelWidth, line.height);
            var caption = current != null ? KPExternalForceMenu.DisplayName(current) : "（選擇外力類型）";
            if (EditorGUI.DropdownButton(typeRect, new GUIContent(caption), FocusType.Keyboard))
                KPExternalForceMenu.Show(property.serializedObject.targetObjects, property.propertyPath, current);

            var labelRect = new Rect(line.x, line.y, EditorGUIUtility.labelWidth, line.height);
            if (current == null)
            {
                EditorGUI.LabelField(labelRect, label);
            }
            else
            {
                property.isExpanded = EditorGUI.Foldout(labelRect, property.isExpanded, label, true);
                if (property.isExpanded)
                {
                    // 子欄位逐一畫（對自己呼叫 PropertyField 會再進到這個 drawer）
                    EditorGUI.indentLevel++;
                    var y = line.yMax + EditorGUIUtility.standardVerticalSpacing;
                    foreach (var child in KPExternalForceMenu.VisibleChildren(property))
                    {
                        var h = EditorGUI.GetPropertyHeight(child, true);
                        EditorGUI.PropertyField(new Rect(position.x, y, position.width, h), child, true);
                        y += h + EditorGUIUtility.standardVerticalSpacing;
                    }
                    EditorGUI.indentLevel--;
                }
            }
            EditorGUI.EndProperty();
        }
    }

    /// <summary>外力型別的清單與設定（Inspector 的選單、測試、編輯器腳本共用）。</summary>
    public static class KPExternalForceMenu
    {
        /// <summary>可以加的外力型別（KPExternalForce 的非抽象子類別、有無參數建構子；使用者自訂的也會列出）。</summary>
        public static Type[] ForceTypes => TypeCache.GetTypesDerivedFrom<KPExternalForce>()
            .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t.Name)
            .ToArray();

        /// <summary>選單上顯示的型別名稱（去掉 KPForce 前綴）。</summary>
        public static string DisplayName(Type t) => t.Name.StartsWith("KPForce") ? t.Name.Substring("KPForce".Length) : t.Name;

        /// <summary>把 path 的外力換成 type 的新實例（null＝清空）；可以復原。</summary>
        public static void SetType(UnityEngine.Object[] targets, string path, Type type)
        {
            // 多選時每個物件各自處理：各自一個新實例，清單長度不同也不會互相影響
            foreach (var target in targets)
            {
                var so = new SerializedObject(target);
                var p = so.FindProperty(path);
                if (p == null) continue;
                p.managedReferenceValue = type != null ? Activator.CreateInstance(type) : null;
                p.isExpanded = true;
                so.ApplyModifiedProperties();
            }
        }

        /// <summary>在 Chain 的外力清單最後加一個 type 的新實例；可以復原。</summary>
        public static void Add(UnityEngine.Object[] chains, Type type)
        {
            // 多選時每個 Chain 各自加在自己的最後（一起處理的話 arraySize 是最短的那個，會截掉其他 Chain 的外力）
            foreach (var chain in chains)
            {
                var so = new SerializedObject(chain);
                var list = so.FindProperty(nameof(KawaiiPhysicsChain.externalForces));
                list.arraySize++;
                var element = list.GetArrayElementAtIndex(list.arraySize - 1);
                element.managedReferenceValue = Activator.CreateInstance(type);
                element.isExpanded = true;
                so.ApplyModifiedProperties();
            }
        }

        internal static void Show(UnityEngine.Object[] targets, string path, Type current)
        {
            var menu = new GenericMenu();
            foreach (var t in ForceTypes)
                menu.AddItem(new GUIContent(DisplayName(t)), t == current, () => SetType(targets, path, t));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("（清空）"), current == null, () => SetType(targets, path, null));
            menu.ShowAsContext();
        }

        internal static System.Collections.Generic.IEnumerable<SerializedProperty> VisibleChildren(SerializedProperty property)
        {
            var it = property.Copy();
            var end = property.GetEndProperty();
            if (!it.NextVisible(true)) yield break;
            while (!SerializedProperty.EqualContents(it, end))
            {
                yield return it.Copy();
                if (!it.NextVisible(false)) yield break;
            }
        }
    }
}
