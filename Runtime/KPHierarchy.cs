// 骨頭的來源：建骨鏈時只透過這個介面看骨架（父、子、名字、建立時的姿勢、是不是碰撞體物件）。
// 骨頭以「節點 id」（int）表示。Transform 版把場景上的 Transform 登記成 id；
// 陣列版（v0.5 的 KPRig）直接用骨架陣列的索引。建骨鏈、細分、約束、SyncBone、碰撞資產都共用同一套程式。
using System.Collections.Generic;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    internal abstract class KPHierarchy
    {
        /// <summary>沒有這個節點（找不到、null）。</summary>
        public const int None = -1;

        /// <summary>節點還在（Transform 版：還沒被刪掉）。</summary>
        public abstract bool IsAlive(int node);
        public abstract int Parent(int node);
        public abstract int ChildCount(int node);
        public abstract int Child(int node, int index);
        public abstract string Name(int node);
        /// <summary>這個節點是碰撞體物件（建骨鏈時不當成子骨頭）。</summary>
        public abstract bool IsCollider(int node);

        public abstract Vector3 Position(int node);
        public abstract Quaternion Rotation(int node);
        public abstract Vector3 LocalPosition(int node);
        public abstract Quaternion LocalRotation(int node);
        public abstract float LossyScaleX(int node);
        public abstract Vector3 TransformDirection(int node, Vector3 direction);

        /// <summary>骨頭放回靜止姿勢（停用、移除、重建前）。</summary>
        public abstract void SetLocalPositionAndRotation(int node, Vector3 position, Quaternion rotation);

        /// <summary>最上層的祖先（Transform.root）。</summary>
        public int Root(int node)
        {
            if (node < 0) return None;
            for (var p = Parent(node); p >= 0; p = Parent(p)) node = p;
            return node;
        }

        /// <summary>自己或子孫裡第一個叫這個名字的（深度優先、子的順序）。</summary>
        public int FindDeep(int root, string name)
        {
            if (root < 0 || string.IsNullOrEmpty(name)) return None;
            if (Name(root) == name) return root;
            var n = ChildCount(root);
            for (int i = 0; i < n; i++)
            {
                var r = FindDeep(Child(root, i), name);
                if (r >= 0) return r;
            }
            return None;
        }

        /// <summary>t 在元件空間的位置（除以元件的縮放）。</summary>
        public Vector3 ComponentSpacePosition(int comp, int node)
        {
            var s = LossyScaleX(comp);
            return Quaternion.Inverse(Rotation(comp)) * (Position(node) - Position(comp)) / (Mathf.Approximately(s, 0f) ? 1f : s);
        }
    }

    /// <summary>
    /// 場景上的 Transform。第一次看到的 Transform 配一個 id；被刪掉、而且沒有段在用的 id 會回收（FreeDead）。
    /// </summary>
    internal sealed class KPTransformHierarchy : KPHierarchy
    {
        readonly List<Transform> nodes = new List<Transform>(256);
        readonly Dictionary<Transform, int> ids = new Dictionary<Transform, int>(256);
        readonly Stack<int> free = new Stack<int>();

        /// <summary>登記中的 Transform 數（測試用）。</summary>
        public int Count => ids.Count;

        /// <summary>Transform 的 id（第一次看到就登記）。null 或已刪除、沒登記過的回傳 None。</summary>
        public int Id(Transform t)
        {
            if (ReferenceEquals(t, null)) return None;
            if (ids.TryGetValue(t, out var id)) return id;
            if (t == null) return None;
            if (free.Count > 0)
            {
                id = free.Pop();
                nodes[id] = t;
            }
            else
            {
                id = nodes.Count;
                nodes.Add(t);
            }
            ids.Add(t, id);
            return id;
        }

        /// <summary>已登記的才給 id（不登記新的）。</summary>
        public int Find(Transform t) => !ReferenceEquals(t, null) && ids.TryGetValue(t, out var id) ? id : None;

        public Transform Get(int node) => node >= 0 && node < nodes.Count ? nodes[node] : null;

        /// <summary>被刪掉、而且不在 referenced 裡的 id 回收（之後登記的 Transform 會重用）。</summary>
        public void FreeDead(HashSet<int> referenced)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                var t = nodes[i];
                if (ReferenceEquals(t, null) || t != null || referenced.Contains(i)) continue;
                ids.Remove(t);
                nodes[i] = null;
                free.Push(i);
            }
        }

        public void Clear()
        {
            nodes.Clear();
            ids.Clear();
            free.Clear();
        }

        public override bool IsAlive(int node) => Get(node) != null;
        public override int Parent(int node) => Id(nodes[node].parent);
        public override int ChildCount(int node) => nodes[node].childCount;
        public override int Child(int node, int index) => Id(nodes[node].GetChild(index));
        public override string Name(int node) => nodes[node].name;
        // TryGetComponent：找不到時不會像 GetComponent 在編輯器裡產生假 null 物件（慢、會配置）
        public override bool IsCollider(int node) => nodes[node].TryGetComponent<KawaiiPhysicsCollider>(out _);
        public override Vector3 Position(int node) => nodes[node].position;
        public override Quaternion Rotation(int node) => nodes[node].rotation;
        public override Vector3 LocalPosition(int node) => nodes[node].localPosition;
        public override Quaternion LocalRotation(int node) => nodes[node].localRotation;
        public override float LossyScaleX(int node) => nodes[node].lossyScale.x;
        public override Vector3 TransformDirection(int node, Vector3 direction) => nodes[node].TransformDirection(direction);

        public override void SetLocalPositionAndRotation(int node, Vector3 position, Quaternion rotation)
        {
            var t = Get(node);
            if (t != null) t.SetLocalPositionAndRotation(position, rotation);
        }
    }
}
