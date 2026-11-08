// 骨鏈主機的共用部分：KPWorld、靜止姿勢的快取、亂數與模擬時間、每幀在主執行緒的準備（外力、倍率、風、簡易世界碰撞）與解算的排程。
// 場景上的 KawaiiPhysicsChain（KPChainHost，骨頭是 Transform）與陣列接口（KPRig）各自衍生，只有讀寫骨頭的方式不同。
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    internal abstract class KPChainCore
    {
        protected readonly KPHierarchy hierarchy;
        /// <summary>靜止姿勢（節點 → 區域位置、旋轉）：第一次看到這根骨頭時記，之後重建也不會把物理結果當成靜止姿勢。</summary>
        protected readonly Dictionary<int, (Vector3 pos, Quaternion rot)> restCache = new Dictionary<int, (Vector3, Quaternion)>();
        /// <summary>SyncBone 的靜止位置（元件空間）。</summary>
        protected readonly Dictionary<int, Vector3> syncRestCache = new Dictionary<int, Vector3>();
        protected KPWorld world;
        // Shutdown 不重設（跟改版前的靜態欄位一樣）
        uint frameSeed = 1;
        NativeArray<int> layerMasks;

        protected KPChainCore(KPHierarchy hierarchy) => this.hierarchy = hierarchy;

        internal KPWorld World => world;
        /// <summary>每幀的亂數種子（外力的雜訊用；測試固定它）。</summary>
        internal uint FrameSeed { get => frameSeed; set => frameSeed = value; }
        /// <summary>模擬的幀數（PrepareFrame 的呼叫次數）。</summary>
        public int FrameIndex { get; private set; }
        /// <summary>累計的模擬時間（秒）。</summary>
        public float SimTime { get; private set; }
        public int RestCacheCount => restCache.Count;
        public bool HasChains => world != null && world.InstanceCount > 0;

        /// <summary>元件（角色根）這一幀的世界位置（簡易世界碰撞的收集中心）。</summary>
        protected abstract Vector3 ComponentPosition(int component);
        /// <summary>這個場景碰撞體是不是角色自己身上的（簡易世界碰撞不收；原作 bIgnoreSelfComponent）。</summary>
        protected abstract bool IsOwnCollider(Transform collider, int component);

        /// <summary>打包內容跟 chains 一致（新加入、dirty 的重建，其他保留模擬狀態）。</summary>
        protected void SyncWorld(List<KPRigChain> chains)
        {
            world ??= new KPWorld(hierarchy);
            world.Sync(chains, restCache, syncRestCache);
            KPWindZones.MarkDirty();
            var n = Mathf.Max(1, world.InstanceCount);
            if (!layerMasks.IsCreated || layerMasks.Length != n)
            {
                if (layerMasks.IsCreated) layerMasks.Dispose();
                layerMasks = new NativeArray<int>(n, Allocator.Persistent);
            }
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

        protected unsafe JobHandle ScheduleWorldCollision(JobHandle dependsOn, KPWorldPtrs ptrs)
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

        protected void PrepareFrame(float dt)
        {
            frameSeed = frameSeed * 1664525u + 1013904223u;
            FrameIndex++;
            SimTime += dt;
            UpdateWindZones();
            var ctx = new KPForceContext { DeltaTime = dt, FrameSeed = frameSeed, FrameIndex = FrameIndex };
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
                        if (rt.PackedForces[k].CanApplyTo(hierarchy, node)) mask |= 1u << k;
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
            var component = hierarchy.ResolveComponent(chain.component);
            var compPosition = ComponentPosition(component);
            rt.GatherTimer += dt;
            if (rt.GatherTimer >= chain.simpleWorldCollisionGatherInterval)
            {
                rt.GatherTimer = 0f;
                rt.Gathered.Clear();
                var n = Physics.OverlapSphereNonAlloc(compPosition, chain.simpleWorldCollisionGatherRadius, overlapBuffer,
                    chain.simpleWorldCollisionLayers, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var c = overlapBuffer[i];
                    if (c == null || IsOwnCollider(c.transform, component)) continue; // 自己身上的不算（原作 bIgnoreSelfComponent）
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
                            // 3 個引數的 Mathf.Max 是 params 陣列（每幀配置）：拆成兩層
                            WorldRadius = sc.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))),
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
                Physics.Raycast(compPosition + Vector3.up * 0.5f, Vector3.down, out var hit, chain.simpleWorldCollisionGatherRadius + 0.5f,
                    chain.simpleWorldCollisionLayers, QueryTriggerInteraction.Ignore) && !IsOwnCollider(hit.collider.transform, component))
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

        /// <summary>world 與原生陣列釋放、快取清掉（亂數種子不重設）。</summary>
        protected void DisposeCore()
        {
            world?.Dispose();
            world = null;
            if (layerMasks.IsCreated) layerMasks.Dispose();
            restCache.Clear();
            syncRestCache.Clear();
            FrameIndex = 0;
            SimTime = 0f;
        }
    }
}
