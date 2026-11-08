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
        public abstract Vector3 LocalScale(int node);
        /// <summary>localToWorldMatrix（陣列版：建立時的姿勢，根在原點）。</summary>
        public abstract Matrix4x4 LocalToWorld(int node);

        /// <summary>骨頭放回靜止姿勢（停用、移除、重建前）。</summary>
        public abstract void SetLocalPositionAndRotation(int node, Vector3 position, Quaternion rotation);
        /// <summary>骨頭放回靜止姿勢，含縮放（軟組織）。</summary>
        public abstract void SetLocalTRS(int node, Vector3 position, Quaternion rotation, Vector3 scale);

        /// <summary>設定裡的元件（角色根）→ 節點。陣列版的 -1 是骨架的根（骨架外面、所有根骨頭的父）。</summary>
        public virtual int ResolveComponent(int component) => component;

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
        public override Vector3 LocalScale(int node) => nodes[node].localScale;
        public override Matrix4x4 LocalToWorld(int node) => nodes[node].localToWorldMatrix;

        public override void SetLocalPositionAndRotation(int node, Vector3 position, Quaternion rotation)
        {
            var t = Get(node);
            if (t != null) t.SetLocalPositionAndRotation(position, rotation);
        }

        public override void SetLocalTRS(int node, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            var t = Get(node);
            if (t == null) return;
            t.SetLocalPositionAndRotation(position, rotation);
            t.localScale = scale;
        }
    }

    /// <summary>
    /// 陣列版的骨架（KPRig）：節點 0..BoneCount−1 是骨頭，節點 BoneCount 是骨架的根（骨架外面，元件空間的原點）。
    /// 建立時的姿勢是靜止姿勢，根放在原點（建骨鏈只用到相對的距離、方向，跟根在世界的哪裡無關）。
    /// </summary>
    internal sealed class KPArrayHierarchy : KPHierarchy
    {
        public readonly int BoneCount;
        public int RootNode => BoneCount;
        readonly int[] parents;
        readonly string[] names;
        readonly bool[] colliders;
        readonly int[][] children;
        readonly Vector3[] restLocalPosition;
        readonly Quaternion[] restLocalRotation;
        readonly Vector3[] restLocalScale;
        readonly Matrix4x4[] restModel;
        readonly Quaternion[] restModelRotation;

        /// <summary>parents 要父在子之前（索引比子小）；兄弟的順序是索引的順序。</summary>
        public KPArrayHierarchy(int[] parents, string[] names, Vector3[] localPositions, Quaternion[] localRotations, Vector3[] localScales,
            bool[] colliderBones)
        {
            BoneCount = parents.Length;
            this.parents = (int[])parents.Clone();
            this.names = (string[])names.Clone();
            colliders = colliderBones != null ? (bool[])colliderBones.Clone() : new bool[BoneCount];
            restLocalPosition = (Vector3[])localPositions.Clone();
            restLocalRotation = (Quaternion[])localRotations.Clone();
            restLocalScale = new Vector3[BoneCount];
            for (int i = 0; i < BoneCount; i++) restLocalScale[i] = localScales != null ? localScales[i] : Vector3.one;
            restModel = new Matrix4x4[BoneCount];
            restModelRotation = new Quaternion[BoneCount];
            var childLists = new List<int>[BoneCount + 1];
            for (int i = 0; i <= BoneCount; i++) childLists[i] = new List<int>();
            for (int i = 0; i < BoneCount; i++)
            {
                var p = parents[i];
                var local = Matrix4x4.TRS(localPositions[i], localRotations[i], localScales != null ? localScales[i] : Vector3.one);
                restModel[i] = p < 0 ? local : restModel[p] * local;
                restModelRotation[i] = p < 0 ? localRotations[i] : restModelRotation[p] * localRotations[i];
                childLists[p < 0 ? BoneCount : p].Add(i);
            }
            children = new int[BoneCount + 1][];
            for (int i = 0; i <= BoneCount; i++) children[i] = childLists[i].ToArray();
        }

        public int ParentBone(int bone) => parents[bone];
        public string BoneName(int bone) => names[bone];
        public Matrix4x4 RestModel(int bone) => restModel[bone];
        public Quaternion RestModelRotation(int bone) => restModelRotation[bone];

        public bool IsValid(int node) => node >= 0 && node <= BoneCount;
        public override int ResolveComponent(int component) => component >= 0 ? component : RootNode;
        public override bool IsAlive(int node) => IsValid(node);
        public override int Parent(int node) => node == RootNode ? None : parents[node] < 0 ? RootNode : parents[node];
        public override int ChildCount(int node) => children[node].Length;
        public override int Child(int node, int index) => children[node][index];
        public override string Name(int node) => node == RootNode ? "" : names[node];
        public override bool IsCollider(int node) => node != RootNode && colliders[node];
        public override Vector3 Position(int node) => node == RootNode ? Vector3.zero : (Vector3)restModel[node].GetColumn(3);
        public override Quaternion Rotation(int node) => node == RootNode ? Quaternion.identity : restModelRotation[node];
        public override Vector3 LocalPosition(int node) => node == RootNode ? Vector3.zero : restLocalPosition[node];
        public override Quaternion LocalRotation(int node) => node == RootNode ? Quaternion.identity : restLocalRotation[node];
        public override float LossyScaleX(int node) => node == RootNode ? 1f : ((Vector3)restModel[node].GetColumn(0)).magnitude;
        public override Vector3 TransformDirection(int node, Vector3 direction) => Rotation(node) * direction;
        public override Vector3 LocalScale(int node) => node == RootNode ? Vector3.one : restLocalScale[node];
        public override Matrix4x4 LocalToWorld(int node) => node == RootNode ? Matrix4x4.identity : restModel[node];
        // 陣列版沒有要還原的 Transform（呼叫端每幀從動畫或靜止姿勢重新開始）
        public override void SetLocalPositionAndRotation(int node, Vector3 position, Quaternion rotation) { }
        public override void SetLocalTRS(int node, Vector3 position, Quaternion rotation, Vector3 scale) { }
    }
}
