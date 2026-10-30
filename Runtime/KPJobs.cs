// 讀 → 解 →（世界碰撞：掃掠 → 修正）→ 寫。讀用 ScheduleReadOnly（所有 Transform 都能平行），
// 解用 IJobParallelFor（一個角色一份），寫用 IJobParallelForTransform（同一根節點的 Transform 在同一條執行緒）。
// 解算工作直接拿指標（主執行緒算好），相依性由 JobHandle 串起來保證。
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace KawaiiPhysicsUnity
{
    [BurstCompile(CompileSynchronously = true)]
    internal struct KPReadJob : IJobParallelForTransform
    {
        [WriteOnly] public NativeArray<KPReadPose> Reads;

        public void Execute(int index, TransformAccess t)
        {
            if (!t.isValid) return;
            var m = (float4x4)t.localToWorldMatrix;
            Reads[index] = new KPReadPose
            {
                Position = m.c3.xyz,
                Rotation = t.rotation,
                LocalRotation = t.localRotation,
                LocalPosition = t.localPosition,
                LossyScale = new float3(math.length(m.c0.xyz), math.length(m.c1.xyz), math.length(m.c2.xyz)),
            };
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    internal unsafe struct KPSolveJob : IJobParallelFor
    {
        [NativeDisableUnsafePtrRestriction] public KPInstance* Instances;
        [NativeDisableUnsafePtrRestriction] public KPWorldPtrs World;
        public float DeltaTime;

        public void Execute(int index) => KPSolver.SolveInstance(ref Instances[index], World, DeltaTime);
    }

    /// <summary>世界碰撞：每根骨頭從這一幀開始的位置掃到解算後的位置（原作每個子步進做一次，這裡一幀一次）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal unsafe struct KPBuildSweepJob : IJobParallelFor
    {
        [NativeDisableUnsafePtrRestriction] public KPInstance* Instances;
        [NativeDisableUnsafePtrRestriction] public KPBone* Bones;
        [NativeDisableParallelForRestriction] public NativeArray<SpherecastCommand> Commands;
        public PhysicsScene Scene;
        [ReadOnly] public NativeArray<int> LayerMasks;

        public void Execute(int index)
        {
            var inst = Instances[index];
            var query = new QueryParameters(LayerMasks[index], false, QueryTriggerInteraction.Ignore, false);
            for (int i = inst.BoneStart; i < inst.BoneStart + inst.BoneCount; i++)
            {
                var b = Bones[i];
                var radius = b.Settings.Radius * inst.SimScale;
                var skip = inst.WorldCollision == 0 || inst.Enabled == 0 || (b.Flags & KPBoneFlags.SkipSimulate) != 0 || radius <= KPMath.KindaSmallNumber;
                var origin = inst.SimPosition + math.mul(inst.SimRotation, b.FrameStartLocation * inst.SimScale);
                var end = inst.SimPosition + math.mul(inst.SimRotation, b.Location * inst.SimScale);
                var d = end - origin;
                var dist = math.length(d);
                if (skip || dist < 1e-5f)
                {
                    Commands[i] = new SpherecastCommand(Scene, origin, 0f, new Vector3(0f, 1f, 0f), query, 0f);
                    continue;
                }
                // Unity 的掃掠不回報「一開始就重疊」的碰撞（PhysX 在 UE 會回報穿透深度）。
                // 起點往回退一個半徑，淺的穿透也能撞到表面。
                var dir = d / dist;
                Commands[i] = new SpherecastCommand(Scene, origin - dir * radius, radius, dir, query, dist + radius);
            }
        }
    }

    /// <summary>掃掠有撞到的骨頭移到撞到的位置，再做一次骨長復原與輸出（原作 AdjustByWorldCollision 之後的流程）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal unsafe struct KPWorldCollisionFixupJob : IJobParallelFor
    {
        [NativeDisableUnsafePtrRestriction] public KPInstance* Instances;
        [NativeDisableUnsafePtrRestriction] public KPWorldPtrs World;
        [ReadOnly] public NativeArray<SpherecastCommand> Commands;
        [ReadOnly] public NativeArray<RaycastHit> Hits;

        public void Execute(int index)
        {
            ref var inst = ref Instances[index];
            if (inst.WorldCollision == 0 || inst.Enabled == 0) return;
            var any = false;
            var invSim = math.conjugate(inst.SimRotation);
            for (int i = inst.BoneStart; i < inst.BoneStart + inst.BoneCount; i++)
            {
                var hit = Hits[i];
                if (math.lengthsq((float3)hit.normal) <= 0f) continue;
                var cmd = Commands[i];
                // 停在接觸位置，沿法線留一點間隙，下一幀的掃掠才會從外面開始
                var center = (float3)cmd.origin + (float3)cmd.direction * hit.distance + (float3)hit.normal * (0.05f * cmd.radius);
                World.Bones[i].Location = math.mul(invSim, center - inst.SimPosition) / inst.SimScale;
                any = true;
            }
            if (!any) return;
            KPSolver.RestoreBoneLengthsAndLimits(ref inst, World);
            KPSolver.ApplySimulateResult(ref inst, World);
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct KPWriteJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<KPWriteLocal> Writes;

        public void Execute(int index, TransformAccess t)
        {
            var o = Writes[index];
            if (o.Mode == 0 || !t.isValid) return;
            if (o.Mode == 2) t.SetLocalPositionAndRotation(o.LocalPosition, o.LocalRotation);
            else t.localRotation = o.LocalRotation;
        }
    }

    /// <summary>動畫更新前把鏈上的骨頭放回靜止姿勢，避免沒被動畫寫到的骨頭把上一幀的物理結果當成姿勢。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct KPRestoreJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<KPWriteLocal> Rest;

        public void Execute(int index, TransformAccess t)
        {
            var o = Rest[index];
            if (o.Mode == 0 || !t.isValid) return;
            t.SetLocalPositionAndRotation(o.LocalPosition, o.LocalRotation);
        }
    }

    /// <summary>只跑物理一幀（不碰 Transform），給 golden 測試與純解算的效能比較用。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal unsafe struct KPStepFrameJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public KPInstance* Instances;
        public int InstanceCount;
        [NativeDisableUnsafePtrRestriction] public KPWorldPtrs World;
        public float DeltaTime;
        public int Frames;

        public void Execute()
        {
            for (int f = 0; f < Frames; f++)
                for (int i = 0; i < InstanceCount; i++)
                    KPSolver.StepFrame(ref Instances[i], World, DeltaTime);
        }
    }
}
