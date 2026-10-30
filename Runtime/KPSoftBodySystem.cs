// 軟組織的系統：KawaiiPhysicsSoftBody 打包成一組 NativeArray，由 KPSystem 的兩個 PlayerLoop 階段驅動
//（Update 前還原靜止姿勢；所有 LateUpdate 之後在 Chain 之前 讀 → 解 → 寫）。
// 元件數與骨頭數都少（一個角色幾根），加入、移除、結構變更時整批重建，模擬狀態依骨頭沿用（其他元件不受影響）。
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

namespace KawaiiPhysicsUnity
{
    /// <summary>元件建置時的記錄。骨頭一律用 Transform 找（清單改過、還沒重建時索引會對不上）。</summary>
    internal sealed class KPSoftBodyRuntime
    {
        public int BodyIndex = -1;
        public Transform Root;
        /// <summary>建置時的 bones 清單（Inspector 改了沒有，跟這個比）。</summary>
        public Transform[] Bones = Array.Empty<Transform>();

        public bool Matches(KawaiiPhysicsSoftBody body)
        {
            if (Root != body.ResolveRoot() || Bones.Length != body.bones.Count) return false;
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

        public static KPSoftRest Capture(Transform t, Transform root)
        {
            t.GetLocalPositionAndRotation(out var p, out var r);
            var parentMatrix = t.parent != null ? (Unity.Mathematics.float4x4)t.parent.localToWorldMatrix : Unity.Mathematics.float4x4.identity;
            var rootMatrix = root != null ? (Unity.Mathematics.float4x4)root.localToWorldMatrix : Unity.Mathematics.float4x4.identity;
            var valid = !KPSoftBodySolver.Degenerate(new Unity.Mathematics.float3x3(parentMatrix)) &&
                        !KPSoftBodySolver.Degenerate(new Unity.Mathematics.float3x3(rootMatrix));
            return new KPSoftRest
            {
                LocalPosition = p,
                LocalRotation = r,
                LocalScale = t.localScale,
                ParentValid = valid,
                ParentInRoot = valid
                    ? Unity.Mathematics.math.mul(Unity.Mathematics.math.transpose(KPSoftBodySolver.Frame(rootMatrix)), KPSoftBodySolver.Frame(parentMatrix))
                    : Unity.Mathematics.float3x3.identity,
            };
        }

        public void Apply(Transform t)
        {
            t.SetLocalPositionAndRotation(LocalPosition, LocalRotation);
            t.localScale = LocalScale;
        }

        public KPSoftLocal ToLocal(int mode) => new KPSoftLocal
        {
            LocalPosition = LocalPosition, LocalRotation = LocalRotation, LocalScale = LocalScale, Mode = mode,
        };
    }

    /// <summary>一次建置的結果。TransformAccessArray 依序是各元件的骨頭與角色根（同一個 Transform 只放一次）。</summary>
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
        public readonly List<KawaiiPhysicsSoftBody> Owners = new List<KawaiiPhysicsSoftBody>();
        public readonly List<Transform> TransformList = new List<Transform>();
        /// <summary>骨頭 → 世界的骨頭索引（重建時沿用狀態用）。</summary>
        public readonly Dictionary<Transform, int> BoneLookup = new Dictionary<Transform, int>();
        /// <summary>每個元件建置時的角色根（跟 Owners 對齊）。</summary>
        public readonly List<Transform> Roots = new List<Transform>();
        /// <summary>元件 → Owners 的索引（重建時找舊狀態用）。</summary>
        public readonly Dictionary<KawaiiPhysicsSoftBody, int> OwnerLookup = new Dictionary<KawaiiPhysicsSoftBody, int>();

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

    internal static class KPSoftBodySystem
    {
        static readonly List<KawaiiPhysicsSoftBody> bodies = new List<KawaiiPhysicsSoftBody>();
        /// <summary>
        /// 靜止姿勢：啟用時（動畫還沒動）記下來；骨頭離開模擬時（還原之後）刪掉，下次啟用重新記錄
        ///（應用端在停用期間改了骨頭的靜止姿勢也會反映）。
        /// </summary>
        static readonly Dictionary<Transform, KPSoftRest> restCache = new Dictionary<Transform, KPSoftRest>();
        static KPSoftWorld world;
        static bool membershipChanged;

        internal static KPSoftWorld World => world;
        /// <summary>重建次數（測試用）。</summary>
        internal static int SyncCount { get; private set; }
        internal static int RestCacheCount => restCache.Count;
        public static int BodyCount => world?.BodyCount ?? 0;
        public static int BoneCount => world?.BoneCount ?? 0;

        /// <summary>這根骨頭是不是這個元件在模擬（用 Transform 找，不用清單索引）；是的話給世界的骨頭索引。</summary>
        internal static bool TryGetOwnBone(KawaiiPhysicsSoftBody body, Transform t, out int wi)
        {
            wi = -1;
            var rt = body.runtime;
            if (world == null || rt == null || t == null || rt.BodyIndex < 0 || rt.BodyIndex >= world.BodyCount || world.Owners[rt.BodyIndex] != body) return false;
            return world.BoneLookup.TryGetValue(t, out wi) && world.Bones[wi].Body == rt.BodyIndex;
        }

        public static void Register(KawaiiPhysicsSoftBody body)
        {
            if (body == null) return;
            if (!bodies.Contains(body))
            {
                bodies.Add(body);
                body.ClearPendingImpulses();
            }
            var root = body.ResolveRoot();
            foreach (var t in body.bones)
                if (t != null && !restCache.ContainsKey(t)) restCache.Add(t, KPSoftRest.Capture(t, root));
            membershipChanged = true;
        }

        public static void Unregister(KawaiiPhysicsSoftBody body)
        {
            if (!bodies.Remove(body)) return;
            membershipChanged = true;
            // 停用再啟用一律重新開始（同一幀內再啟用也一樣，不沿用停用前的狀態）
            body.resetOnRebuild = true;
        }

        /// <summary>現在的姿勢重新記成靜止姿勢與重力參考（KawaiiPhysicsSoftBody.CaptureRestPose）。</summary>
        public static void CaptureRestPose(KawaiiPhysicsSoftBody body)
        {
            var root = body.ResolveRoot();
            foreach (var t in body.bones)
            {
                if (t == null) continue;
                var mine = TryGetOwnBone(body, t, out var wi);
                // 別的元件正在模擬、而且清單裡還有的骨頭不動（靜止姿勢歸那個元件管）。
                // 那個元件的清單已經拿掉它（搬到這個元件、還沒重建）時照樣記錄，下次重建採用
                if (!mine && world != null && world.BoneLookup.TryGetValue(t, out var owi))
                {
                    var owner = world.Owners[world.Bones[owi].Body];
                    if (owner != null && owner != body && bodies.Contains(owner) && owner.bones.Contains(t)) continue;
                }
                var rest = KPSoftRest.Capture(t, root);
                restCache[t] = rest;
                if (!mine) continue;
                var bone = world.Bones[wi];
                bone.RestParentInRoot = rest.ParentInRoot;
                bone.RestPending = rest.ParentValid ? 0 : 1;
                world.Bones[wi] = bone;
                world.Rest[bone.TransformIndex] = rest.ToLocal(1);
            }
        }

        /// <summary>動畫更新前：需要時重建，骨頭放回靜止姿勢（動畫沒寫到的骨頭不會殘留上一幀的物理結果）。</summary>
        public static void RestorePhase()
        {
            var needsSync = membershipChanged;
            foreach (var b in bodies)
            {
                if (b == null)
                {
                    needsSync = true;
                    continue;
                }
                if (b.validateRequested)
                {
                    b.validateRequested = false;
                    if (b.runtime == null || !b.runtime.Matches(b)) b.dirty = true;
                }
                // 角色根被刪掉：重建（ResolveRoot 退回上層的 Animator 或自己）
                var root = b.runtime?.Root;
                if (!ReferenceEquals(root, null) && root == null) b.dirty = true;
                needsSync |= b.dirty;
            }
            if (needsSync) Sync();
            if (world == null) return;
            new KPSoftApplyJob { Locals = world.Rest }.Schedule(world.Transforms).Complete();
        }

        /// <summary>所有 LateUpdate 之後（Chain 之前）：讀 → 解 → 寫。</summary>
        public static void SimulatePhase(float deltaTime)
        {
            if (world == null) return;
            PrepareFrame();
            var read = new KPSoftReadJob { Reads = world.Reads }.ScheduleReadOnly(world.Transforms, 32);
            var solve = ScheduleSolve(deltaTime, read);
            new KPSoftApplyJob { Locals = world.Writes }.Schedule(world.Transforms, solve).Complete();
        }

        static JobHandle ScheduleSolve(float deltaTime, JobHandle dependsOn)
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

        // ===================================================================
        // 每幀在主執行緒準備：設定、碰撞體、衝量
        // ===================================================================

        static void PrepareFrame()
        {
            var colliderTotal = 0;
            foreach (var body in world.Owners)
                if (body != null) colliderTotal += body.colliders.Count;
            world.EnsureColliderCapacity(colliderTotal);

            var ci = 0;
            for (int i = 0; i < world.BodyCount; i++)
            {
                var body = world.Owners[i];
                var data = world.Bodies[i];
                if (body == null)
                {
                    data.Enabled = 0;
                    world.Bodies[i] = data;
                    continue;
                }
                CopySettings(body, ref data);
                data.ColliderStart = ci;
                foreach (var c in body.colliders)
                    if (c != null && c.enabled && c.gameObject.activeInHierarchy && TryMakeCollider(c, out var sc)) world.Colliders[ci++] = sc;
                data.ColliderCount = ci - data.ColliderStart;
                if (body.resetRequested)
                {
                    body.resetRequested = false;
                    data.ResetRequested = 1;
                }
                world.Bodies[i] = data;
                TransferImpulses(body);
            }
        }

        static void CopySettings(KawaiiPhysicsSoftBody body, ref KPSoftBodyData data)
        {
            data.Enabled = body.isActiveAndEnabled ? 1 : 0;
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

        /// <summary>主執行緒累積的衝量搬到骨頭上（位置衰減用這一刻骨頭的位置＝動畫後的姿勢）。</summary>
        static void TransferImpulses(KawaiiPhysicsSoftBody body)
        {
            var uniform = body.pendingUniformImpulse;
            var perBone = body.pendingImpulses;
            var points = body.pendingPointImpulses;
            body.pendingUniformImpulse = Vector3.zero;
            if (uniform == Vector3.zero && perBone == null && points.Count == 0) return;
            for (int i = 0; i < body.bones.Count; i++)
            {
                var t = body.bones[i];
                // 清單裡重複的骨頭只算第一次（建置時重複的也略過）
                if (!TryGetOwnBone(body, t, out var wi) || body.bones.IndexOf(t) != i) continue;
                var add = uniform;
                if (perBone != null && i < perBone.Length) add += perBone[i];
                if (points.Count > 0)
                {
                    var pos = t.position;
                    foreach (var p in points)
                    {
                        var w = 1f;
                        if (p.Radius > 0f)
                        {
                            w = Mathf.Max(0f, 1f - Vector3.Distance(pos, p.Position) / p.Radius);
                            w *= w;
                        }
                        add += p.VelocityChange * w;
                    }
                }
                if (add == Vector3.zero) continue;
                var bone = world.Bones[wi];
                bone.PendingImpulse += (Unity.Mathematics.float3)add;
                world.Bones[wi] = bone;
            }
            if (perBone != null) Array.Clear(perBone, 0, perBone.Length);
            points.Clear();
        }

        static bool TryMakeCollider(KawaiiPhysicsCollider c, out KPSoftCollider result)
        {
            var t = c.transform;
            var s = t.lossyScale;
            var maxScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            result = new KPSoftCollider { A = t.TransformPoint(c.center), Rotation = t.rotation };
            switch (c.shape)
            {
                case KawaiiPhysicsCollider.Shape.Sphere:
                    result.Shape = KPSoftCollider.Sphere;
                    result.RadiusA = c.radius * maxScale;
                    result.Inner = c.inner ? 1 : 0;
                    return true;
                case KawaiiPhysicsCollider.Shape.Capsule:
                case KawaiiPhysicsCollider.Shape.TaperedCapsule:
                {
                    var axis = c.AxisRotation * Vector3.up * (c.length * 0.5f);
                    result.Shape = KPSoftCollider.Capsule;
                    result.A = t.TransformPoint(c.center + axis);
                    result.B = t.TransformPoint(c.center - axis);
                    result.RadiusA = c.radius * maxScale;
                    result.RadiusB = (c.shape == KawaiiPhysicsCollider.Shape.TaperedCapsule ? c.radius1 : c.radius) * maxScale;
                    return true;
                }
                case KawaiiPhysicsCollider.Shape.Box:
                    result.Shape = KPSoftCollider.Box;
                    result.Extent = Vector3.Scale(c.extent, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
                    return true;
                case KawaiiPhysicsCollider.Shape.Plane:
                    result.Shape = KPSoftCollider.Plane;
                    result.B = (t.rotation * Vector3.up).normalized;
                    return true;
                default:
                    return false; // 凸包：軟組織不支援
            }
        }

        // ===================================================================
        // 建置與清理
        // ===================================================================

        static readonly List<(Transform t, int listIndex, int depth)> entries = new List<(Transform, int, int)>();
        static readonly HashSet<Transform> claimed = new HashSet<Transform>();
        static readonly Dictionary<Transform, int> transformIndex = new Dictionary<Transform, int>();
        static readonly List<Transform> staleKeys = new List<Transform>();

        static void Sync()
        {
            membershipChanged = false;
            SyncCount++;
            for (int i = bodies.Count - 1; i >= 0; i--)
                if (bodies[i] == null) bodies.RemoveAt(i);

            var old = world;
            var next = new KPSoftWorld();
            var boneList = new List<KPSoftBone>();
            var boneDepth = new List<int>();
            var keptFrom_ = new List<int>();
            var bodyList = new List<KPSoftBodyData>();
            var restList = new List<KPSoftLocal>();
            transformIndex.Clear();

            int AddTransform(Transform t, KPSoftLocal rest)
            {
                if (transformIndex.TryGetValue(t, out var idx))
                {
                    if (rest.Mode != 0) restList[idx] = rest; // 先當角色根加入、後來才是骨頭
                    return idx;
                }
                idx = next.TransformList.Count;
                next.TransformList.Add(t);
                restList.Add(rest);
                transformIndex.Add(t, idx);
                return idx;
            }

            foreach (var body in bodies)
            {
                var root = body.ResolveRoot();
                var rt = body.runtime ??= new KPSoftBodyRuntime();
                var bodyIndex = bodyList.Count;
                entries.Clear();
                claimed.Clear();
                for (int i = 0; i < body.bones.Count; i++)
                {
                    var t = body.bones[i];
                    if (t == null) continue;
                    if (next.BoneLookup.ContainsKey(t) || !claimed.Add(t))
                    {
                        Debug.LogWarning($"KawaiiPhysicsSoftBody「{body.name}」：骨頭 {t.name} 已經由其他軟組織元件模擬，略過。", body);
                        continue;
                    }
                    var depth = 0;
                    for (var p = t.parent; p != null; p = p.parent) depth++;
                    entries.Add((t, i, depth));
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
                    var t = entries[k].t;
                    if (!restCache.TryGetValue(t, out var rest))
                    {
                        rest = KPSoftRest.Capture(t, root);
                        restCache.Add(t, rest);
                    }
                    var bone = new KPSoftBone
                    {
                        TransformIndex = AddTransform(t, rest.ToLocal(1)),
                        Body = bodyIndex,
                        ParentSoft = -1,
                        RestParentInRoot = rest.ParentInRoot,
                        RestPending = rest.ParentValid ? 0 : 1,
                        Delta = Unity.Mathematics.float4x4.identity,
                        DeltaInverse = Unity.Mathematics.float4x4.identity,
                    };
                    var keptFrom = -1;
                    if (keep && old.BoneLookup.TryGetValue(t, out var ob) && old.Bones[ob].Body == oldBody)
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
                    if (keptFrom < 0 && old != null && old.BoneLookup.ContainsKey(t)) bone.WasRunning = 1;
                    next.BoneLookup.Add(t, start + k);
                    boneList.Add(bone);
                    boneDepth.Add(entries[k].depth);
                    keptFrom_.Add(keptFrom);
                }

                var data = new KPSoftBodyData
                {
                    BoneStart = start,
                    BoneCount = entries.Count,
                    RootIndex = AddTransform(root, default),
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
                var t = next.TransformList[boneList[i].TransformIndex];
                for (var p = t.parent; p != null; p = p.parent)
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
                for (int i = 0; i < old.TransformList.Count; i++)
                {
                    var t = old.TransformList[i];
                    if (old.Rest[i].Mode == 0 || t == null || next.BoneLookup.ContainsKey(t)) continue;
                    var r = old.Rest[i];
                    t.SetLocalPositionAndRotation(r.LocalPosition, r.LocalRotation);
                    t.localScale = r.LocalScale;
                }
                old.Dispose();
            }
            staleKeys.Clear();
            foreach (var t in restCache.Keys)
                if (t == null || !next.BoneLookup.ContainsKey(t)) staleKeys.Add(t);
            // 已註冊但這次沒建到的（例如骨頭清單暫時是空的）也刪掉；啟用中的元件下次重建時會重新記錄
            foreach (var t in staleKeys) restCache.Remove(t);
            staleKeys.Clear();
            entries.Clear();
            claimed.Clear();
            transformIndex.Clear();

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
            next.Transforms = new TransformAccessArray(next.TransformList.ToArray());
            next.EnsureColliderCapacity(16);
            world = next;
        }

        static readonly List<Transform> oldChain = new List<Transform>();
        static readonly List<Transform> newChain = new List<Transform>();

        /// <summary>重建後祖先鏈換了的骨頭：在新的鏈下重算上一幀的祖先晃動（位置、速度）。</summary>
        static void RebaseJiggle(KPSoftWorld old, KPSoftWorld next, List<KPSoftBone> boneList, int i, int ob)
        {
            oldChain.Clear();
            newChain.Clear();
            for (var a = old.Bones[ob].ParentSoft; a >= 0; a = old.Bones[a].ParentSoft) oldChain.Add(old.TransformList[old.Bones[a].TransformIndex]);
            for (var a = boneList[i].ParentSoft; a >= 0; a = boneList[a].ParentSoft) newChain.Add(next.TransformList[boneList[a].TransformIndex]);
            var same = oldChain.Count == newChain.Count;
            for (int k = 0; same && k < oldChain.Count; k++) same = ReferenceEquals(oldChain[k], newChain[k]);
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

        public static void Shutdown()
        {
            world?.Dispose();
            world = null;
            bodies.Clear();
            restCache.Clear();
            entries.Clear();
            claimed.Clear();
            transformIndex.Clear();
            staleKeys.Clear();
            oldChain.Clear();
            newChain.Clear();
            membershipChanged = false;
        }
    }
}
