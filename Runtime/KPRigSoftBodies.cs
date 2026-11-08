// KPRig 的軟組織（陣列接口）：KPRigSoftBody 用骨架的索引指定骨頭，每幀 ScheduleSoftBodies：
// 讀（從模擬空間姿勢算 KPSoftRead）→ 準備（碰撞體、撞擊點衝量，KawaiiPhysicsSoftBody 版在主執行緒做的部分）→ 解 → 寫到 Output（Mode 3）。
// 建置、每幀的設定、解算都跟 KawaiiPhysicsSoftBody 共用（KPSoftCore、KPSoftBodySolver）。
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    public sealed partial class KPRig
    {
        readonly KPRigSoftCore softCore;
        NativeArray<int> softNodes;
        NativeArray<int> softBones;
        NativeArray<KPRigSoftColliderSpec> colliderSpecs;
        NativeArray<KPRigSoftPointImpulse> pointImpulses;
        int colliderSpecCount, pointImpulseCount;
        JobHandle softPending;
        readonly HashSet<int> softBoneSet = new HashSet<int>();

        void InitSoft()
        {
            softNodes = new NativeArray<int>(1, Allocator.Persistent);
            softBones = new NativeArray<int>(0, Allocator.Persistent);
            colliderSpecs = new NativeArray<KPRigSoftColliderSpec>(16, Allocator.Persistent);
            pointImpulses = new NativeArray<KPRigSoftPointImpulse>(8, Allocator.Persistent);
        }

        void DisposeSoft()
        {
            softCore.Dispose();
            if (softNodes.IsCreated) softNodes.Dispose();
            if (softBones.IsCreated) softBones.Dispose();
            if (colliderSpecs.IsCreated) colliderSpecs.Dispose();
            if (pointImpulses.IsCreated) pointImpulses.Dispose();
        }

        bool IsSoftBone(int bone) => softBoneSet.Contains(bone);

        /// <summary>軟組織清單（用 AddSoftBody／RemoveSoftBody 增減）。</summary>
        public IReadOnlyList<KPRigSoftBody> SoftBodies => softCore.Bodies;

        /// <summary>模擬中的軟組織數（下一次排程才反映 AddSoftBody／RemoveSoftBody）。</summary>
        public int SoftBodyCount => softCore.BodyCount;

        /// <summary>軟組織會寫的骨頭（遞增排序）。ScheduleSoftBodies 之後、ScheduleChains 之前重算模擬空間姿勢時只要看這些。</summary>
        public NativeArray<int> SoftBodyBones => softBones;

        /// <summary>加一組軟組織（下一次排程時建立）。骨頭的靜止姿勢用建構時給的骨架。</summary>
        /// <exception cref="ArgumentException">骨頭或角色根的索引超出骨架的範圍。</exception>
        public void AddSoftBody(KPRigSoftBody softBody)
        {
            if (softBody == null) throw new ArgumentNullException(nameof(softBody));
            Complete();
            softCore.Validate(softBody);
            softCore.Add(softBody);
        }

        /// <summary>拿掉一組軟組織（下一次排程時生效）。回傳有沒有這組。</summary>
        public bool RemoveSoftBody(KPRigSoftBody softBody)
        {
            Complete();
            return softCore.Remove(softBody);
        }

        /// <summary>
        /// 排程這一幀的軟組織（接在 dependsOn 之後；frame 的陣列要在 dependsOn 完成時算好）。回傳寫完 Output 的 JobHandle。
        /// 完成後把 SoftBodyBones 的結果套進姿勢、重算模擬空間，再排 ScheduleChains（掛在軟組織骨頭底下的骨鏈會跟著動）。
        /// </summary>
        /// <exception cref="ArgumentException">frame 的陣列長度跟骨頭數不一致，或骨頭索引超出範圍。</exception>
        public JobHandle ScheduleSoftBodies(in KPRigFrame frame, JobHandle dependsOn = default)
        {
            if (disposed) throw new ObjectDisposedException(nameof(KPRig));
            if (!frame.Model.IsCreated || frame.Model.Length < BoneCount || !frame.ModelRotation.IsCreated || frame.ModelRotation.Length < BoneCount)
                throw new ArgumentException("KPRigFrame 的 Model／ModelRotation 要有骨頭數那麼長");
            softPending.Complete();
            if (softCore.Update()) RebuildSoftSlots();
            var world = softCore.World;
            if (world == null) return softPending = dependsOn;
            softCore.Prepare(ref colliderSpecs, ref colliderSpecCount, ref pointImpulses, ref pointImpulseCount);

            var read = new KPRigSoftReadJob
            {
                Nodes = softNodes,
                Parents = parents,
                Model = frame.Model,
                RootToWorld = frame.RootToWorld,
                RootNode = hierarchy.RootNode,
                Reads = world.Reads,
            }.Schedule(softNodes.Length, 32, dependsOn);
            var prepare = new KPRigSoftPrepareJob
            {
                Specs = colliderSpecs,
                SpecCount = colliderSpecCount,
                Points = pointImpulses,
                PointCount = pointImpulseCount,
                Model = frame.Model,
                ModelRotation = frame.ModelRotation,
                RootToWorld = frame.RootToWorld,
                RootRotation = frame.RootRotation,
                Bodies = world.Bodies,
                Bones = world.Bones,
                Reads = world.Reads,
                Colliders = world.Colliders,
            }.Schedule(read);
            var solve = softCore.Solve(frame.DeltaTime, prepare);
            // Output 也由骨鏈的寫入工作寫：接在它後面
            softPending = new KPRigSoftWriteJob
            {
                Nodes = softNodes,
                Bones = world.Bones,
                Writes = world.Writes,
                SoftBones = softBones,
                Output = output,
                BoneCount = BoneCount,
            }.Schedule(JobHandle.CombineDependencies(solve, chainPending));
            return softPending;
        }

        /// <summary>軟組織重建之後：讀寫陣列對應的骨頭、軟組織會寫的骨頭。</summary>
        void RebuildSoftSlots()
        {
            Complete();
            var world = softCore.World;
            var n = world != null ? world.NodeList.Count : 0;
            if (softNodes.Length != Mathf.Max(1, n))
            {
                softNodes.Dispose();
                softNodes = new NativeArray<int>(Mathf.Max(1, n), Allocator.Persistent);
            }
            for (int i = 0; i < softNodes.Length; i++) softNodes[i] = i < n ? world.NodeList[i] : -1;
            var written = new SortedSet<int>();
            if (world != null)
                foreach (var kv in world.BoneLookup)
                    if (kv.Key < BoneCount) written.Add(kv.Key);
            // 不再模擬的軟組織骨頭：結果清掉（骨鏈的不動）
            var chain = new HashSet<int>(chainBones);
            foreach (var b in softBones)
            {
                if (written.Contains(b) || chain.Contains(b)) continue;
                var o = output[b];
                o.Mode = 0;
                output[b] = o;
            }
            softBones.Dispose();
            softBones = new NativeArray<int>(written.Count, Allocator.Persistent);
            softBoneSet.Clear();
            var j = 0;
            foreach (var b in written)
            {
                softBones[j++] = b;
                softBoneSet.Add(b);
            }
            RebuildAffected();
        }

        // ===================================================================

        /// <summary>KPRig 的軟組織主機：清單、重建、每幀的設定與碰撞體、衝量的準備。</summary>
        sealed class KPRigSoftCore : KPSoftCore
        {
            readonly KPArrayHierarchy arrayHierarchy;
            readonly KPRig rig;

            public KPRigSoftCore(KPArrayHierarchy h, KPRig rig) : base(h)
            {
                arrayHierarchy = h;
                this.rig = rig;
            }

            public List<KPRigSoftBody> Bodies => bodies;

            protected override void EnsureReadable() => rig.softPending.Complete();

            public void Add(KPRigSoftBody b) => AddBody(b);
            public bool Remove(KPRigSoftBody b) => RemoveBody(b);

            /// <summary>需要時重建。回傳有沒有重建。</summary>
            public bool Update()
            {
                foreach (var b in bodies) Validate(b);
                if (!NeedsSync()) return false;
                Sync();
                return true;
            }

            public void Validate(KPRigSoftBody b)
            {
                void Check(int bone, string field)
                {
                    if (bone < -1 || bone >= arrayHierarchy.BoneCount)
                        throw new ArgumentException($"KPRigSoftBody.{field}＝{bone} 超出骨架的範圍（0～{arrayHierarchy.BoneCount - 1}，-1＝沒有）");
                }
                foreach (var bone in b.bones) Check(bone, nameof(b.bones));
                Check(b.referenceRoot, nameof(b.referenceRoot));
                foreach (var c in b.colliders) if (c != null) Check(c.bone, "colliders.bone");
            }

            /// <summary>每幀的設定、碰撞體（工作裡換算成世界空間）、衝量（撞擊點的留給工作）。</summary>
            public void Prepare(ref NativeArray<KPRigSoftColliderSpec> specs, ref int specCount,
                ref NativeArray<KPRigSoftPointImpulse> points, ref int pointCount)
            {
                var colliderTotal = 0;
                var pointTotal = 0;
                foreach (var body in world.Owners)
                {
                    colliderTotal += body.colliders.Count;
                    pointTotal += body.pendingPointImpulses.Count;
                }
                world.EnsureColliderCapacity(colliderTotal);
                Grow(ref specs, colliderTotal);
                Grow(ref points, pointTotal);

                var ci = 0;
                var pi = 0;
                for (int i = 0; i < world.BodyCount; i++)
                {
                    var body = world.Owners[i];
                    var data = world.Bodies[i];
                    CopySettings(body, ref data);
                    data.ColliderStart = ci;
                    foreach (var c in body.colliders)
                    {
                        if (c == null || c.bone < 0 || c.shape == KawaiiPhysicsCollider.Shape.Convex) continue; // 凸包：軟組織不支援
                        var capsule = c.shape == KawaiiPhysicsCollider.Shape.Capsule || c.shape == KawaiiPhysicsCollider.Shape.TaperedCapsule;
                        specs[ci++] = new KPRigSoftColliderSpec
                        {
                            Bone = c.bone, Shape = c.shape, Center = c.center,
                            Axis = capsule ? KPSoftColliders.CapsuleAxis(c.direction, c.length) : Vector3.zero,
                            Radius = c.radius, Radius1 = c.radius1, Extent = c.extent, Inner = c.inner,
                        };
                    }
                    data.ColliderCount = ci - data.ColliderStart;
                    if (body.resetRequested)
                    {
                        body.resetRequested = false;
                        data.ResetRequested = 1;
                    }
                    world.Bodies[i] = data;
                    TransferImpulses(body, false);
                    foreach (var p in body.pendingPointImpulses)
                        points[pi++] = new KPRigSoftPointImpulse { Body = i, Position = p.Position, VelocityChange = p.VelocityChange, Radius = p.Radius };
                    body.pendingPointImpulses.Clear();
                }
                specCount = ci;
                pointCount = pi;
            }

            static void Grow<T>(ref NativeArray<T> a, int n) where T : struct
            {
                if (a.Length >= n) return;
                var size = Mathf.Max(8, a.Length);
                while (size < n) size *= 2;
                a.Dispose();
                a = new NativeArray<T>(size, Allocator.Persistent);
            }

            public JobHandle Solve(float deltaTime, JobHandle dependsOn) => ScheduleSolve(deltaTime, dependsOn);

            public void Dispose() => DisposeCore();
        }
    }

    /// <summary>軟組織的碰撞體（骨架的索引＋區域的形狀；工作裡換算成世界空間）。</summary>
    internal struct KPRigSoftColliderSpec
    {
        public int Bone;
        public KawaiiPhysicsCollider.Shape Shape;
        public Vector3 Center;
        /// <summary>膠囊兩端球心相對中心的區域偏移。</summary>
        public Vector3 Axis;
        public float Radius, Radius1;
        public Vector3 Extent;
        public bool Inner;
    }

    /// <summary>撞擊點衝量（世界空間；工作裡用骨頭這一幀的位置衰減）。</summary>
    internal struct KPRigSoftPointImpulse
    {
        public int Body;
        public Vector3 Position;
        public Vector3 VelocityChange;
        public float Radius;
    }

    /// <summary>讀：軟組織的骨頭與角色根從模擬空間姿勢算 KPSoftRead（KawaiiPhysicsSoftBody 版是從 Transform 讀）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct KPRigSoftReadJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> Nodes;
        [ReadOnly] public NativeArray<int> Parents;
        [ReadOnly] public NativeArray<float4x4> Model;
        public float4x4 RootToWorld;
        public int RootNode;
        [WriteOnly] public NativeArray<KPSoftRead> Reads;

        public void Execute(int index)
        {
            if (index >= Reads.Length) return;
            var node = Nodes[index];
            if (node < 0 || node > RootNode)
            {
                Reads[index] = default;
                return;
            }
            float4x4 world, parent, local;
            if (node == RootNode)
            {
                world = RootToWorld;
                parent = float4x4.identity;
                local = RootToWorld;
            }
            else
            {
                var p = Parents[node];
                var parentModel = p < 0 ? float4x4.identity : Model[p];
                world = math.mul(RootToWorld, Model[node]);
                parent = math.mul(RootToWorld, parentModel);
                local = math.mul(math.inverse(parentModel), Model[node]);
            }
            // 區域 TRS：區域矩陣＝旋轉×縮放（欄的長度是縮放，行列式為負時 X 取負）
            var sx = math.length(local.c0.xyz);
            var sy = math.length(local.c1.xyz);
            var sz = math.length(local.c2.xyz);
            if (math.determinant(new float3x3(local)) < 0f) sx = -sx;
            var ls = new float3(sx, sy, sz);
            var valid = math.all(math.abs(ls) > 1e-8f) && !KPSoftBodySolver.Degenerate(new float3x3(world));
            var read = new KPSoftRead { World = world, LocalPosition = local.c3.xyz, LocalScale = ls };
            if (valid)
            {
                read.LocalRotation = math.normalize(new quaternion(new float3x3(local.c0.xyz / sx, local.c1.xyz / sy, local.c2.xyz / sz)));
                read.ParentMatrix = parent;
                read.ParentInverse = math.inverse(parent);
                valid = Finite(world) && Finite(read.ParentMatrix) && Finite(read.ParentInverse) && math.all(math.isfinite(read.LocalRotation.value));
            }
            read.Valid = valid ? 1 : 0;
            Reads[index] = read;
        }

        static bool Finite(float4x4 m) =>
            math.all(math.isfinite(m.c0)) && math.all(math.isfinite(m.c1)) && math.all(math.isfinite(m.c2)) && math.all(math.isfinite(m.c3));
    }

    /// <summary>準備：碰撞體換算成世界空間、撞擊點衝量依骨頭這一幀的位置衰減後加到骨頭上。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct KPRigSoftPrepareJob : IJob
    {
        [ReadOnly] public NativeArray<KPRigSoftColliderSpec> Specs;
        public int SpecCount;
        [ReadOnly] public NativeArray<KPRigSoftPointImpulse> Points;
        public int PointCount;
        [ReadOnly] public NativeArray<float4x4> Model;
        [ReadOnly] public NativeArray<quaternion> ModelRotation;
        public float4x4 RootToWorld;
        public quaternion RootRotation;
        [ReadOnly] public NativeArray<KPSoftBodyData> Bodies;
        public NativeArray<KPSoftBone> Bones;
        [ReadOnly] public NativeArray<KPSoftRead> Reads;
        public NativeArray<KPSoftCollider> Colliders;

        public void Execute()
        {
            for (int i = 0; i < SpecCount; i++)
            {
                var c = Specs[i];
                var m = math.mul(RootToWorld, Model[c.Bone]);
                var rotation = math.mul(RootRotation, ModelRotation[c.Bone]);
                var lossy = new float3(math.length(m.c0.xyz), math.length(m.c1.xyz), math.length(m.c2.xyz));
                var center = math.transform(m, c.Center);
                var a = math.transform(m, (float3)c.Center + (float3)c.Axis);
                var b = math.transform(m, (float3)c.Center - (float3)c.Axis);
                KPSoftColliders.Make(c.Shape, center, a, b, rotation, lossy, c.Radius, c.Radius1, c.Extent, c.Inner, out var result);
                Colliders[i] = result;
            }
            for (int k = 0; k < PointCount; k++)
            {
                var p = Points[k];
                var body = Bodies[p.Body];
                for (int i = body.BoneStart; i < body.BoneStart + body.BoneCount; i++)
                {
                    var bone = Bones[i];
                    var pos = (Vector3)Reads[bone.TransformIndex].World.c3.xyz;
                    bone.PendingImpulse += (float3)KPRigSoftBody.PointImpulseAt(pos, p.Position, p.VelocityChange, p.Radius);
                    Bones[i] = bone;
                }
            }
        }
    }

    /// <summary>寫：軟組織的結果搬到每根骨頭的 Output（Mode 3：位置、旋轉、縮放）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct KPRigSoftWriteJob : IJob
    {
        [ReadOnly] public NativeArray<int> Nodes;
        [ReadOnly] public NativeArray<KPSoftBone> Bones;
        [ReadOnly] public NativeArray<KPSoftLocal> Writes;
        [ReadOnly] public NativeArray<int> SoftBones;
        public NativeArray<KPRigBoneOutput> Output;
        public int BoneCount;

        public void Execute()
        {
            for (int i = 0; i < SoftBones.Length; i++)
            {
                var o = Output[SoftBones[i]];
                o.Mode = 0;
                Output[SoftBones[i]] = o;
            }
            for (int i = 0; i < Bones.Length; i++)
            {
                var ti = Bones[i].TransformIndex;
                var w = Writes[ti];
                var node = Nodes[ti];
                if (w.Mode == 0 || node < 0 || node >= BoneCount) continue;
                Output[node] = new KPRigBoneOutput { LocalPosition = w.LocalPosition, LocalRotation = w.LocalRotation, LocalScale = w.LocalScale, Mode = 3 };
            }
        }
    }
}
