// 可重用的資料資產：碰撞組（LimitsDataAsset）、骨間約束組（BoneConstraintsDataAsset）、設定預設（PresetDataAsset）、
// 共用程序風（Shared Publisher 的風）。都用骨頭名稱指定，套到不同角色時用名稱在階層裡找。
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>對應 UKawaiiPhysicsLimitsDataAsset：用骨頭名稱指定的一組碰撞形狀，可選擇左右鏡像。</summary>
    [CreateAssetMenu(menuName = "KawaiiPhysics/Limits Asset")]
    public class KawaiiPhysicsLimitsAsset : ScriptableObject
    {
        /// <summary>一個碰撞形狀。</summary>
        [Serializable]
        public class Entry
        {
            [Tooltip("形狀")]
            public KawaiiPhysicsCollider.Shape shape = KawaiiPhysicsCollider.Shape.Sphere;
            [Tooltip("掛在哪根骨頭（名稱）")]
            public string boneName;
            [Tooltip("骨頭區域空間的位置偏移")]
            public Vector3 offset;
            [Tooltip("骨頭區域空間的旋轉（歐拉角，度）")]
            public Vector3 rotation;
            [Tooltip("膠囊、錐形膠囊的軸")]
            public KawaiiPhysicsCollider.Axis direction = KawaiiPhysicsCollider.Axis.Y;
            [Tooltip("半徑（公尺）；錐形膠囊是 +軸端的半徑")]
            [Min(0f)] public float radius = 0.05f;
            [Min(0f)] public float radius1 = 0.03f;
            [Min(0f)] public float length = 0.1f;
            public Vector3 extent = new Vector3(0.05f, 0.05f, 0.05f);
            [Tooltip("球：把骨頭關在球裡")]
            public bool inner;
            [Tooltip("凸包：用這個網格的三角形面當平面")]
            public Mesh convexMesh;
        }

        [Tooltip("碰撞形狀（掛在哪根骨頭用名稱指定）")]
        public List<Entry> entries = new List<Entry>();

        [Header("Mirror（對應原作用 MirrorDataTable 產生反側）")]
        [Tooltip("產生左右鏡像")]
        public bool mirror;
        [Tooltip("名稱裡含這個字串的條目，換成 mirrorTo 後再產生一份")]
        public string mirrorFrom = "_L";
        [Tooltip("鏡像後把 mirrorFrom 換成這個字串")]
        public string mirrorTo = "_R";
        [Tooltip("在骨頭區域空間裡翻轉的軸")]
        public KawaiiPhysicsCollider.Axis mirrorAxis = KawaiiPhysicsCollider.Axis.X;

        /// <summary>含鏡像後的全部條目。已經有同名骨頭條目的反側不再產生（原作 bSkipMirroredBoneWithExistingCollision）。</summary>
        public List<Entry> Resolve()
        {
            var list = new List<Entry>(entries);
            if (!mirror || string.IsNullOrEmpty(mirrorFrom)) return list;
            var existing = new HashSet<string>();
            foreach (var e in entries) existing.Add(e.boneName + "/" + e.shape);
            foreach (var e in entries)
            {
                if (e.boneName == null || !e.boneName.Contains(mirrorFrom)) continue;
                var name = e.boneName.Replace(mirrorFrom, mirrorTo);
                if (existing.Contains(name + "/" + e.shape)) continue;
                var m = JsonUtility.FromJson<Entry>(JsonUtility.ToJson(e));
                m.convexMesh = e.convexMesh;
                m.boneName = name;
                var axis = (int)mirrorAxis;
                var o = m.offset;
                o[axis] = -o[axis];
                m.offset = o;
                // 鏡像的旋轉：翻轉軸以外的兩個歐拉分量取負
                var r = m.rotation;
                for (int i = 0; i < 3; i++) if (i != axis) r[i] = -r[i];
                m.rotation = r;
                list.Add(m);
            }
            return list;
        }
    }
}
