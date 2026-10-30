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

        internal bool dirty = true;
        internal bool settingsDirty;
        internal bool validateRequested;
        internal KPChainRuntime runtime;

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
        internal bool HasSettingsCurves()
        {
            static bool Has(AnimationCurve c) => c != null && c.length > 0;
            return Has(dampingCurve) || Has(stiffnessCurve) || Has(worldDampingLocationCurve) || Has(worldDampingRotationCurve) ||
                   Has(radiusCurve) || Has(limitAngleCurve);
        }

        /// <summary>依骨長比例算出的基本設定（還沒夾範圍；倍率在每幀的工作裡乘）。</summary>
        public KPSettings EvaluateSettings(float lengthRate)
        {
            static float Eval(AnimationCurve c, float t) => c != null && c.length > 0 ? c.Evaluate(t) : 1f;
            return new KPSettings
            {
                Damping = damping * Eval(dampingCurve, lengthRate),
                Stiffness = stiffness * Eval(stiffnessCurve, lengthRate),
                WorldDampingLocation = worldDampingLocation * Eval(worldDampingLocationCurve, lengthRate),
                WorldDampingRotation = worldDampingRotation * Eval(worldDampingRotationCurve, lengthRate),
                Radius = radius * Eval(radiusCurve, lengthRate),
                LimitAngle = limitAngle * Eval(limitAngleCurve, lengthRate),
            };
        }

        // ===== 執行時的操作（對應原作 AnimNode 的 Request* 與 ResetDynamics） =====

        /// <summary>原作 ResetDynamics。</summary>
        public void ResetDynamics(KPTeleportType type = KPTeleportType.Reset)
        {
            if (type == KPTeleportType.Teleport) PendingTeleport = true;
            else
            {
                PendingReset = true;
                if (useWarmUpWhenResetDynamics) needWarmUp = true;
            }
        }

        internal bool PendingTeleport;
        internal bool PendingReset;

        static long nextHandle = 1;
        internal static long NewHandle() => nextHandle++;

        internal readonly List<KPTransientForce> transientForces = new List<KPTransientForce>();
        internal readonly List<KPActiveMultiplier> multipliers = new List<KPActiveMultiplier>();
        /// <summary>目前有效的暫時外力數。</summary>
        public int TransientForceCount => transientForces.Count;
        /// <summary>目前有效的設定倍率數。</summary>
        public int SettingsMultiplierCount => multipliers.Count;
        /// <summary>同時存在的暫時外力（RequestTransientExternalForce、RequestTransientGust）上限。</summary>
        public const int MaxTransientExternalForces = 8;
        /// <summary>同時存在的設定倍率（StartSettingsMultiplier）上限。</summary>
        public const int MaxPhysicsSettingsMultipliers = 8;
        /// <summary>RequestTransientGust 的 inheritForceIndex：所有啟用中的程序風各產生一個突風。</summary>
        public const int TransientGustInheritAllWinds = -2;

        /// <summary>加一個暫時外力，lifetime 秒後移除。回傳停止用的 handle。</summary>
        public long RequestTransientExternalForce(KPExternalForce force, float lifetime)
        {
            var h = NewHandle();
            force.oneShot = false;
            transientForces.Add(new KPTransientForce { Force = force, RemainingLifetime = lifetime, Handle = h });
            TrimTransients();
            return h;
        }

        /// <summary>
        /// 原作 RequestTransientGust：以某個程序風為底（inheritForceIndex；-1 找第一個啟用中的）產生一陣突風。
        /// direction 非零時用世界空間的這個方向。
        /// </summary>
        public long RequestTransientGust(float strength, float riseTime, float decayTime, Vector3 direction = default,
            int inheritForceIndex = -1, float holdTime = 0f, bool realTimeEnvelope = false)
        {
            var h = NewHandle();
            var sources = new List<KPForceProceduralWind>();
            if (inheritForceIndex == TransientGustInheritAllWinds)
            {
                foreach (var f in externalForces)
                    if (f is KPForceProceduralWind pw && pw.enabled) sources.Add(pw);
            }
            else
            {
                KPForceProceduralWind src = null;
                if (inheritForceIndex >= 0 && inheritForceIndex < externalForces.Count) src = externalForces[inheritForceIndex] as KPForceProceduralWind;
                if (src == null)
                    foreach (var f in externalForces)
                        if (f is KPForceProceduralWind pw && pw.enabled) { src = pw; break; }
                sources.Add(src);
            }
            if (sources.Count == 0) sources.Add(null);
            foreach (var src in sources)
            {
                var wind = src != null ? (KPForceProceduralWind)src.CloneForTransient() : new KPForceProceduralWind();
                if (realTimeEnvelope) wind.parameters.timeScale = 1f;
                if (direction.sqrMagnitude > 1e-8f)
                {
                    // 原作：指定方向時不繼承來源的方向與方向雜訊
                    wind.space = KPForceSpace.WorldSpace;
                    wind.parameters.windDirection = direction;
                    wind.parameters.windDirectionNoiseAngle = 0f;
                }
                // 突風本身以外的成分歸零：只留下突風
                var p = wind.parameters;
                p.constantForce = p.swayForce = p.rippleForce = p.randomForce = 0f;
                p.strengthCycleRange = Vector2.one;
                var ts = Mathf.Max(p.timeScale, KPMath.KindaSmallNumber);
                var lifetime = (Mathf.Max(riseTime, 0f) + Mathf.Max(holdTime, 0f) + Mathf.Max(decayTime, 0f)) / ts + 0.2f;
                wind.RequestGust(strength, riseTime, decayTime, holdTime, realTimeEnvelope);
                transientForces.Add(new KPTransientForce { Force = wind, RemainingLifetime = lifetime, Handle = h });
            }
            TrimTransients();
            return h;
        }

        /// <summary>原作 RequestStopTransientExternalForce：blendOut 秒內淡出（程序風是讓突風落到 0）。</summary>
        public void RequestStopTransientExternalForce(long handle, float blendOutTime)
        {
            for (int i = transientForces.Count - 1; i >= 0; i--)
            {
                var t = transientForces[i];
                if (t.Handle != handle) continue;
                if (blendOutTime <= KPMath.KindaSmallNumber) { transientForces.RemoveAt(i); continue; }
                if (t.Force is KPForceProceduralWind pw)
                {
                    pw.RequestGustStop(blendOutTime);
                    t.RemainingLifetime = blendOutTime / Mathf.Max(pw.parameters.timeScale, KPMath.KindaSmallNumber) + 0.2f;
                }
                else t.RemainingLifetime = Mathf.Min(t.RemainingLifetime, Mathf.Max(blendOutTime, 0f));
            }
        }

        void TrimTransients()
        {
            while (transientForces.Count > MaxTransientExternalForces) transientForces.RemoveAt(0);
        }

        /// <summary>原作 RequestStartPhysicsSettingsMultiplier：起、持、落的包絡；infiniteHold 時持續到停止。</summary>
        public long StartSettingsMultiplier(KPSettingsMultiplier scale, float riseTime, float holdTime, float decayTime, bool infiniteHold = false)
        {
            var h = NewHandle();
            multipliers.Add(new KPActiveMultiplier
            {
                Handle = h, Scale = scale, RiseTime = riseTime, HoldTime = holdTime, DecayTime = decayTime,
                InfiniteHold = infiniteHold, PeakAlpha = 1f,
            });
            TrimMultipliers();
            return h;
        }

        /// <summary>
        /// 原作 RequestPushPhysicsSettingsMultiplier：由外部每幀推送強度（alpha）。
        /// leaseEvaluations＞0 時，這麼多幀沒推送就在 leaseExpireBlendOut 秒內淡出。
        /// </summary>
        public void PushSettingsMultiplier(long handle, KPSettingsMultiplier scale, float pushAlpha, int leaseEvaluations = 0, float leaseExpireBlendOut = 0.2f)
        {
            if (handle == 0) return;
            // 逐幀呼叫的 API：用迴圈找（lambda 會捕捉 handle，每次呼叫都配置）
            KPActiveMultiplier m = null;
            foreach (var x in multipliers)
                if (x.Handle == handle)
                {
                    m = x;
                    break;
                }
            if (m == null)
            {
                m = new KPActiveMultiplier { Handle = handle };
                multipliers.Add(m);
            }
            m.Scale = scale;
            m.DrivenAlpha = Mathf.Clamp01(pushAlpha);
            m.LeaseEvaluations = Mathf.Max(0, leaseEvaluations);
            m.LeaseRemaining = m.LeaseEvaluations;
            m.LeaseExpireBlendOut = Mathf.Max(0f, leaseExpireBlendOut);
            m.InfiniteHold = false;
            m.ExternallyDriven = true;
            m.PeakAlpha = 1f;
            m.RiseTime = m.HoldTime = m.DecayTime = m.ElapsedTime = 0f;
            TrimMultipliers();
        }

        /// <summary>原作 RequestStopPhysicsSettingsMultiplier。</summary>
        public void StopSettingsMultiplier(long handle, float blendOutTime)
        {
            for (int i = multipliers.Count - 1; i >= 0; i--)
                if (multipliers[i].Handle == handle) StopMultiplierAt(i, blendOutTime);
        }

        internal void StopMultiplierAt(int index, float blendOutTime)
        {
            var m = multipliers[index];
            if (blendOutTime <= KPMath.KindaSmallNumber) { multipliers.RemoveAt(index); return; }
            m.PeakAlpha = m.ExternallyDriven ? m.DrivenAlpha : m.PeakAlpha * m.CurrentEnvelope();
            m.InfiniteHold = false;
            m.ExternallyDriven = false;
            m.DrivenAlpha = 0f;
            m.LeaseEvaluations = m.LeaseRemaining = 0;
            m.RiseTime = 0f;
            m.HoldTime = 0f;
            m.DecayTime = blendOutTime;
            m.ElapsedTime = 0f;
        }

        void TrimMultipliers()
        {
            while (multipliers.Count > MaxPhysicsSettingsMultipliers) multipliers.RemoveAt(0);
        }

        /// <summary>原作 ComputeEffectivePhysicsSettingsMultiplierScale：每個倍率依包絡從 1 內插過去，彼此相乘。</summary>
        internal KPSettingsMultiplier EffectiveMultiplier()
        {
            var e = KPSettingsMultiplier.One;
            foreach (var m in multipliers)
            {
                var a = m.ExternallyDriven ? m.DrivenAlpha : m.PeakAlpha * m.CurrentEnvelope();
                e.Damping *= Mathf.Lerp(1f, m.Scale.Damping, a);
                e.Stiffness *= Mathf.Lerp(1f, m.Scale.Stiffness, a);
                e.WorldDampingLocation *= Mathf.Lerp(1f, m.Scale.WorldDampingLocation, a);
                e.WorldDampingRotation *= Mathf.Lerp(1f, m.Scale.WorldDampingRotation, a);
                e.Radius *= Mathf.Lerp(1f, m.Scale.Radius, a);
                e.LimitAngle *= Mathf.Lerp(1f, m.Scale.LimitAngle, a);
            }
            return e;
        }

        /// <summary>原作 ConsumeAndAdvancePhysicsSettingsMultipliers 的時間推進、租約到期與結束移除。</summary>
        internal void AdvanceMultipliers(float dt)
        {
            foreach (var m in multipliers)
            {
                m.ElapsedTime += dt;
                if (m.ExternallyDriven && m.LeaseEvaluations > 0) m.LeaseRemaining--;
            }
            for (int i = multipliers.Count - 1; i >= 0; i--)
            {
                var m = multipliers[i];
                if (m.ExternallyDriven && m.LeaseEvaluations > 0 && m.LeaseRemaining <= 0) StopMultiplierAt(i, m.LeaseExpireBlendOut);
            }
            for (int i = multipliers.Count - 1; i >= 0; i--)
            {
                var m = multipliers[i];
                if (!m.ExternallyDriven && !m.InfiniteHold && m.ElapsedTime >= m.RiseTime + m.HoldTime + m.DecayTime) multipliers.RemoveAt(i);
            }
        }

        /// <summary>原作 ConsumeAndRemoveExpiredTransientExternalForces 的壽命遞減。</summary>
        internal void AdvanceTransients(float dt)
        {
            for (int i = transientForces.Count - 1; i >= 0; i--)
            {
                transientForces[i].RemainingLifetime -= dt;
                if (transientForces[i].RemainingLifetime <= 0f) transientForces.RemoveAt(i);
            }
        }

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
