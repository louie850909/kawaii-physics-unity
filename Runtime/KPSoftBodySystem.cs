// 軟組織的系統：一組 KPRigSoftBody 打包成一組 NativeArray（KPSoftWorld）。骨頭以節點 id 表示，透過 KPHierarchy 看骨架。
// KPSoftCore 是共用的部分（建置、每幀的設定、解算的排程）；場景上的 KawaiiPhysicsSoftBody 用 KPSoftHost（Transform 版，
// 由 KPSystem 的兩個 PlayerLoop 階段驅動：Update 前還原靜止姿勢；所有 LateUpdate 之後在 Chain 之前 讀 → 解 → 寫），
// 陣列接口（KPRig）有自己的衍生類別。
// 元件數與骨頭數都少（一個角色幾根），加入、移除、結構變更時整批重建，模擬狀態依骨頭沿用（其他元件不受影響）。
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

namespace KawaiiPhysicsUnity
{
    /// <summary>建置時的記錄。骨頭一律用節點找（清單改過、還沒重建時索引會對不上）。</summary>
    internal sealed class KPSoftBodyRuntime
    {
        public int BodyIndex = -1;
        public int Root = KPHierarchy.None;
        /// <summary>建置時的 bones 清單（改了沒有，跟這個比）。</summary>
        public int[] Bones = Array.Empty<int>();
        /// <summary>建置這個元件的主機（GetOffset 等查詢用）。</summary>
        public KPSoftCore Host;

        public bool Matches(KPRigSoftBody body, int root)
        {
            if (Root != root || Bones.Length != body.bones.Count) return false;
            for (int i = 0; i < Bones.Length; i++)
                if (Bones[i] != body.bones[i]) return false;
            return true;
        }
    }

    /// <summary>靜止姿勢（區域）與重力參考（父骨相對角色根的軸向，從世界矩陣取）。</summary>
    internal struct KPSoftRest
    {
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Vector3 LocalScale;
        public Unity.Mathematics.float3x3 ParentInRoot;
        /// <summary>重力參考有沒有記到（父骨或角色根的矩陣退化時，例如縮放 0，等第一次能用的幀再記）。</summary>
        public bool ParentValid;

        public static KPSoftRest Capture(KPHierarchy h, int node, int root)
        {
            var p = h.LocalPosition(node);
            var r = h.LocalRotation(node);
            var parent = h.Parent(node);
            var parentMatrix = parent >= 0 ? (Unity.Mathematics.float4x4)h.LocalToWorld(parent) : Unity.Mathematics.float4x4.identity;
            var rootMatrix = root >= 0 && h.IsAlive(root) ? (Unity.Mathematics.float4x4)h.LocalToWorld(root) : Unity.Mathematics.float4x4.identity;
            var valid = !KPSoftBodySolver.Degenerate(new Unity.Mathematics.float3x3(parentMatrix)) &&
                        !KPSoftBodySolver.Degenerate(new Unity.Mathematics.float3x3(rootMatrix));
            return new KPSoftRest
            {
                LocalPosition = p,
                LocalRotation = r,
                LocalScale = h.LocalScale(node),
                ParentValid = valid,
                ParentInRoot = valid
                    ? Unity.Mathematics.math.mul(Unity.Mathematics.math.transpose(KPSoftBodySolver.Frame(rootMatrix)), KPSoftBodySolver.Frame(parentMatrix))
                    : Unity.Mathematics.float3x3.identity,
            };
        }

        public KPSoftLocal ToLocal(int mode) => new KPSoftLocal
        {
            LocalPosition = LocalPosition, LocalRotation = LocalRotation, LocalScale = LocalScale, Mode = mode,
        };
    }

    /// <summary>
    /// 一次建置的結果。讀寫陣列依序是各元件的骨頭與角色根（同一個節點只放一次）；Transform 版另有對應的 TransformAccessArray。
    /// </summary>
    internal sealed class KPSoftWorld : IDisposable
    {
        public NativeArray<KPSoftBodyData> Bodies;
        public NativeArray<KPSoftBone> Bones;
        public NativeArray<KPSoftRead> Reads;
        public NativeArray<KPSoftLocal> Writes;
        public NativeArray<KPSoftLocal> Rest;
        public NativeArray<KPSoftCollider> Colliders;
        public NativeArray<KPSoftGroup> Groups;
        public NativeArray<int> GroupBodies;
        public NativeArray<int> GroupBones;
        public TransformAccessArray Transforms;
        public readonly List<KPRigSoftBody> Owners = new List<KPRigSoftBody>();
        /// <summary>讀寫陣列的每一格 → 節點。</summary>
        public readonly List<int> NodeList = new List<int>();
        /// <summary>骨頭的節點 → 世界的骨頭索引（重建時沿用狀態用）。</summary>
        public readonly Dictionary<int, int> BoneLookup = new Dictionary<int, int>();
        /// <summary>每個元件建置時的角色根（跟 Owners 對齊）。</summary>
        public readonly List<int> Roots = new List<int>();
        /// <summary>元件 → Owners 的索引（重建時找舊狀態用）。</summary>
        public readonly Dictionary<KPRigSoftBody, int> OwnerLookup = new Dictionary<KPRigSoftBody, int>();

        public int BodyCount => Owners.Count;
        public int BoneCount => Bones.IsCreated ? Bones.Length : 0;

        public void EnsureColliderCapacity(int n)
        {
            if (Colliders.IsCreated && Colliders.Length >= n) return;
            var size = Mathf.Max(16, Colliders.IsCreated ? Colliders.Length : 0);
            while (size < n) size *= 2;
            if (Colliders.IsCreated) Colliders.Dispose();
            Colliders = new NativeArray<KPSoftCollider>(size, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (Bodies.IsCreated) Bodies.Dispose();
            if (Bones.IsCreated) Bones.Dispose();
            if (Reads.IsCreated) Reads.Dispose();
            if (Writes.IsCreated) Writes.Dispose();
            if (Rest.IsCreated) Rest.Dispose();
            if (Colliders.IsCreated) Colliders.Dispose();
            if (Groups.IsCreated) Groups.Dispose();
            if (GroupBodies.IsCreated) GroupBodies.Dispose();
            if (GroupBones.IsCreated) GroupBones.Dispose();
            if (Transforms.isCreated) Transforms.Dispose();
        }
    }

    /// <summary>軟組織的碰撞體換算（KawaiiPhysicsSoftBody 在主執行緒、KPRig 在工作裡，共用這一段）。</summary>
    internal static class KPSoftColliders
    {
        /// <summary>
        /// 碰撞體 → 這一幀的世界空間形狀。center＝中心的世界位置；capsuleA／B＝膠囊兩端球心的世界位置（膠囊以外不用）；
        /// rotation、lossyScale＝碰撞體所在骨頭的世界旋轉與縮放。凸包不支援（回傳 false）。
        /// </summary>
        public static bool Make(KawaiiPhysicsCollider.Shape shape, Vector3 center, Vector3 capsuleA, Vector3 capsuleB, Quaternion rotation,
            Vector3 lossyScale, float radius, float radius1, Vector3 extent, bool inner, out KPSoftCollider result)
        {
            var s = lossyScale;
            var maxScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            result = new KPSoftCollider { A = center, Rotation = rotation };
            switch (shape)
            {
                case KawaiiPhysicsCollider.Shape.Sphere:
                    result.Shape = KPSoftCollider.Sphere;
                    result.RadiusA = radius * maxScale;
                    result.Inner = inner ? 1 : 0;
                    return true;
                case KawaiiPhysicsCollider.Shape.Capsule:
                case KawaiiPhysicsCollider.Shape.TaperedCapsule:
                    result.Shape = KPSoftCollider.Capsule;
                    result.A = capsuleA;
                    result.B = capsuleB;
                    result.RadiusA = radius * maxScale;
                    result.RadiusB = (shape == KawaiiPhysicsCollider.Shape.TaperedCapsule ? radius1 : radius) * maxScale;
                    return true;
                case KawaiiPhysicsCollider.Shape.Box:
                    result.Shape = KPSoftCollider.Box;
                    result.Extent = Vector3.Scale(extent, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
                    return true;
                case KawaiiPhysicsCollider.Shape.Plane:
                    result.Shape = KPSoftCollider.Plane;
                    result.B = (rotation * Vector3.up).normalized;
                    return true;
                default:
                    return false; // 凸包：軟組織不支援
            }
        }

        /// <summary>膠囊兩端球心相對中心的區域偏移（axisRotation × up × 長度的一半）。</summary>
        public static Vector3 CapsuleAxis(KawaiiPhysicsCollider.Axis direction, float length) =>
            KPRigCollider.AxisRotationOf(direction) * Vector3.up * (length * 0.5f);
    }

    /// <summary>軟組織主機的共用部分：元件清單、靜止姿勢的快取、建置、每幀的設定、解算的排程。</summary>
    internal abstract class KPSoftCore
    {
        protected readonly KPHierarchy hierarchy;
        protected readonly List<KPRigSoftBody> bodies = new List<KPRigSoftBody>();
        /// <summary>
        /// 靜止姿勢：啟用時（動畫還沒動）記下來；骨頭離開模擬時（還原之後）刪掉，下次啟用重新記錄
        ///（應用端在停用期間改了骨頭的靜止姿勢也會反映）。
        /// </summary>
        protected readonly Dictionary<int, KPSoftRest> restCache = new Dictionary<int, KPSoftRest>();
        protected KPSoftWorld world;
        protected bool membershipChanged;

        protected KPSoftCore(KPHierarchy hierarchy) => this.hierarchy = hierarchy;

        internal KPSoftWorld World => world;
        /// <summary>重建次數（測試用）。</summary>
        internal int SyncCount { get; private set; }
        internal int RestCacheCount => restCache.Count;
        public int BodyCount => world?.BodyCount ?? 0;
        public int BoneCount => world?.BoneCount ?? 0;

        /// <summary>body 的角色根的節點。</summary>
        protected int RootOf(KPRigSoftBody body) => hierarchy.ResolveComponent(body.referenceRoot);

        /// <summary>主執行緒要讀世界的骨頭前（陣列版：等排程的工作完成）。</summary>
        protected virtual void EnsureReadable() { }

        /// <summary>這個節點是不是這個元件在模擬（用節點找，不用清單索引）；是的話給世界的骨頭索引。</summary>
        internal bool TryGetOwnBone(KPRigSoftBody body, int node, out int wi)
        {
            wi = -1;
            var rt = body.runtime;
            if (world == null || rt == null || node < 0 || rt.BodyIndex < 0 || rt.BodyIndex >= world.BodyCount || world.Owners[rt.BodyIndex] != body) return false;
            return world.BoneLookup.TryGetValue(node, out wi) && world.Bones[wi].Body == rt.BodyIndex;
        }

        /// <summary>bones[boneIndex] 的世界骨頭索引（清單裡重複的只有第一次算）；沒在模擬是 -1。</summary>
        internal int WorldBone(KPRigSoftBody body, int boneIndex)
        {
            if (boneIndex < 0 || boneIndex >= body.bones.Count) return -1;
            var node = body.bones[boneIndex];
            if (body.bones.IndexOf(node) != boneIndex) return -1;
            EnsureReadable();
            return TryGetOwnBone(body, node, out var wi) ? wi : -1;
        }

        internal Vector3 Offset(KPRigSoftBody body, int boneIndex)
        {
            var wi = WorldBone(body, boneIndex);
            return wi >= 0 ? (Vector3)world.Bones[wi].Offset : Vector3.zero;
        }

        internal Vector3 Velocity(KPRigSoftBody body, int boneIndex)
        {
            var wi = WorldBone(body, boneIndex);
            return wi >= 0 ? (Vector3)world.Bones[wi].Velocity : Vector3.zero;
        }

        /// <summary>加入（設定的骨頭與角色根要是最新的）。骨頭的靜止姿勢在這時記下來。</summary>
        protected void AddBody(KPRigSoftBody body)
        {
            if (!bodies.Contains(body))
            {
                bodies.Add(body);
                body.ClearPendingImpulses();
            }
            var root = RootOf(body);
            foreach (var node in body.bones)
                if (node >= 0 && !restCache.ContainsKey(node)) restCache.Add(node, KPSoftRest.Capture(hierarchy, node, root));
            membershipChanged = true;
        }

        protected bool RemoveBody(KPRigSoftBody body)
        {
            if (!bodies.Remove(body)) return false;
            membershipChanged = true;
            // 停用再啟用一律重新開始（同一幀內再啟用也一樣，不沿用停用前的狀態）
            body.resetOnRebuild = true;
            return true;
        }

        /// <summary>現在的姿勢重新記成靜止姿勢與重力參考（設定的骨頭要是最新的）。</summary>
        protected void CaptureRestPoseOf(KPRigSoftBody body)
        {
            EnsureReadable();
            var root = RootOf(body);
            foreach (var node in body.bones)
            {
                if (node < 0) continue;
                var mine = TryGetOwnBone(body, node, out var wi);
                // 別的元件正在模擬、而且清單裡還有的骨頭不動（靜止姿勢歸那個元件管）。
                // 那個元件的清單已經拿掉它（搬到這個元件、還沒重建）時照樣記錄，下次重建採用
                if (!mine && world != null && world.BoneLookup.TryGetValue(node, out var owi))
                {
                    var owner = world.Owners[world.Bones[owi].Body];
                    if (owner != null && owner != body && !owner.OwnerDestroyed && bodies.Contains(owner) && owner.bones.Contains(node)) continue;
                }
                var rest = KPSoftRest.Capture(hierarchy, node, root);
                restCache[node] = rest;
                if (!mine) continue;
                var bone = world.Bones[wi];
                bone.RestParentInRoot = rest.ParentInRoot;
                bone.RestPending = rest.ParentValid ? 0 : 1;
                world.Bones[wi] = bone;
                world.Rest[bone.TransformIndex] = rest.ToLocal(1);
            }
        }

        /// <summary>有沒有要重建（validateRequested 時 body 的骨頭與角色根要先更新）。</summary>
        protected bool NeedsSync()
        {
            var needsSync = membershipChanged;
            foreach (var b in bodies)
            {
                if (b.OwnerDestroyed)
                {
                    needsSync = true;
                    continue;
                }
                if (b.validateRequested)
                {
                    b.validateRequested = false;
                    if (b.runtime == null || !b.runtime.Matches(b, RootOf(b))) b.dirty = true;
                }
                // 角色根被刪掉：重建（元件版的 ResolveRoot 退回上層的 Animator 或自己）
                var root = b.runtime?.Root ?? KPHierarchy.None;
                if (root >= 0 && !hierarchy.IsAlive(root)) b.dirty = true;
                needsSync |= b.dirty;
            }
            return needsSync;
        }

        protected JobHandle ScheduleSolve(float deltaTime, JobHandle dependsOn)
        {
            var job = new KPSoftSolveJob
            {
                Groups = world.Groups,
                GroupBodies = world.GroupBodies,
                GroupBones = world.GroupBones,
                Bodies = world.Bodies,
                Bones = world.Bones,
                Reads = world.Reads,
                Writes = world.Writes,
                Colliders = world.Colliders,
                DeltaTime = deltaTime,
            };
            switch (KPSystem.Backend)
            {
                case KPBackend.BurstParallel:
                    return job.Schedule(world.Groups.Length, 1, dependsOn);
                case KPBackend.BurstSingleThread:
                    dependsOn.Complete();
                    job.Run(world.Groups.Length);
                    return default;
                default:
                    dependsOn.Complete();
                    for (int i = 0; i < world.Groups.Length; i++)
                        KPSoftBodySolver.SolveGroup(i, world.Groups, world.GroupBodies, world.GroupBones, world.Bodies, world.Bones, world.Reads,
                            world.Writes, world.Colliders, deltaTime);
                    return default;
            }
        }

        internal static void CopySettings(KPRigSoftBody body, ref KPSoftBodyData data)
        {
            data.Enabled = body.enabled ? 1 : 0;
            data.Alpha = Mathf.Clamp01(body.alpha);
            data.Omega = 2f * Mathf.PI * Mathf.Clamp(body.frequency, 0.01f, 1000f);
            data.DampingRatio = Mathf.Max(0f, body.dampingRatio);
            data.Gravity = body.gravity;
            data.GravityRelativeToRest = body.gravityRelativeToRestPose ? 1 : 0;
            data.ExternalAcceleration = body.externalAcceleration;
            data.AnimationInertia = Mathf.Max(0f, body.animationInertia);
            data.MovementInertia = Mathf.Max(0f, body.movementInertia);
            data.LimitMin = Vector3.Min(body.limitMin, Vector3.zero);
            data.LimitMax = Vector3.Max(body.limitMax, Vector3.zero);
            data.CollisionPriority = body.collisionPriority ? 1 : 0;
            data.Tilt = Mathf.Clamp01(body.tilt);
            data.StretchAxis = (int)body.stretchAxis;
            data.StretchAmount = body.stretchAmount;
            data.VolumePreservation = Mathf.Clamp01(body.volumePreservation);
            data.Radius = Mathf.Max(0f, body.radius);
            data.TargetFramerate = Mathf.Max(1, body.targetFramerate);
            data.MaxSubsteps = Mathf.Max(1, body.maxSubsteps);
            data.TeleportDistance = Mathf.Max(0f, body.teleportDistanceThreshold);
            data.TeleportAngle = Mathf.Max(0f, body.teleportRotationThreshold) * Mathf.Deg2Rad;
        }

        /// <summary>
        /// 主執行緒累積的衝量搬到骨頭上。positions＝true 時撞擊點衝量也在這裡算（位置用這一刻骨頭的位置＝動畫後的姿勢）；
        /// false 時留給呼叫端（陣列版在工作裡算）。
        /// </summary>
        protected void TransferImpulses(KPRigSoftBody body, bool positions)
        {
            var uniform = body.pendingUniformImpulse;
            var perBone = body.pendingImpulses;
            var points = body.pendingPointImpulses;
            body.pendingUniformImpulse = Vector3.zero;
            var usePoints = positions && points.Count > 0;
            if (uniform == Vector3.zero && perBone == null && !usePoints) return;
            for (int i = 0; i < body.bones.Count; i++)
            {
                var node = body.bones[i];
                // 清單裡重複的骨頭只算第一次（建置時重複的也略過）
                if (!TryGetOwnBone(body, node, out var wi) || body.bones.IndexOf(node) != i) continue;
                var add = uniform;
                if (perBone != null && i < perBone.Length) add += perBone[i];
                if (usePoints)
                {
                    var pos = hierarchy.Position(node);
                    foreach (var p in points) add += KPRigSoftBody.PointImpulseAt(pos, p.Position, p.VelocityChange, p.Radius);
                }
                if (add == Vector3.zero) continue;
                var bone = world.Bones[wi];
                bone.PendingImpulse += (Unity.Mathematics.float3)add;
                world.Bones[wi] = bone;
            }
            if (perBone != null) Array.Clear(perBone, 0, perBone.Length);
            if (positions) points.Clear();
        }

        // ===================================================================
        // 建置與清理
        // ===================================================================

        static readonly List<(int node, int listIndex, int depth)> entries = new List<(int, int, int)>();
        static readonly HashSet<int> claimed = new HashSet<int>();
        static readonly Dictionary<int, int> nodeIndex = new Dictionary<int, int>();
        static readonly List<int> staleKeys = new List<int>();

        /// <summary>TransformAccessArray（Transform 版才有）。</summary>
        protected virtual void CreateTransformAccess(KPSoftWorld next) { }

        protected void Sync()
        {
            membershipChanged = false;
            SyncCount++;
            for (int i = bodies.Count - 1; i >= 0; i--)
                if (bodies[i] == null || bodies[i].OwnerDestroyed) bodies.RemoveAt(i);

            var old = world;
            var next = new KPSoftWorld();
            var boneList = new List<KPSoftBone>();
            var boneDepth = new List<int>();
            var keptFrom_ = new List<int>();
            var bodyList = new List<KPSoftBodyData>();
            var restList = new List<KPSoftLocal>();
            nodeIndex.Clear();

            int AddNode(int node, KPSoftLocal rest)
            {
                if (nodeIndex.TryGetValue(node, out var idx))
                {
                    if (rest.Mode != 0) restList[idx] = rest; // 先當角色根加入、後來才是骨頭
                    return idx;
                }
                idx = next.NodeList.Count;
                next.NodeList.Add(node);
                restList.Add(rest);
                nodeIndex.Add(node, idx);
                return idx;
            }

            foreach (var body in bodies)
            {
                var root = RootOf(body);
                var rt = body.runtime ??= new KPSoftBodyRuntime();
                rt.Host = this;
                var bodyIndex = bodyList.Count;
                entries.Clear();
                claimed.Clear();
                for (int i = 0; i < body.bones.Count; i++)
                {
                    var node = body.bones[i];
                    if (node < 0) continue;
                    if (next.BoneLookup.ContainsKey(node) || !claimed.Add(node))
                    {
                        Debug.LogWarning($"KawaiiPhysicsSoftBody「{body.Label}」：骨頭 {hierarchy.Name(node)} 已經由其他軟組織元件模擬，略過。", body.component);
                        continue;
                    }
                    var depth = 0;
                    for (var p = hierarchy.Parent(node); p >= 0; p = hierarchy.Parent(p)) depth++;
                    entries.Add((node, i, depth));
                }
                // 元件內祖先排在前面（同深度保持清單順序）
                entries.Sort((a, b) => a.depth != b.depth ? a.depth.CompareTo(b.depth) : a.listIndex.CompareTo(b.listIndex));

                var start = boneList.Count;
                var oldBody = old != null && old.OwnerLookup.TryGetValue(body, out var ob0) ? ob0 : -1;
                // 同一個元件、同一個角色根、中間沒有停用過：模擬狀態沿用（其他元件加入或移除時不重置）
                var keep = oldBody >= 0 && old.Roots[oldBody] == root && !body.resetOnRebuild;
                body.resetOnRebuild = false;
                for (int k = 0; k < entries.Count; k++)
                {
                    var node = entries[k].node;
                    if (!restCache.TryGetValue(node, out var rest))
                    {
                        rest = KPSoftRest.Capture(hierarchy, node, root);
                        restCache.Add(node, rest);
                    }
                    var bone = new KPSoftBone
                    {
                        TransformIndex = AddNode(node, rest.ToLocal(1)),
                        Body = bodyIndex,
                        ParentSoft = -1,
                        RestParentInRoot = rest.ParentInRoot,
                        RestPending = rest.ParentValid ? 0 : 1,
                        Delta = Unity.Mathematics.float4x4.identity,
                        DeltaInverse = Unity.Mathematics.float4x4.identity,
                    };
                    var keptFrom = -1;
                    if (keep && old.BoneLookup.TryGetValue(node, out var ob) && old.Bones[ob].Body == oldBody)
                    {
                        var prev = old.Bones[ob];
                        bone.Initialized = prev.Initialized;
                        bone.Offset = prev.Offset;
                        bone.Velocity = prev.Velocity;
                        bone.PrevBase = prev.PrevBase;
                        bone.PrevBaseInRoot = prev.PrevBaseInRoot;
                        bone.PrevBaseFrameInRoot = prev.PrevBaseFrameInRoot;
                        bone.JiggleStage = prev.JiggleStage;
                        bone.Delta = prev.Delta;
                        bone.DeltaInverse = prev.DeltaInverse;
                        bone.PrevDelta = prev.PrevDelta;
                        bone.PrevDeltaInverse = prev.PrevDeltaInverse;
                        // 第一次能用的幀才記的重力參考也沿用（靜止姿勢的記錄裡是單位矩陣）
                        bone.RestParentInRoot = prev.RestParentInRoot;
                        bone.RestPending = prev.RestPending;
                        keptFrom = ob;
                        bone.PrevJiggle = prev.PrevJiggle;
                        bone.PrevMoveVelocity = prev.PrevMoveVelocity;
                        bone.PrevAnimVelocity = prev.PrevAnimVelocity;
                        bone.PrevJiggleVelocity = prev.PrevJiggleVelocity;
                        bone.PendingImpulse = prev.PendingImpulse;
                    }
                    // 沒沿用狀態、但上一幀還在模擬：第一次解算當成重置（子孫重新取基準），不當成新加入
                    if (keptFrom < 0 && old != null && old.BoneLookup.ContainsKey(node)) bone.WasRunning = 1;
                    next.BoneLookup.Add(node, start + k);
                    boneList.Add(bone);
                    boneDepth.Add(entries[k].depth);
                    keptFrom_.Add(keptFrom);
                }

                var data = new KPSoftBodyData
                {
                    BoneStart = start,
                    BoneCount = entries.Count,
                    RootIndex = AddNode(root, default),
                };
                if (keep)
                {
                    var prev = old.Bodies[oldBody];
                    data.PrevRootPosition = prev.PrevRootPosition;
                    data.PrevRootFrame = prev.PrevRootFrame;
                    data.RootInitialized = prev.RootInitialized;
                    data.ResetRequested = prev.ResetRequested;
                    data.LastDeltaTime = prev.LastDeltaTime;
                    data.PrevAlpha = prev.PrevAlpha;
                    data.PrevTilt = prev.PrevTilt;
                    data.PrevStretchAxis = prev.PrevStretchAxis;
                    data.PrevStretchAmount = prev.PrevStretchAmount;
                    data.PrevVolumePreservation = prev.PrevVolumePreservation;
                    data.PrevLimitMin = prev.PrevLimitMin;
                    data.PrevLimitMax = prev.PrevLimitMax;
                }
                rt.BodyIndex = bodyIndex;
                rt.Root = root;
                rt.Bones = body.bones.ToArray();
                bodyList.Add(data);
                next.OwnerLookup.Add(body, next.Owners.Count);
                next.Owners.Add(body);
                next.Roots.Add(root);
                body.dirty = false;
            }

            // 最近的祖先軟組織骨頭（元件不同也算）；有祖先關係的元件併成一個群組（並查集）
            var groupOf = new int[bodyList.Count];
            for (int i = 0; i < groupOf.Length; i++) groupOf[i] = i;
            int Find(int x)
            {
                while (groupOf[x] != x) x = groupOf[x] = groupOf[groupOf[x]];
                return x;
            }
            for (int i = 0; i < boneList.Count; i++)
            {
                var node = next.NodeList[boneList[i].TransformIndex];
                for (var p = hierarchy.Parent(node); p >= 0; p = hierarchy.Parent(p))
                {
                    if (!next.BoneLookup.TryGetValue(p, out var pi)) continue;
                    var bone = boneList[i];
                    bone.ParentSoft = pi;
                    boneList[i] = bone;
                    groupOf[Find(bone.Body)] = Find(boneList[pi].Body);
                    break;
                }
            }
            // 沿用狀態的骨頭，祖先鏈換了（祖先加入、離開，不論哪一層）：用祖先各自上兩幀的變化，
            // 在新的祖先鏈下重算祖先晃動的位置與速度（不把換鏈的跳動當成加速，也不丟掉祖先真的晃動）
            for (int i = 0; i < boneList.Count; i++)
                if (keptFrom_[i] >= 0) RebaseJiggle(old, next, boneList, i, keptFrom_[i]);
            // 群組編號 → 連續的索引；元件與骨頭依群組排好（計數排序），群組裡的骨頭再依階層深度排序
            var groupId = new int[bodyList.Count];
            var groupCount = 0;
            for (int i = 0; i < bodyList.Count; i++)
                groupId[i] = Find(i) == i ? groupCount++ : -1;
            for (int i = 0; i < bodyList.Count; i++) groupId[i] = groupId[Find(i)];
            var groups = new KPSoftGroup[groupCount];
            for (int i = 0; i < bodyList.Count; i++)
            {
                groups[groupId[i]].BodyCount++;
                groups[groupId[i]].BoneCount += bodyList[i].BoneCount;
            }
            for (int gi = 1; gi < groupCount; gi++)
            {
                groups[gi].BodyStart = groups[gi - 1].BodyStart + groups[gi - 1].BodyCount;
                groups[gi].BoneStart = groups[gi - 1].BoneStart + groups[gi - 1].BoneCount;
            }
            var groupBodies = new int[bodyList.Count];
            var groupBones = new int[boneList.Count];
            var sortKeys = new long[boneList.Count];
            var bodyFill = new int[groupCount];
            var boneFill = new int[groupCount];
            for (int i = 0; i < bodyList.Count; i++)
            {
                var gi = groupId[i];
                groupBodies[groups[gi].BodyStart + bodyFill[gi]++] = i;
                for (int k = bodyList[i].BoneStart; k < bodyList[i].BoneStart + bodyList[i].BoneCount; k++)
                {
                    var at = groups[gi].BoneStart + boneFill[gi]++;
                    groupBones[at] = k;
                    sortKeys[at] = (long)boneDepth[k] * boneList.Count + k; // 祖先一定先解（同深度依索引）
                }
            }
            for (int gi = 0; gi < groupCount; gi++)
                if (groups[gi].BoneCount > 1) Array.Sort(sortKeys, groupBones, groups[gi].BoneStart, groups[gi].BoneCount);

            // 離開模擬的骨頭（元件停用、從清單拿掉）放回靜止姿勢，靜止姿勢的記錄刪掉
            if (old != null)
            {
                for (int i = 0; i < old.NodeList.Count; i++)
                {
                    var node = old.NodeList[i];
                    if (old.Rest[i].Mode == 0 || !hierarchy.IsAlive(node) || next.BoneLookup.ContainsKey(node)) continue;
                    var r = old.Rest[i];
                    hierarchy.SetLocalTRS(node, r.LocalPosition, r.LocalRotation, r.LocalScale);
                }
                old.Dispose();
            }
            staleKeys.Clear();
            foreach (var node in restCache.Keys)
                if (!hierarchy.IsAlive(node) || !next.BoneLookup.ContainsKey(node)) staleKeys.Add(node);
            // 已註冊但這次沒建到的（例如骨頭清單暫時是空的）也刪掉；啟用中的元件下次重建時會重新記錄
            foreach (var node in staleKeys) restCache.Remove(node);
            staleKeys.Clear();
            entries.Clear();
            claimed.Clear();
            nodeIndex.Clear();

            if (next.BodyCount == 0)
            {
                next.Dispose();
                world = null;
                return;
            }
            next.Bodies = new NativeArray<KPSoftBodyData>(bodyList.ToArray(), Allocator.Persistent);
            next.Bones = new NativeArray<KPSoftBone>(boneList.ToArray(), Allocator.Persistent);
            next.Groups = new NativeArray<KPSoftGroup>(groups, Allocator.Persistent);
            next.GroupBodies = new NativeArray<int>(groupBodies, Allocator.Persistent);
            next.GroupBones = new NativeArray<int>(groupBones, Allocator.Persistent);
            next.Rest = new NativeArray<KPSoftLocal>(restList.ToArray(), Allocator.Persistent);
            next.Reads = new NativeArray<KPSoftRead>(restList.Count, Allocator.Persistent);
            next.Writes = new NativeArray<KPSoftLocal>(restList.Count, Allocator.Persistent);
            CreateTransformAccess(next);
            next.EnsureColliderCapacity(16);
            world = next;
        }

        static readonly List<int> oldChain = new List<int>();
        static readonly List<int> newChain = new List<int>();

        /// <summary>重建後祖先鏈換了的骨頭：在新的鏈下重算上一幀的祖先晃動（位置、速度）。</summary>
        static void RebaseJiggle(KPSoftWorld old, KPSoftWorld next, List<KPSoftBone> boneList, int i, int ob)
        {
            oldChain.Clear();
            newChain.Clear();
            for (var a = old.Bones[ob].ParentSoft; a >= 0; a = old.Bones[a].ParentSoft) oldChain.Add(old.NodeList[old.Bones[a].TransformIndex]);
            for (var a = boneList[i].ParentSoft; a >= 0; a = boneList[a].ParentSoft) newChain.Add(next.NodeList[boneList[a].TransformIndex]);
            var same = oldChain.Count == newChain.Count;
            for (int k = 0; same && k < oldChain.Count; k++) same = oldChain[k] == newChain[k];
            if (same) return;

            // 新的鏈的變換＝由上往下乘各祖先自己的變化 E＝D_父⁻¹·D（上一幀與上上一幀）；上一幀沒在模擬的祖先是 I
            var d1 = Unity.Mathematics.float4x4.identity;
            var d2 = Unity.Mathematics.float4x4.identity;
            for (int k = newChain.Count - 1; k >= 0; k--)
            {
                if (!old.BoneLookup.TryGetValue(newChain[k], out var oa)) continue;
                var a = old.Bones[oa];
                var e1 = a.Delta;
                var e2 = a.PrevDelta;
                if (a.ParentSoft >= 0)
                {
                    var ap = old.Bones[a.ParentSoft];
                    e1 = Unity.Mathematics.math.mul(ap.DeltaInverse, e1);
                    e2 = Unity.Mathematics.math.mul(ap.PrevDeltaInverse, e2);
                }
                d1 = Unity.Mathematics.math.mul(d1, e1);
                d2 = Unity.Mathematics.math.mul(d2, e2);
            }
            var bone = boneList[i];
            var basePos = bone.PrevBase;
            var j1 = Unity.Mathematics.math.transform(d1, basePos) - basePos;
            var j2 = Unity.Mathematics.math.transform(d2, basePos) - basePos;
            bone.PrevJiggle = j1;
            var lastDt = old.Bodies[old.Bones[ob].Body].LastDeltaTime;
            if (bone.JiggleStage == 2 && lastDt > 0f) bone.PrevJiggleVelocity = (j1 - j2) / lastDt;
            else if (bone.JiggleStage == 2) bone.JiggleStage = 1;
            boneList[i] = bone;
        }

        protected void DisposeCore()
        {
            world?.Dispose();
            world = null;
            bodies.Clear();
            restCache.Clear();
            entries.Clear();
            claimed.Clear();
            nodeIndex.Clear();
            staleKeys.Clear();
            oldChain.Clear();
            newChain.Clear();
            membershipChanged = false;
        }
    }

    /// <summary>場景上的 KawaiiPhysicsSoftBody 的主機（骨頭是 Transform）。KPSoftBodySystem 持有全域的那一個。</summary>
    internal sealed class KPSoftHost : KPSoftCore
    {
        internal readonly KPTransformHierarchy Hierarchy;
        int hierarchyCountAfterFree;

        public KPSoftHost() : this(new KPTransformHierarchy()) { }
        KPSoftHost(KPTransformHierarchy h) : base(h) => Hierarchy = h;

        /// <summary>這根骨頭是不是這個元件在模擬（用 Transform 找）；是的話給世界的骨頭索引。</summary>
        internal bool TryGetOwnBone(KawaiiPhysicsSoftBody body, Transform t, out int wi)
        {
            wi = -1;
            return t != null && TryGetOwnBone(body.settings, Hierarchy.Find(t), out wi);
        }

        public void Register(KawaiiPhysicsSoftBody body)
        {
            if (body == null) return;
            body.CopyStructure(Hierarchy);
            AddBody(body.settings);
        }

        public void Unregister(KawaiiPhysicsSoftBody body) => RemoveBody(body.settings);

        /// <summary>現在的姿勢重新記成靜止姿勢與重力參考（KawaiiPhysicsSoftBody.CaptureRestPose）。</summary>
        public void CaptureRestPose(KawaiiPhysicsSoftBody body)
        {
            // 別的元件現在的清單也要是最新的（骨頭剛從那個元件搬過來、還沒重建時照樣記錄）
            foreach (var b in bodies)
                if (b.component != null) b.component.CopyStructure(Hierarchy);
            body.CopyStructure(Hierarchy);
            CaptureRestPoseOf(body.settings);
        }

        /// <summary>動畫更新前：需要時重建，骨頭放回靜止姿勢（動畫沒寫到的骨頭不會殘留上一幀的物理結果）。</summary>
        public void RestorePhase()
        {
            // Inspector 改了東西：骨頭清單與角色根換算成節點（比對建置時的記錄用）
            foreach (var b in bodies)
                if (b.validateRequested && b.component != null) b.component.CopyStructure(Hierarchy);
            if (NeedsSync())
            {
                foreach (var b in bodies)
                    if (b.component != null) b.component.CopyStructure(Hierarchy);
                Sync();
                if (Hierarchy.Count > 2 * Mathf.Max(256, hierarchyCountAfterFree)) FreeDeadNodes();
            }
            if (world == null) return;
            new KPSoftApplyJob { Locals = world.Rest }.Schedule(world.Transforms).Complete();
        }

        /// <summary>所有 LateUpdate 之後（Chain 之前）：讀 → 解 → 寫。</summary>
        public void SimulatePhase(float deltaTime)
        {
            if (world == null) return;
            PrepareFrame();
            var read = new KPSoftReadJob { Reads = world.Reads }.ScheduleReadOnly(world.Transforms, 32);
            var solve = ScheduleSolve(deltaTime, read);
            new KPSoftApplyJob { Locals = world.Writes }.Schedule(world.Transforms, solve).Complete();
        }

        /// <summary>每幀在主執行緒準備：設定、碰撞體、衝量。</summary>
        void PrepareFrame()
        {
            var colliderTotal = 0;
            foreach (var body in world.Owners)
                if (body.component != null) colliderTotal += body.component.colliders.Count;
            world.EnsureColliderCapacity(colliderTotal);

            var ci = 0;
            for (int i = 0; i < world.BodyCount; i++)
            {
                var settings = world.Owners[i];
                var body = settings.component;
                var data = world.Bodies[i];
                if (body == null)
                {
                    data.Enabled = 0;
                    world.Bodies[i] = data;
                    continue;
                }
                CopySettings(body.CopyToSettings(), ref data);
                data.ColliderStart = ci;
                foreach (var c in body.colliders)
                    if (c != null && c.enabled && c.gameObject.activeInHierarchy && TryMakeCollider(c, out var sc)) world.Colliders[ci++] = sc;
                data.ColliderCount = ci - data.ColliderStart;
                if (settings.resetRequested)
                {
                    settings.resetRequested = false;
                    data.ResetRequested = 1;
                }
                world.Bodies[i] = data;
                TransferImpulses(settings, true);
            }
        }

        static bool TryMakeCollider(KawaiiPhysicsCollider c, out KPSoftCollider result)
        {
            var t = c.transform;
            var capsule = c.shape == KawaiiPhysicsCollider.Shape.Capsule || c.shape == KawaiiPhysicsCollider.Shape.TaperedCapsule;
            var axis = capsule ? c.AxisRotation * Vector3.up * (c.length * 0.5f) : Vector3.zero;
            return KPSoftColliders.Make(c.shape, t.TransformPoint(c.center), capsule ? t.TransformPoint(c.center + axis) : default,
                capsule ? t.TransformPoint(c.center - axis) : default, t.rotation, t.lossyScale, c.radius, c.radius1, c.extent, c.inner, out result);
        }

        protected override void CreateTransformAccess(KPSoftWorld next)
        {
            var transforms = new Transform[next.NodeList.Count];
            for (int i = 0; i < transforms.Length; i++) transforms[i] = Hierarchy.Get(next.NodeList[i]);
            next.Transforms = new TransformAccessArray(transforms);
        }

        readonly HashSet<int> referencedNodes = new HashSet<int>();

        /// <summary>被刪掉的 Transform 的 id 回收（世界、快取、元件的設定都沒在用的）。</summary>
        void FreeDeadNodes()
        {
            referencedNodes.Clear();
            if (world != null) foreach (var n in world.NodeList) referencedNodes.Add(n);
            foreach (var n in restCache.Keys) referencedNodes.Add(n);
            foreach (var b in bodies)
            {
                foreach (var n in b.bones) referencedNodes.Add(n);
                referencedNodes.Add(b.referenceRoot);
                if (b.runtime != null) referencedNodes.Add(b.runtime.Root);
            }
            Hierarchy.FreeDead(referencedNodes);
            referencedNodes.Clear();
            hierarchyCountAfterFree = Hierarchy.Count;
        }

        public void Shutdown()
        {
            DisposeCore();
            Hierarchy.Clear();
            hierarchyCountAfterFree = 0;
        }
    }

    /// <summary>全域的軟組織主機（場景上的 KawaiiPhysicsSoftBody）。KPSystem 的兩個 PlayerLoop 階段呼叫它。</summary>
    internal static class KPSoftBodySystem
    {
        static readonly KPSoftHost host = new KPSoftHost();

        internal static KPSoftHost Host => host;
        internal static KPSoftWorld World => host.World;
        /// <summary>重建次數（測試用）。</summary>
        internal static int SyncCount => host.SyncCount;
        internal static int RestCacheCount => host.RestCacheCount;
        public static int BodyCount => host.BodyCount;
        public static int BoneCount => host.BoneCount;

        internal static bool TryGetOwnBone(KawaiiPhysicsSoftBody body, Transform t, out int wi) => host.TryGetOwnBone(body, t, out wi);
        public static void Register(KawaiiPhysicsSoftBody body) => host.Register(body);
        public static void Unregister(KawaiiPhysicsSoftBody body) => host.Unregister(body);
        public static void CaptureRestPose(KawaiiPhysicsSoftBody body) => host.CaptureRestPose(body);
        public static void RestorePhase() => host.RestorePhase();
        public static void SimulatePhase(float deltaTime) => host.SimulatePhase(deltaTime);
        public static void Shutdown() => host.Shutdown();
    }
}
