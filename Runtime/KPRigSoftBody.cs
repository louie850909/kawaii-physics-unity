// 陣列接口的軟組織設定：欄位名稱與預設值跟 KawaiiPhysicsSoftBody 一樣，只有骨頭參照改成骨架的索引（int，-1＝沒有）。
// KawaiiPhysicsSoftBody 每幀把設定值複製進自己持有的一份，交給同一套每幀的換算。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>
    /// 一組軟組織骨頭的設定（陣列接口用）。欄位與 KawaiiPhysicsSoftBody 相同（說明見那邊），骨頭參照是骨架的索引（-1＝沒有）。
    /// </summary>
    [Serializable]
    public class KPRigSoftBody
    {
        [Header("Bones")]
        [Tooltip("要模擬的骨頭，各自是一個彈簧。祖先也是軟組織骨頭時（不論是不是同一個元件），子骨的錨點跟著祖先的位移與伸縮，也感受祖先晃動的慣性")]
        public List<int> bones = new List<int>();
        [Tooltip("角色根（慣性拆成角色移動與動畫、重力參考用）；空的話用上層的 Animator，再沒有就用自己。站立時 +Y 要朝上")]
        public int referenceRoot = -1;

        [Header("Spring")]
        [Tooltip("振動頻率（Hz）：無阻尼時每秒來回幾次。越大越硬")]
        [Min(0.01f)] public float frequency = 3f;
        [Tooltip("阻尼比：0＝不衰減、1＝臨界阻尼（不回彈）、大於 1＝過阻尼")]
        [Min(0f)] public float dampingRatio = 0.25f;
        [Tooltip("重力加速度（世界空間，公尺/秒²）。平衡時的下垂量＝重力 ÷ (2π×頻率)²")]
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);
        [Tooltip("重力相對靜止姿勢：站立時的形狀已經包含重力，只有姿勢偏離站立（躺、趴、倒立）時才往重力方向偏")]
        public bool gravityRelativeToRestPose = true;
        [Tooltip("額外的加速度（世界空間，公尺/秒²），應用端可以每幀改")]
        public Vector3 externalAcceleration;

        [Header("Inertia")]
        [Tooltip("動畫造成的錨點加減速的慣性倍率（0＝跟著動畫走、不晃；1＝真實慣性）。祖先軟組織骨頭的晃動不受這個影響，一律是真實慣性")]
        [Range(0f, 2f)] public float animationInertia = 1f;
        [Tooltip("角色根移動、轉向造成的慣性倍率（0＝跟著角色走、不晃；1＝真實慣性）")]
        [Range(0f, 2f)] public float movementInertia = 1f;

        [Header("Limit（骨頭區域軸向，公尺）")]
        [Tooltip("各軸往負方向最多離開錨點多遠（≤0；0＝鎖住這個方向）")]
        public Vector3 limitMin = new Vector3(-0.05f, -0.05f, -0.05f);
        [Tooltip("各軸往正方向最多離開錨點多遠（≥0；0＝鎖住這個方向）")]
        public Vector3 limitMax = new Vector3(0.05f, 0.05f, 0.05f);

        [Header("Shape")]
        [Tooltip("依位移傾斜：骨頭朝『父骨 → 位移後的位置』轉動的比例（0＝只平移）")]
        [Range(0f, 1f)] public float tilt;
        [Tooltip("壓扁與拉伸的軸（骨頭區域）")]
        public KawaiiPhysicsSoftBody.Axis stretchAxis = KawaiiPhysicsSoftBody.Axis.Y;
        [Tooltip("沿軸位移到上限時伸長的比例（負值＝反向；0＝不縮放）。子骨頭跟著縮放（子軟組織骨頭的位置會跟著伸縮移動），建議只用在末端骨頭")]
        [Range(-1f, 1f)] public float stretchAmount;
        [Tooltip("維持體積：沿軸伸長時另外兩軸縮小的程度（1＝體積不變）")]
        [Range(0f, 1f)] public float volumePreservation = 1f;

        [Header("Collision")]
        [Tooltip("骨頭的碰撞半徑（公尺）")]
        [Min(0f)] public float radius = 0.03f;
        [Tooltip("碰撞體（球、膠囊、錐形膠囊、箱、平面；凸包不支援）。清單每幀讀取，增減不用重建")]
        public List<KPRigCollider> colliders = new List<KPRigCollider>();
        [Tooltip("碰撞優先：碰撞體壓過來時骨頭可以被推到位移上限之外（不穿透）。關掉時位移上限優先（碰撞體可能穿進骨頭）")]
        public bool collisionPriority = true;

        [Header("Simulation")]
        [Tooltip("物理結果的混合比例（0＝完全照動畫）。它是動畫與物理的混合比例、不是力：改了（一次跳或漸變）時子孫軟組織骨頭不把因此的移動當成加速；祖先真的晃動照常傳下去")]
        [Range(0f, 1f)] public float alpha = 1f;
        [Tooltip("子步進的步頻（每秒步數）。彈簧本身是解析解，子步進只影響碰撞與位移上限的精度")]
        [Min(1)] public int targetFramerate = 60;
        [Tooltip("每幀最多幾個子步進")]
        [Range(1, 16)] public int maxSubsteps = 4;
        [Tooltip("角色根一幀移動超過這個距離（公尺）就當成瞬移、重置（0＝不判定）")]
        [Min(0f)] public float teleportDistanceThreshold = 3f;
        [Tooltip("角色根一幀轉動超過這個角度（度）就當成瞬移、重置（0＝不判定）")]
        [Min(0f)] public float teleportRotationThreshold = 90f;

        [Tooltip("模擬中（停用時骨頭維持動畫姿勢；元件版對應 isActiveAndEnabled）")]
        public bool enabled = true;

        internal bool dirty = true;
        internal bool validateRequested;
        internal bool resetRequested;
        /// <summary>停用過：下次重建時不沿用狀態。</summary>
        internal bool resetOnRebuild;
        internal KPSoftBodyRuntime runtime;
        internal Vector3 pendingUniformImpulse;
        internal Vector3[] pendingImpulses;
        internal readonly List<PointImpulse> pendingPointImpulses = new List<PointImpulse>();
        /// <summary>元件版：持有這份設定的元件（警告訊息與元件被刪掉的判斷用）；陣列接口是 null。</summary>
        internal KawaiiPhysicsSoftBody component;

        internal struct PointImpulse
        {
            public Vector3 Position;
            public Vector3 VelocityChange;
            public float Radius;
        }

        /// <summary>警告訊息裡的名稱。</summary>
        internal string Label => component != null ? component.name : "KPRigSoftBody";
        /// <summary>元件版的元件被刪掉了。</summary>
        internal bool OwnerDestroyed => !ReferenceEquals(component, null) && component == null;

        /// <summary>改了 bones 或 referenceRoot 之後呼叫，下一次排程時重建（同一根骨頭的模擬狀態沿用）。其他設定每幀讀取。</summary>
        public void MarkDirty() => dirty = true;

        /// <summary>下一次模擬時重新初始化：位移回到平衡位置、速度歸零。</summary>
        public void ResetDynamics() => resetRequested = true;

        /// <summary>所有骨頭加上速度變化（世界空間，公尺/秒），下一次模擬時生效。停用中（enabled＝false）忽略。</summary>
        public void AddImpulse(Vector3 velocityChange)
        {
            if (enabled) PushImpulse(velocityChange);
        }

        /// <summary>bones[boneIndex] 加上速度變化（世界空間，公尺/秒），下一次模擬時生效。停用中忽略。</summary>
        /// <exception cref="ArgumentOutOfRangeException">boneIndex 超出 bones 的範圍。</exception>
        public void AddImpulse(int boneIndex, Vector3 velocityChange)
        {
            if (boneIndex < 0 || boneIndex >= bones.Count) throw new ArgumentOutOfRangeException(nameof(boneIndex));
            if (enabled) PushImpulse(boneIndex, velocityChange, bones.Count);
        }

        /// <summary>撞擊點附近的骨頭加上速度變化（世界空間），依距離衰減：權重 (1 − 距離/radius)²；radius ≤ 0 時不衰減。停用中忽略。</summary>
        public void AddImpulseAtPosition(Vector3 worldPosition, Vector3 velocityChange, float radius)
        {
            if (enabled) PushImpulseAtPosition(worldPosition, velocityChange, radius);
        }

        /// <summary>bones[boneIndex] 有沒有在模擬（還沒建置、索引無效、被其他軟組織先拿走的骨頭是 false）。</summary>
        public bool IsSimulating(int boneIndex) => runtime?.Host != null && runtime.Host.WorldBone(this, boneIndex) >= 0;

        /// <summary>bones[boneIndex] 目前離開錨點的位移（世界空間，公尺；alpha 混合之前）。沒在模擬時是零。</summary>
        public Vector3 GetOffset(int boneIndex) => runtime?.Host != null ? runtime.Host.Offset(this, boneIndex) : Vector3.zero;

        /// <summary>bones[boneIndex] 目前相對錨點的速度（世界空間，公尺/秒）。沒在模擬時是零。</summary>
        public Vector3 GetVelocity(int boneIndex) => runtime?.Host != null ? runtime.Host.Velocity(this, boneIndex) : Vector3.zero;

        internal void PushImpulse(Vector3 velocityChange) => pendingUniformImpulse += velocityChange;

        internal void PushImpulse(int boneIndex, Vector3 velocityChange, int boneCount)
        {
            if (pendingImpulses == null || pendingImpulses.Length != boneCount)
            {
                var resized = new Vector3[boneCount];
                if (pendingImpulses != null) Array.Copy(pendingImpulses, resized, Mathf.Min(pendingImpulses.Length, resized.Length));
                pendingImpulses = resized;
            }
            pendingImpulses[boneIndex] += velocityChange;
        }

        internal void PushImpulseAtPosition(Vector3 worldPosition, Vector3 velocityChange, float radius) =>
            pendingPointImpulses.Add(new PointImpulse { Position = worldPosition, VelocityChange = velocityChange, Radius = radius });

        /// <summary>還沒套用的衝量清掉（重新啟用時）。</summary>
        internal void ClearPendingImpulses()
        {
            pendingUniformImpulse = Vector3.zero;
            if (pendingImpulses != null) Array.Clear(pendingImpulses, 0, pendingImpulses.Length);
            pendingPointImpulses.Clear();
        }

        /// <summary>撞擊點衝量在 pos 的那一份（KawaiiPhysicsSoftBody 與 KPRig 共用）。</summary>
        internal static Vector3 PointImpulseAt(Vector3 pos, Vector3 point, Vector3 velocityChange, float radius)
        {
            var w = 1f;
            if (radius > 0f)
            {
                w = Mathf.Max(0f, 1f - Vector3.Distance(pos, point) / radius);
                w *= w;
            }
            return velocityChange * w;
        }
    }
}
