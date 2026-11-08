using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>
    /// 放在身體骨頭上（或底下）的碰撞體，跟著這個 Transform 走。
    /// 膠囊、錐形膠囊的軸由 direction 決定（預設 Y，跟 Unity 的 CapsuleCollider 一樣）；平面法線是區域 +Y。
    /// </summary>
    public class KawaiiPhysicsCollider : MonoBehaviour
    {
        /// <summary>碰撞的形狀。</summary>
        public enum Shape
        {
            /// <summary>球（inner 時把骨頭關在球裡）</summary>
            Sphere,
            /// <summary>膠囊</summary>
            Capsule,
            /// <summary>錐形膠囊（兩端半徑不同）</summary>
            TaperedCapsule,
            /// <summary>箱（extent 是半邊長）</summary>
            Box,
            /// <summary>平面（區域 +Y 是法線，骨頭推到正面）</summary>
            Plane,
            /// <summary>凸包（convexMesh 的三角形面）</summary>
            Convex,
        }
        /// <summary>區域空間的軸。</summary>
        public enum Axis
        {
            /// <summary>區域 X 軸</summary>
            X,
            /// <summary>區域 Y 軸</summary>
            Y,
            /// <summary>區域 Z 軸</summary>
            Z,
        }

        [Tooltip("形狀")]
        public Shape shape = Shape.Sphere;
        [Tooltip("中心（這個 Transform 區域空間的偏移）")]
        public Vector3 center;
        [Tooltip("膠囊、錐形膠囊的軸")]
        public Axis direction = Axis.Y;
        [Tooltip("半徑（公尺）；錐形膠囊是 +軸端的半徑")]
        [Min(0f)] public float radius = 0.05f;
        [Tooltip("錐形膠囊 -軸端的半徑（+軸端用 radius）")]
        [Min(0f)] public float radius1 = 0.03f;
        [Tooltip("膠囊兩端球心之間的長度")]
        [Min(0f)] public float length = 0.1f;
        public Vector3 extent = new Vector3(0.05f, 0.05f, 0.05f);
        [Tooltip("球限定：勾選時把骨頭關在球裡，不勾是往外推")]
        public bool inner;
        [Tooltip("凸包：用這個網格的三角形面當平面（網格要是凸的）")]
        public Mesh convexMesh;

        // Inspector 改了尺寸或形狀：用到它的 Chain 下一幀比對結構簽章後重建
        void OnValidate() => KPSystem.RequestValidateAll();

        /// <summary>把區域 +Y 轉到 direction 軸的旋轉。</summary>
        public Quaternion AxisRotation => KPRigCollider.AxisRotationOf(direction);

        [System.NonSerialized] KPRigCollider settings;

        /// <summary>欄位複製進自己持有的一份 KPRigCollider（跟著的骨頭＝這個 Transform 的節點 id）。</summary>
        internal KPRigCollider ToSettings(KPTransformHierarchy h)
        {
            var s = settings ??= new KPRigCollider();
            s.bone = h.Id(transform);
            s.shape = shape;
            s.center = center;
            s.direction = direction;
            s.radius = radius;
            s.radius1 = radius1;
            s.length = length;
            s.extent = extent;
            s.inner = inner;
            s.convexMesh = convexMesh;
            return s;
        }

        /// <summary>凸包網格的平面（區域空間，xyz＝外法線、w＝距離），重複的面合併。</summary>
        internal static List<float4> ConvexPlanesOf(Mesh mesh, out Bounds bounds)
        {
            var planes = new List<float4>();
            bounds = new Bounds();
            if (mesh == null || !mesh.isReadable) return planes;
            var v = mesh.vertices;
            var t = mesh.triangles;
            bounds = mesh.bounds;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                var n = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                if (n.sqrMagnitude < 1e-12f) continue;
                n.Normalize();
                var p = new float4(n, Vector3.Dot(n, v[t[i]]));
                var dup = false;
                foreach (var q in planes)
                    if (math.dot(q.xyz, p.xyz) > 0.9999f && math.abs(q.w - p.w) < 1e-4f) { dup = true; break; }
                if (!dup) planes.Add(p);
            }
            return planes;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.8f);
            var axisRot = shape == Shape.Capsule || shape == Shape.TaperedCapsule ? AxisRotation : Quaternion.identity;
            Gizmos.matrix = Matrix4x4.TRS(transform.TransformPoint(center), transform.rotation * axisRot, Vector3.one);
            switch (shape)
            {
                case Shape.Sphere:
                    Gizmos.DrawWireSphere(Vector3.zero, radius);
                    break;
                case Shape.Capsule:
                    Gizmos.DrawWireSphere(Vector3.up * length * 0.5f, radius);
                    Gizmos.DrawWireSphere(Vector3.down * length * 0.5f, radius);
                    Gizmos.DrawLine(Vector3.up * length * 0.5f, Vector3.down * length * 0.5f);
                    break;
                case Shape.TaperedCapsule:
                    Gizmos.DrawWireSphere(Vector3.up * length * 0.5f, radius);
                    Gizmos.DrawWireSphere(Vector3.down * length * 0.5f, radius1);
                    break;
                case Shape.Box:
                    Gizmos.DrawWireCube(Vector3.zero, extent * 2f);
                    break;
                case Shape.Plane:
                    Gizmos.DrawWireCube(Vector3.zero, new Vector3(1f, 0f, 1f));
                    Gizmos.DrawLine(Vector3.zero, Vector3.up * 0.2f);
                    break;
                case Shape.Convex:
                    if (convexMesh != null) Gizmos.DrawWireMesh(convexMesh);
                    break;
            }
        }
    }
}
