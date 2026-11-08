using System;
using System.Collections.Generic;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>
    /// 平移式軟組織（胸、臀、腹部等）：每根骨頭以這一幀動畫後的姿勢為錨點，像掛在阻尼彈簧上的質點，
    /// 可以離開錨點一小段再彈回（位移有上限），並可依位移傾斜、沿軸壓扁與拉伸。
    /// 跟 KawaiiPhysicsChain（保持骨長、只轉動的鐘擺）互補；同一根骨頭不要同時給兩者模擬。
    /// 實際運算由 KPSystem 在 Chain 之前用 Burst 平行處理；設定每幀讀取，改了就地生效。
    /// </summary>
    public class KawaiiPhysicsSoftBody : MonoBehaviour
    {
        /// <summary>骨頭區域空間的軸。</summary>
        public enum Axis
        {
            /// <summary>區域 X 軸</summary>
            X,
            /// <summary>區域 Y 軸</summary>
            Y,
            /// <summary>區域 Z 軸</summary>
            Z,
        }

        [Header("Bones")]
        [Tooltip("要模擬的骨頭，各自是一個彈簧。祖先也是軟組織骨頭時（不論是不是同一個元件），子骨的錨點跟著祖先的位移與伸縮，也感受祖先晃動的慣性")]
        public List<Transform> bones = new List<Transform>();
        [Tooltip("角色根（慣性拆成角色移動與動畫、重力參考用）；空的話用上層的 Animator，再沒有就用自己。站立時 +Y 要朝上")]
        public Transform referenceRoot;

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
        public Axis stretchAxis = Axis.Y;
        [Tooltip("沿軸位移到上限時伸長的比例（負值＝反向；0＝不縮放）。子骨頭跟著縮放（子軟組織骨頭的位置會跟著伸縮移動），建議只用在末端骨頭")]
        [Range(-1f, 1f)] public float stretchAmount;
        [Tooltip("維持體積：沿軸伸長時另外兩軸縮小的程度（1＝體積不變）")]
        [Range(0f, 1f)] public float volumePreservation = 1f;

        [Header("Collision")]
        [Tooltip("骨頭的碰撞半徑（公尺）")]
        [Min(0f)] public float radius = 0.03f;
        [Tooltip("碰撞體（球、膠囊、錐形膠囊、箱、平面；凸包不支援）。清單每幀讀取，增減不用重建")]
        public List<KawaiiPhysicsCollider> colliders = new List<KawaiiPhysicsCollider>();
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

        /// <summary>內部的每幀換算吃的設定（每幀從這個元件的欄位複製過去）。</summary>
        [NonSerialized] internal readonly KPRigSoftBody settings = new KPRigSoftBody();

        /// <summary>每幀的設定值複製進 settings（骨頭、角色根、碰撞體清單仍由主機直接讀這個元件）。</summary>
        internal KPRigSoftBody CopyToSettings()
        {
            var s = settings;
            s.enabled = isActiveAndEnabled;
            s.frequency = frequency;
            s.dampingRatio = dampingRatio;
            s.gravity = gravity;
            s.gravityRelativeToRestPose = gravityRelativeToRestPose;
            s.externalAcceleration = externalAcceleration;
            s.animationInertia = animationInertia;
            s.movementInertia = movementInertia;
            s.limitMin = limitMin;
            s.limitMax = limitMax;
            s.tilt = tilt;
            s.stretchAxis = stretchAxis;
            s.stretchAmount = stretchAmount;
            s.volumePreservation = volumePreservation;
            s.radius = radius;
            s.collisionPriority = collisionPriority;
            s.alpha = alpha;
            s.targetFramerate = targetFramerate;
            s.maxSubsteps = maxSubsteps;
            s.teleportDistanceThreshold = teleportDistanceThreshold;
            s.teleportRotationThreshold = teleportRotationThreshold;
            return s;
        }

        internal bool dirty = true;
        internal bool validateRequested;
        internal bool resetRequested;
        /// <summary>停用過：下次重建時不沿用狀態。</summary>
        internal bool resetOnRebuild;
        internal KPSoftBodyRuntime runtime;
        internal Vector3 pendingUniformImpulse;
        internal Vector3[] pendingImpulses;
        internal readonly List<PointImpulse> pendingPointImpulses = new List<PointImpulse>();

        internal struct PointImpulse
        {
            public Vector3 Position;
            public Vector3 VelocityChange;
            public float Radius;
        }

        void OnEnable() => KPSystem.Register(this);

        void OnDisable() => KPSystem.Unregister(this);

        // Inspector 改了東西：骨頭清單或角色根變了才重建，其他設定每幀讀取
        void OnValidate() => validateRequested = true;

        /// <summary>角色根：有指定 referenceRoot 就用它，否則用上層的 Animator，再沒有就用自己。</summary>
        public Transform ResolveRoot()
        {
            if (referenceRoot != null) return referenceRoot;
            var animator = GetComponentInParent<Animator>();
            return animator != null ? animator.transform : transform;
        }

        /// <summary>
        /// 用程式改了 bones 或 referenceRoot 之後呼叫，下一幀重建（同一根骨頭的模擬狀態沿用）。
        /// 其他設定（頻率、上限、碰撞體清單等）每幀讀取，不用呼叫。
        /// </summary>
        public void MarkDirty() => dirty = true;

        /// <summary>下一幀重新初始化：位移回到平衡位置、速度歸零。</summary>
        public void ResetDynamics() => resetRequested = true;

        /// <summary>
        /// 現在的區域姿勢記成靜止姿勢（每幀動畫前還原到這裡），父骨相對角色根的旋轉記成重力參考。
        /// 應用端改了骨頭的靜止姿勢（體型調整等）之後，在 Update 或 LateUpdate（物理寫回之前）呼叫；
        /// 重力參考要在角色站立的姿勢下記錄。
        /// </summary>
        public void CaptureRestPose() => KPSoftBodySystem.CaptureRestPose(this);

        /// <summary>所有骨頭加上速度變化（世界空間，公尺/秒），下一次模擬時生效。停用中的元件忽略。</summary>
        public void AddImpulse(Vector3 velocityChange)
        {
            if (isActiveAndEnabled) pendingUniformImpulse += velocityChange;
        }

        /// <summary>bones[boneIndex] 加上速度變化（世界空間，公尺/秒），下一次模擬時生效。停用中的元件忽略。</summary>
        /// <exception cref="ArgumentOutOfRangeException">boneIndex 超出 bones 的範圍。</exception>
        public void AddImpulse(int boneIndex, Vector3 velocityChange)
        {
            if (boneIndex < 0 || boneIndex >= bones.Count) throw new ArgumentOutOfRangeException(nameof(boneIndex));
            if (!isActiveAndEnabled) return;
            if (pendingImpulses == null || pendingImpulses.Length != bones.Count)
            {
                var resized = new Vector3[bones.Count];
                if (pendingImpulses != null) Array.Copy(pendingImpulses, resized, Mathf.Min(pendingImpulses.Length, resized.Length));
                pendingImpulses = resized;
            }
            pendingImpulses[boneIndex] += velocityChange;
        }

        /// <summary>
        /// 撞擊點附近的骨頭加上速度變化（世界空間，公尺/秒），依距離衰減：權重 (1 − 距離/radius)²，radius 以外為 0；
        /// radius ≤ 0 時不衰減。距離用模擬那一刻骨頭動畫後的位置計算。停用中的元件忽略。
        /// </summary>
        public void AddImpulseAtPosition(Vector3 worldPosition, Vector3 velocityChange, float radius)
        {
            if (isActiveAndEnabled) pendingPointImpulses.Add(new PointImpulse { Position = worldPosition, VelocityChange = velocityChange, Radius = radius });
        }

        /// <summary>還沒套用的衝量清掉（重新啟用時）。</summary>
        internal void ClearPendingImpulses()
        {
            pendingUniformImpulse = Vector3.zero;
            if (pendingImpulses != null) Array.Clear(pendingImpulses, 0, pendingImpulses.Length);
            pendingPointImpulses.Clear();
        }

        /// <summary>bones[boneIndex] 有沒有在模擬（還沒建置、null 或已刪除、被其他元件先拿走的骨頭是 false）。</summary>
        public bool IsSimulating(int boneIndex) => WorldBone(boneIndex) >= 0;

        /// <summary>bones[boneIndex] 目前離開錨點的位移（世界空間，公尺；alpha 混合之前）。沒在模擬時是零。</summary>
        public Vector3 GetOffset(int boneIndex)
        {
            var wi = WorldBone(boneIndex);
            return wi >= 0 ? (Vector3)KPSoftBodySystem.World.Bones[wi].Offset : Vector3.zero;
        }

        /// <summary>bones[boneIndex] 目前相對錨點的速度（世界空間，公尺/秒）。沒在模擬時是零。</summary>
        public Vector3 GetVelocity(int boneIndex)
        {
            var wi = WorldBone(boneIndex);
            return wi >= 0 ? (Vector3)KPSoftBodySystem.World.Bones[wi].Velocity : Vector3.zero;
        }

        // 用 Transform 找（清單改過、還沒重建時，索引對的是現在的清單）
        int WorldBone(int boneIndex)
        {
            if (boneIndex < 0 || boneIndex >= bones.Count) return -1;
            // 清單裡重複的骨頭只有第一次算（建置時重複的也略過）
            var t = bones[boneIndex];
            if (bones.IndexOf(t) != boneIndex) return -1;
            return KPSoftBodySystem.TryGetOwnBone(this, t, out var wi) ? wi : -1;
        }

        void OnDrawGizmosSelected()
        {
            foreach (var t in bones)
            {
                if (t == null) continue;
                Gizmos.matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                Gizmos.color = new Color(1f, 0.5f, 0.7f, 0.8f);
                Gizmos.DrawWireCube((limitMin + limitMax) * 0.5f, limitMax - limitMin);
                Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.8f);
                Gizmos.DrawWireSphere(Vector3.zero, radius);
            }
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
