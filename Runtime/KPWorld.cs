// 把所有 KawaiiPhysicsChain 打包成一組 NativeArray 與 TransformAccessArray。
// 每條 Chain 是一段（KPSegment）：用自己的 Builder 建範本（索引從 0 起算），打包時平移到全域位置。
// 加入、移除、結構變更只重建那一條的範本；其他段從舊陣列整段搬過去（索引平移），模擬狀態原樣保留。
// 骨頭的建立照原作 InitModifyBones／AddModifyBone／InsertInterBoneDummyBones／CalcBoneLength／InitBoneConstraints／
// InsertBridgeDummiesForConstraints／InitSyncBones；碰撞來源是元件上的碰撞體、碰撞資產、共用碰撞、簡易世界碰撞的保留槽。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace KawaiiPhysicsUnity
{
    /// <summary>每個 Chain 在主執行緒保留的執行時資料。</summary>
    internal class KPChainRuntime
    {
        public KawaiiPhysicsChain Chain;
        public KPSegment Segment;
        public int InstanceIndex;
        public int ForceCapacity;
        /// <summary>每根骨頭（instance 內的順序）的節點 id；假骨是 -1。外力的骨頭篩選、約束與 SyncBone 找骨頭用。</summary>
        public List<int> BoneNodes = new List<int>(256);
        public readonly List<KPExternalForce> PackedForces = new List<KPExternalForce>();
        public readonly List<KPExternalForce> LastMaskForces = new List<KPExternalForce>();
        // 簡易世界碰撞
        public int StaticSpheres, StaticCapsules, StaticBoxes, StaticConvexes;
        public int SimpleCapacity;
        public float GatherTimer = float.MaxValue;
        public readonly List<Collider> Gathered = new List<Collider>();
        public Unity.Mathematics.Random Rng;
        public List<bool> ConstraintExcludeFromSubdivision;

        // 上次烘進骨頭的設定（每幀比對，變了就只重算 BaseSettings，不重建）
        float damping, stiffness, worldDampingLocation, worldDampingRotation, radius, limitAngle;
        int radiusCurveHash;

        public void CaptureSettings(KawaiiPhysicsChain c)
        {
            radiusCurveHash = KPWorld.CurveHash(c.radiusCurve);
            damping = c.damping;
            stiffness = c.stiffness;
            worldDampingLocation = c.worldDampingLocation;
            worldDampingRotation = c.worldDampingRotation;
            radius = c.radius;
            limitAngle = c.limitAngle;
        }

        public bool SettingsChanged(KawaiiPhysicsChain c) =>
            damping != c.damping || stiffness != c.stiffness || worldDampingLocation != c.worldDampingLocation ||
            worldDampingRotation != c.worldDampingRotation || radius != c.radius || limitAngle != c.limitAngle;

        /// <summary>改了半徑（值或依骨長比例的曲線），而且半徑會決定假骨的數量（依半徑加密、橋接假骨）。只在設定變了時呼叫。</summary>
        public bool RadiusChangedStructurally(KawaiiPhysicsChain c) =>
            KPWorld.RadiusIsStructural(c) && (radius != c.radius || radiusCurveHash != KPWorld.CurveHash(c.radiusCurve));
    }

    /// <summary>各種資料在打包陣列裡的起點（或一段的大小）。</summary>
    internal struct KPOffsets
    {
        public int Bone, Transform, Sphere, Capsule, Tapered, Box, Plane, Convex, ConvexPlane, Constraint, Sync, SyncTarget, Force;

        public bool IsZero => (Bone | Transform | Sphere | Capsule | Tapered | Box | Plane | Convex | ConvexPlane | Constraint | Sync | SyncTarget | Force) == 0;

        public static KPOffsets operator +(KPOffsets a, KPOffsets b) => new KPOffsets
        {
            Bone = a.Bone + b.Bone, Transform = a.Transform + b.Transform, Sphere = a.Sphere + b.Sphere, Capsule = a.Capsule + b.Capsule,
            Tapered = a.Tapered + b.Tapered, Box = a.Box + b.Box, Plane = a.Plane + b.Plane, Convex = a.Convex + b.Convex,
            ConvexPlane = a.ConvexPlane + b.ConvexPlane, Constraint = a.Constraint + b.Constraint, Sync = a.Sync + b.Sync,
            SyncTarget = a.SyncTarget + b.SyncTarget, Force = a.Force + b.Force,
        };

        public static KPOffsets operator -(KPOffsets a, KPOffsets b) => new KPOffsets
        {
            Bone = a.Bone - b.Bone, Transform = a.Transform - b.Transform, Sphere = a.Sphere - b.Sphere, Capsule = a.Capsule - b.Capsule,
            Tapered = a.Tapered - b.Tapered, Box = a.Box - b.Box, Plane = a.Plane - b.Plane, Convex = a.Convex - b.Convex,
            ConvexPlane = a.ConvexPlane - b.ConvexPlane, Constraint = a.Constraint - b.Constraint, Sync = a.Sync - b.Sync,
            SyncTarget = a.SyncTarget - b.SyncTarget, Force = a.Force - b.Force,
        };
    }

    /// <summary>一條 Chain 在打包陣列裡的一段。範本的索引從 0 起算。</summary>
    internal sealed class KPSegment
    {
        public KawaiiPhysicsChain Chain;
        public KPChainRuntime Runtime;
        public int Signature;
        /// <summary>共用碰撞的來源時，所屬的家族（同一個根物件＋標籤）。</summary>
        public bool IsSharedSource;
        public (int root, string tag) Family;

        // 範本（放進打包陣列之後就放掉，只留節點與靜止姿勢）
        public KPInstance Instance;
        public KPWorld.Builder Template;
        /// <summary>範本的 Transform 索引 → 節點 id。</summary>
        public int[] Nodes;
        /// <summary>靜止姿勢（範本的 Transform 索引、區域位置、區域旋轉）。</summary>
        public (int index, Vector3 pos, Quaternion rot)[] Rest;
        public KPOffsets Size;

        /// <summary>打包陣列裡有這一段的模擬狀態（false＝下次打包從範本複製）。</summary>
        public bool Live;
        /// <summary>目前在打包陣列裡的起點。</summary>
        public KPOffsets At;
        /// <summary>這次同步才放進陣列（Instances 要從範本來）。</summary>
        public bool PlacedThisSync;

        /// <summary>骨頭放回靜止姿勢（停用、移除、重建結構前）。</summary>
        public void RestoreRestPose(KPHierarchy hierarchy)
        {
            if (Rest == null) return;
            foreach (var (index, pos, rot) in Rest)
                hierarchy.SetLocalPositionAndRotation(Nodes[index], pos, rot);
        }
    }

    internal sealed class KPWorld : IDisposable
    {
        public NativeArray<KPInstance> Instances;
        public NativeArray<KPBone> Bones;
        public NativeArray<KPSphere> Spheres;
        public NativeArray<KPCapsule> Capsules;
        public NativeArray<KPTaperedCapsule> Tapered;
        public NativeArray<KPBox> Boxes;
        public NativeArray<KPPlane> Planes;
        public NativeArray<KPConvex> Convexes;
        public NativeArray<float4> ConvexPlanes;
        public NativeArray<KPConstraint> Constraints;
        public NativeArray<KPSyncBone> SyncBones;
        public NativeArray<KPSyncTarget> SyncTargets;
        public NativeArray<KPForce> Forces;
        public NativeArray<float> Luts;
        public NativeArray<KPWindZone> WindZones;
        public int WindZoneCount;
        /// <summary>骨頭的來源（節點 id 由它解讀）。</summary>
        public readonly KPHierarchy Hierarchy;
        /// <summary>Transform 版的骨架才有 TransformAccessArray。</summary>
        readonly KPTransformHierarchy transformHierarchy;

        public KPWorld(KPHierarchy hierarchy)
        {
            Hierarchy = hierarchy;
            transformHierarchy = hierarchy as KPTransformHierarchy;
        }
        public NativeArray<KPReadPose> Reads;
        public NativeArray<KPWriteLocal> Writes;
        public NativeArray<KPWriteLocal> Rest;
        public NativeArray<SpherecastCommand> SweepCommands;
        public NativeArray<RaycastHit> SweepHits;
        public TransformAccessArray Transforms;
        public bool AnyWorldCollision;

        /// <summary>打包中的 Chain 數（Instances 的有效長度）。</summary>
        public int InstanceCount => Segments.Count;
        /// <summary>上一次同步重建範本的 Chain 數（其他 Chain 的模擬狀態保留）。</summary>
        public int LastSyncRebuilt { get; private set; }
        /// <summary>上一次同步花的時間（毫秒）。</summary>
        public double LastSyncMs { get; private set; }
        /// <summary>同步的次數（測試用：確認只改設定時不會同步）。</summary>
        internal int SyncCount { get; private set; }
        /// <summary>打包陣列用到的 Transform 數（含空洞）。</summary>
        internal int UsedTransforms => used.Transform;
        /// <summary>上一次同步的細項（毫秒）：重建範本、重新打包、其中 TransformAccessArray 的更新。</summary>
        public double LastBuildMs { get; private set; }
        public double LastRepackMs { get; private set; }
        public double LastTransformArrayMs { get; private set; }

        internal readonly List<KPChainRuntime> Runtimes = new List<KPChainRuntime>();
        internal readonly List<KPSegment> Segments = new List<KPSegment>();
        // 外力、SyncBone 的曲線表：世界存在期間只增不減，索引一直有效
        internal readonly List<float> LutData = new List<float>();
        internal bool LutsDirty;
        static int generationCounter;
        /// <summary>外力記住的曲線表索引屬於哪一個世界（Domain Reload 關閉時外力物件會留到下一次 Play）。</summary>
        internal readonly int Generation = ++generationCounter;

        public unsafe KPWorldPtrs Pointers() => new KPWorldPtrs
        {
            Bones = (KPBone*)Bones.GetUnsafePtr(),
            Spheres = (KPSphere*)Spheres.GetUnsafePtr(),
            Capsules = (KPCapsule*)Capsules.GetUnsafePtr(),
            Tapered = (KPTaperedCapsule*)Tapered.GetUnsafePtr(),
            Boxes = (KPBox*)Boxes.GetUnsafePtr(),
            Planes = (KPPlane*)Planes.GetUnsafePtr(),
            Convexes = (KPConvex*)Convexes.GetUnsafePtr(),
            ConvexPlanes = (float4*)ConvexPlanes.GetUnsafePtr(),
            Constraints = (KPConstraint*)Constraints.GetUnsafePtr(),
            SyncBones = (KPSyncBone*)SyncBones.GetUnsafePtr(),
            SyncTargets = (KPSyncTarget*)SyncTargets.GetUnsafePtr(),
            Forces = (KPForce*)Forces.GetUnsafePtr(),
            Luts = (float*)Luts.GetUnsafePtr(),
            WindZones = (KPWindZone*)WindZones.GetUnsafePtr(),
            WindZoneCount = WindZoneCount,
            Reads = (KPReadPose*)Reads.GetUnsafePtr(),
            Writes = (KPWriteLocal*)Writes.GetUnsafePtr(),
        };

        internal void UploadLutsIfDirty()
        {
            if (!LutsDirty && Luts.IsCreated) return;
            if (Luts.IsCreated) Luts.Dispose();
            Luts = new NativeArray<float>(Mathf.Max(1, LutData.Count), Allocator.Persistent);
            for (int i = 0; i < LutData.Count; i++) Luts[i] = LutData[i];
            LutsDirty = false;
        }

        internal int BakeForceLut(KPExternalForce f)
        {
            if (f.lutBaked && f.lutGeneration == Generation) return f.lut;
            f.lut = InternLut(f.forceRateByBoneLengthRate);
            f.lutBaked = true;
            f.lutGeneration = Generation;
            return f.lut;
        }

        // 曲線表依內容共用：取樣結果一樣的曲線只放一份。角色反覆生成、重建、事件重複觸發都不會讓表變大，
        // 只有出現新的曲線形狀才會增加。
        readonly Dictionary<int, int> lutByContent = new Dictionary<int, int>();
        static readonly float[] lutScratch = new float[KPMath.LutSamples];

        /// <summary>曲線取樣（t 在 [0, domain]）放進曲線表，回傳起點；曲線空的回傳 -1（呼叫端當成固定值）。</summary>
        internal int InternLut(AnimationCurve curve, float domain = 1f)
        {
            if (curve == null || curve.length == 0) return -1;
            var h = new HashCode();
            for (int i = 0; i < KPMath.LutSamples; i++)
            {
                lutScratch[i] = curve.Evaluate(domain * i / (KPMath.LutSamples - 1));
                h.Add(lutScratch[i]);
            }
            var key = h.ToHashCode();
            if (lutByContent.TryGetValue(key, out var start) && SameAsScratch(start)) return start;
            start = LutData.Count;
            for (int i = 0; i < KPMath.LutSamples; i++) LutData.Add(lutScratch[i]);
            lutByContent[key] = start; // 雜湊碰撞但內容不同時改指新的一份（舊的那份仍給原本的使用者）
            LutsDirty = true;
            return start;
        }

        bool SameAsScratch(int start)
        {
            for (int i = 0; i < KPMath.LutSamples; i++)
                if (LutData[start + i] != lutScratch[i]) return false;
            return true;
        }

        // ===================================================================
        // 同步：決定哪些段要重建範本，再重新打包
        // ===================================================================

        /// <summary>
        /// 讓打包內容跟 chains 一致。新加入、dirty 的 Chain 重建範本（只有它重置）；
        /// 共用碰撞的來源變了，同家族的使用端也重建。其他段保留模擬狀態。
        /// 移除的段把骨頭放回靜止姿勢。
        /// </summary>
        internal void Sync(List<KawaiiPhysicsChain> chains, Dictionary<int, (Vector3 pos, Quaternion rot)> restCache,
            Dictionary<int, Vector3> syncRestCache)
        {
            var sw = Stopwatch.StartNew();
            var present = new HashSet<KawaiiPhysicsChain>();
            foreach (var c in chains)
                if (c != null && c.rootBone != null) present.Add(c);

            // 要拿掉的段：Chain 不在了、結構改了（dirty）、共用碰撞的來源變了的使用端
            var removed = new List<KPSegment>();
            var kept = new HashSet<KawaiiPhysicsChain>();
            var changedFamilies = new HashSet<(int, string)>();
            foreach (var s in Segments)
            {
                if (!present.Contains(s.Chain) || s.Chain.dirty)
                {
                    removed.Add(s);
                    if (s.IsSharedSource) changedFamilies.Add(s.Family);
                }
                else kept.Add(s.Chain);
            }
            foreach (var c in present)
                if (!kept.Contains(c) && c.sharedCollisionSource) changedFamilies.Add(FamilyOf(c));
            if (changedFamilies.Count > 0)
            {
                foreach (var s in Segments)
                {
                    var c = s.Chain;
                    if (!kept.Contains(c) || !c.useSharedCollision || c.sharedCollisionSource || !changedFamilies.Contains(FamilyOf(c))) continue;
                    removed.Add(s);
                    kept.Remove(c);
                }
            }

            foreach (var s in removed)
            {
                // 骨頭放回靜止姿勢（元件停用時不要停在物理的姿勢；重建結構時從靜止姿勢重算）
                s.RestoreRestPose(Hierarchy);
                // 已刪除的 Transform 從快取拿掉（角色反覆生成、刪除時快取不會一直長）
                foreach (var node in s.Nodes)
                {
                    if (Hierarchy.IsAlive(node)) continue;
                    restCache.Remove(node);
                    syncRestCache.Remove(node);
                }
                ClearRegion(s);
                holes.Add((s.At, s.Size));
            }
            var list = new List<KPSegment>(present.Count);
            foreach (var s in Segments)
                if (kept.Contains(s.Chain)) list.Add(s);

            var t0 = sw.Elapsed.TotalMilliseconds;
            var added = new List<KPSegment>();
            foreach (var c in chains)
            {
                if (c == null || !present.Contains(c) || kept.Contains(c)) continue;
                var s = new KPSegment { Chain = c };
                BuildSegment(s, chains, restCache, syncRestCache);
                added.Add(s);
                kept.Add(c);
            }
            var t1 = sw.Elapsed.TotalMilliseconds;

            LastTransformArrayMs = 0;
            if (list.Count == 0)
            {
                // 空的世界：照順序整個打包
                list.AddRange(added);
                Repack(list);
            }
            else
            {
                foreach (var s in added)
                {
                    Place(s);
                    list.Add(s);
                }
                RebuildInstances(list);
                // 空洞超過一半才整理（整個重新打包、索引平移）
                var holeBones = 0;
                var holeTransforms = 0;
                foreach (var (_, size) in holes)
                {
                    holeBones += size.Bone;
                    holeTransforms += size.Transform;
                }
                if (holeBones * 2 > used.Bone || holeTransforms * 2 > used.Transform) Repack(list);
            }

            LastSyncRebuilt = added.Count;
            SyncCount++;
            LastSyncMs = sw.Elapsed.TotalMilliseconds;
            LastBuildMs = t1 - t0;
            LastRepackMs = LastSyncMs - t1;
        }

        // ===================================================================
        // 增量打包：空洞（移除的段）＋就地放入
        // ===================================================================

        // 已使用到的位置（之後是空的容量）；TransformAccessArray 的長度＝used.Transform
        KPOffsets used;
        readonly List<(KPOffsets at, KPOffsets size)> holes = new List<(KPOffsets, KPOffsets)>();

        /// <summary>目前的空洞數（移除的段留下、還沒被新段填掉的位置；測試用）。</summary>
        public int HoleCount => holes.Count;

        static bool Fits(in KPOffsets need, in KPOffsets room) =>
            need.Bone <= room.Bone && need.Transform <= room.Transform && need.Sphere <= room.Sphere && need.Capsule <= room.Capsule &&
            need.Tapered <= room.Tapered && need.Box <= room.Box && need.Plane <= room.Plane && need.Convex <= room.Convex &&
            need.ConvexPlane <= room.ConvexPlane && need.Constraint <= room.Constraint && need.Sync <= room.Sync &&
            need.SyncTarget <= room.SyncTarget;

        /// <summary>移除的段：寫回、還原、掃掠都停掉（TransformAccessArray 的項目留著，模式 0 不會寫）。</summary>
        void ClearRegion(KPSegment s)
        {
            for (int i = 0; i < s.Size.Transform; i++)
            {
                Rest[s.At.Transform + i] = default;
                Writes[s.At.Transform + i] = default;
            }
            if (SweepCommands.IsCreated)
                for (int i = 0; i < s.Size.Bone && s.At.Bone + i < SweepCommands.Length; i++)
                    SweepCommands[s.At.Bone + i] = default;
        }

        /// <summary>新段放進放得下的空洞（同類角色通常剛好），沒有就接在最後面。</summary>
        void Place(KPSegment s)
        {
            KPOffsets at;
            var hole = holes.FindIndex(h => Fits(s.Size, h.size));
            if (hole >= 0)
            {
                at = holes[hole].at;
                var rest = holes[hole].size - s.Size;
                if (rest.Bone > 0 || rest.Transform > 0) holes[hole] = (at + s.Size, rest);
                else holes.RemoveAt(hole);
            }
            else
            {
                at = used;
                EnsureCapacity(used + s.Size);
                used += s.Size;
            }

            CopyTemplate(s, at);

            if (transformHierarchy != null)
            {
                var taStart = Stopwatch.GetTimestamp();
                for (int k = 0; k < s.Nodes.Length; k++)
                {
                    var index = at.Transform + k;
                    var t = transformHierarchy.Get(s.Nodes[k]);
                    if (index < Transforms.length) Transforms[index] = t;
                    else Transforms.Add(t);
                }
                LastTransformArrayMs += (Stopwatch.GetTimestamp() - taStart) * 1000.0 / Stopwatch.Frequency;
            }
            foreach (var (index, pos, rot) in s.Rest)
                Rest[at.Transform + index] = new KPWriteLocal { LocalPosition = pos, LocalRotation = rot, Mode = 2 };

            s.At = at;
            s.Live = true;
            s.PlacedThisSync = true;
        }

        /// <summary>容量不夠就放大（1.5 倍），已使用的部分原樣複製（索引不變）。</summary>
        void EnsureCapacity(in KPOffsets need)
        {
            Grow(ref Bones, used.Bone, need.Bone);
            Grow(ref Spheres, used.Sphere, need.Sphere);
            Grow(ref Capsules, used.Capsule, need.Capsule);
            Grow(ref Tapered, used.Tapered, need.Tapered);
            Grow(ref Boxes, used.Box, need.Box);
            Grow(ref Planes, used.Plane, need.Plane);
            Grow(ref Convexes, used.Convex, need.Convex);
            Grow(ref ConvexPlanes, used.ConvexPlane, need.ConvexPlane);
            Grow(ref Constraints, used.Constraint, need.Constraint);
            Grow(ref SyncBones, used.Sync, need.Sync);
            Grow(ref SyncTargets, used.SyncTarget, need.SyncTarget);
            Grow(ref Rest, used.Transform, need.Transform);
            Grow(ref Writes, used.Transform, need.Transform);
            Grow(ref Reads, used.Transform, need.Transform);
        }

        static void Grow<T>(ref NativeArray<T> a, int usedCount, int need) where T : struct
        {
            if (a.IsCreated && a.Length >= need) return;
            var bigger = new NativeArray<T>(Mathf.Max(need, a.IsCreated ? a.Length * 3 / 2 : 0, 1), Allocator.Persistent);
            if (a.IsCreated)
            {
                if (usedCount > 0) NativeArray<T>.Copy(a, bigger, usedCount);
                a.Dispose();
            }
            a = bigger;
        }

        /// <summary>Instances 依段的順序重排（資料沒搬，索引不用平移），外力的槽位重新分配。</summary>
        void RebuildInstances(List<KPSegment> list)
        {
            var old = Instances;
            Instances = Alloc<KPInstance>(list.Count);
            Runtimes.Clear();
            var force = 0;
            var anyWorldCollision = false;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                KPInstance inst;
                if (s.PlacedThisSync)
                {
                    inst = s.Instance;
                    RelocInstance(ref inst, s.At);
                    s.PlacedThisSync = false;
                }
                else inst = old[s.Runtime.InstanceIndex];
                // 外力的槽位每次同步重新依序配置；段的位置也要跟著改，之後整理（Repack）才不會用舊值平移
                inst.ForceStart = force;
                s.At.Force = force;
                force += s.Size.Force;
                Instances[i] = inst;
                s.Runtime.InstanceIndex = i;
                Runtimes.Add(s.Runtime);
                anyWorldCollision |= s.Chain.allowWorldCollision;
            }
            Dispose(ref old);
            Segments.Clear();
            Segments.AddRange(list);
            if (!Forces.IsCreated || Forces.Length < force)
            {
                Dispose(ref Forces);
                Forces = Alloc<KPForce>(force);
            }
            if (!WindZones.IsCreated) WindZones = new NativeArray<KPWindZone>(16, Allocator.Persistent);
            EnsureSweepCapacity(anyWorldCollision);
            UploadLutsIfDirty();
        }


        (int, string) FamilyOf(KawaiiPhysicsChain c) => (Hierarchy.Root(NodeOf(c.ResolveComponent())), c.sharedCollisionTag ?? "");

        /// <summary>元件上的 Transform 參照 → 節點 id（Transform 版的骨架）。</summary>
        int NodeOf(Transform t) => transformHierarchy.Id(t);

        void BuildSegment(KPSegment seg, List<KawaiiPhysicsChain> chains, Dictionary<int, (Vector3 pos, Quaternion rot)> restCache,
            Dictionary<int, Vector3> syncRestCache)
        {
            var chain = seg.Chain;
            var rt = new KPChainRuntime
            {
                Chain = chain,
                Segment = seg,
                Rng = new Unity.Mathematics.Random((uint)chain.GetHashCode() * 2654435761u | 1u),
            };
            chain.runtime = rt;
            // 外力的曲線表重新取（曲線改過才會用到新的一格；沒改就是原本那一格）
            foreach (var f in chain.externalForces)
                if (f != null) f.lutBaked = false;
            var b = new Builder();
            var inst = BuildInstance(b, chain, rt, chains, syncRestCache);
            rt.ForceCapacity = Mathf.Min(32, chain.externalForces.Count + KawaiiPhysicsChain.MaxTransientExternalForces);
            rt.CaptureSettings(chain);

            seg.Runtime = rt;
            seg.Signature = ComputeSignature(chain);
            seg.IsSharedSource = chain.sharedCollisionSource;
            seg.Family = FamilyOf(chain);
            seg.Instance = inst;
            seg.Template = b;
            seg.Nodes = b.Transforms.ToArray();
            seg.Size = new KPOffsets
            {
                Bone = b.Bones.Count, Transform = b.Transforms.Count, Sphere = b.Spheres.Count, Capsule = b.Capsules.Count,
                Tapered = b.Tapered.Count, Box = b.Boxes.Count, Plane = b.Planes.Count, Convex = b.Convexes.Count,
                ConvexPlane = b.ConvexPlanes.Count, Constraint = b.Constraints.Count, Sync = b.SyncBones.Count,
                SyncTarget = b.SyncTargets.Count, Force = rt.ForceCapacity,
            };

            // 靜止姿勢只在第一次看到這根骨頭時記，之後重建也不會把物理結果當成靜止姿勢
            seg.Rest = new (int, Vector3, Quaternion)[b.RestBones.Count];
            for (int i = 0; i < b.RestBones.Count; i++)
            {
                var node = b.RestBones[i];
                if (!restCache.TryGetValue(node, out var rest))
                {
                    rest = (Hierarchy.LocalPosition(node), Hierarchy.LocalRotation(node));
                    restCache.Add(node, rest);
                }
                seg.Rest[i] = (b.Index[node], rest.pos, rest.rot);
            }
            seg.Live = false;
        }

        // ===================================================================
        // 打包：每段放到新的位置；有狀態的段從舊陣列搬（索引平移），新段從範本複製
        // ===================================================================

        void Repack(List<KPSegment> next)
        {
            var total = default(KPOffsets);
            var starts = new KPOffsets[next.Count];
            for (int i = 0; i < next.Count; i++)
            {
                starts[i] = total;
                total += next[i].Size;
            }

            var oldInstances = Instances;
            var oldBones = Bones;
            var oldSpheres = Spheres;
            var oldCapsules = Capsules;
            var oldTapered = Tapered;
            var oldBoxes = Boxes;
            var oldPlanes = Planes;
            var oldConvexes = Convexes;
            var oldConvexPlanes = ConvexPlanes;
            var oldConstraints = Constraints;
            var oldSyncBones = SyncBones;
            var oldSyncTargets = SyncTargets;

            // 容量多留一半：之後加角色時不必馬上放大陣列
            static int Room(int n) => n + n / 2;
            Instances = Alloc<KPInstance>(next.Count);
            Bones = Alloc<KPBone>(Room(total.Bone));
            Spheres = Alloc<KPSphere>(Room(total.Sphere));
            Capsules = Alloc<KPCapsule>(Room(total.Capsule));
            Tapered = Alloc<KPTaperedCapsule>(Room(total.Tapered));
            Boxes = Alloc<KPBox>(Room(total.Box));
            Planes = Alloc<KPPlane>(Room(total.Plane));
            Convexes = Alloc<KPConvex>(Room(total.Convex));
            ConvexPlanes = Alloc<float4>(Room(total.ConvexPlane));
            Constraints = Alloc<KPConstraint>(Room(total.Constraint));
            SyncBones = Alloc<KPSyncBone>(Room(total.Sync));
            SyncTargets = Alloc<KPSyncTarget>(Room(total.SyncTarget));

            var transforms = transformHierarchy != null ? new Transform[total.Transform] : null;
            var rest = Alloc<KPWriteLocal>(Room(total.Transform));
            var anyWorldCollision = false;
            Runtimes.Clear();
            for (int i = 0; i < next.Count; i++)
            {
                var s = next[i];
                var at = starts[i];
                KPInstance inst;
                if (s.Live)
                {
                    var d = at - s.At;
                    inst = oldInstances[s.Runtime.InstanceIndex];
                    Copy(Bones, at.Bone, oldBones, s.At.Bone, s.Size.Bone, d, RelocBone);
                    Copy(Spheres, at.Sphere, oldSpheres, s.At.Sphere, s.Size.Sphere, d, RelocSphere);
                    Copy(Capsules, at.Capsule, oldCapsules, s.At.Capsule, s.Size.Capsule, d, RelocCapsule);
                    Copy(Tapered, at.Tapered, oldTapered, s.At.Tapered, s.Size.Tapered, d, RelocTapered);
                    Copy(Boxes, at.Box, oldBoxes, s.At.Box, s.Size.Box, d, RelocBox);
                    Copy(Planes, at.Plane, oldPlanes, s.At.Plane, s.Size.Plane, d, RelocPlane);
                    Copy(Convexes, at.Convex, oldConvexes, s.At.Convex, s.Size.Convex, d, RelocConvex);
                    Copy(ConvexPlanes, at.ConvexPlane, oldConvexPlanes, s.At.ConvexPlane, s.Size.ConvexPlane, d, null);
                    Copy(Constraints, at.Constraint, oldConstraints, s.At.Constraint, s.Size.Constraint, d, RelocConstraint);
                    Copy(SyncBones, at.Sync, oldSyncBones, s.At.Sync, s.Size.Sync, d, RelocSyncBone);
                    Copy(SyncTargets, at.SyncTarget, oldSyncTargets, s.At.SyncTarget, s.Size.SyncTarget, d, RelocSyncTarget);
                    RelocInstance(ref inst, d);
                }
                else
                {
                    inst = s.Instance;
                    CopyTemplate(s, at);
                    RelocInstance(ref inst, at);
                    s.Live = true;
                    s.PlacedThisSync = false;
                }
                // 外力的槽位不是模擬狀態，直接放到新的起點（不靠平移，避免舊位置不一致時算錯）
                inst.ForceStart = at.Force;
                Instances[i] = inst;
                s.At = at;
                s.Runtime.InstanceIndex = i;
                Runtimes.Add(s.Runtime);
                if (transforms != null)
                    for (int k = 0; k < s.Nodes.Length; k++) transforms[at.Transform + k] = transformHierarchy.Get(s.Nodes[k]);
                foreach (var (index, pos, rot) in s.Rest)
                    rest[at.Transform + index] = new KPWriteLocal { LocalPosition = pos, LocalRotation = rot, Mode = 2 };
                anyWorldCollision |= s.Chain.allowWorldCollision;
            }
            Segments.Clear();
            Segments.AddRange(next);
            used = total;
            holes.Clear();

            Dispose(ref oldInstances); Dispose(ref oldBones); Dispose(ref oldSpheres); Dispose(ref oldCapsules); Dispose(ref oldTapered);
            Dispose(ref oldBoxes); Dispose(ref oldPlanes); Dispose(ref oldConvexes); Dispose(ref oldConvexPlanes); Dispose(ref oldConstraints);
            Dispose(ref oldSyncBones); Dispose(ref oldSyncTargets);

            Dispose(ref Forces);
            Forces = Alloc<KPForce>(total.Force);
            if (!WindZones.IsCreated) WindZones = new NativeArray<KPWindZone>(16, Allocator.Persistent);
            Dispose(ref Reads);
            Dispose(ref Writes);
            Dispose(ref Rest);
            Reads = Alloc<KPReadPose>(rest.Length);
            Writes = Alloc<KPWriteLocal>(rest.Length);
            Rest = rest;
            EnsureSweepCapacity(anyWorldCollision);
            if (transforms != null)
            {
                var taStart = Stopwatch.GetTimestamp();
                if (Transforms.isCreated) Transforms.SetTransforms(transforms);
                else Transforms = new TransformAccessArray(transforms);
                LastTransformArrayMs = (Stopwatch.GetTimestamp() - taStart) * 1000.0 / Stopwatch.Frequency;
            }
            UploadLutsIfDirty();
        }

        /// <summary>世界碰撞的掃掠指令：有任何 Chain 開著就配到骨頭數（執行中才打開也會補上）。</summary>
        internal void EnsureSweepCapacity(bool any)
        {
            AnyWorldCollision = any;
            var n = any ? Bones.Length : 0;
            if (SweepCommands.IsCreated && SweepCommands.Length == n) return;
            Dispose(ref SweepCommands);
            Dispose(ref SweepHits);
            SweepCommands = new NativeArray<SpherecastCommand>(n, Allocator.Persistent);
            SweepHits = new NativeArray<RaycastHit>(n, Allocator.Persistent);
        }

        /// <summary>Chain 的設定（damping 等與依骨長比例的曲線）重算進這一段的骨頭；模擬狀態不動。</summary>
        internal void RefreshSettings(KPChainRuntime rt)
        {
            var inst = Instances[rt.InstanceIndex];
            var flat = !rt.Chain.HasSettingsCurves();
            var flatSettings = flat ? rt.Chain.EvaluateSettings(0f) : default;
            for (int i = inst.BoneStart; i < inst.BoneStart + inst.BoneCount; i++)
            {
                var b = Bones[i];
                b.BaseSettings = flat ? flatSettings : rt.Chain.EvaluateSettings(b.LengthRate);
                Bones[i] = b;
            }
            rt.CaptureSettings(rt.Chain);
        }

        static NativeArray<T> Alloc<T>(int n) where T : struct => new NativeArray<T>(Mathf.Max(1, n), Allocator.Persistent);

        static void Dispose<T>(ref NativeArray<T> a) where T : struct
        {
            if (a.IsCreated) a.Dispose();
            a = default;
        }

        delegate void Reloc<T>(ref T x, in KPOffsets d);

        static unsafe void Copy<T>(NativeArray<T> dst, int dstStart, NativeArray<T> src, int srcStart, int n, in KPOffsets d, Reloc<T> reloc)
            where T : unmanaged
        {
            if (n <= 0) return;
            var to = (T*)dst.GetUnsafePtr() + dstStart;
            var from = (T*)src.GetUnsafeReadOnlyPtr() + srcStart;
            // 位置沒變的段（加在後面的情況下大多是這樣）整段複製
            if (reloc == null || d.IsZero)
            {
                UnsafeUtility.MemCpy(to, from, (long)n * sizeof(T));
                return;
            }
            for (int k = 0; k < n; k++)
            {
                var x = from[k];
                reloc(ref x, d);
                to[k] = x;
            }
        }

        static unsafe void Copy<T>(NativeArray<T> dst, int dstStart, List<T> src, in KPOffsets d, Reloc<T> reloc) where T : unmanaged
        {
            if (src.Count == 0) return;
            var to = (T*)dst.GetUnsafePtr() + dstStart;
            for (int k = 0; k < src.Count; k++)
            {
                var x = src[k];
                reloc?.Invoke(ref x, d);
                to[k] = x;
            }
        }

        /// <summary>範本（索引從 0 起算）平移到 at 放進打包陣列，之後範本就放掉。</summary>
        void CopyTemplate(KPSegment s, in KPOffsets at)
        {
            var b = s.Template;
            Copy(Bones, at.Bone, b.Bones, at, RelocBone);
            Copy(Spheres, at.Sphere, b.Spheres, at, RelocSphere);
            Copy(Capsules, at.Capsule, b.Capsules, at, RelocCapsule);
            Copy(Tapered, at.Tapered, b.Tapered, at, RelocTapered);
            Copy(Boxes, at.Box, b.Boxes, at, RelocBox);
            Copy(Planes, at.Plane, b.Planes, at, RelocPlane);
            Copy(Convexes, at.Convex, b.Convexes, at, RelocConvex);
            Copy(ConvexPlanes, at.ConvexPlane, b.ConvexPlanes, at, null);
            Copy(Constraints, at.Constraint, b.Constraints, at, RelocConstraint);
            Copy(SyncBones, at.Sync, b.SyncBones, at, RelocSyncBone);
            Copy(SyncTargets, at.SyncTarget, b.SyncTargets, at, RelocSyncTarget);
            s.Template = null;
        }

        // 索引欄位的平移（負值是「沒有」或特殊值，不動）
        static int Shift(int index, int delta) => index >= 0 ? index + delta : index;

        static void RelocBone(ref KPBone b, in KPOffsets d)
        {
            b.ParentIndex = Shift(b.ParentIndex, d.Bone);
            b.RealParentIndex = Shift(b.RealParentIndex, d.Bone);
            b.RealChildIndex = Shift(b.RealChildIndex, d.Bone);
            b.TransformIndex = Shift(b.TransformIndex, d.Transform);
        }

        static void RelocSphere(ref KPSphere x, in KPOffsets d) => x.DrivingIndex = Shift(x.DrivingIndex, d.Transform);
        static void RelocCapsule(ref KPCapsule x, in KPOffsets d) => x.DrivingIndex = Shift(x.DrivingIndex, d.Transform);
        static void RelocTapered(ref KPTaperedCapsule x, in KPOffsets d) => x.DrivingIndex = Shift(x.DrivingIndex, d.Transform);
        static void RelocBox(ref KPBox x, in KPOffsets d) => x.DrivingIndex = Shift(x.DrivingIndex, d.Transform);
        static void RelocPlane(ref KPPlane x, in KPOffsets d) => x.DrivingIndex = Shift(x.DrivingIndex, d.Transform);

        static void RelocConvex(ref KPConvex x, in KPOffsets d)
        {
            x.DrivingIndex = Shift(x.DrivingIndex, d.Transform);
            x.PlaneStart += d.ConvexPlane;
        }

        static void RelocConstraint(ref KPConstraint x, in KPOffsets d)
        {
            x.Bone1 += d.Bone;
            x.Bone2 += d.Bone;
        }

        static void RelocSyncBone(ref KPSyncBone x, in KPOffsets d)
        {
            x.TransformIndex = Shift(x.TransformIndex, d.Transform);
            x.TargetStart += d.SyncTarget;
        }

        static void RelocSyncTarget(ref KPSyncTarget x, in KPOffsets d) => x.BoneIndex = Shift(x.BoneIndex, d.Bone);

        static void RelocInstance(ref KPInstance x, in KPOffsets d)
        {
            x.BoneStart += d.Bone;
            x.SphereStart += d.Sphere;
            x.CapsuleStart += d.Capsule;
            x.TaperedStart += d.Tapered;
            x.BoxStart += d.Box;
            x.PlaneStart += d.Plane;
            x.ConvexStart += d.Convex;
            x.ConstraintStart += d.Constraint;
            x.SyncStart += d.Sync;
            x.ForceStart += d.Force;
            x.ComponentIndex = Shift(x.ComponentIndex, d.Transform);
            x.BaseBoneIndex = Shift(x.BaseBoneIndex, d.Transform);
        }

        // ===================================================================
        // 結構簽章：Inspector 改了東西（OnValidate）時，用來分辨「只改設定」與「改了結構」
        // ===================================================================

        /// <summary>半徑會決定假骨的數量（依半徑加密、橋接假骨）時，半徑算結構。</summary>
        internal static bool RadiusIsStructural(KawaiiPhysicsChain c) => c.boneSubdivisionDensifyByRadius || c.boneConstraintSubdivisionCount > 0;

        internal static int ComputeSignature(KawaiiPhysicsChain c)
        {
            var h = new HashCode();
            h.Add(c.ResolveComponent());
            h.Add(c.rootBone);
            foreach (var t in c.excludeBones) h.Add(t);
            foreach (var r in c.additionalRootBones)
            {
                if (r == null) continue;
                h.Add(r.rootBone);
                h.Add(r.useOverrideExcludeBones);
                if (r.useOverrideExcludeBones)
                    foreach (var t in r.overrideExcludeBones) h.Add(t);
            }
            h.Add(c.dummyBoneLength);
            h.Add(c.boneForwardAxis);
            h.Add(c.boneSubdivisionCount);
            h.Add(c.boneSubdivisionDensifyByRadius);
            h.Add(c.boneConstraintSubdivisionCount);
            if (RadiusIsStructural(c))
            {
                h.Add(c.radius);
                AddCurve(ref h, c.radiusCurve);
            }
            h.Add(c.simulationSpace);
            h.Add(c.simulationBaseBone);
            foreach (var col in c.colliders) AddCollider(ref h, col);
            h.Add(c.limitsAsset);
            h.Add(c.sharedCollisionSource);
            h.Add(c.useSharedCollision);
            h.Add(c.sharedCollisionTag);
            foreach (var bc in c.boneConstraints)
            {
                if (bc == null) continue;
                h.Add(bc.bone1);
                h.Add(bc.bone2);
                h.Add(bc.overrideCompliance);
                h.Add(bc.complianceType);
                h.Add(bc.excludeFromSubdivision);
            }
            h.Add(c.boneConstraintsAsset);
            h.Add(c.boneConstraintGlobalComplianceType);
            h.Add(c.autoAddChildDummyBoneConstraint);
            h.Add(c.useSimpleWorldCollision);
            h.Add(c.simpleWorldCollisionMaxShapes);
            h.Add(c.externalForces.Count);
            foreach (var s in c.syncBones)
            {
                if (s == null) continue;
                h.Add(s.bone);
                h.Add(s.globalScale);
                h.Add(s.applyDirectionX);
                h.Add(s.applyDirectionY);
                h.Add(s.applyDirectionZ);
                h.Add(s.enableDistanceAttenuation);
                h.Add(s.attenuationInnerRadius);
                h.Add(s.attenuationOuterRadius);
                h.Add(s.maxAttenuationRate);
                AddCurve(ref h, s.scaleCurveByDeltaDistance);
                foreach (var r in s.targetRoots)
                {
                    if (r == null) continue;
                    h.Add(r.bone);
                    h.Add(r.includeChildBones);
                    AddCurve(ref h, r.scaleCurveByBoneLengthRate);
                }
            }
            return h.ToHashCode();
        }

        static void AddCollider(ref HashCode h, KawaiiPhysicsCollider col)
        {
            h.Add(col);
            if (col == null) return;
            h.Add(col.shape);
            h.Add(col.center);
            h.Add(col.direction);
            h.Add(col.radius);
            h.Add(col.radius1);
            h.Add(col.length);
            h.Add(col.extent);
            h.Add(col.inner);
            h.Add(col.convexMesh);
        }

        internal static int CurveHash(AnimationCurve c)
        {
            var h = new HashCode();
            AddCurve(ref h, c);
            return h.ToHashCode();
        }

        static void AddCurve(ref HashCode h, AnimationCurve c)
        {
            if (c == null) return;
            for (int i = 0; i < c.length; i++)
            {
                var k = c[i];
                h.Add(k.time);
                h.Add(k.value);
                h.Add(k.inTangent);
                h.Add(k.outTangent);
            }
        }

        // ===================================================================
        // 建立（一條 Chain 的範本）
        // ===================================================================

        internal sealed class Builder
        {
            // 一般的角色（數百根骨頭）不必中途擴充
            /// <summary>Transform 索引 → 節點 id（讀寫陣列的順序）。</summary>
            public readonly List<int> Transforms = new List<int>(256);
            public readonly Dictionary<int, int> Index = new Dictionary<int, int>(256);
            public readonly List<KPBone> Bones = new List<KPBone>(256);
            public readonly List<KPSphere> Spheres = new List<KPSphere>();
            public readonly List<KPCapsule> Capsules = new List<KPCapsule>();
            public readonly List<KPTaperedCapsule> Tapered = new List<KPTaperedCapsule>();
            public readonly List<KPBox> Boxes = new List<KPBox>();
            public readonly List<KPPlane> Planes = new List<KPPlane>();
            public readonly List<KPConvex> Convexes = new List<KPConvex>();
            public readonly List<float4> ConvexPlanes = new List<float4>();
            public readonly List<KPConstraint> Constraints = new List<KPConstraint>();
            public readonly List<KPSyncBone> SyncBones = new List<KPSyncBone>();
            public readonly List<KPSyncTarget> SyncTargets = new List<KPSyncTarget>();
            public readonly List<int> RestBones = new List<int>(256);

            public int IndexOf(int node)
            {
                if (Index.TryGetValue(node, out var i)) return i;
                i = Transforms.Count;
                Transforms.Add(node);
                Index.Add(node, i);
                return i;
            }
        }

        KPInstance BuildInstance(Builder b, KawaiiPhysicsChain chain, KPChainRuntime rt,
            List<KawaiiPhysicsChain> allChains, Dictionary<int, Vector3> syncRestCache)
        {
            var h = Hierarchy;
            var comp = NodeOf(chain.ResolveComponent());
            var inst = KPInstance.Create(Mathf.Max(1, chain.targetFramerate));
            inst.UseFixedSubstep = chain.fixedSubstepping ? 1 : 0;
            inst.MaxSubsteps = chain.maxSubsteps;
            inst.ComponentIndex = b.IndexOf(comp);
            inst.SimulationSpace = (int)chain.simulationSpace;
            if (chain.simulationSpace == KPSimulationSpace.BaseBoneSpace && chain.simulationBaseBone != null)
                inst.BaseBoneIndex = b.IndexOf(NodeOf(chain.simulationBaseBone));
            inst.ForwardAxis = (int)chain.boneForwardAxis;
            inst.DummyBoneLength = chain.dummyBoneLength;
            inst.CollisionOnlySubdivision = chain.boneSubdivisionCollisionOnly ? 1 : 0;
            inst.ConstraintItersBefore = chain.boneConstraintIterationCountBeforeCollision;
            inst.ConstraintItersAfter = chain.boneConstraintIterationCountAfterCollision;
            inst.WarmUpPending = chain.needWarmUp ? chain.warmUpFrames : 0;
            chain.needWarmUp = false;

            // 長度換到模擬空間的單位（元件空間＝除以元件縮放）
            var simScale = chain.simulationSpace == KPSimulationSpace.WorldSpace ? 1f
                : chain.simulationSpace == KPSimulationSpace.BaseBoneSpace && chain.simulationBaseBone != null
                    ? h.LossyScaleX(NodeOf(chain.simulationBaseBone)) : h.LossyScaleX(comp);
            simScale = Mathf.Approximately(simScale, 0f) ? 1f : simScale;

            // ---- 骨頭 ----
            inst.BoneStart = b.Bones.Count;
            var worldPos = new List<Vector3>(256); // instance 內每根骨頭建立時的世界位置（長度、約束用）
            AddRoot(b, chain, NodeOf(chain.rootBone), chain.excludeBones, rt, worldPos, simScale);
            foreach (var r in chain.additionalRootBones)
                if (r != null && r.rootBone != null)
                    AddRoot(b, chain, NodeOf(r.rootBone), r.useOverrideExcludeBones ? r.overrideExcludeBones : chain.excludeBones, rt, worldPos, simScale);

            // ---- 骨間約束 ----
            inst.ConstraintStart = b.Constraints.Count;
            BuildConstraints(b, chain, inst.BoneStart, rt, worldPos, comp, simScale);
            inst.ConstraintCount = b.Constraints.Count - inst.ConstraintStart;
            InsertBridgeDummies(b, chain, inst.BoneStart, inst.ConstraintStart, inst.ConstraintCount, rt, worldPos, simScale);
            inst.BridgeFeedbackScale = chain.boneConstraintSubdivisionCount > 0 ? chain.boneConstraintSubdivisionFeedbackScale : 0f;
            inst.BoneCount = b.Bones.Count - inst.BoneStart;

            // ---- SyncBone ----
            inst.SyncStart = b.SyncBones.Count;
            BuildSyncBones(b, chain, inst.BoneStart, rt, comp, syncRestCache);
            inst.SyncCount = b.SyncBones.Count - inst.SyncStart;

            // ---- 碰撞 ----
            inst.SphereStart = b.Spheres.Count;
            inst.CapsuleStart = b.Capsules.Count;
            inst.TaperedStart = b.Tapered.Count;
            inst.BoxStart = b.Boxes.Count;
            inst.PlaneStart = b.Planes.Count;
            inst.ConvexStart = b.Convexes.Count;
            AddColliders(b, chain.colliders);
            AddLimitsAsset(b, chain.limitsAsset, comp);
            if (chain.useSharedCollision && !chain.sharedCollisionSource)
            {
                foreach (var other in allChains)
                {
                    if (other == null || other == chain || !other.sharedCollisionSource) continue;
                    if (other.sharedCollisionTag != chain.sharedCollisionTag) continue;
                    var otherComp = NodeOf(other.ResolveComponent());
                    if (h.Root(otherComp) != h.Root(comp)) continue; // 原作：同一個 attached actor family
                    AddColliders(b, other.colliders);
                    AddLimitsAsset(b, other.limitsAsset, otherComp);
                }
            }
            rt.StaticSpheres = b.Spheres.Count - inst.SphereStart;
            rt.StaticCapsules = b.Capsules.Count - inst.CapsuleStart;
            rt.StaticBoxes = b.Boxes.Count - inst.BoxStart;
            rt.StaticConvexes = b.Convexes.Count - inst.ConvexStart;
            if (chain.useSimpleWorldCollision)
            {
                // 簡易世界碰撞的保留槽：每種形狀 N 個、箱子多一個給地面
                rt.SimpleCapacity = Mathf.Max(1, chain.simpleWorldCollisionMaxShapes);
                for (int i = 0; i < rt.SimpleCapacity; i++)
                {
                    b.Spheres.Add(new KPSphere { DrivingIndex = KPDriving.World });
                    b.Capsules.Add(new KPCapsule { DrivingIndex = KPDriving.World, Rotation = quaternion.identity, WorldRotation = quaternion.identity, OffsetRotation = quaternion.identity });
                }
                for (int i = 0; i < rt.SimpleCapacity + 1; i++)
                    b.Boxes.Add(new KPBox { DrivingIndex = KPDriving.World, Rotation = quaternion.identity, WorldRotation = quaternion.identity, OffsetRotation = quaternion.identity });
            }
            inst.SphereCount = rt.StaticSpheres;
            inst.CapsuleCount = rt.StaticCapsules;
            inst.TaperedCount = b.Tapered.Count - inst.TaperedStart;
            inst.BoxCount = rt.StaticBoxes;
            inst.PlaneCount = b.Planes.Count - inst.PlaneStart;
            inst.ConvexCount = rt.StaticConvexes;
            return inst;
        }

        // ---- 骨頭（AddModifyBone） ----

        void AddRoot(Builder b, KawaiiPhysicsChain chain, int root, List<Transform> exclude, KPChainRuntime rt,
            List<Vector3> worldPos, float simScale)
        {
            var start = b.Bones.Count;
            // 排除清單是空的時候不建也不查（走訪是建置裡最花時間的部分）
            HashSet<int> excludeSet = null;
            if (exclude != null && exclude.Count > 0)
            {
                excludeSet = new HashSet<int>();
                foreach (var t in exclude) excludeSet.Add(NodeOf(t));
            }
            AddBone(b, chain, root, -1, excludeSet, rt, worldPos, simScale);

            // CalcBoneLength：離根長度、總長、比例
            var lengthFromRoot = new float[b.Bones.Count - start];
            var total = 0f;
            for (int i = start; i < b.Bones.Count; i++)
            {
                var bone = b.Bones[i];
                if (bone.ParentIndex < 0) continue;
                lengthFromRoot[i - start] = lengthFromRoot[bone.ParentIndex - start] + bone.BoneLength;
                total = Mathf.Max(total, lengthFromRoot[i - start]);
            }
            // 依骨長比例的曲線都沒設時，每根骨頭的設定都一樣，只算一次
            var flat = !chain.HasSettingsCurves();
            var flatSettings = flat ? chain.EvaluateSettings(0f) : default;
            for (int i = start; i < b.Bones.Count; i++)
            {
                var bone = b.Bones[i];
                var l = lengthFromRoot[i - start];
                bone.LengthRate = l > 0f && total > 0f ? l / total : 0f;
                bone.BaseSettings = flat ? flatSettings : chain.EvaluateSettings(bone.LengthRate);
                bone.Settings = bone.BaseSettings;
                b.Bones[i] = bone;
            }
        }

        static KPBone NewBone(int parent, int transformIndex, int flags) => new KPBone
        {
            ParentIndex = parent,
            TransformIndex = transformIndex,
            Flags = flags,
            RealParentIndex = -1,
            RealChildIndex = -1,
            PoseRotation = quaternion.identity,
            PrevPoseRotation = quaternion.identity,
            CurrentPoseRotation = quaternion.identity,
            ForceMask = uint.MaxValue,
        };

        int AddBone(Builder b, KawaiiPhysicsChain chain, int t, int parent, HashSet<int> exclude, KPChainRuntime rt,
            List<Vector3> worldPos, float simScale)
        {
            var h = Hierarchy;
            if (exclude != null && exclude.Contains(t)) return -1;
            var self = b.Bones.Count;
            var bone = NewBone(parent, b.IndexOf(t), 0);
            if (parent >= 0)
            {
                // 父是細分假骨時用最後一段的長度（原作 CalcBoneLength）
                var p = b.Bones[parent];
                bone.BoneLength = (p.Flags & KPBoneFlags.InterBone) != 0 ? p.BoneLength : h.LocalPosition(t).magnitude;
            }
            b.Bones.Add(bone);
            rt.BoneNodes.Add(t);
            var selfPos = h.Position(t);
            worldPos.Add(selfPos);
            b.RestBones.Add(t);

            var childCount = 0;
            var n = h.ChildCount(t);
            for (int c = 0; c < n; c++)
            {
                var child = h.Child(t, c);
                if (h.IsCollider(child) || (exclude != null && exclude.Contains(child))) continue;
                var childPos = h.Position(child);
                var inserted = InsertInterBoneDummies(b, chain, self, childPos, (childPos - selfPos).magnitude / simScale,
                    rt, worldPos, out var effectiveParent, out var firstDummy);
                var childIndex = AddBone(b, chain, child, effectiveParent, exclude, rt, worldPos, simScale);
                if (childIndex >= 0)
                {
                    if (inserted.Count > 0) AddChild(b, inserted[inserted.Count - 1]);
                    FinalizeInterBoneDummies(b, inserted, childIndex);
                    childCount++;
                }
            }
            if (childCount == 0 && chain.dummyBoneLength > 0f)
            {
                // 末端假骨（實骨往前方軸延伸 DummyBoneLength）；之間也插細分假骨
                var tipWorld = selfPos + h.TransformDirection(t, AxisVector(chain.boneForwardAxis)) * chain.dummyBoneLength * simScale;
                var inserted = InsertInterBoneDummies(b, chain, self, tipWorld, chain.dummyBoneLength, rt, worldPos, out var effectiveParent, out _);
                var dummy = NewBone(effectiveParent, -1, KPBoneFlags.Dummy);
                dummy.RealParentIndex = self;
                dummy.BoneLength = inserted.Count > 0 ? chain.dummyBoneLength / (inserted.Count + 1) : chain.dummyBoneLength;
                var dummyIndex = b.Bones.Count;
                b.Bones.Add(dummy);
                rt.BoneNodes.Add(KPHierarchy.None);
                worldPos.Add(tipWorld);
                AddChild(b, effectiveParent);
                FinalizeInterBoneDummies(b, inserted, dummyIndex);
                childCount++;
            }
            // 實骨的子數：第一個細分假骨或子骨頭都算（原作 ChildIndices）。每個子各掛一個直接的子
            var selfBone = b.Bones[self];
            selfBone.ChildCount = childCount;
            b.Bones[self] = selfBone;
            return self;
        }

        static void AddChild(Builder b, int parent)
        {
            var p = b.Bones[parent];
            p.ChildCount++;
            b.Bones[parent] = p;
        }

        static Vector3 AxisVector(KawaiiPhysicsChain.Axis a) => (Vector3)KPMath.ForwardAxis((int)a);

        /// <summary>原作 InsertInterBoneDummyBonesCore：父與子之間插入細分假骨（依半徑加密時插更多，每段最多 50）。</summary>
        static List<int> InsertInterBoneDummies(Builder b, KawaiiPhysicsChain chain, int parentIndex, Vector3 childWorld, float distance,
            KPChainRuntime rt, List<Vector3> worldPos, out int effectiveParent, out int firstDummy)
        {
            effectiveParent = parentIndex;
            firstDummy = -1;
            if (chain.boneSubdivisionCount <= 0 && !chain.boneSubdivisionDensifyByRadius) return NoDummies;
            var inserted = new List<int>();
            var count = distance > KPMath.KindaSmallNumber ? Mathf.Clamp(chain.boneSubdivisionCount, 0, 10) : 0;
            if (chain.boneSubdivisionDensifyByRadius && distance > KPMath.KindaSmallNumber)
            {
                var minScale = 1f;
                if (chain.radiusCurve != null && chain.radiusCurve.length > 0)
                {
                    minScale = Mathf.Min(chain.radiusCurve.Evaluate(0f), chain.radiusCurve.Evaluate(1f));
                    foreach (var k in chain.radiusCurve.keys)
                        if (k.time >= 0f && k.time <= 1f) minScale = Mathf.Min(minScale, k.value);
                }
                var avgRadius = chain.radius * Mathf.Max(minScale, 0f);
                if (avgRadius > KPMath.KindaSmallNumber)
                    count = Mathf.Max(count, Mathf.Max(Mathf.CeilToInt(distance / (2f * avgRadius)) - 1, 0));
            }
            count = Mathf.Min(count, 50);
            if (count <= 0) return inserted;
            var parentWorld = worldPos[parentIndex - (b.Bones.Count - worldPos.Count)];
            for (int j = 0; j < count; j++)
            {
                var alpha = (float)(j + 1) / (count + 1);
                var d = NewBone(effectiveParent, -1, KPBoneFlags.InterBone);
                d.InterBoneAlpha = alpha;
                d.RealParentIndex = parentIndex;
                d.BoneLength = distance / (count + 1);
                var idx = b.Bones.Count;
                b.Bones.Add(d);
                rt.BoneNodes.Add(KPHierarchy.None);
                worldPos.Add(Vector3.Lerp(parentWorld, childWorld, alpha));
                AddChild(b, effectiveParent);
                effectiveParent = idx;
                inserted.Add(idx);
                if (firstDummy < 0) firstDummy = idx;
            }
            return inserted;
        }

        // 沒有插入細分假骨時共用的空清單（呼叫端只讀）
        static readonly List<int> NoDummies = new List<int>();

        static void FinalizeInterBoneDummies(Builder b, List<int> inserted, int childIndex)
        {
            foreach (var i in inserted)
            {
                var d = b.Bones[i];
                d.RealChildIndex = childIndex;
                b.Bones[i] = d;
            }
        }

        // ---- 骨間約束（InitBoneConstraints） ----

        void BuildConstraints(Builder b, KawaiiPhysicsChain chain, int boneStart, KPChainRuntime rt, List<Vector3> worldPos,
            int comp, float simScale)
        {
            int Find(int node) => node < 0 ? -1 : rt.BoneNodes.IndexOf(node);
            var pairs = new List<(int, int, bool, KPComplianceType, bool)>();
            foreach (var c in chain.boneConstraints)
                pairs.Add((Find(c.bone1 == null ? -1 : NodeOf(c.bone1)), Find(c.bone2 == null ? -1 : NodeOf(c.bone2)),
                    c.overrideCompliance, c.complianceType, c.excludeFromSubdivision));
            if (chain.boneConstraintsAsset != null)
            {
                foreach (var e in chain.boneConstraintsAsset.entries)
                {
                    var t1 = Hierarchy.FindDeep(comp, e.boneName1);
                    var t2 = Hierarchy.FindDeep(comp, e.boneName2);
                    pairs.Add((Find(t1), Find(t2), e.overrideCompliance, e.complianceType, e.excludeFromSubdivision));
                }
            }

            var globalCompliance = KPCompliance.Of(chain.boneConstraintGlobalComplianceType);
            var extra = new List<KPConstraint>();
            var excludeFlags = new List<bool>();
            foreach (var (i1, i2, overrideC, type, excludeSub) in pairs)
            {
                if (i1 < 0 || i2 < 0) continue;
                var compliance = overrideC ? KPCompliance.Of(type) : globalCompliance;
                b.Constraints.Add(MakeConstraint(boneStart + i1, boneStart + i2, worldPos[i1], worldPos[i2], compliance, simScale));
                excludeFlags.Add(excludeSub);
                if (!chain.autoAddChildDummyBoneConstraint) continue;

                // 末端假骨之間也加約束
                var d1 = TipDummyChild(b, boneStart, i1);
                var d2 = TipDummyChild(b, boneStart, i2);
                if (d1 >= 0 && d2 >= 0)
                {
                    extra.Add(MakeConstraint(boneStart + d1, boneStart + d2, worldPos[d1], worldPos[d2], compliance, simScale));
                    excludeFlags.Add(excludeSub);
                }
                // 細分假骨兩兩配對
                var s1 = InterBoneChain(b, boneStart, i1);
                var s2 = InterBoneChain(b, boneStart, i2);
                for (int k = 0; k < Mathf.Min(s1.Count, s2.Count); k++)
                {
                    extra.Add(MakeConstraint(boneStart + s1[k], boneStart + s2[k], worldPos[s1[k]], worldPos[s2[k]], compliance, simScale));
                    excludeFlags.Add(excludeSub);
                }
            }
            b.Constraints.AddRange(extra);
            rt.ConstraintExcludeFromSubdivision = excludeFlags;
        }

        static KPConstraint MakeConstraint(int a, int c, Vector3 pa, Vector3 pc, float compliance, float simScale) => new KPConstraint
        {
            Bone1 = a, Bone2 = c, Length = (pa - pc).magnitude / simScale, Compliance = compliance,
        };

        static int TipDummyChild(Builder b, int boneStart, int local)
        {
            for (int i = boneStart + local + 1; i < b.Bones.Count; i++)
            {
                var bone = b.Bones[i];
                if (bone.ParentIndex == boneStart + local && (bone.Flags & KPBoneFlags.Dummy) != 0) return i - boneStart;
            }
            return -1;
        }

        /// <summary>從某根實骨往下的細分假骨（連續的），最後接上末端假骨（原作 CollectInterBoneDummies）。</summary>
        static List<int> InterBoneChain(Builder b, int boneStart, int local)
        {
            var list = new List<int>();
            var current = boneStart + local;
            while (true)
            {
                var next = -1;
                for (int i = current + 1; i < b.Bones.Count; i++)
                {
                    if (b.Bones[i].ParentIndex != current) continue;
                    if ((b.Bones[i].Flags & KPBoneFlags.InterBone) != 0) { next = i; break; }
                }
                if (next < 0) break;
                list.Add(next - boneStart);
                current = next;
            }
            if (list.Count > 0)
            {
                var last = boneStart + list[list.Count - 1];
                for (int i = last + 1; i < b.Bones.Count; i++)
                    if (b.Bones[i].ParentIndex == last && (b.Bones[i].Flags & KPBoneFlags.Dummy) != 0) { list.Add(i - boneStart); break; }
            }
            return list;
        }

        /// <summary>原作 InsertBridgeDummiesForConstraints：每條橫向約束插入 N 個碰撞代理（放在骨頭陣列最後，沒有縱向的父）。</summary>
        static void InsertBridgeDummies(Builder b, KawaiiPhysicsChain chain, int boneStart, int constraintStart, int constraintCount,
            KPChainRuntime rt, List<Vector3> worldPos, float simScale)
        {
            var n = chain.boneConstraintSubdivisionCount;
            if (n <= 0) return;
            for (int c = 0; c < constraintCount; c++)
            {
                if (rt.ConstraintExcludeFromSubdivision != null && c < rt.ConstraintExcludeFromSubdivision.Count &&
                    rt.ConstraintExcludeFromSubdivision[c]) continue;
                var con = b.Constraints[constraintStart + c];
                var b1 = b.Bones[con.Bone1];
                var b2 = b.Bones[con.Bone2];
                var p1 = worldPos[con.Bone1 - boneStart];
                var p2 = worldPos[con.Bone2 - boneStart];
                var dist = (p2 - p1).magnitude / simScale;
                var r1 = Mathf.Max(b1.BaseSettings.Radius, 0f);
                var r2 = Mathf.Max(b2.BaseSettings.Radius, 0f);
                if (dist <= Mathf.Max(r1 + r2, KPMath.KindaSmallNumber)) continue;
                for (int k = 0; k < n; k++)
                {
                    var alpha = (float)(k + 1) / (n + 1);
                    var d = NewBone(-1, -1, KPBoneFlags.Bridge);
                    d.RealParentIndex = con.Bone1;
                    d.RealChildIndex = con.Bone2;
                    d.InterBoneAlpha = alpha;
                    d.BoneLength = dist / (n + 1);
                    d.LengthRate = 0.5f * (b1.LengthRate + b2.LengthRate);
                    d.BaseSettings = chain.EvaluateSettings(d.LengthRate);
                    d.Settings = d.BaseSettings;
                    b.Bones.Add(d);
                    rt.BoneNodes.Add(KPHierarchy.None);
                    worldPos.Add(Vector3.Lerp(p1, p2, alpha));
                }
            }
        }

        // ---- SyncBone（InitSyncBones） ----

        void BuildSyncBones(Builder b, KawaiiPhysicsChain chain, int boneStart, KPChainRuntime rt, int comp,
            Dictionary<int, Vector3> syncRestCache)
        {
            foreach (var s in chain.syncBones)
            {
                if (s == null || s.bone == null) continue;
                var syncNode = NodeOf(s.bone);
                if (!syncRestCache.TryGetValue(syncNode, out var restCS)) restCS = Hierarchy.ComponentSpacePosition(comp, syncNode);
                var maxDist = s.scaleCurveByDeltaDistance != null && s.scaleCurveByDeltaDistance.length > 0
                    ? s.scaleCurveByDeltaDistance[s.scaleCurveByDeltaDistance.length - 1].time : 0f;
                var sync = new KPSyncBone
                {
                    TransformIndex = b.IndexOf(syncNode),
                    TargetStart = b.SyncTargets.Count,
                    DeltaCurveLut = maxDist > 0f ? InternLut(s.scaleCurveByDeltaDistance, maxDist) : -1,
                    DeltaCurveMaxDistance = maxDist,
                    InitialPoseLocation = restCS,
                    GlobalScale = s.globalScale,
                    Directions = new int3((int)s.applyDirectionX, (int)s.applyDirectionY, (int)s.applyDirectionZ),
                    EnableAttenuation = s.enableDistanceAttenuation ? 1 : 0,
                    AttenuationInner = s.attenuationInnerRadius,
                    AttenuationOuter = s.attenuationOuterRadius,
                    MaxAttenuation = s.maxAttenuationRate,
                };
                foreach (var root in s.targetRoots)
                {
                    if (root == null || root.bone == null) continue;
                    var local = rt.BoneNodes.IndexOf(NodeOf(root.bone));
                    if (local < 0) continue;
                    // 目標根自己：比例 0
                    b.SyncTargets.Add(new KPSyncTarget { BoneIndex = boneStart + local, Scale = EvalCurve(root.scaleCurveByBoneLengthRate, 0f) });
                    if (!root.includeChildBones) continue;
                    // 子孫（細分假骨與細分過的末端假骨除外），比例＝離目標根的長度÷最大長度
                    var children = new List<(int index, float length)>();
                    CollectSyncChildren(b, boneStart + local, 0f, children);
                    var maxLen = 0f;
                    foreach (var c in children) maxLen = Mathf.Max(maxLen, c.length);
                    foreach (var c in children)
                    {
                        var rate = maxLen > KPMath.KindaSmallNumber ? c.length / maxLen : 0f;
                        b.SyncTargets.Add(new KPSyncTarget { BoneIndex = c.index, Scale = EvalCurve(root.scaleCurveByBoneLengthRate, rate) });
                    }
                }
                sync.TargetCount = b.SyncTargets.Count - sync.TargetStart;
                b.SyncBones.Add(sync);
            }
        }

        static void CollectSyncChildren(Builder b, int index, float lengthSoFar, List<(int, float)> output)
        {
            for (int i = index + 1; i < b.Bones.Count; i++)
            {
                var c = b.Bones[i];
                if (c.ParentIndex != index) continue;
                var len = lengthSoFar + c.BoneLength;
                // 原作 IsExcludedFromSyncBoneChildTarget：細分假骨、細分過的末端假骨
                var excluded = (c.Flags & KPBoneFlags.InterBone) != 0 ||
                               ((c.Flags & KPBoneFlags.Dummy) != 0 && (b.Bones[c.ParentIndex].Flags & KPBoneFlags.InterBone) != 0);
                if (!excluded) output.Add((i, len));
                CollectSyncChildren(b, i, len, output);
            }
        }

        static float EvalCurve(AnimationCurve c, float t) => c != null && c.length > 0 ? c.Evaluate(t) : 1f;

        // ---- 碰撞來源 ----

        void AddColliders(Builder b, List<KawaiiPhysicsCollider> colliders)
        {
            foreach (var col in colliders)
            {
                if (col == null) continue;
                AddShape(b, col.shape, b.IndexOf(NodeOf(col.transform)), col.center, col.shape == KawaiiPhysicsCollider.Shape.Capsule ||
                    col.shape == KawaiiPhysicsCollider.Shape.TaperedCapsule ? col.AxisRotation : Quaternion.identity,
                    col.radius, col.radius1, col.length, col.extent, col.inner, col.convexMesh);
            }
        }

        void AddLimitsAsset(Builder b, KawaiiPhysicsLimitsAsset asset, int comp)
        {
            if (asset == null) return;
            foreach (var e in asset.Resolve())
            {
                var t = Hierarchy.FindDeep(comp, e.boneName);
                if (t < 0) continue;
                var axisRot = e.direction == KawaiiPhysicsCollider.Axis.X ? Quaternion.Euler(0f, 0f, -90f)
                    : e.direction == KawaiiPhysicsCollider.Axis.Z ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
                var rot = Quaternion.Euler(e.rotation);
                if (e.shape == KawaiiPhysicsCollider.Shape.Capsule || e.shape == KawaiiPhysicsCollider.Shape.TaperedCapsule) rot *= axisRot;
                AddShape(b, e.shape, b.IndexOf(t), e.offset, rot, e.radius, e.radius1, e.length, e.extent, e.inner, e.convexMesh);
            }
        }

        static void AddShape(Builder b, KawaiiPhysicsCollider.Shape shape, int driving, Vector3 offset, Quaternion offsetRot,
            float radius, float radius1, float length, Vector3 extent, bool inner, Mesh convexMesh)
        {
            switch (shape)
            {
                case KawaiiPhysicsCollider.Shape.Sphere:
                    b.Spheres.Add(new KPSphere { Radius = radius, WorldRadius = radius, Inner = inner ? 1 : 0, DrivingIndex = driving, Offset = offset });
                    break;
                case KawaiiPhysicsCollider.Shape.Capsule:
                    b.Capsules.Add(new KPCapsule { Radius = radius, Length = length, WorldRadius = radius, WorldLength = length, DrivingIndex = driving, Offset = offset, OffsetRotation = offsetRot,
                        Rotation = quaternion.identity, WorldRotation = quaternion.identity });
                    break;
                case KawaiiPhysicsCollider.Shape.TaperedCapsule:
                    b.Tapered.Add(new KPTaperedCapsule { Radius0 = radius, Radius1 = radius1, Length = length, WorldRadius0 = radius, WorldRadius1 = radius1, WorldLength = length, DrivingIndex = driving, Offset = offset,
                        OffsetRotation = offsetRot, Rotation = quaternion.identity, WorldRotation = quaternion.identity });
                    break;
                case KawaiiPhysicsCollider.Shape.Box:
                    b.Boxes.Add(new KPBox { Extent = extent, WorldExtent = extent, DrivingIndex = driving, Offset = offset, OffsetRotation = offsetRot,
                        Rotation = quaternion.identity, WorldRotation = quaternion.identity });
                    break;
                case KawaiiPhysicsCollider.Shape.Plane:
                    b.Planes.Add(new KPPlane { DrivingIndex = driving, Offset = offset, OffsetRotation = offsetRot,
                        Rotation = quaternion.identity, WorldRotation = quaternion.identity });
                    break;
                case KawaiiPhysicsCollider.Shape.Convex:
                {
                    var planes = KawaiiPhysicsCollider.ConvexPlanesOf(convexMesh, out var bounds);
                    if (planes.Count == 0) break;
                    b.Convexes.Add(new KPConvex
                    {
                        DrivingIndex = driving, Offset = offset, OffsetRotation = offsetRot, Rotation = quaternion.identity,
                        WorldRotation = quaternion.identity, LocalBoundsMin = bounds.min, LocalBoundsMax = bounds.max,
                        PlaneStart = b.ConvexPlanes.Count, PlaneCount = planes.Count,
                    });
                    b.ConvexPlanes.AddRange(planes);
                    break;
                }
            }
        }

        public void Dispose()
        {
            Dispose(ref Instances); Dispose(ref Bones); Dispose(ref Spheres); Dispose(ref Capsules); Dispose(ref Tapered); Dispose(ref Boxes);
            Dispose(ref Planes); Dispose(ref Convexes); Dispose(ref ConvexPlanes); Dispose(ref Constraints); Dispose(ref SyncBones);
            Dispose(ref SyncTargets); Dispose(ref Forces); Dispose(ref Luts); Dispose(ref WindZones); Dispose(ref Reads); Dispose(ref Writes);
            Dispose(ref Rest); Dispose(ref SweepCommands); Dispose(ref SweepHits);
            if (Transforms.isCreated) Transforms.Dispose();
            Segments.Clear();
            Runtimes.Clear();
        }
    }
}
