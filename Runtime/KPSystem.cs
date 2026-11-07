using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.SceneManagement;

namespace KawaiiPhysicsUnity
{
    /// <summary>解算的執行方式。</summary>
    public enum KPBackend
    {
        /// <summary>Burst，一個角色一個工作，丟給工作執行緒平行跑（預設）。</summary>
        BurstParallel,
        /// <summary>Burst，在主執行緒依序跑。</summary>
        BurstSingleThread,
        /// <summary>不經 Burst、直接呼叫 C# 解算（可以下中斷點除錯）。</summary>
        Managed,
    }

    /// <summary>
    /// 把所有 KawaiiPhysicsChain 打包成一組 NativeArray，每幀：
    /// Update 開頭（動畫前）把骨頭放回靜止姿勢；所有 LateUpdate 之後 準備外力等 → 讀 → 解 →（世界碰撞）→ 寫。
    /// 兩個時間點都用 PlayerLoop 插入，不用 MonoBehaviour。
    /// Chain 加入、移除或結構變更時只重建那一條（其他 Chain 的模擬狀態保留）；只改設定時就地更新，不重建。
    /// 軟組織（KawaiiPhysicsSoftBody）也在同樣兩個時間點處理，模擬在 Chain 之前。
    /// </summary>
    public static class KPSystem
    {
        /// <summary>解算的執行方式（預設 Burst 平行）。</summary>
        public static KPBackend Backend = KPBackend.BurstParallel;

        static readonly List<KawaiiPhysicsChain> chains = new List<KawaiiPhysicsChain>();
        static readonly Dictionary<Transform, (Vector3 pos, Quaternion rot)> restCache = new Dictionary<Transform, (Vector3, Quaternion)>();
        static readonly Dictionary<Transform, Vector3> syncRestCache = new Dictionary<Transform, Vector3>();
        static readonly List<WindZone> windZones = new List<WindZone>();
        static KPWorld world;
        static bool membershipChanged;
        static bool windZonesDirty = true;
        static uint frameSeed = 1;
        /// <summary>SimulatePhase 的呼叫次數與累計時間（編輯模式下 Time.frameCount 不會前進）。</summary>
        public static int FrameIndex { get; private set; }
        /// <summary>累計的模擬時間（秒）。</summary>
        public static float SimTime { get; private set; }
        static NativeArray<int> layerMasks;

        internal static KPWorld World => world;

        /// <summary>模擬中的 Chain 數。</summary>
        public static int ChainCount => world?.InstanceCount ?? 0;

        /// <summary>模擬中的骨頭數（含末端、細分、橋接的假骨）。</summary>
        public static int BoneCount
        {
            get
            {
                if (world == null) return 0;
                var n = 0;
                foreach (var s in world.Segments) n += s.Size.Bone;
                return n;
            }
        }

        /// <summary>上一次 Chain 加入、移除或重建結構時，同步花的時間（毫秒）。</summary>
        public static double LastSyncMs => world?.LastSyncMs ?? 0;

        /// <summary>物理寫回 Transform 之後（同一幀、所有 LateUpdate 之後）。要拿物理後骨頭的處理（鏡頭跟隨、截圖）掛在這裡。</summary>
        public static event Action AfterSimulate;

        /// <summary>上一幀 SimulatePhase 在主執行緒花的時間（毫秒，含等待工作完成；含軟組織）。</summary>
        public static double LastSimulateMs { get; private set; }

        static readonly System.Diagnostics.Stopwatch simulateWatch = new System.Diagnostics.Stopwatch();

        /// <summary>加入模擬（KawaiiPhysicsChain.OnEnable 會呼叫）。下一幀只建這一條，其他 Chain 不受影響。</summary>
        public static void Register(KawaiiPhysicsChain chain)
        {
            if (!chains.Contains(chain)) chains.Add(chain);
            // SyncBone 的靜止位置在啟用時（動畫還沒動）記下來，對應原作的參考姿勢
            var comp = chain.ResolveComponent();
            foreach (var s in chain.syncBones)
                if (s != null && s.bone != null && !syncRestCache.ContainsKey(s.bone))
                    syncRestCache.Add(s.bone, KPWorld.ComponentSpacePosition(comp, s.bone));
            membershipChanged = true;
        }

        /// <summary>
        /// 移出模擬（KawaiiPhysicsChain.OnDisable 會呼叫）。當場移除這一條：骨頭立刻放回靜止姿勢，這一幀之後的解算也不再寫回；
        /// 並忘掉這些骨頭的靜止姿勢，之後再有 Chain 用到同一根骨頭（重新啟用、換裝後的新 Chain）時，從那時候的姿勢重新記。
        /// </summary>
        public static void Unregister(KawaiiPhysicsChain chain)
        {
            if (!chains.Remove(chain)) return;
            membershipChanged = true;
            var seg = chain.runtime?.Segment;
            if (world == null || seg == null || !world.Segments.Contains(seg)) return;
            // 等到下一幀才移除的話，呼叫端在那之前改的區域姿勢（換裝、體型）會被舊的靜止姿勢蓋掉
            Sync();
            if (seg.Rest == null) return;
            foreach (var (index, _, _) in seg.Rest)
            {
                var t = seg.Transforms[index];
                if (t != null) restCache.Remove(t);
            }
        }

        /// <summary>
        /// 軟組織加入模擬（KawaiiPhysicsSoftBody.OnEnable 會呼叫）。骨頭的靜止姿勢在這時記下來（動畫還沒動）。
        /// 下一幀重建軟組織，其他軟組織元件的模擬狀態沿用。
        /// </summary>
        public static void Register(KawaiiPhysicsSoftBody softBody) => KPSoftBodySystem.Register(softBody);

        /// <summary>軟組織移出模擬（KawaiiPhysicsSoftBody.OnDisable 會呼叫）。下一幀動畫前骨頭放回靜止姿勢。</summary>
        public static void Unregister(KawaiiPhysicsSoftBody softBody) => KPSoftBodySystem.Unregister(softBody);

        /// <summary>模擬中的軟組織元件數。</summary>
        public static int SoftBodyCount => KPSoftBodySystem.BodyCount;

        /// <summary>模擬中的軟組織骨頭數。</summary>
        public static int SoftBoneCount => KPSoftBodySystem.BoneCount;

        /// <summary>
        /// 場景的 WindZone 重新收集。場景載入／卸載、Chain 加入或移除時會自動做；
        /// 執行中用程式新增 WindZone 時呼叫這個（刪除的 WindZone 每幀會自動略過）。
        /// </summary>
        public static void RefreshWindZones() => windZonesDirty = true;

        /// <summary>碰撞體在 Inspector 改了：所有 Chain 下一幀比對結構簽章，有用到它的才重建。</summary>
        internal static void RequestValidateAll()
        {
            foreach (var c in chains)
                if (c != null) c.validateRequested = true;
        }

        /// <summary>動畫更新前呼叫。</summary>
        public static void RestorePhase()
        {
            KPSoftBodySystem.RestorePhase();
            var needsSync = membershipChanged;
            foreach (var c in chains)
            {
                if (c == null)
                {
                    needsSync = true;
                    continue;
                }
                // Inspector 改了東西：結構簽章沒變就只更新設定，變了才重建這一條
                if (c.validateRequested)
                {
                    c.validateRequested = false;
                    var seg = c.runtime?.Segment;
                    if (seg == null || seg.Signature != KPWorld.ComputeSignature(c)) c.dirty = true;
                    else c.settingsDirty = true;
                }
                needsSync |= c.dirty;
            }
            if (needsSync) Sync();
            if (world == null || world.InstanceCount == 0) return;
            new KPRestoreJob { Rest = world.Rest }.Schedule(world.Transforms).Complete();
        }

        /// <summary>所有 LateUpdate 之後呼叫。軟組織先解（寫回之後 Chain 才讀，掛在軟組織骨頭底下的 Chain 會跟著動）。</summary>
        public static void SimulatePhase(float deltaTime)
        {
            var hasChains = world != null && world.InstanceCount > 0;
            if (!hasChains && KPSoftBodySystem.World == null) return;
            simulateWatch.Restart();
            KPSoftBodySystem.SimulatePhase(deltaTime);
            if (hasChains)
            {
                PrepareFrame(deltaTime);
                // 指標要在排程之前取：讀取工作排下去之後再對 Reads 呼叫 GetUnsafePtr 會被安全檢查擋下
                var ptrs = world.Pointers();
                var read = new KPReadJob { Reads = world.Reads }.ScheduleReadOnly(world.Transforms, 32);
                var handle = ScheduleSolve(deltaTime, read, ptrs);
                handle = ScheduleWorldCollision(handle, ptrs);
                new KPWriteJob { Writes = world.Writes }.Schedule(world.Transforms, handle).Complete();
            }
            LastSimulateMs = simulateWatch.Elapsed.TotalMilliseconds;
            AfterSimulate?.Invoke();
        }

        /// <summary>沒有其他工作在跑時用（會自己取指標）。</summary>
        internal static JobHandle ScheduleSolve(float deltaTime, JobHandle dependsOn) => ScheduleSolve(deltaTime, dependsOn, world.Pointers());

        static unsafe JobHandle ScheduleSolve(float deltaTime, JobHandle dependsOn, KPWorldPtrs ptrs)
        {
            var job = new KPSolveJob
            {
                Instances = (KPInstance*)world.Instances.GetUnsafePtr(),
                World = ptrs,
                DeltaTime = deltaTime,
            };
            switch (Backend)
            {
                case KPBackend.BurstParallel:
                    return job.Schedule(world.InstanceCount, 1, dependsOn);
                case KPBackend.BurstSingleThread:
                    dependsOn.Complete();
                    job.Run(world.InstanceCount);
                    return default;
                default:
                {
                    dependsOn.Complete();
                    var instances = (KPInstance*)world.Instances.GetUnsafePtr();
                    for (int i = 0; i < world.InstanceCount; i++) KPSolver.SolveInstance(ref instances[i], ptrs, deltaTime);
                    return default;
                }
            }
        }

        static unsafe JobHandle ScheduleWorldCollision(JobHandle dependsOn, KPWorldPtrs ptrs)
        {
            if (!world.AnyWorldCollision) return dependsOn;
            var build = new KPBuildSweepJob
            {
                Instances = (KPInstance*)world.Instances.GetUnsafePtr(),
                Bones = (KPBone*)world.Bones.GetUnsafePtr(),
                Commands = world.SweepCommands,
                Scene = Physics.defaultPhysicsScene,
                LayerMasks = layerMasks,
            }.Schedule(world.InstanceCount, 1, dependsOn);
            var sweep = SpherecastCommand.ScheduleBatch(world.SweepCommands, world.SweepHits, 32, 1, build);
            return new KPWorldCollisionFixupJob
            {
                Instances = (KPInstance*)world.Instances.GetUnsafePtr(),
                World = ptrs,
                Commands = world.SweepCommands,
                Hits = world.SweepHits,
            }.Schedule(world.InstanceCount, 1, sweep);
        }

        // ===================================================================
        // 每幀在主執行緒準備（外力 PreApply、暫時外力、倍率、風、簡易世界碰撞）
        // ===================================================================

        static void PrepareFrame(float dt)
        {
            frameSeed = frameSeed * 1664525u + 1013904223u;
            FrameIndex++;
            SimTime += dt;
            UpdateWindZones();
            var ctx = new KPForceContext { DeltaTime = dt, FrameSeed = frameSeed };
            var wantWorldCollision = false;

            for (int r = 0; r < world.Runtimes.Count; r++)
            {
                var rt = world.Runtimes[r];
                var chain = rt.Chain;
                if (chain == null) continue;
                // 設定（damping 等）改了：就地重算骨頭的設定，不重建、不重置
                if (chain.settingsDirty || rt.SettingsChanged(chain))
                {
                    var requested = chain.settingsDirty;
                    chain.settingsDirty = false;
                    if (rt.RadiusChangedStructurally(chain)) chain.dirty = true; // 假骨數量會變，下一幀重建這一條
                    else world.RefreshSettings(rt);
                    // Inspector 或 MarkSettingsDirty：外力的骨頭篩選、曲線也可能改了 → 遮罩重算、曲線表重取（不重建）
                    if (requested)
                    {
                        foreach (var f in chain.externalForces)
                            if (f != null) f.lutBaked = false;
                        rt.LastMaskForces.Clear();
                    }
                }
                var inst = world.Instances[rt.InstanceIndex];

                inst.Enabled = chain.isActiveAndEnabled ? 1 : 0;
                inst.Alpha = chain.alpha;
                inst.TargetFramerate = Mathf.Max(1, chain.targetFramerate);
                inst.UseFixedSubstep = chain.fixedSubstepping ? 1 : 0;
                inst.MaxSubsteps = chain.maxSubsteps;
                inst.PlanarConstraint = (int)chain.planarConstraint;
                inst.Gravity = chain.gravity;
                inst.UseWorldSpaceGravity = chain.useWorldSpaceGravity ? 1 : 0;
                inst.UseLegacyGravity = chain.useLegacyGravity ? 1 : 0;
                inst.SimpleExternalForce = chain.simpleExternalForce;
                inst.SimpleExternalForceWorldSpace = chain.useWorldSpaceSimpleExternalForce ? 1 : 0;
                inst.SkelCompMoveScale = chain.skelCompMoveScale;
                inst.TeleportDistanceThreshold = chain.teleportDistanceThreshold;
                inst.TeleportRotationThreshold = chain.teleportRotationThreshold;
                inst.WorldCollision = chain.allowWorldCollision ? 1 : 0;
                wantWorldCollision |= chain.allowWorldCollision;
                inst.CollisionOnlySubdivision = chain.boneSubdivisionCollisionOnly ? 1 : 0;
                inst.ConstraintItersBefore = chain.boneConstraintIterationCountBeforeCollision;
                inst.ConstraintItersAfter = chain.boneConstraintIterationCountAfterCollision;
                inst.BridgeFeedbackScale = chain.boneConstraintSubdivisionCount > 0 ? chain.boneConstraintSubdivisionFeedbackScale : 0f;
                layerMasks[rt.InstanceIndex] = chain.worldCollisionLayers;

                // 場景風：這一幀的突風係數 [0,2) 與錐角雜訊（原作 GetWindVelocity 一幀抽一次）
                inst.EnableWind = chain.enableWind && windZones.Count > 0 ? 1 : 0;
                inst.WindScale = chain.windScale;
                inst.WindNoiseAngle = chain.windDirectionNoiseAngle;
                inst.WindGustFactor = rt.Rng.NextFloat(0f, 2f);
                inst.WindNoiseRotation = chain.windDirectionNoiseAngle > 0f
                    ? quaternion.AxisAngle(rt.Rng.NextFloat3Direction(), rt.Rng.NextFloat(0f, math.radians(chain.windDirectionNoiseAngle)))
                    : quaternion.identity;

                if (chain.PendingTeleport) { inst.Teleport = 1; chain.PendingTeleport = false; }
                if (chain.PendingReset)
                {
                    inst.HasPrevComponent = 0;
                    inst.PoseInitialized = 0;
                    inst.SubstepAccumulator = 0f;
                    chain.PendingReset = false;
                }
                if (chain.needWarmUp && chain.warmUpFrames > 0)
                {
                    inst.WarmUpPending = chain.warmUpFrames;
                    chain.needWarmUp = false;
                }

                // 暫時外力、設定倍率（原作 ConsumeAndRemoveExpired*／ConsumeAndAdvance*）
                chain.AdvanceTransients(dt);
                chain.AdvanceMultipliers(dt);
                inst.Multiplier = chain.EffectiveMultiplier();

                PrepareForces(rt, ref inst, ctx);
                // 簡易世界碰撞的槽位是建立時保留的：執行中才打開（或關掉）就重建這一條，這一幀先不收
                if (chain.useSimpleWorldCollision != rt.SimpleCapacity > 0) chain.dirty = true;
                else if (chain.useSimpleWorldCollision) UpdateSimpleWorldCollision(rt, ref inst, dt);

                world.Instances[rt.InstanceIndex] = inst;
            }
            // 世界碰撞在執行中才打開：掃掠指令的陣列補上（只在開關改變時配置）
            if (wantWorldCollision != world.AnyWorldCollision) world.EnsureSweepCapacity(wantWorldCollision);
            world.UploadLutsIfDirty();
        }

        static void PrepareForces(KPChainRuntime rt, ref KPInstance inst, in KPForceContext ctx)
        {
            var chain = rt.Chain;
            rt.PackedForces.Clear();
            foreach (var f in chain.externalForces) if (f != null && f.enabled) rt.PackedForces.Add(f);
            foreach (var t in chain.transientForces) if (t.Force != null && t.Force.enabled) rt.PackedForces.Add(t.Force);
            while (rt.PackedForces.Count > rt.ForceCapacity) rt.PackedForces.RemoveAt(rt.PackedForces.Count - 1);

            var n = 0;
            for (int k = 0; k < rt.PackedForces.Count; k++)
            {
                var force = rt.PackedForces[k];
                var f = new KPForce { RateLut = world.BakeForceLut(force), Scale = 1f };
                if (!force.PreApply(ctx, ref f)) f.Scale = 0f;
                world.Forces[inst.ForceStart + n++] = f;
            }
            inst.ForceCount = n;

            // 骨頭篩選（ApplyBoneFilter／IgnoreBoneFilter）只在外力的組成改變時重算
            var changed = rt.LastMaskForces.Count != rt.PackedForces.Count;
            for (int k = 0; !changed && k < rt.PackedForces.Count; k++) changed = rt.LastMaskForces[k] != rt.PackedForces[k];
            if (changed)
            {
                rt.LastMaskForces.Clear();
                rt.LastMaskForces.AddRange(rt.PackedForces);
                for (int i = 0; i < inst.BoneCount; i++)
                {
                    var b = world.Bones[inst.BoneStart + i];
                    uint mask = 0;
                    var t = i < rt.BoneTransforms.Count ? rt.BoneTransforms[i] : null;
                    for (int k = 0; k < rt.PackedForces.Count && k < 32; k++)
                        if (rt.PackedForces[k].CanApplyTo(t)) mask |= 1u << k;
                    b.ForceMask = mask;
                    world.Bones[inst.BoneStart + i] = b;
                }
            }

            // PostApply：一次性的外力用完移除
            chain.externalForces.RemoveAll(f => f != null && f.enabled && f.oneShot);
        }

        static void UpdateWindZones()
        {
            // 搜尋會配置陣列，只在場景或 Chain 有變動時做（穩態每幀 0 配置）
            if (windZonesDirty)
            {
                windZonesDirty = false;
                windZones.Clear();
                // 停用中的也收：之後啟用時每幀的 activeInHierarchy 檢查會把它算進來
                windZones.AddRange(UnityEngine.Object.FindObjectsByType<WindZone>(FindObjectsInactive.Include));
            }
            var n = 0;
            foreach (var z in windZones)
            {
                if (z == null || !z.gameObject.activeInHierarchy || n >= world.WindZones.Length) continue;
                // 主風＋脈動（WindZone 的 pulse）當成風速
                var pulse = 1f + z.windPulseMagnitude * Mathf.Sin(SimTime * z.windPulseFrequency * Mathf.PI * 2f);
                world.WindZones[n++] = new KPWindZone
                {
                    Position = z.transform.position,
                    Direction = z.transform.forward,
                    Radius = z.radius,
                    Spherical = z.mode == WindZoneMode.Spherical ? 1 : 0,
                    Speed = z.windMain * pulse,
                };
            }
            world.WindZoneCount = n;
        }

        // ===================================================================
        // 簡易世界碰撞（原作 Simple World Collision：定期收集附近的簡單碰撞，不做掃掠）
        // ===================================================================

        static readonly Collider[] overlapBuffer = new Collider[256];
        // 地形碰撞的型別（TerrainPhysics 模組沒裝時是 null）：用型別比對，套件就不必依賴 Terrain 模組
        static readonly Type terrainColliderType = Type.GetType("UnityEngine.TerrainCollider, UnityEngine.TerrainPhysicsModule");

        static void UpdateSimpleWorldCollision(KPChainRuntime rt, ref KPInstance inst, float dt)
        {
            var chain = rt.Chain;
            var comp = chain.ResolveComponent();
            rt.GatherTimer += dt;
            if (rt.GatherTimer >= chain.simpleWorldCollisionGatherInterval)
            {
                rt.GatherTimer = 0f;
                rt.Gathered.Clear();
                var n = Physics.OverlapSphereNonAlloc(comp.position, chain.simpleWorldCollisionGatherRadius, overlapBuffer,
                    chain.simpleWorldCollisionLayers, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var c = overlapBuffer[i];
                    if (c == null || c.transform.IsChildOf(comp.root)) continue; // 自己身上的不算（原作 bIgnoreSelfComponent）
                    if (terrainColliderType != null && c.GetType() == terrainColliderType) continue; // 地面交給地面碰撞
                    rt.Gathered.Add(c);
                }
            }

            // 已收集的 Collider 每幀更新位置（它們可能在動）
            int spheres = 0, capsules = 0, boxes = 0;
            var cap = rt.SimpleCapacity;
            foreach (var c in rt.Gathered)
            {
                if (c == null || !c.enabled) continue;
                var t = c.transform;
                var s = t.lossyScale;
                switch (c)
                {
                    case SphereCollider sc when spheres < cap:
                        world.Spheres[inst.SphereStart + rt.StaticSpheres + spheres++] = new KPSphere
                        {
                            DrivingIndex = KPDriving.World, WorldLocation = t.TransformPoint(sc.center),
                            WorldRadius = sc.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)),
                        };
                        break;
                    case CapsuleCollider cc when capsules < cap:
                    {
                        var axisScale = cc.direction == 0 ? Mathf.Abs(s.x) : cc.direction == 1 ? Mathf.Abs(s.y) : Mathf.Abs(s.z);
                        var radiusScale = cc.direction == 0 ? Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))
                            : cc.direction == 1 ? Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z)) : Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y));
                        var r = cc.radius * radiusScale;
                        var axisRot = cc.direction == 0 ? Quaternion.Euler(0f, 0f, -90f) : cc.direction == 2 ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
                        world.Capsules[inst.CapsuleStart + rt.StaticCapsules + capsules++] = new KPCapsule
                        {
                            DrivingIndex = KPDriving.World, WorldLocation = t.TransformPoint(cc.center), WorldRotation = t.rotation * axisRot,
                            WorldRadius = r, WorldLength = Mathf.Max(0f, cc.height * axisScale - 2f * r),
                            Rotation = quaternion.identity, OffsetRotation = quaternion.identity,
                        };
                        break;
                    }
                    case BoxCollider bc when boxes < cap:
                        world.Boxes[inst.BoxStart + rt.StaticBoxes + boxes++] = new KPBox
                        {
                            DrivingIndex = KPDriving.World, WorldLocation = t.TransformPoint(bc.center), WorldRotation = t.rotation,
                            WorldExtent = Vector3.Scale(bc.size * 0.5f, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z))),
                            Rotation = quaternion.identity, OffsetRotation = quaternion.identity,
                        };
                        break;
                    case MeshCollider mc when boxes < cap && mc.sharedMesh != null:
                    {
                        // 凸包資料拿不到，退回包圍盒（原作 Convex Fallback：Bounding Box）
                        var bounds = mc.sharedMesh.bounds;
                        world.Boxes[inst.BoxStart + rt.StaticBoxes + boxes++] = new KPBox
                        {
                            DrivingIndex = KPDriving.World, WorldLocation = t.TransformPoint(bounds.center), WorldRotation = t.rotation,
                            WorldExtent = Vector3.Scale(bounds.extents, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z))),
                            Rotation = quaternion.identity, OffsetRotation = quaternion.identity,
                        };
                        break;
                    }
                    case CharacterController ch when capsules < cap:
                    {
                        var r = ch.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
                        world.Capsules[inst.CapsuleStart + rt.StaticCapsules + capsules++] = new KPCapsule
                        {
                            DrivingIndex = KPDriving.World, WorldLocation = t.TransformPoint(ch.center), WorldRotation = t.rotation,
                            WorldRadius = r, WorldLength = Mathf.Max(0f, ch.height * Mathf.Abs(s.y) - 2f * r),
                            Rotation = quaternion.identity, OffsetRotation = quaternion.identity,
                        };
                        break;
                    }
                }
            }

            // 地面：往下打一條射線，命中點當成厚 0.1m、寬 10m 的薄箱子的上表面
            if (chain.simpleWorldCollisionGroundCollision &&
                Physics.Raycast(comp.position + Vector3.up * 0.5f, Vector3.down, out var hit, chain.simpleWorldCollisionGatherRadius + 0.5f,
                    chain.simpleWorldCollisionLayers, QueryTriggerInteraction.Ignore) && !hit.collider.transform.IsChildOf(comp.root))
            {
                const float half = 0.05f;
                var rot = Quaternion.FromToRotation(Vector3.up, hit.normal);
                world.Boxes[inst.BoxStart + rt.StaticBoxes + boxes++] = new KPBox
                {
                    DrivingIndex = KPDriving.World, WorldLocation = hit.point - hit.normal * half, WorldRotation = rot,
                    WorldExtent = new Vector3(5f, half, 5f), Rotation = quaternion.identity, OffsetRotation = quaternion.identity,
                };
            }

            inst.SphereCount = rt.StaticSpheres + spheres;
            inst.CapsuleCount = rt.StaticCapsules + capsules;
            inst.BoxCount = rt.StaticBoxes + boxes;
        }

        // ===================================================================
        // 建立與清理
        // ===================================================================

        static void Sync()
        {
            membershipChanged = false;
            chains.RemoveAll(c => c == null);
            world ??= new KPWorld();
            world.Sync(chains, restCache, syncRestCache);
            foreach (var c in chains) c.dirty = false;
            windZonesDirty = true;
            // 靜止姿勢快取：停用後才刪除的角色，骨頭不會在移除時被清掉 → 快取超過使用中的兩倍時清一次（攤提成本）
            var limit = 2 * Mathf.Max(256, world.UsedTransforms);
            if (restCache.Count > limit) PruneDestroyed(restCache);
            if (syncRestCache.Count > limit) PruneDestroyed(syncRestCache);
            var n = Mathf.Max(1, world.InstanceCount);
            if (!layerMasks.IsCreated || layerMasks.Length != n)
            {
                if (layerMasks.IsCreated) layerMasks.Dispose();
                layerMasks = new NativeArray<int>(n, Allocator.Persistent);
            }
        }

        static readonly List<Transform> pruneKeys = new List<Transform>();

        static void PruneDestroyed<T>(Dictionary<Transform, T> cache)
        {
            pruneKeys.Clear();
            foreach (var t in cache.Keys)
                if (t == null) pruneKeys.Add(t);
            foreach (var t in pruneKeys) cache.Remove(t);
            pruneKeys.Clear();
        }

        /// <summary>靜止姿勢快取的筆數（測試用）。</summary>
        public static int RestCacheCount => restCache.Count;

        /// <summary>全部清掉並釋放原生記憶體（離開 Play、程式結束、重新編譯腳本時自動呼叫）。</summary>
        public static void Shutdown()
        {
            KPSoftBodySystem.Shutdown();
            world?.Dispose();
            world = null;
            if (layerMasks.IsCreated) layerMasks.Dispose();
            chains.Clear();
            restCache.Clear();
            syncRestCache.Clear();
            windZones.Clear();
            membershipChanged = false;
            FrameIndex = 0;
            SimTime = 0f;
            windZonesDirty = true;
        }

        // ===================================================================
        // PlayerLoop
        // ===================================================================

        struct KPRestore { }
        struct KPSimulate { }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void InstallPlayerLoop()
        {
            // Domain Reload 關閉時靜態值會留到下一次 Play，這裡先清掉
            Shutdown();
            AfterSimulate = null;
            Application.quitting -= Shutdown;
            Application.quitting += Shutdown;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
#if UNITY_EDITOR
            // Play 中重新編譯腳本：Domain 卸載前先釋放原生陣列
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
#endif

            var loop = PlayerLoop.GetCurrentPlayerLoop();
            RemoveSystems(ref loop);
            Insert(ref loop, typeof(Update), typeof(Update.ScriptRunBehaviourUpdate), false,
                new PlayerLoopSystem { type = typeof(KPRestore), updateDelegate = RestorePhase });
            Insert(ref loop, typeof(PreLateUpdate), typeof(PreLateUpdate.ScriptRunBehaviourLateUpdate), true,
                new PlayerLoopSystem { type = typeof(KPSimulate), updateDelegate = () => SimulatePhase(Time.deltaTime) });
            PlayerLoop.SetPlayerLoop(loop);
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => windZonesDirty = true;
        static void OnSceneUnloaded(Scene scene) => windZonesDirty = true;

        static void Insert(ref PlayerLoopSystem loop, Type phase, Type anchor, bool after, PlayerLoopSystem system)
        {
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != phase) continue;
                var subs = new List<PlayerLoopSystem>(loop.subSystemList[i].subSystemList);
                var at = subs.FindIndex(s => s.type == anchor);
                subs.Insert(at < 0 ? (after ? subs.Count : 0) : (after ? at + 1 : at), system);
                loop.subSystemList[i].subSystemList = subs.ToArray();
                return;
            }
        }

        static void RemoveSystems(ref PlayerLoopSystem loop)
        {
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                var subs = loop.subSystemList[i].subSystemList;
                if (subs == null) continue;
                var list = new List<PlayerLoopSystem>(subs);
                list.RemoveAll(s => s.type == typeof(KPRestore) || s.type == typeof(KPSimulate));
                loop.subSystemList[i].subSystemList = list.ToArray();
            }
        }
    }
}
