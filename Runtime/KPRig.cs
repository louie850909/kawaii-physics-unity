// 陣列接口：沒有 Transform 的角色（姿勢放在 NativeArray、由工作鏈計算）用的骨鏈物理。
// 每個角色一個 KPRig，各自持有 KPWorld。呼叫端每幀給骨架的模擬空間姿勢（Model、ModelRotation）與根的世界矩陣，
// ScheduleChains 接在呼叫端的 JobHandle 後面：讀（從模擬空間姿勢算 KPReadPose）→ 解 →（世界碰撞）→ 寫到 Output。
// 骨鏈的建立、每幀的準備、解算都跟 KawaiiPhysicsChain 共用同一套程式（KPWorld、KPChainCore、KPSolver）。
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>KPRig 每幀的輸入。</summary>
    public struct KPRigFrame
    {
        /// <summary>這一幀的時間（秒）。同一幀第二次排程給 0。</summary>
        public float DeltaTime;
        /// <summary>骨架的根（模擬空間的原點）的世界矩陣。</summary>
        public float4x4 RootToWorld;
        /// <summary>骨架的根的世界旋轉（跟 RootToWorld 一致；Transform.rotation）。</summary>
        public quaternion RootRotation;
        /// <summary>每根骨頭在根空間的矩陣（長度＝骨頭數）。排程的工作完成前不能改。</summary>
        public NativeArray<float4x4> Model;
        /// <summary>每根骨頭在根空間的旋轉（父的旋轉連乘自己的區域旋轉，跟 Transform.rotation 的算法一致）。</summary>
        public NativeArray<quaternion> ModelRotation;
    }

    /// <summary>一根骨頭的物理結果（區域空間）。</summary>
    public struct KPRigBoneOutput
    {
        /// <summary>區域位置（Mode＝2、3 時有效）。</summary>
        public float3 LocalPosition;
        /// <summary>區域旋轉（Mode＝1、2、3 時有效）。</summary>
        public quaternion LocalRotation;
        /// <summary>區域縮放（Mode＝3 時有效：軟組織的伸縮）。</summary>
        public float3 LocalScale;
        /// <summary>0＝這一幀沒寫（用動畫的值）、1＝只有旋轉、2＝位置與旋轉（骨鏈）、3＝位置、旋轉與縮放（軟組織）。</summary>
        public int Mode;
    }

    /// <summary>
    /// 一個角色的骨鏈物理（陣列接口）。骨架在建構時給：父的索引（父要在子之前）、名稱、靜止姿勢（區域 TRS）。
    /// 骨鏈用 KPRigChain 設定（骨頭是這個骨架的索引；component＝-1 是骨架的根）。
    /// 每幀呼叫 ScheduleChains，工作完成後讀 Output：AffectedBones 裡、Mode 不是 0 的骨頭換成物理的區域值，再重算模擬空間姿勢。
    /// 用完要 Dispose（會先等正在跑的工作）。
    /// </summary>
    public sealed partial class KPRig : IDisposable
    {
        readonly KPRigCore core;
        readonly KPArrayHierarchy hierarchy;
        NativeArray<int> parents;
        NativeArray<int> slotNodes;
        /// <summary>骨鏈會寫的骨頭。</summary>
        NativeArray<int> chainBones;
        /// <summary>骨鏈與軟組織會寫的骨頭（AffectedBones）。</summary>
        NativeArray<int> affected;
        NativeArray<KPRigBoneOutput> output;
        JobHandle chainPending;
        bool disposed;

        /// <summary>
        /// 骨架：parents[i]＝父的索引（-1＝根骨頭，父要比子小）、names、靜止姿勢的區域位置、旋轉、縮放（null＝都是 1）。
        /// colliderBones[i]＝true 的骨頭是碰撞體物件，建骨鏈時不當成子骨頭（對應 KawaiiPhysicsCollider 所在的物件；null＝沒有）。
        /// </summary>
        /// <exception cref="ArgumentException">陣列長度不一致，或有父不在子之前的骨頭。</exception>
        public KPRig(int[] parents, string[] names, Vector3[] restLocalPositions, Quaternion[] restLocalRotations,
            Vector3[] restLocalScales = null, bool[] colliderBones = null)
        {
            if (parents == null || names == null || restLocalPositions == null || restLocalRotations == null)
                throw new ArgumentNullException(parents == null ? nameof(parents) : names == null ? nameof(names)
                    : restLocalPositions == null ? nameof(restLocalPositions) : nameof(restLocalRotations));
            var n = parents.Length;
            if (names.Length != n || restLocalPositions.Length != n || restLocalRotations.Length != n ||
                (restLocalScales != null && restLocalScales.Length != n) || (colliderBones != null && colliderBones.Length != n))
                throw new ArgumentException("骨架的陣列長度不一致");
            for (int i = 0; i < n; i++)
                if (parents[i] >= i || parents[i] < -1)
                    throw new ArgumentException($"骨頭 {i}（{names[i]}）的父 {parents[i]} 不在它之前：父的索引要比子小");
            hierarchy = new KPArrayHierarchy(parents, names, restLocalPositions, restLocalRotations, restLocalScales, colliderBones);
            core = new KPRigCore(hierarchy, this);
            this.parents = new NativeArray<int>(parents, Allocator.Persistent);
            output = new NativeArray<KPRigBoneOutput>(Mathf.Max(1, n), Allocator.Persistent);
            for (int i = 0; i < n; i++)
                output[i] = new KPRigBoneOutput
                {
                    LocalPosition = restLocalPositions[i], LocalRotation = restLocalRotations[i],
                    LocalScale = restLocalScales != null ? restLocalScales[i] : Vector3.one,
                };
            slotNodes = new NativeArray<int>(1, Allocator.Persistent);
            chainBones = new NativeArray<int>(0, Allocator.Persistent);
            affected = new NativeArray<int>(0, Allocator.Persistent);
            softCore = new KPRigSoftCore(hierarchy, this);
            InitSoft();
        }

        /// <summary>骨頭數。</summary>
        public int BoneCount => hierarchy.BoneCount;

        internal KPWorld World => core.World;
        internal uint FrameSeed { get => core.FrameSeed; set => core.FrameSeed = value; }

        /// <summary>簡易世界碰撞不收這個 Transform 底下的碰撞體（角色自己身上的）；null＝都收。</summary>
        public Transform Owner { get; set; }

        /// <summary>骨鏈清單（用 AddChain／RemoveChain 增減）。</summary>
        public IReadOnlyList<KPRigChain> Chains => core.Chains;

        /// <summary>模擬中的骨鏈數（下一次排程才反映 AddChain／RemoveChain）。</summary>
        public int ChainCount => core.World?.InstanceCount ?? 0;

        /// <summary>模擬中的骨頭數（含末端、細分、橋接的假骨）。</summary>
        public int SimulatedBoneCount
        {
            get
            {
                var w = core.World;
                if (w == null) return 0;
                var n = 0;
                foreach (var s in w.Segments) n += s.Size.Bone;
                return n;
            }
        }

        /// <summary>每根骨頭的物理結果（長度＝骨頭數）。ScheduleSoftBodies／ScheduleChains 的工作完成後才能讀。</summary>
        public NativeArray<KPRigBoneOutput> Output => output;

        /// <summary>骨鏈與軟組織會寫的骨頭（遞增排序）。重算模擬空間姿勢時只要看這些；下一次排程可能因為加減骨鏈、軟組織而改變。</summary>
        public NativeArray<int> AffectedBones => affected;

        /// <summary>名稱是 name 的第一根骨頭的索引；沒有時是 -1。</summary>
        public int FindBone(string name)
        {
            for (int i = 0; i < hierarchy.BoneCount; i++)
                if (hierarchy.BoneName(i) == name) return i;
            return -1;
        }

        /// <summary>加一條骨鏈（下一次排程時建立）。</summary>
        public void AddChain(KPRigChain chain)
        {
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            Complete();
            core.Add(chain);
        }

        /// <summary>拿掉一條骨鏈（下一次排程時生效）。回傳有沒有這條。</summary>
        public bool RemoveChain(KPRigChain chain)
        {
            Complete();
            return core.Remove(chain);
        }

        /// <summary>所有骨鏈的 ResetDynamics。</summary>
        public void ResetDynamics(KPTeleportType type = KPTeleportType.Reset)
        {
            foreach (var c in core.Chains) c.ResetDynamics(type);
        }

        /// <summary>
        /// 排程這一幀的骨鏈物理（接在 dependsOn 之後；frame 的陣列要在 dependsOn 完成時算好）。
        /// 主執行緒先做需要時的重建與每幀的準備（外力、倍率、風、簡易世界碰撞）。回傳寫完 Output 的 JobHandle。
        /// </summary>
        /// <exception cref="ArgumentException">frame 的陣列長度跟骨頭數不一致，或骨鏈的骨頭索引超出範圍。</exception>
        public JobHandle ScheduleChains(in KPRigFrame frame, JobHandle dependsOn = default)
        {
            if (disposed) throw new ObjectDisposedException(nameof(KPRig));
            if (!frame.Model.IsCreated || frame.Model.Length < BoneCount || !frame.ModelRotation.IsCreated || frame.ModelRotation.Length < BoneCount)
                throw new ArgumentException("KPRigFrame 的 Model／ModelRotation 要有骨頭數那麼長");
            chainPending.Complete();
            if (core.Update(frame)) return chainPending = dependsOn;
            var world = core.World;
            var ptrs = world.Pointers();
            var read = new KPRigReadJob
            {
                SlotNodes = slotNodes,
                Parents = parents,
                Model = frame.Model,
                ModelRotation = frame.ModelRotation,
                RootToWorld = frame.RootToWorld,
                RootRotation = frame.RootRotation,
                RootNode = hierarchy.RootNode,
                Reads = world.Reads,
            }.Schedule(slotNodes.Length, 64, dependsOn);
            var handle = core.ScheduleSolve(frame.DeltaTime, read, ptrs);
            handle = core.ScheduleCollision(handle, ptrs);
            // Output 也由軟組織的寫入工作寫：接在它後面
            chainPending = new KPRigWriteJob
            {
                SlotNodes = slotNodes,
                Affected = chainBones,
                Writes = world.Writes,
                Output = output,
                BoneCount = BoneCount,
            }.Schedule(JobHandle.CombineDependencies(handle, softPending));
            return chainPending;
        }

        /// <summary>等排程的工作完成（骨鏈與軟組織；之後可以讀 Output、改骨鏈與軟組織）。</summary>
        public void Complete()
        {
            softPending.Complete();
            chainPending.Complete();
        }

        /// <summary>等工作完成，釋放原生記憶體。</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Complete();
            core.Dispose();
            DisposeSoft();
            if (parents.IsCreated) parents.Dispose();
            if (slotNodes.IsCreated) slotNodes.Dispose();
            if (chainBones.IsCreated) chainBones.Dispose();
            if (affected.IsCreated) affected.Dispose();
            if (output.IsCreated) output.Dispose();
        }

        /// <summary>重建之後：讀寫陣列的每一格對應哪根骨頭、骨鏈會寫哪些骨頭。</summary>
        void RebuildSlots()
        {
            Complete();
            var world = core.World;
            var slots = world != null ? world.Reads.Length : 0;
            if (slotNodes.Length != Mathf.Max(1, slots))
            {
                slotNodes.Dispose();
                slotNodes = new NativeArray<int>(Mathf.Max(1, slots), Allocator.Persistent);
            }
            for (int i = 0; i < slotNodes.Length; i++) slotNodes[i] = -1;
            var written = new SortedSet<int>();
            if (world != null)
                foreach (var s in world.Segments)
                {
                    for (int k = 0; k < s.Nodes.Length; k++) slotNodes[s.At.Transform + k] = s.Nodes[k];
                    if (s.Rest == null) continue;
                    foreach (var (index, _, _) in s.Rest)
                        if (s.Nodes[index] < BoneCount) written.Add(s.Nodes[index]);
                }
            // 不再寫的骨頭：結果清掉（呼叫端用動畫的值）。軟組織的不動
            foreach (var b in chainBones)
            {
                if (IsSoftBone(b)) continue;
                var o = output[b];
                o.Mode = 0;
                output[b] = o;
            }
            chainBones.Dispose();
            chainBones = new NativeArray<int>(written.Count, Allocator.Persistent);
            var j = 0;
            foreach (var b in written) chainBones[j++] = b;
            RebuildAffected();
        }

        /// <summary>AffectedBones＝骨鏈與軟組織會寫的骨頭（遞增）。</summary>
        void RebuildAffected()
        {
            var all = new SortedSet<int>();
            foreach (var b in chainBones) all.Add(b);
            foreach (var b in softBones) all.Add(b);
            affected.Dispose();
            affected = new NativeArray<int>(all.Count, Allocator.Persistent);
            var j = 0;
            foreach (var b in all) affected[j++] = b;
        }

        // ===================================================================

        /// <summary>KPRig 的主機：骨鏈清單、重建、每幀的準備。</summary>
        sealed class KPRigCore : KPChainCore
        {
            readonly KPArrayHierarchy arrayHierarchy;
            readonly KPRig rig;
            readonly List<KPRigChain> chains = new List<KPRigChain>();
            readonly List<KPRigChain> building = new List<KPRigChain>();
            bool membershipChanged = true;
            Vector3 rootPosition;

            public KPRigCore(KPArrayHierarchy h, KPRig rig) : base(h)
            {
                arrayHierarchy = h;
                this.rig = rig;
            }

            public List<KPRigChain> Chains => chains;

            public void Add(KPRigChain c)
            {
                if (chains.Contains(c)) return;
                chains.Add(c);
                membershipChanged = true;
            }

            public bool Remove(KPRigChain c)
            {
                if (!chains.Remove(c)) return false;
                membershipChanged = true;
                return true;
            }

            protected override Vector3 ComponentPosition(int component) =>
                component == arrayHierarchy.RootNode ? rootPosition : rootPosition + rigRootRotation * arrayHierarchy.Position(component);

            protected override bool IsOwnCollider(Transform collider, int component) => rig.Owner != null && collider.IsChildOf(rig.Owner);

            Quaternion rigRootRotation = Quaternion.identity;

            /// <summary>需要時重建、這一幀的準備。回傳 true＝沒有骨鏈（不用排程）；重建過時呼叫端要重新對應讀寫陣列。</summary>
            public bool Update(in KPRigFrame frame)
            {
                rootPosition = frame.RootToWorld.c3.xyz;
                rigRootRotation = frame.RootRotation;
                var needsSync = membershipChanged;
                foreach (var c in chains)
                {
                    if (c.validateRequested)
                    {
                        c.validateRequested = false;
                        var seg = c.runtime?.Segment;
                        if (seg == null || seg.Signature != KPWorld.ComputeSignature(c)) c.dirty = true;
                        else c.settingsDirty = true;
                    }
                    needsSync |= c.dirty;
                }
                if (needsSync)
                {
                    building.Clear();
                    foreach (var c in chains)
                    {
                        Validate(c);
                        building.Add(c);
                    }
                    membershipChanged = false;
                    SyncWorld(building);
                    building.Clear();
                    foreach (var c in chains) c.dirty = false;
                    rig.RebuildSlots();
                }
                if (!HasChains) return true;
                PrepareFrame(frame.DeltaTime);
                return false;
            }

            public JobHandle ScheduleCollision(JobHandle dependsOn, KPWorldPtrs ptrs) => ScheduleWorldCollision(dependsOn, ptrs);

            void Validate(KPRigChain c)
            {
                void Check(int bone, string field)
                {
                    if (bone < -1 || bone >= arrayHierarchy.BoneCount)
                        throw new ArgumentException($"KPRigChain.{field}＝{bone} 超出骨架的範圍（0～{arrayHierarchy.BoneCount - 1}，-1＝沒有）");
                }
                Check(c.rootBone, nameof(c.rootBone));
                Check(c.component, nameof(c.component));
                Check(c.simulationBaseBone, nameof(c.simulationBaseBone));
                foreach (var b in c.excludeBones) Check(b, nameof(c.excludeBones));
                foreach (var r in c.additionalRootBones)
                {
                    if (r == null) continue;
                    Check(r.rootBone, "additionalRootBones.rootBone");
                    foreach (var b in r.overrideExcludeBones) Check(b, "additionalRootBones.overrideExcludeBones");
                }
                foreach (var col in c.colliders) if (col != null) Check(col.bone, "colliders.bone");
                foreach (var bc in c.boneConstraints)
                {
                    if (bc == null) continue;
                    Check(bc.bone1, "boneConstraints.bone1");
                    Check(bc.bone2, "boneConstraints.bone2");
                }
                foreach (var s in c.syncBones)
                {
                    if (s == null) continue;
                    Check(s.bone, "syncBones.bone");
                    foreach (var t in s.targetRoots) if (t != null) Check(t.bone, "syncBones.targetRoots.bone");
                }
            }

            public void Dispose() => DisposeCore();
        }
    }

    /// <summary>讀：讀寫陣列的每一格從骨架的模擬空間姿勢算出世界姿勢與區域姿勢（KawaiiPhysicsChain 版是從 Transform 讀）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct KPRigReadJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> SlotNodes;
        [ReadOnly] public NativeArray<int> Parents;
        [ReadOnly] public NativeArray<float4x4> Model;
        [ReadOnly] public NativeArray<quaternion> ModelRotation;
        public float4x4 RootToWorld;
        public quaternion RootRotation;
        public int RootNode;
        [WriteOnly] public NativeArray<KPReadPose> Reads;

        public void Execute(int index)
        {
            var node = SlotNodes[index];
            if (node < 0 || node > RootNode) return;
            float4x4 world;
            quaternion rotation, localRotation;
            float3 localPosition;
            if (node == RootNode)
            {
                world = RootToWorld;
                rotation = RootRotation;
                localRotation = RootRotation;
                localPosition = RootToWorld.c3.xyz;
            }
            else
            {
                var m = Model[node];
                world = math.mul(RootToWorld, m);
                rotation = math.mul(RootRotation, ModelRotation[node]);
                var p = Parents[node];
                if (p < 0)
                {
                    localPosition = m.c3.xyz;
                    localRotation = ModelRotation[node];
                }
                else
                {
                    localPosition = math.transform(math.inverse(Model[p]), m.c3.xyz);
                    localRotation = math.mul(math.conjugate(ModelRotation[p]), ModelRotation[node]);
                }
            }
            Reads[index] = new KPReadPose
            {
                Position = world.c3.xyz,
                Rotation = rotation,
                LocalRotation = localRotation,
                LocalPosition = localPosition,
                LossyScale = new float3(math.length(world.c0.xyz), math.length(world.c1.xyz), math.length(world.c2.xyz)),
            };
        }
    }

    /// <summary>寫：解算結果搬到每根骨頭的 Output（同一根骨頭被兩條鏈寫時，後面的格子優先，跟 Transform 版一樣是單一順序）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct KPRigWriteJob : IJob
    {
        [ReadOnly] public NativeArray<int> SlotNodes;
        [ReadOnly] public NativeArray<int> Affected;
        [ReadOnly] public NativeArray<KPWriteLocal> Writes;
        public NativeArray<KPRigBoneOutput> Output;
        public int BoneCount;

        public void Execute()
        {
            for (int i = 0; i < Affected.Length; i++)
            {
                var o = Output[Affected[i]];
                o.Mode = 0;
                Output[Affected[i]] = o;
            }
            var n = math.min(SlotNodes.Length, Writes.Length);
            for (int i = 0; i < n; i++)
            {
                var w = Writes[i];
                var node = SlotNodes[i];
                if (w.Mode == 0 || node < 0 || node >= BoneCount) continue;
                var o = Output[node];
                o.LocalPosition = w.LocalPosition;
                o.LocalRotation = w.LocalRotation;
                o.Mode = w.Mode;
                Output[node] = o;
            }
        }
    }
}
