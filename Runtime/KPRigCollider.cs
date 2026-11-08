using System;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>
    /// 碰撞體的設定（陣列接口用）：欄位與 KawaiiPhysicsCollider 相同，跟著的骨頭改成骨架的索引 bone。
    /// KawaiiPhysicsCollider 也把自己的欄位複製進一份這個，交給同一套建立程式。
    /// </summary>
    [Serializable]
    public class KPRigCollider
    {
        [Tooltip("跟著的骨頭（骨架的索引）；-1＝沒有")]
        public int bone = -1;
        [Tooltip("形狀")]
        public KawaiiPhysicsCollider.Shape shape = KawaiiPhysicsCollider.Shape.Sphere;
        [Tooltip("中心（骨頭區域空間的偏移）")]
        public Vector3 center;
        [Tooltip("膠囊、錐形膠囊的軸")]
        public KawaiiPhysicsCollider.Axis direction = KawaiiPhysicsCollider.Axis.Y;
        [Tooltip("半徑（公尺）；錐形膠囊是 +軸端的半徑")]
        [Min(0f)] public float radius = 0.05f;
        [Tooltip("錐形膠囊 -軸端的半徑（+軸端用 radius）")]
        [Min(0f)] public float radius1 = 0.03f;
        [Tooltip("膠囊兩端球心之間的長度")]
        [Min(0f)] public float length = 0.1f;
        [Tooltip("箱的半邊長")]
        public Vector3 extent = new Vector3(0.05f, 0.05f, 0.05f);
        [Tooltip("球限定：勾選時把骨頭關在球裡，不勾是往外推")]
        public bool inner;
        [Tooltip("凸包：用這個網格的三角形面當平面（網格要是凸的）")]
        public Mesh convexMesh;

        /// <summary>把區域 +Y 轉到 direction 軸的旋轉。</summary>
        public Quaternion AxisRotation => AxisRotationOf(direction);

        internal static Quaternion AxisRotationOf(KawaiiPhysicsCollider.Axis direction) =>
            direction == KawaiiPhysicsCollider.Axis.X ? Quaternion.Euler(0f, 0f, -90f)
            : direction == KawaiiPhysicsCollider.Axis.Z ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
    }
}
