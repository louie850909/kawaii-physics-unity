using System;
using System.Collections.Generic;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>ResetDynamics 的種類。</summary>
    public enum KPTeleportType
    {
        /// <summary>這一幀不反映元件的移動（原作 TeleportPhysics）。</summary>
        Teleport,
        /// <summary>重新初始化（位置回到姿勢），可搭配暖機（原作 ResetPhysics）。</summary>
        Reset,
    }

    /// <summary>
    /// 對應原作的一個 KawaiiPhysics AnimNode：從 rootBone（與 additionalRootBones）往下的骨頭全部模擬。
    /// 實際運算由 KPSystem 把所有 Chain 打包成一批 Burst 工作處理。
    /// </summary>
    public class KawaiiPhysicsChain : MonoBehaviour
    {
        /// <summary>骨頭的前方軸（末端假骨往這個方向延伸）。</summary>
        public enum Axis
        {
            /// <summary>區域 +X</summary>
            XPositive,
            /// <summary>區域 −X</summary>
            XNegative,
            /// <summary>區域 +Y</summary>
            YPositive,
            /// <summary>區域 −Y</summary>
            YNegative,
            /// <summary>區域 +Z</summary>
            ZPositive,
            /// <summary>區域 −Z</summary>
            ZNegative,
        }

        /// <summary>額外的起點骨頭。</summary>
        [Serializable]
        public class RootBoneSetting
        {
            [Tooltip("起點骨頭")]
            public Transform rootBone;
            [Tooltip("用自己的排除清單（不用元件的 excludeBones）")]
            public bool useOverrideExcludeBones;
            [Tooltip("這個起點用的排除清單")]
            public List<Transform> overrideExcludeBones = new List<Transform>();
        }

        /// <summary>骨間約束：兩根骨頭之間維持建立時的距離（XPBD）。</summary>
        [Serializable]
        public class BoneConstraint
        {
            [Tooltip("約束的第一根骨頭")]
            public Transform bone1;
            [Tooltip("約束的第二根骨頭")]
            public Transform bone2;
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
            public Transform bone;
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
            public Transform bone;
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
        public Transform rootBone;
        [Tooltip("不模擬的骨頭（連同它底下的骨頭）")]
        public List<Transform> excludeBones = new List<Transform>();
        [Tooltip("其他的起點骨頭（例如左右兩束頭髮放在同一個元件）；可以各自指定排除清單")]
        public List<RootBoneSetting> additionalRootBones = new List<RootBoneSetting>();
        [Tooltip("末端假骨的長度（公尺）；0 表示不加。末端骨頭沒有子骨頭時，要靠它才會轉")]
        [Min(0f)] public float dummyBoneLength;
        [Tooltip("骨頭的前方軸，只用在末端假骨的方向")]
        public Axis boneForwardAxis = Axis.YPositive;
        [Tooltip("元件（角色根）。不指定時用上層的 Animator，再沒有就用自己")]
        public Transform component;

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
        public Transform simulationBaseBone;
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
        public List<KawaiiPhysicsCollider> colliders = new List<KawaiiPhysicsCollider>();
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

        [Header("Preset")]
        [Tooltip("啟用時把這個預設的設定套到這條鏈")]
        public KawaiiPhysicsPreset preset;

        /// <summary>內部流程吃的設定（每幀從這個元件的欄位複製過去）。</summary>
        [NonSerialized] internal readonly KPRigChain settings = new KPRigChain();

        internal bool dirty { get => settings.dirty; set => settings.dirty = value; }
        internal bool settingsDirty { get => settings.settingsDirty; set => settings.settingsDirty = value; }
        internal bool validateRequested { get => settings.validateRequested; set => settings.validateRequested = value; }
        internal KPChainRuntime runtime => settings.runtime;
        KPChainControl Control => settings.control;

        void OnEnable()
        {
            if (preset != null) preset.ApplyTo(this);
            KPSystem.Register(this);
        }

        void OnDisable() => KPSystem.Unregister(this);

        // Inspector 改了東西：下一幀比對結構簽章，只改設定就不重建
        void OnValidate()
        {
            validateRequested = true;
            // 清單按「+」時新元素可能跟上一個指到同一個外力物件：複製成獨立的一份
            for (int i = 1; i < externalForces.Count; i++)
            {
                var f = externalForces[i];
                if (f == null) continue;
                for (int j = 0; j < i; j++)
                {
                    if (!ReferenceEquals(f, externalForces[j])) continue;
                    externalForces[i] = (KPExternalForce)JsonUtility.FromJson(JsonUtility.ToJson(f), f.GetType());
                    break;
                }
            }
        }

        /// <summary>
        /// 結構改了（骨頭、碰撞體、約束、SyncBone、模擬空間等），下一幀重建這一條（只有它的模擬狀態重置，其他 Chain 不受影響）。
        /// damping、stiffness、worldDamping、radius、limitAngle 的值會自動偵測，不用呼叫這個。
        /// </summary>
        public void MarkDirty() => dirty = true;

        /// <summary>
        /// 改了依骨長比例的曲線（dampingCurve 等）、外力的骨頭篩選或 forceRateByBoneLengthRate 之後呼叫：
        /// 下一幀就地更新，不重建、不重置。例外：依半徑加密細分或有橋接假骨時，改了 radiusCurve 會改變假骨數量，這時重建這一條。
        /// </summary>
        public void MarkSettingsDirty() => settingsDirty = true;

        /// <summary>元件（角色根）：有指定 component 就用它，否則用上層的 Animator，再沒有就用自己。</summary>
        public Transform ResolveComponent()
        {
            if (component != null) return component;
            var animator = GetComponentInParent<Animator>();
            return animator != null ? animator.transform : transform;
        }

        /// <summary>有沒有設依骨長比例的曲線（都沒設時每根骨頭的設定一樣）。</summary>
        internal bool HasSettingsCurves() => KPChainSettingsMath.HasCurves(dampingCurve, stiffnessCurve, worldDampingLocationCurve,
            worldDampingRotationCurve, radiusCurve, limitAngleCurve);

        /// <summary>依骨長比例算出的基本設定（還沒夾範圍；倍率在每幀的工作裡乘）。</summary>
        public KPSettings EvaluateSettings(float lengthRate) => KPChainSettingsMath.Evaluate(damping, stiffness, worldDampingLocation,
            worldDampingRotation, radius, limitAngle, dampingCurve, stiffnessCurve, worldDampingLocationCurve, worldDampingRotationCurve,
            radiusCurve, limitAngleCurve, lengthRate);

        // ===== 複製進設定類別 =====

        /// <summary>
        /// 欄位複製進 settings。每幀的值（數值、曲線與資產的參照、外力清單）每次都複製；
        /// structure＝true 時連骨頭與碰撞體（Transform → 節點 id）也換算（同步、比對結構簽章前）。穩態（structure＝false）0 配置。
        /// </summary>
        internal void CopyToSettings(KPTransformHierarchy h, bool structure)
        {
            var s = settings;
            s.enabled = isActiveAndEnabled;
            s.skipFrame = false;
            s.rngSeed = GetHashCode();
            s.dummyBoneLength = dummyBoneLength;
            s.boneForwardAxis = boneForwardAxis;
            s.boneSubdivisionCount = boneSubdivisionCount;
            s.boneSubdivisionCollisionOnly = boneSubdivisionCollisionOnly;
            s.boneSubdivisionDensifyByRadius = boneSubdivisionDensifyByRadius;
            s.boneConstraintSubdivisionCount = boneConstraintSubdivisionCount;
            s.boneConstraintSubdivisionFeedbackScale = boneConstraintSubdivisionFeedbackScale;
            s.damping = damping;
            s.stiffness = stiffness;
            s.worldDampingLocation = worldDampingLocation;
            s.worldDampingRotation = worldDampingRotation;
            s.radius = radius;
            s.limitAngle = limitAngle;
            s.planarConstraint = planarConstraint;
            s.alpha = alpha;
            s.dampingCurve = dampingCurve;
            s.stiffnessCurve = stiffnessCurve;
            s.worldDampingLocationCurve = worldDampingLocationCurve;
            s.worldDampingRotationCurve = worldDampingRotationCurve;
            s.radiusCurve = radiusCurve;
            s.limitAngleCurve = limitAngleCurve;
            s.simulationSpace = simulationSpace;
            s.targetFramerate = targetFramerate;
            s.fixedSubstepping = fixedSubstepping;
            s.maxSubsteps = maxSubsteps;
            s.skelCompMoveScale = skelCompMoveScale;
            s.teleportDistanceThreshold = teleportDistanceThreshold;
            s.teleportRotationThreshold = teleportRotationThreshold;
            s.warmUpFrames = warmUpFrames;
            s.needWarmUp = needWarmUp;
            s.useWarmUpWhenResetDynamics = useWarmUpWhenResetDynamics;
            s.limitsAsset = limitsAsset;
            s.sharedCollisionSource = sharedCollisionSource;
            s.useSharedCollision = useSharedCollision;
            s.sharedCollisionTag = sharedCollisionTag;
            s.boneConstraintsAsset = boneConstraintsAsset;
            s.boneConstraintGlobalComplianceType = boneConstraintGlobalComplianceType;
            s.boneConstraintIterationCountBeforeCollision = boneConstraintIterationCountBeforeCollision;
            s.boneConstraintIterationCountAfterCollision = boneConstraintIterationCountAfterCollision;
            s.autoAddChildDummyBoneConstraint = autoAddChildDummyBoneConstraint;
            s.allowWorldCollision = allowWorldCollision;
            s.worldCollisionLayers = worldCollisionLayers;
            s.useSimpleWorldCollision = useSimpleWorldCollision;
            s.simpleWorldCollisionGatherInterval = simpleWorldCollisionGatherInterval;
            s.simpleWorldCollisionGatherRadius = simpleWorldCollisionGatherRadius;
            s.simpleWorldCollisionLayers = simpleWorldCollisionLayers;
            s.simpleWorldCollisionGroundCollision = simpleWorldCollisionGroundCollision;
            s.simpleWorldCollisionMaxShapes = simpleWorldCollisionMaxShapes;
            s.gravity = gravity;
            s.useWorldSpaceGravity = useWorldSpaceGravity;
            s.useLegacyGravity = useLegacyGravity;
            s.simpleExternalForce = simpleExternalForce;
            s.useWorldSpaceSimpleExternalForce = useWorldSpaceSimpleExternalForce;
            s.enableWind = enableWind;
            s.windScale = windScale;
            s.windDirectionNoiseAngle = windDirectionNoiseAngle;
            s.externalForces = externalForces;
            if (!structure) return;

            int Node(Transform t) => t == null ? KPHierarchy.None : h.Id(t);
            void Nodes(List<Transform> from, List<int> to)
            {
                to.Clear();
                foreach (var t in from) to.Add(Node(t));
            }
            s.rootBone = Node(rootBone);
            Nodes(excludeBones, s.excludeBones);
            s.additionalRootBones.Clear();
            foreach (var r in additionalRootBones)
            {
                if (r == null) continue;
                var rr = new KPRigChain.RootBoneSetting { rootBone = Node(r.rootBone), useOverrideExcludeBones = r.useOverrideExcludeBones };
                Nodes(r.overrideExcludeBones, rr.overrideExcludeBones);
                s.additionalRootBones.Add(rr);
            }
            s.component = Node(ResolveComponent());
            s.simulationBaseBone = Node(simulationBaseBone);
            s.colliders.Clear();
            foreach (var c in colliders)
                if (c != null) s.colliders.Add(c.ToSettings(h));
            s.boneConstraints.Clear();
            foreach (var c in boneConstraints)
            {
                if (c == null) continue;
                s.boneConstraints.Add(new KPRigChain.BoneConstraint
                {
                    bone1 = Node(c.bone1), bone2 = Node(c.bone2), overrideCompliance = c.overrideCompliance,
                    complianceType = c.complianceType, excludeFromSubdivision = c.excludeFromSubdivision,
                });
            }
            s.syncBones.Clear();
            foreach (var sb in syncBones)
            {
                if (sb == null) continue;
                var o = new KPRigChain.SyncBone
                {
                    bone = Node(sb.bone), globalScale = sb.globalScale, scaleCurveByDeltaDistance = sb.scaleCurveByDeltaDistance,
                    applyDirectionX = sb.applyDirectionX, applyDirectionY = sb.applyDirectionY, applyDirectionZ = sb.applyDirectionZ,
                    enableDistanceAttenuation = sb.enableDistanceAttenuation, attenuationInnerRadius = sb.attenuationInnerRadius,
                    attenuationOuterRadius = sb.attenuationOuterRadius, maxAttenuationRate = sb.maxAttenuationRate,
                };
                foreach (var r in sb.targetRoots)
                {
                    if (r == null) continue;
                    o.targetRoots.Add(new KPRigChain.SyncTargetRoot
                        { bone = Node(r.bone), includeChildBones = r.includeChildBones, scaleCurveByBoneLengthRate = r.scaleCurveByBoneLengthRate });
                }
                s.syncBones.Add(o);
            }
        }

        // ===== 執行時的操作（對應原作 AnimNode 的 Request* 與 ResetDynamics） =====

        /// <summary>原作 ResetDynamics。</summary>
        public void ResetDynamics(KPTeleportType type = KPTeleportType.Reset)
        {
            if (type == KPTeleportType.Teleport) Control.PendingTeleport = true;
            else
            {
                Control.PendingReset = true;
                if (useWarmUpWhenResetDynamics) needWarmUp = true;
            }
        }

        /// <summary>目前有效的暫時外力數。</summary>
        public int TransientForceCount => Control.transientForces.Count;
        /// <summary>目前有效的設定倍率數。</summary>
        public int SettingsMultiplierCount => Control.multipliers.Count;
        /// <summary>同時存在的暫時外力（RequestTransientExternalForce、RequestTransientGust）上限。</summary>
        public const int MaxTransientExternalForces = 8;
        /// <summary>同時存在的設定倍率（StartSettingsMultiplier）上限。</summary>
        public const int MaxPhysicsSettingsMultipliers = 8;
        /// <summary>RequestTransientGust 的 inheritForceIndex：所有啟用中的程序風各產生一個突風。</summary>
        public const int TransientGustInheritAllWinds = -2;

        /// <summary>加一個暫時外力，lifetime 秒後移除。回傳停止用的 handle。</summary>
        public long RequestTransientExternalForce(KPExternalForce force, float lifetime) => Control.RequestTransientExternalForce(force, lifetime);

        /// <summary>
        /// 原作 RequestTransientGust：以某個程序風為底（inheritForceIndex；-1 找第一個啟用中的）產生一陣突風。
        /// direction 非零時用世界空間的這個方向。
        /// </summary>
        public long RequestTransientGust(float strength, float riseTime, float decayTime, Vector3 direction = default,
            int inheritForceIndex = -1, float holdTime = 0f, bool realTimeEnvelope = false) =>
            Control.RequestTransientGust(externalForces, strength, riseTime, decayTime, direction, inheritForceIndex, holdTime, realTimeEnvelope);

        /// <summary>原作 RequestStopTransientExternalForce：blendOut 秒內淡出（程序風是讓突風落到 0）。</summary>
        public void RequestStopTransientExternalForce(long handle, float blendOutTime) => Control.RequestStopTransientExternalForce(handle, blendOutTime);

        /// <summary>原作 RequestStartPhysicsSettingsMultiplier：起、持、落的包絡；infiniteHold 時持續到停止。</summary>
        public long StartSettingsMultiplier(KPSettingsMultiplier scale, float riseTime, float holdTime, float decayTime, bool infiniteHold = false) =>
            Control.StartSettingsMultiplier(scale, riseTime, holdTime, decayTime, infiniteHold);

        /// <summary>
        /// 原作 RequestPushPhysicsSettingsMultiplier：由外部每幀推送強度（alpha）。
        /// leaseEvaluations＞0 時，這麼多幀沒推送就在 leaseExpireBlendOut 秒內淡出。
        /// </summary>
        public void PushSettingsMultiplier(long handle, KPSettingsMultiplier scale, float pushAlpha, int leaseEvaluations = 0, float leaseExpireBlendOut = 0.2f) =>
            Control.PushSettingsMultiplier(handle, scale, pushAlpha, leaseEvaluations, leaseExpireBlendOut);

        /// <summary>原作 RequestStopPhysicsSettingsMultiplier。</summary>
        public void StopSettingsMultiplier(long handle, float blendOutTime) => Control.StopSettingsMultiplier(handle, blendOutTime);

        // ===== 編輯器輔助 =====

        [ContextMenu("KawaiiPhysics/Apply Constraint Regex To Asset")]
        void ApplyConstraintRegex()
        {
            if (boneConstraintsAsset == null || rootBone == null) return;
            var names = new List<string>();
            void Collect(Transform t) { names.Add(t.name); foreach (Transform c in t) Collect(c); }
            Collect(rootBone);
            foreach (var r in additionalRootBones) if (r.rootBone != null) Collect(r.rootBone);
            var n = boneConstraintsAsset.ApplyRegex(names);
            Debug.Log($"KawaiiPhysics: added {n} bone constraints to {boneConstraintsAsset.name}");
        }

        [ContextMenu("KawaiiPhysics/Save Settings To Preset")]
        void SaveToPreset()
        {
            if (preset != null) preset.CaptureFrom(this);
        }

        void OnDrawGizmosSelected()
        {
            if (runtime == null || KPSystem.World == null) return;
            var w = KPSystem.World;
            if (runtime.InstanceIndex < 0 || runtime.InstanceIndex >= w.InstanceCount) return;
            var inst = w.Instances[runtime.InstanceIndex];
            for (int i = inst.BoneStart; i < inst.BoneStart + inst.BoneCount; i++)
            {
                var b = w.Bones[i];
                var p = (Vector3)(inst.SimPosition + Unity.Mathematics.math.mul(inst.SimRotation, b.Location * inst.SimScale));
                Gizmos.color = (b.Flags & KPBoneFlags.Bridge) != 0 ? Color.magenta
                    : (b.Flags & (KPBoneFlags.Dummy | KPBoneFlags.InterBone)) != 0 ? Color.yellow : Color.green;
                Gizmos.DrawWireSphere(p, b.Settings.Radius * inst.SimScale);
                if (b.ParentIndex >= 0)
                {
                    var pb = w.Bones[b.ParentIndex];
                    Gizmos.DrawLine(p, (Vector3)(inst.SimPosition + Unity.Mathematics.math.mul(inst.SimRotation, pb.Location * inst.SimScale)));
                }
            }
        }
    }

    /// <summary>暫時外力（原作 FKawaiiPhysicsTransientExternalForce）。</summary>
    internal class KPTransientForce
    {
        public KPExternalForce Force;
        public float RemainingLifetime;
        public long Handle;
    }

    /// <summary>進行中的設定倍率（原作 FKawaiiPhysicsActiveSettingsMultiplier）。</summary>
    internal class KPActiveMultiplier
    {
        public long Handle;
        public KPSettingsMultiplier Scale;
        public float RiseTime, HoldTime, DecayTime, ElapsedTime;
        public float PeakAlpha = 1f;
        public bool InfiniteHold;
        public bool ExternallyDriven;
        public float DrivenAlpha;
        public int LeaseEvaluations, LeaseRemaining;
        public float LeaseExpireBlendOut = 0.2f;

        public float CurrentEnvelope() => InfiniteHold
            ? (RiseTime > 0f ? Mathf.Clamp01(ElapsedTime / RiseTime) : 1f)
            : KPMath.EnvelopeAlpha01(RiseTime, HoldTime, DecayTime, ElapsedTime);
    }
}
