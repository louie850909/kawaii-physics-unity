// 陣列接口的骨鏈設定：欄位名稱與預設值跟 KawaiiPhysicsChain 一樣，只有骨頭參照改成骨架的索引（int，-1＝沒有）。
// 套件內部只有一條吃這個類別的流程：KawaiiPhysicsChain 每幀把自己的欄位複製進自己持有的一份，KPRig（v0.5）直接用。
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>
    /// 一條骨鏈的設定（陣列接口用）。欄位與 KawaiiPhysicsChain 相同（說明見那邊），骨頭參照是骨架的索引（-1＝沒有）。
    /// 執行時的操作（暫時外力、設定倍率、ResetDynamics）也跟元件一樣。
    /// </summary>
    [Serializable]
    public class KPRigChain
    {
        /// <summary>額外的起點骨頭。</summary>
        [Serializable]
        public class RootBoneSetting
        {
            [Tooltip("起點骨頭（骨架的索引）")]
            public int rootBone = -1;
            [Tooltip("用自己的排除清單（不用元件的 excludeBones）")]
            public bool useOverrideExcludeBones;
            [Tooltip("這個起點用的排除清單")]
            public List<int> overrideExcludeBones = new List<int>();
        }

        /// <summary>骨間約束：兩根骨頭之間維持建立時的距離（XPBD）。</summary>
        [Serializable]
        public class BoneConstraint
        {
            [Tooltip("約束的第一根骨頭")]
            public int bone1 = -1;
            [Tooltip("約束的第二根骨頭")]
            public int bone2 = -1;
            [Tooltip("用這條約束自己的柔軟度（不用全域預設）")]
            public bool overrideCompliance;
            [Tooltip("這條約束的柔軟度")]
            public KPComplianceType complianceType = KPComplianceType.Leather;
            [Tooltip("這條約束不插入橋接假骨")]
            public bool excludeFromSubdivision;
        }

        /// <summary>SyncBone 推動的目標。</summary>
        [Serializable]
        public class SyncTargetRoot
        {
            [Tooltip("目標骨頭")]
            public int bone = -1;
            [Tooltip("連子骨頭一起推動")]
            public bool includeChildBones = true;
            [Tooltip("依骨長比例乘上的倍率")]
            public AnimationCurve scaleCurveByBoneLengthRate = new AnimationCurve();
        }

        /// <summary>對應 FKawaiiPhysicsSyncBone：來源骨頭偏離靜止姿勢的量推動目標骨頭的姿勢（例：大腿推裙子）。</summary>
        [Serializable]
        public class SyncBone
        {
            [Tooltip("來源骨頭（例：大腿）")]
            public int bone = -1;
            [Tooltip("被推動的骨頭")]
            public List<SyncTargetRoot> targetRoots = new List<SyncTargetRoot>();
            [Tooltip("推動量各軸的倍率")]
            public Vector3 globalScale = Vector3.one;
            [Tooltip("依移動距離（公尺）乘上的倍率")]
            public AnimationCurve scaleCurveByDeltaDistance = new AnimationCurve();
            [Tooltip("X 軸只套用正向、負向、兩者或不套用")]
            public KPSyncDirection applyDirectionX = KPSyncDirection.Both;
            [Tooltip("Y 軸只套用正向、負向、兩者或不套用")]
            public KPSyncDirection applyDirectionY = KPSyncDirection.Both;
            [Tooltip("Z 軸只套用正向、負向、兩者或不套用")]
            public KPSyncDirection applyDirectionZ = KPSyncDirection.Both;
            [Tooltip("依目標骨頭與來源骨頭的距離衰減")]
            public bool enableDistanceAttenuation;
            [Tooltip("距離小於這個值不衰減（公尺）")]
            public float attenuationInnerRadius;
            [Tooltip("距離大於這個值衰減到 maxAttenuationRate（公尺）")]
            public float attenuationOuterRadius;
            [Tooltip("最遠處的衰減比例")]
            [Range(0f, 1f)] public float maxAttenuationRate = 1f;
        }

        [Header("Bones")]
        [Tooltip("模擬的起點骨頭；它底下的骨頭（排除清單以外）全部模擬。起點骨頭本身跟著動畫")]
        public int rootBone = -1;
        [Tooltip("不模擬的骨頭（連同它底下的骨頭）")]
        public List<int> excludeBones = new List<int>();
        [Tooltip("其他的起點骨頭（例如左右兩束頭髮放在同一個元件）；可以各自指定排除清單")]
        public List<RootBoneSetting> additionalRootBones = new List<RootBoneSetting>();
        [Tooltip("末端假骨的長度（公尺）；0 表示不加。末端骨頭沒有子骨頭時，要靠它才會轉")]
        [Min(0f)] public float dummyBoneLength;
        [Tooltip("骨頭的前方軸，只用在末端假骨的方向")]
        public KawaiiPhysicsChain.Axis boneForwardAxis = KawaiiPhysicsChain.Axis.YPositive;
        [Tooltip("元件（角色根）。不指定時用上層的 Animator，再沒有就用自己")]
        public int component = -1;

        [Header("Bone Subdivision")]
        [Tooltip("相鄰骨頭之間至少插入幾個假骨（改善碰撞，例如裙子穿腿）")]
        [Range(0, 10)] public int boneSubdivisionCount;
        [Tooltip("插入的假骨只參加碰撞與約束，不做速度積分")]
        public bool boneSubdivisionCollisionOnly = true;
        [Tooltip("依半徑加密：骨頭間距比半徑大的地方插更多（每段最多 50）")]
        public bool boneSubdivisionDensifyByRadius;
        [Tooltip("每條橫向骨間約束插入幾個碰撞代理（填補相鄰兩列之間的縫）")]
        [Range(0, 10)] public int boneConstraintSubdivisionCount;
        [Range(0f, 2f)] public float boneConstraintSubdivisionFeedbackScale = 1f;

        [Header("Physics Settings")]
        [Tooltip("阻尼：每步速度保留 1−damping，越大越快停下")]
        [Range(0f, 1f)] public float damping = 0.1f;
        [Range(0f, 1f)] public float stiffness = 0.05f;
        [Range(0f, 1f)] public float worldDampingLocation = 0.8f;
        [Range(0f, 1f)] public float worldDampingRotation = 0.8f;
        [Min(0f)] public float radius = 0.03f;
        [Min(0f)] public float limitAngle;
        public KPPlanarConstraint planarConstraint = KPPlanarConstraint.None;
        [Tooltip("物理結果與動畫姿勢的混合比例（0＝只有動畫、1＝完全物理）")]
        [Range(0f, 1f)] public float alpha = 1f;

        [Header("Rate by Bone Length Rate（沒有 key 視為 1）")]
        [Tooltip("damping 依骨長比例（根 0 → 末端 1）乘上的倍率；沒有關鍵格時是 1")]
        public AnimationCurve dampingCurve = new AnimationCurve();
        [Tooltip("stiffness 依骨長比例（根 0 → 末端 1）乘上的倍率；沒有關鍵格時是 1")]
        public AnimationCurve stiffnessCurve = new AnimationCurve();
        [Tooltip("worldDampingLocation 依骨長比例乘上的倍率；沒有關鍵格時是 1")]
        public AnimationCurve worldDampingLocationCurve = new AnimationCurve();
        [Tooltip("worldDampingRotation 依骨長比例乘上的倍率；沒有關鍵格時是 1")]
        public AnimationCurve worldDampingRotationCurve = new AnimationCurve();
        [Tooltip("radius 依骨長比例乘上的倍率；沒有關鍵格時是 1")]
        public AnimationCurve radiusCurve = new AnimationCurve();
        [Tooltip("limitAngle 依骨長比例乘上的倍率；沒有關鍵格時是 1")]
        public AnimationCurve limitAngleCurve = new AnimationCurve();

        [Header("Simulation")]
        [Tooltip("模擬空間：元件空間（跟著角色，預設）、世界空間、基準骨空間")]
        public KPSimulationSpace simulationSpace = KPSimulationSpace.ComponentSpace;
        [Tooltip("simulationSpace＝BaseBoneSpace 時的基準骨頭")]
        public int simulationBaseBone = -1;
        [Tooltip("模擬的步頻（每秒步數）；固定子步進時每步是 1/targetFramerate 秒")]
        public int targetFramerate = 60;
        [Tooltip("用固定時間的子步進（幀率變動時結果穩定）；關閉時每幀一步")]
        public bool fixedSubstepping = true;
        [Tooltip("一幀最多幾個子步進（掉幀時不會無限追趕）")]
        [Min(1)] public int maxSubsteps = 4;
        public Vector3 skelCompMoveScale = Vector3.one;
        [Tooltip("角色一幀移動超過這個距離（公尺）就當成瞬移、物理重新開始；0＝不偵測")]
        [Min(0f)] public float teleportDistanceThreshold = 3f;
        [Min(0f)] public float teleportRotationThreshold = 10f;
        [Tooltip("開始時空跑幾幀，讓物理先穩定")]
        [Min(0)] public int warmUpFrames;
        public bool needWarmUp;
        [Tooltip("ResetDynamics 之後先暖機 warmUpFrames 幀")]
        public bool useWarmUpWhenResetDynamics = true;

        [Header("Collision")]
        [Tooltip("跟這條鏈碰撞的碰撞體")]
        public List<KPRigCollider> colliders = new List<KPRigCollider>();
        [Tooltip("碰撞資產（用骨頭名稱指定的一組碰撞形狀）")]
        public KawaiiPhysicsLimitsAsset limitsAsset;
        [Tooltip("把自己的碰撞體提供給同一角色底下、同一標籤的其他 Chain")]
        public bool sharedCollisionSource;
        [Tooltip("使用同一角色底下、同一標籤的其他 Chain 提供的碰撞體")]
        public bool useSharedCollision;
        [Tooltip("共用碰撞的標籤：同一個根物件底下、標籤相同的來源與使用端才共用")]
        public string sharedCollisionTag = "";

        [Header("Bone Constraint")]
        [Tooltip("骨間約束：兩根骨頭之間維持建立時的距離（例如裙子相鄰的兩條）")]
        public List<BoneConstraint> boneConstraints = new List<BoneConstraint>();
        [Tooltip("骨間約束資產（用骨頭名稱指定）")]
        public KawaiiPhysicsConstraintsAsset boneConstraintsAsset;
        [Tooltip("骨間約束預設的柔軟度（約束沒有個別指定時用）")]
        public KPComplianceType boneConstraintGlobalComplianceType = KPComplianceType.Leather;
        [Tooltip("碰撞之前解約束的次數")]
        [Min(0)] public int boneConstraintIterationCountBeforeCollision = 1;
        [Min(0)] public int boneConstraintIterationCountAfterCollision = 1;
        public bool autoAddChildDummyBoneConstraint = true;

        [Header("World Collision（物理場景的掃掠，較貴）")]
        [Tooltip("跟場景的 Physics 碰撞體碰撞（每幀用球掃掠）")]
        public bool allowWorldCollision;
        [Tooltip("世界碰撞要碰的圖層")]
        public LayerMask worldCollisionLayers = ~0;

        [Header("Simple World Collision（定期收集附近的 Collider，不做掃掠）")]
        [Tooltip("簡易世界碰撞：定期收集附近的簡單碰撞體（球、膠囊、箱、網格的包圍盒）與地面，不做掃掠")]
        public bool useSimpleWorldCollision;
        [Tooltip("多久重新收集一次附近的碰撞體（秒）")]
        [Min(0f)] public float simpleWorldCollisionGatherInterval = 0.2f;
        [Min(0f)] public float simpleWorldCollisionGatherRadius = 2f;
        public LayerMask simpleWorldCollisionLayers = ~0;
        [Tooltip("往下打一條射線，把地面當成薄箱子")]
        public bool simpleWorldCollisionGroundCollision = true;
        [Tooltip("每種形狀最多收集幾個")]
        [Min(1)] public int simpleWorldCollisionMaxShapes = 16;

        [Header("Force")]
        [Tooltip("重力（m/s²）。原作預設是 0")]
        public Vector3 gravity;
        [Tooltip("gravity 是世界空間的向量（關閉時是元件空間）")]
        public bool useWorldSpaceGravity = true;
        [Tooltip("原作 legacy：把 0.5·g·dt² 直接加到位置")]
        public bool useLegacyGravity;
        [Tooltip("固定的外力（加速度，公尺／秒²）")]
        public Vector3 simpleExternalForce;
        [Tooltip("simpleExternalForce 是世界空間的向量（關閉時是元件空間）")]
        public bool useWorldSpaceSimpleExternalForce;
        [Tooltip("場景的 WindZone 會吹動")]
        public bool enableWind;
        [Tooltip("場景風（WindZone）的強度倍率")]
        public float windScale = 1f;
        [Tooltip("場景風的方向每幀隨機偏移的最大角度（度）")]
        [Min(0f)] public float windDirectionNoiseAngle;
        [SerializeReference] public List<KPExternalForce> externalForces = new List<KPExternalForce>();
        public List<SyncBone> syncBones = new List<SyncBone>();

        [Tooltip("模擬中（停用時骨頭維持動畫姿勢；元件版對應 isActiveAndEnabled）")]
        public bool enabled = true;

        internal bool dirty = true;
        internal bool settingsDirty;
        internal bool validateRequested;
        internal KPChainRuntime runtime;
        internal readonly KPChainControl control = new KPChainControl();
        /// <summary>場景風、程序風的亂數種子（元件版用元件的 GetHashCode，跟改版前一樣）。</summary>
        internal int rngSeed;
        /// <summary>這一幀不更新（元件版：元件被刪掉、主機還沒同步）。</summary>
        internal bool skipFrame;

        public KPRigChain() => rngSeed = RuntimeHelpers.GetHashCode(this);

        /// <summary>結構改了（骨頭、碰撞體、約束、SyncBone、模擬空間等），下一幀重建這一條。</summary>
        public void MarkDirty() => dirty = true;

        /// <summary>改了依骨長比例的曲線、外力的骨頭篩選或 forceRateByBoneLengthRate 之後呼叫：下一幀就地更新，不重建、不重置。</summary>
        public void MarkSettingsDirty() => settingsDirty = true;

        /// <summary>有沒有設依骨長比例的曲線（都沒設時每根骨頭的設定一樣）。</summary>
        internal bool HasSettingsCurves() => KPChainSettingsMath.HasCurves(dampingCurve, stiffnessCurve, worldDampingLocationCurve,
            worldDampingRotationCurve, radiusCurve, limitAngleCurve);

        /// <summary>依骨長比例算出的基本設定（還沒夾範圍；倍率在每幀的工作裡乘）。</summary>
        public KPSettings EvaluateSettings(float lengthRate) => KPChainSettingsMath.Evaluate(damping, stiffness, worldDampingLocation,
            worldDampingRotation, radius, limitAngle, dampingCurve, stiffnessCurve, worldDampingLocationCurve, worldDampingRotationCurve,
            radiusCurve, limitAngleCurve, lengthRate);

        // ===== 執行時的操作（跟 KawaiiPhysicsChain 一樣） =====

        /// <summary>原作 ResetDynamics。</summary>
        public void ResetDynamics(KPTeleportType type = KPTeleportType.Reset)
        {
            if (type == KPTeleportType.Teleport) control.PendingTeleport = true;
            else
            {
                control.PendingReset = true;
                if (useWarmUpWhenResetDynamics) needWarmUp = true;
            }
        }

        /// <summary>目前有效的暫時外力數。</summary>
        public int TransientForceCount => control.transientForces.Count;
        /// <summary>目前有效的設定倍率數。</summary>
        public int SettingsMultiplierCount => control.multipliers.Count;

        /// <summary>加一個暫時外力，lifetime 秒後移除。回傳停止用的 handle。</summary>
        public long RequestTransientExternalForce(KPExternalForce force, float lifetime) => control.RequestTransientExternalForce(force, lifetime);

        /// <summary>原作 RequestTransientGust：以某個程序風為底（inheritForceIndex；-1 找第一個啟用中的）產生一陣突風。</summary>
        public long RequestTransientGust(float strength, float riseTime, float decayTime, Vector3 direction = default,
            int inheritForceIndex = -1, float holdTime = 0f, bool realTimeEnvelope = false) =>
            control.RequestTransientGust(externalForces, strength, riseTime, decayTime, direction, inheritForceIndex, holdTime, realTimeEnvelope);

        /// <summary>原作 RequestStopTransientExternalForce：blendOut 秒內淡出。</summary>
        public void RequestStopTransientExternalForce(long handle, float blendOutTime) => control.RequestStopTransientExternalForce(handle, blendOutTime);

        /// <summary>原作 RequestStartPhysicsSettingsMultiplier：起、持、落的包絡；infiniteHold 時持續到停止。</summary>
        public long StartSettingsMultiplier(KPSettingsMultiplier scale, float riseTime, float holdTime, float decayTime, bool infiniteHold = false) =>
            control.StartSettingsMultiplier(scale, riseTime, holdTime, decayTime, infiniteHold);

        /// <summary>原作 RequestPushPhysicsSettingsMultiplier：由外部每幀推送強度（alpha）。</summary>
        public void PushSettingsMultiplier(long handle, KPSettingsMultiplier scale, float pushAlpha, int leaseEvaluations = 0, float leaseExpireBlendOut = 0.2f) =>
            control.PushSettingsMultiplier(handle, scale, pushAlpha, leaseEvaluations, leaseExpireBlendOut);

        /// <summary>原作 RequestStopPhysicsSettingsMultiplier。</summary>
        public void StopSettingsMultiplier(long handle, float blendOutTime) => control.StopSettingsMultiplier(handle, blendOutTime);
    }

    /// <summary>依骨長比例的設定（KawaiiPhysicsChain 與 KPRigChain 共用）。</summary>
    internal static class KPChainSettingsMath
    {
        static bool Has(AnimationCurve c) => c != null && c.length > 0;
        static float Eval(AnimationCurve c, float t) => c != null && c.length > 0 ? c.Evaluate(t) : 1f;

        public static bool HasCurves(AnimationCurve a, AnimationCurve b, AnimationCurve c, AnimationCurve d, AnimationCurve e, AnimationCurve f) =>
            Has(a) || Has(b) || Has(c) || Has(d) || Has(e) || Has(f);

        public static KPSettings Evaluate(float damping, float stiffness, float worldDampingLocation, float worldDampingRotation, float radius,
            float limitAngle, AnimationCurve dampingCurve, AnimationCurve stiffnessCurve, AnimationCurve worldDampingLocationCurve,
            AnimationCurve worldDampingRotationCurve, AnimationCurve radiusCurve, AnimationCurve limitAngleCurve, float lengthRate) => new KPSettings
        {
            Damping = damping * Eval(dampingCurve, lengthRate),
            Stiffness = stiffness * Eval(stiffnessCurve, lengthRate),
            WorldDampingLocation = worldDampingLocation * Eval(worldDampingLocationCurve, lengthRate),
            WorldDampingRotation = worldDampingRotation * Eval(worldDampingRotationCurve, lengthRate),
            Radius = radius * Eval(radiusCurve, lengthRate),
            LimitAngle = limitAngle * Eval(limitAngleCurve, lengthRate),
        };
    }
}
