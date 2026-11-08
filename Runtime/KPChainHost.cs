// Chain 的主機：一組 Chain、它們打包成的 KPWorld、靜止姿勢的快取、亂數與模擬時間。
// KPSystem 持有一個全域的主機（場景上的 KawaiiPhysicsChain，骨頭是 Transform），PlayerLoop 每幀呼叫它。
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace KawaiiPhysicsUnity
{
    internal sealed class KPChainHost
    {
        /// <summary>骨頭的來源（場景上的 Transform）。</summary>
        internal readonly KPTransformHierarchy Hierarchy = new KPTransformHierarchy();

        readonly List<KawaiiPhysicsChain> chains = new List<KawaiiPhysicsChain>();
        /// <summary>靜止姿勢（節點 → 區域位置、旋轉）：第一次看到這根骨頭時記，之後重建也不會把物理結果當成靜止姿勢。</summary>
        readonly Dictionary<int, (Vector3 pos, Quaternion rot)> restCache = new Dictionary<int, (Vector3, Quaternion)>();
        /// <summary>SyncBone 的靜止位置（元件空間）。</summary>
        readonly Dictionary<int, Vector3> syncRestCache = new Dictionary<int, Vector3>();
        KPWorld world;
        bool membershipChanged;
        // Shutdown 不重設（跟改版前的靜態欄位一樣）
        uint frameSeed = 1;
        NativeArray<int> layerMasks;
        /// <summary>上次回收節點 id 之後的登記數（超過兩倍才再回收）。</summary>
        int hierarchyCountAfterFree;

        internal KPWorld World => world;
        /// <summary>每幀的亂數種子（外力的雜訊用；測試固定它）。</summary>
        internal uint FrameSeed { get => frameSeed; set => frameSeed = value; }
        /// <summary>模擬的幀數（PrepareFrame 的呼叫次數）。</summary>
        public int FrameIndex { get; private set; }
        /// <summary>累計的模擬時間（秒）。</summary>
        public float SimTime { get; private set; }
        public int RestCacheCount => restCache.Count;
        public bool HasChains => world != null && world.InstanceCount > 0;

        /// <summary>加入模擬。下一幀只建這一條，其他 Chain 不受影響。</summary>
        public void Register(KawaiiPhysicsChain chain)
        {
            if (!chains.Contains(chain)) chains.Add(chain);
            // SyncBone 的靜止位置在啟用時（動畫還沒動）記下來，對應原作的參考姿勢
            var comp = Hierarchy.Id(chain.ResolveComponent());
            foreach (var s in chain.syncBones)
            {
                if (s == null || s.bone == null) continue;
                var node = Hierarchy.Id(s.bone);
                if (!syncRestCache.ContainsKey(node)) syncRestCache.Add(node, Hierarchy.ComponentSpacePosition(comp, node));
            }
            membershipChanged = true;
        }

        /// <summary>移出模擬：當場移除這一條（骨頭放回靜止姿勢），並忘掉這些骨頭的靜止姿勢。</summary>
        public void Unregister(KawaiiPhysicsChain chain)
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
                var node = seg.Nodes[index];
                if (Hierarchy.IsAlive(node)) restCache.Remove(node);
            }
        }

        /// <summary>碰撞體在 Inspector 改了：所有 Chain 下一幀比對結構簽章，有用到它的才重建。</summary>
        public void RequestValidateAll()
        {
            foreach (var c in chains)
                if (c != null) c.validateRequested = true;
        }

        /// <summary>動畫更新前：需要時同步，骨頭放回靜止姿勢。</summary>
        public void Restore()
        {
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
                    c.CopyToSettings(Hierarchy, true);
                    var seg = c.runtime?.Segment;
                    if (seg == null || seg.Signature != KPWorld.ComputeSignature(c.settings)) c.dirty = true;
                    else c.settingsDirty = true;
                }
                needsSync |= c.dirty;
            }
            if (needsSync) Sync();
            if (world == null || world.InstanceCount == 0) return;
            new KPRestoreJob { Rest = world.Rest }.Schedule(world.Transforms).Complete();
        }

        /// <summary>準備 → 讀 → 解 →（世界碰撞）→ 寫，做完才回來。</summary>
        public void Simulate(float deltaTime)
        {
            // 元件的欄位複製進設定類別（每幀的值；結構在同步時換算）
            foreach (var c in chains)
            {
                if (c != null) c.CopyToSettings(Hierarchy, false);
                else if (!ReferenceEquals(c, null)) c.settings.skipFrame = true; // 刪掉了、下次動畫前才同步：這一幀照舊
            }
            PrepareFrame(deltaTime);
            // 流程消耗掉的旗標寫回元件
            foreach (var c in chains)
                if (c != null) c.needWarmUp = c.settings.needWarmUp;
            // 指標要在排程之前取：讀取工作排下去之後再對 Reads 呼叫 GetUnsafePtr 會被安全檢查擋下
            var ptrs = world.Pointers();
            var read = new KPReadJob { Reads = world.Reads }.ScheduleReadOnly(world.Transforms, 32);
            var handle = ScheduleSolve(deltaTime, read, ptrs);
            handle = ScheduleWorldCollision(handle, ptrs);
            new KPWriteJob { Writes = world.Writes }.Schedule(world.Transforms, handle).Complete();
        }

        internal unsafe JobHandle ScheduleSolve(float deltaTime, JobHandle dependsOn, KPWorldPtrs ptrs)
        {
            var job = new KPSolveJob
            {
                Instances = (KPInstance*)world.Instances.GetUnsafePtr(),
                World = ptrs,
                DeltaTime = deltaTime,
            };
            switch (KPSystem.Backend)
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

        unsafe JobHandle ScheduleWorldCollision(JobHandle dependsOn, KPWorldPtrs ptrs)
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

        void PrepareFrame(float dt)
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
                if (chain.skipFrame) continue;
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

                inst.Enabled = chain.enabled ? 1 : 0;
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
                inst.EnableWind = chain.enableWind && KPWindZones.Count > 0 ? 1 : 0;
                inst.WindScale = chain.windScale;
                inst.WindNoiseAngle = chain.windDirectionNoiseAngle;
                inst.WindGustFactor = rt.Rng.NextFloat(0f, 2f);
                inst.WindNoiseRotation = chain.windDirectionNoiseAngle > 0f
                    ? quaternion.AxisAngle(rt.Rng.NextFloat3Direction(), rt.Rng.NextFloat(0f, math.radians(chain.windDirectionNoiseAngle)))
                    : quaternion.identity;

                var control = chain.control;
                if (control.PendingTeleport) { inst.Teleport = 1; control.PendingTeleport = false; }
                if (control.PendingReset)
                {
                    inst.HasPrevComponent = 0;
                    inst.PoseInitialized = 0;
                    inst.SubstepAccumulator = 0f;
                    control.PendingReset = false;
                }
                if (chain.needWarmUp && chain.warmUpFrames > 0)
                {
                    inst.WarmUpPending = chain.warmUpFrames;
                    chain.needWarmUp = false;
                }

                // 暫時外力、設定倍率（原作 ConsumeAndRemoveExpired*／ConsumeAndAdvance*）
                control.AdvanceTransients(dt);
                control.AdvanceMultipliers(dt);
                inst.Multiplier = control.EffectiveMultiplier();

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

        void PrepareForces(KPChainRuntime rt, ref KPInstance inst, in KPForceContext ctx)
        {
            var chain = rt.Chain;
            rt.PackedForces.Clear();
            foreach (var f in chain.externalForces) if (f != null && f.enabled) rt.PackedForces.Add(f);
            foreach (var t in chain.control.transientForces) if (t.Force != null && t.Force.enabled) rt.PackedForces.Add(t.Force);
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
                    var node = i < rt.BoneNodes.Count ? rt.BoneNodes[i] : KPHierarchy.None;
                    for (int k = 0; k < rt.PackedForces.Count && k < 32; k++)
                        if (rt.PackedForces[k].CanApplyTo(Hierarchy, node)) mask |= 1u << k;
                    b.ForceMask = mask;
                    world.Bones[inst.BoneStart + i] = b;
                }
            }

            // PostApply：一次性的外力用完移除
            chain.externalForces.RemoveAll(f => f != null && f.enabled && f.oneShot);
        }

        void UpdateWindZones() => world.WindZoneCount = KPWindZones.Fill(world.WindZones, SimTime);

        // ===================================================================
        // 簡易世界碰撞（原作 Simple World Collision：定期收集附近的簡單碰撞，不做掃掠）
        // ===================================================================

        static readonly Collider[] overlapBuffer = new Collider[256];
        // 地形碰撞的型別（TerrainPhysics 模組沒裝時是 null）：用型別比對，套件就不必依賴 Terrain 模組
        static readonly Type terrainColliderType = Type.GetType("UnityEngine.TerrainCollider, UnityEngine.TerrainPhysicsModule");

        void UpdateSimpleWorldCollision(KPChainRuntime rt, ref KPInstance inst, float dt)
        {
            var chain = rt.Chain;
            var comp = Hierarchy.Get(chain.component);
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

        void Sync()
        {
            membershipChanged = false;
            chains.RemoveAll(c => c == null);
            world ??= new KPWorld(Hierarchy);
            settingsList.Clear();
            foreach (var c in chains)
            {
                c.CopyToSettings(Hierarchy, true);
                settingsList.Add(c.settings);
            }
            world.Sync(settingsList, restCache, syncRestCache);
            settingsList.Clear();
            foreach (var c in chains)
            {
                c.dirty = false;
                c.needWarmUp = c.settings.needWarmUp; // 建立時消耗的暖機旗標寫回元件
            }
            KPWindZones.MarkDirty();
            // 靜止姿勢快取：停用後才刪除的角色，骨頭不會在移除時被清掉 → 快取超過使用中的兩倍時清一次（攤提成本）
            var limit = 2 * Mathf.Max(256, world.UsedTransforms);
            if (restCache.Count > limit) PruneDestroyed(restCache);
            if (syncRestCache.Count > limit) PruneDestroyed(syncRestCache);
            if (Hierarchy.Count > 2 * Mathf.Max(256, hierarchyCountAfterFree)) FreeDeadNodes();
            var n = Mathf.Max(1, world.InstanceCount);
            if (!layerMasks.IsCreated || layerMasks.Length != n)
            {
                if (layerMasks.IsCreated) layerMasks.Dispose();
                layerMasks = new NativeArray<int>(n, Allocator.Persistent);
            }
        }

        readonly List<int> pruneKeys = new List<int>();
        readonly List<KPRigChain> settingsList = new List<KPRigChain>();

        void PruneDestroyed<T>(Dictionary<int, T> cache)
        {
            pruneKeys.Clear();
            foreach (var node in cache.Keys)
                if (!Hierarchy.IsAlive(node)) pruneKeys.Add(node);
            foreach (var node in pruneKeys) cache.Remove(node);
            pruneKeys.Clear();
        }

        readonly HashSet<int> referencedNodes = new HashSet<int>();

        /// <summary>被刪掉的 Transform 的 id 回收（角色反覆生成、刪除時登記表不會一直長）。段還在用的不回收。</summary>
        void FreeDeadNodes()
        {
            PruneDestroyed(restCache);
            PruneDestroyed(syncRestCache);
            referencedNodes.Clear();
            foreach (var s in world.Segments)
            {
                foreach (var node in s.Nodes) referencedNodes.Add(node);
                referencedNodes.Add(s.Family.root);
            }
            Hierarchy.FreeDead(referencedNodes);
            referencedNodes.Clear();
            hierarchyCountAfterFree = Hierarchy.Count;
        }

        /// <summary>全部清掉並釋放原生記憶體（亂數種子不重設）。</summary>
        public void Shutdown()
        {
            world?.Dispose();
            world = null;
            if (layerMasks.IsCreated) layerMasks.Dispose();
            chains.Clear();
            restCache.Clear();
            syncRestCache.Clear();
            Hierarchy.Clear();
            hierarchyCountAfterFree = 0;
            membershipChanged = false;
            FrameIndex = 0;
            SimTime = 0f;
        }
    }
}
