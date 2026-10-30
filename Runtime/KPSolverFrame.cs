// 一個角色的一整幀：跟 Transform 交換資料的部分（原作 EvaluateSkeletalControl_AnyThread 的流程）。
using Unity.Mathematics;
using static Unity.Mathematics.math;
using static KawaiiPhysicsUnity.KPMath;

namespace KawaiiPhysicsUnity
{
    internal static unsafe partial class KPSolver
    {
        /// <summary>
        /// 讀到的世界姿勢 → 模擬空間 → SyncBone → 外力換空間 → （暖機）→ StepFrame → 輸出區域值。
        /// w.Reads 要先由讀取工作填好；結果寫到 w.Writes（以 TransformIndex 為索引）。
        /// </summary>
        public static void SolveInstance(ref KPInstance inst, in KPWorldPtrs w, float frameDt)
        {
            if (inst.Enabled == 0 || inst.BoneCount == 0) return;
            var bones = w.Bones;
            int begin = inst.BoneStart, end = inst.BoneStart + inst.BoneCount;

            // 元件（角色根）
            float3 cPos = new float3(0f);
            quaternion cRot = Unity.Mathematics.quaternion.identity;
            float cScale = 1f;
            if (inst.ComponentIndex >= 0)
            {
                var c = w.Reads[inst.ComponentIndex];
                cPos = c.Position;
                cRot = c.Rotation;
                cScale = c.LossyScale.x; // 假設等比縮放
            }
            inst.ComponentPosition = cPos;
            inst.ComponentRotation = cRot;
            inst.ComponentScale = cScale;

            // 模擬空間（BuildSimulationSpaceCache）
            if (inst.SimulationSpace == (int)KPSimulationSpace.WorldSpace)
            {
                inst.SimPosition = new float3(0f);
                inst.SimRotation = Unity.Mathematics.quaternion.identity;
                inst.SimScale = 1f;
            }
            else if (inst.SimulationSpace == (int)KPSimulationSpace.BaseBoneSpace && inst.BaseBoneIndex >= 0)
            {
                var bb = w.Reads[inst.BaseBoneIndex];
                inst.SimPosition = bb.Position;
                inst.SimRotation = bb.Rotation;
                inst.SimScale = bb.LossyScale.x;
            }
            else
            {
                inst.SimPosition = cPos;
                inst.SimRotation = cRot;
                inst.SimScale = cScale;
            }
            // 縮放 0（或極小）：這一幀不模擬也不寫回（除以 0 會產生 NaN）；縮放恢復時重新初始化
            if (abs(cScale) < 1e-6f || abs(inst.SimScale) < 1e-6f)
            {
                for (int i = begin; i < end; i++)
                    if (bones[i].TransformIndex >= 0) w.Writes[bones[i].TransformIndex].Mode = 0;
                inst.PoseInitialized = 0;
                inst.HasPrevComponent = 0;
                return;
            }
            var invSim = conjugate(inst.SimRotation);
            var invSimScale = 1f / inst.SimScale;

            UpdateEffectiveSettings(ref inst, w);
            UpdatePoseFromReads(ref inst, w, invSim, invSimScale);

            var firstFrame = inst.HasPrevComponent == 0;
            if (firstFrame)
            {
                // InitModifyBones：位置從姿勢開始
                for (int i = begin; i < end; i++)
                {
                    var b = bones + i;
                    b->Location = b->PoseLocation;
                    b->PrevLocation = b->PoseLocation;
                }
                inst.PrevComponentPosition = cPos;
                inst.PrevComponentRotation = cRot;
                StorePrevSimInComponent(ref inst);
                inst.HasPrevComponent = 1;
            }

            ApplySyncBones(ref inst, w, invSim, invSimScale);

            // UpdateSkelCompMove：上一幀的元件位置在這一幀元件空間裡的位置
            var invC = conjugate(cRot);
            inst.SkelCompMoveVector = mul(invC, inst.PrevComponentPosition - cPos) / cScale * inst.SkelCompMoveScale;
            inst.SkelCompMoveRotation = mul(invC, inst.PrevComponentRotation);
            var moveAngle = degrees(2f * acos(clamp(inst.SkelCompMoveRotation.value.w, -1f, 1f)));
            if ((inst.TeleportDistanceThreshold > 0f && lengthsq(inst.SkelCompMoveVector) > inst.TeleportDistanceThreshold * inst.TeleportDistanceThreshold) ||
                (inst.TeleportRotationThreshold > 0f && moveAngle > inst.TeleportRotationThreshold))
                inst.Teleport = 1;

            // 重力：世界（或元件）空間 → 模擬空間。簡易外力：世界空間指定時才換，否則原作就當成模擬空間的值
            var gravityWorld = inst.UseWorldSpaceGravity != 0 ? inst.Gravity : mul(cRot, inst.Gravity * cScale);
            inst.SimSpaceGravity = mul(invSim, gravityWorld) * invSimScale;
            inst.SimSpaceSimpleExternalForce = inst.SimpleExternalForceWorldSpace != 0
                ? mul(invSim, inst.SimpleExternalForce) * invSimScale
                : inst.SimpleExternalForce;

            ConvertForcesToSimSpace(ref inst, w, cRot, cScale, invSim, invSimScale);
            UpdateLimits(ref inst, w, invSim, invSimScale);

            for (int i = begin; i < end; i++) bones[i].FrameStartLocation = bones[i].Location;

            // WarmUp：固定 dt 空跑幾幀（累積器不混進這一幀）
            if (inst.WarmUpPending > 0)
            {
                var savedAcc = inst.SubstepAccumulator;
                var warmDt = inst.UseFixedSubstep != 0 ? 1f / max(1, inst.TargetFramerate) : frameDt;
                inst.SubstepAccumulator = 0f;
                for (int k = 0; k < inst.WarmUpPending; k++) StepFrame(ref inst, w, warmDt);
                inst.SubstepAccumulator = savedAcc;
                inst.WarmUpPending = 0;
            }

            if (inst.SimulationSpace == (int)KPSimulationSpace.WorldSpace && inst.Teleport != 0)
            {
                // 世界空間瞬移：不模擬，把上一幀的位置跟著元件一起搬過去
                var prevInv = conjugate(inst.PrevComponentRotation);
                for (int i = begin; i < end; i++)
                {
                    var b = bones + i;
                    var prevCS = mul(prevInv, b->PrevLocation - inst.PrevComponentPosition);
                    b->Location = cPos + mul(cRot, prevCS);
                    b->PrevLocation = b->Location;
                }
                inst.SubstepAccumulator = 0f;
                inst.PoseInitialized = 0;
                inst.ConsumeFraction = 1f;
            }
            else
            {
                StepFrame(ref inst, w, frameDt);
            }

            ApplySimulateResult(ref inst, w);

            // AdvancePreSkelCompTransform
            var consume = inst.Teleport != 0 ? 1f : clamp(inst.ConsumeFraction, 0f, 1f);
            if (consume >= 1f - KindaSmallNumber)
            {
                inst.PrevComponentPosition = cPos;
                inst.PrevComponentRotation = cRot;
            }
            else
            {
                inst.PrevComponentPosition = lerp(inst.PrevComponentPosition, cPos, consume);
                inst.PrevComponentRotation = SlerpUE(inst.PrevComponentRotation, cRot, consume);
            }
            StorePrevSimInComponent(ref inst);
            inst.Teleport = 0;
        }

        static void StorePrevSimInComponent(ref KPInstance inst)
        {
            var invC = conjugate(inst.ComponentRotation);
            inst.PrevSimPosition = mul(invC, inst.SimPosition - inst.ComponentPosition) / inst.ComponentScale;
            inst.PrevSimRotation = mul(invC, inst.SimRotation);
            inst.PrevSimScale = inst.SimScale / inst.ComponentScale;
        }

        /// <summary>UpdatePhysicsSettingsOfModifyBones 的倍率部分：基本值×倍率，再夾範圍。</summary>
        static void UpdateEffectiveSettings(ref KPInstance inst, in KPWorldPtrs w)
        {
            var m = inst.Multiplier;
            for (int i = inst.BoneStart; i < inst.BoneStart + inst.BoneCount; i++)
            {
                var b = w.Bones + i;
                var s = b->BaseSettings;
                b->Settings.Damping = clamp(s.Damping * m.Damping, 0f, 1f);
                b->Settings.Stiffness = clamp(s.Stiffness * m.Stiffness, 0f, 1f);
                b->Settings.WorldDampingLocation = clamp(s.WorldDampingLocation * m.WorldDampingLocation, 0f, 1f);
                b->Settings.WorldDampingRotation = clamp(s.WorldDampingRotation * m.WorldDampingRotation, 0f, 1f);
                b->Settings.Radius = max(s.Radius * m.Radius, 0f);
                var baseLimit = max(s.LimitAngle, 0f);
                b->Settings.LimitAngle = baseLimit > 0f ? max(baseLimit * m.LimitAngle, KindaSmallNumber) : 0f;
            }
        }

        /// <summary>UpdateModifyBonesPoseTransform：實骨從 Transform、末端假骨往前延伸、細分假骨在兩端間內插。</summary>
        static void UpdatePoseFromReads(ref KPInstance inst, in KPWorldPtrs w, quaternion invSim, float invSimScale)
        {
            var bones = w.Bones;
            int begin = inst.BoneStart, end = inst.BoneStart + inst.BoneCount;
            var forward = ForwardAxis(inst.ForwardAxis);
            for (int i = begin; i < end; i++)
            {
                var b = bones + i;
                if (Has(b, KPBoneFlags.Bridge) || Has(b, KPBoneFlags.InterBone)) continue;
                if (b->TransformIndex >= 0)
                {
                    var r = w.Reads[b->TransformIndex];
                    b->PoseLocation = mul(invSim, r.Position - inst.SimPosition) * invSimScale;
                    b->PoseRotation = mul(invSim, r.Rotation);
                }
                else if (Has(b, KPBoneFlags.Dummy))
                {
                    var p = bones + b->RealParentIndex;
                    b->PoseLocation = p->PoseLocation + mul(p->PoseRotation, forward) * inst.DummyBoneLength;
                    b->PoseRotation = p->PoseRotation;
                }
            }
            UpdateInterBoneDummyPoses(ref inst, w);
        }

        static void UpdateInterBoneDummyPoses(ref KPInstance inst, in KPWorldPtrs w)
        {
            var bones = w.Bones;
            for (int i = inst.BoneStart; i < inst.BoneStart + inst.BoneCount; i++)
            {
                var b = bones + i;
                if (!Has(b, KPBoneFlags.InterBone)) continue;
                var p = bones + b->RealParentIndex;
                var c = bones + b->RealChildIndex;
                b->PoseLocation = lerp(p->PoseLocation, c->PoseLocation, b->InterBoneAlpha);
                b->PoseRotation = SlerpUE(p->PoseRotation, c->PoseRotation, b->InterBoneAlpha);
            }
        }

        /// <summary>
        /// 對應原作 ApplySyncBones：同步來源骨頭偏離靜止姿勢的量，推動目標骨頭的姿勢位置（保持到父的骨長）。
        /// 例：大腿往前抬，裙子的姿勢跟著往前，減少穿模。
        /// </summary>
        static void ApplySyncBones(ref KPInstance inst, in KPWorldPtrs w, quaternion invSim, float invSimScale)
        {
            if (inst.SyncCount == 0) return;
            var bones = w.Bones;
            var invC = conjugate(inst.ComponentRotation);
            for (int s = 0; s < inst.SyncCount; s++)
            {
                var sync = w.SyncBones + inst.SyncStart + s;
                var r = w.Reads[sync->TransformIndex];
                var posCS = mul(invC, r.Position - inst.ComponentPosition) / inst.ComponentScale;
                var delta = posCS - sync->InitialPoseLocation;
                delta *= EvalLut(w.Luts, sync->DeltaCurveLut, length(delta), sync->DeltaCurveMaxDistance, 1f);
                delta *= sync->GlobalScale;
                var filtered = new float3(
                    PassDirection(delta.x, sync->Directions.x) ? delta.x : 0f,
                    PassDirection(delta.y, sync->Directions.y) ? delta.y : 0f,
                    PassDirection(delta.z, sync->Directions.z) ? delta.z : 0f);
                if (IsNearlyZero(filtered)) continue;

                // 元件空間 → 模擬空間
                var simDelta = mul(invSim, mul(inst.ComponentRotation, filtered * inst.ComponentScale)) * invSimScale;
                var syncSim = mul(invSim, r.Position - inst.SimPosition) * invSimScale;
                for (int t = 0; t < sync->TargetCount; t++)
                {
                    var target = w.SyncTargets + sync->TargetStart + t;
                    var b = bones + target->BoneIndex;
                    var atten = Attenuation(sync, distance(syncSim, b->Location));
                    ApplySyncTarget(bones, b, simDelta * atten * target->Scale);
                }
            }
            UpdateInterBoneDummyPoses(ref inst, w);
            // 末端假骨跟著實骨（姿勢被推過）重新延伸
            var forward = ForwardAxis(inst.ForwardAxis);
            for (int i = inst.BoneStart; i < inst.BoneStart + inst.BoneCount; i++)
            {
                var b = bones + i;
                if (!Has(b, KPBoneFlags.Dummy) || Has(b, KPBoneFlags.Bridge) || b->RealParentIndex < 0) continue;
                var p = bones + b->RealParentIndex;
                b->PoseLocation = p->PoseLocation + mul(p->PoseRotation, forward) * inst.DummyBoneLength;
            }
        }

        static bool PassDirection(float v, int dir) =>
            dir == (int)KPSyncDirection.Both || (dir == (int)KPSyncDirection.Positive && v > 0f) || (dir == (int)KPSyncDirection.Negative && v < 0f);

        static float Attenuation(KPSyncBone* sync, float d)
        {
            if (sync->EnableAttenuation == 0) return 1f;
            var inner = sync->AttenuationInner;
            var outer = max(sync->AttenuationOuter, inner);
            float amount;
            if (d <= inner) amount = 0f;
            else if (d >= outer) amount = sync->MaxAttenuation;
            else
            {
                var denom = outer - inner;
                amount = (denom > KindaSmallNumber ? (d - inner) / denom : 1f) * sync->MaxAttenuation;
            }
            return max(0f, 1f - amount);
        }

        /// <summary>FKawaiiPhysicsSyncTarget::Apply（上一幀跳過模擬的骨頭不推）。</summary>
        static void ApplySyncTarget(KPBone* bones, KPBone* b, float3 translation)
        {
            if (Has(b, KPBoneFlags.SkipSimulate)) return;
            if (b->ParentIndex < 0)
            {
                b->PoseLocation += translation;
                return;
            }
            var parent = bones + b->ParentIndex;
            var newPose = b->PoseLocation + translation;
            if (Has(parent, KPBoneFlags.InterBone))
            {
                var oneMinusAlpha = 1f - parent->InterBoneAlpha;
                if (oneMinusAlpha > KindaSmallNumber && parent->RealParentIndex >= 0)
                {
                    var gp = bones + parent->RealParentIndex;
                    var targetLength = b->BoneLength / oneMinusAlpha;
                    var dir = SafeNormal(newPose - gp->PoseLocation);
                    if (IsNearlyZero(dir)) dir = SafeNormal(b->PoseLocation - gp->PoseLocation);
                    b->PoseLocation = IsNearlyZero(dir) ? newPose : dir * targetLength + gp->PoseLocation;
                }
                else b->PoseLocation = newPose;
            }
            else
            {
                var dir = SafeNormal(newPose - parent->PoseLocation);
                if (IsNearlyZero(dir)) dir = SafeNormal(b->PoseLocation - parent->PoseLocation);
                b->PoseLocation = IsNearlyZero(dir) ? newPose : dir * b->BoneLength + parent->PoseLocation;
            }
        }

        /// <summary>主執行緒算好的外力（世界或元件空間）換到模擬空間。BoneSpace 的留到每根骨頭再轉。</summary>
        static void ConvertForcesToSimSpace(ref KPInstance inst, in KPWorldPtrs w, quaternion cRot, float cScale, quaternion invSim, float invSimScale)
        {
            for (int k = 0; k < inst.ForceCount; k++)
            {
                var f = w.Forces + inst.ForceStart + k;
                if (f->Space == (int)KPForceSpace.BoneSpace) continue;
                var world = f->Space == (int)KPForceSpace.WorldSpace ? f->Vector : mul(cRot, f->Vector * cScale);
                f->Vector = mul(invSim, world) * invSimScale;
                f->Space = -1; // 已換好
            }
        }

        /// <summary>對應 Update*Limits：碰撞體跟著驅動骨頭（或世界空間的值）換到模擬空間。</summary>
        static void UpdateLimits(ref KPInstance inst, in KPWorldPtrs w, quaternion invSim, float invSimScale)
        {
            for (int c = 0; c < inst.SphereCount; c++)
            {
                var s = w.Spheres + inst.SphereStart + c;
                if (s->DrivingIndex == KPDriving.SimSpace) continue;
                var world = s->DrivingIndex >= 0 ? DrivenPosition(w, s->DrivingIndex, s->Offset) : s->WorldLocation;
                s->Location = mul(invSim, world - inst.SimPosition) * invSimScale;
            }
            for (int c = 0; c < inst.CapsuleCount; c++)
            {
                var s = w.Capsules + inst.CapsuleStart + c;
                if (s->DrivingIndex == KPDriving.SimSpace) continue;
                DrivenPose(w, s->DrivingIndex, s->Offset, s->OffsetRotation, s->WorldLocation, s->WorldRotation, out var p, out var q);
                s->Location = mul(invSim, p - inst.SimPosition) * invSimScale;
                s->Rotation = mul(invSim, q);
            }
            for (int c = 0; c < inst.TaperedCount; c++)
            {
                var s = w.Tapered + inst.TaperedStart + c;
                if (s->DrivingIndex == KPDriving.SimSpace) continue;
                DrivenPose(w, s->DrivingIndex, s->Offset, s->OffsetRotation, s->WorldLocation, s->WorldRotation, out var p, out var q);
                s->Location = mul(invSim, p - inst.SimPosition) * invSimScale;
                s->Rotation = mul(invSim, q);
            }
            for (int c = 0; c < inst.BoxCount; c++)
            {
                var s = w.Boxes + inst.BoxStart + c;
                if (s->DrivingIndex == KPDriving.SimSpace) continue;
                DrivenPose(w, s->DrivingIndex, s->Offset, s->OffsetRotation, s->WorldLocation, s->WorldRotation, out var p, out var q);
                s->Location = mul(invSim, p - inst.SimPosition) * invSimScale;
                s->Rotation = mul(invSim, q);
            }
            for (int c = 0; c < inst.PlaneCount; c++)
            {
                var s = w.Planes + inst.PlaneStart + c;
                if (s->DrivingIndex == KPDriving.SimSpace) continue;
                DrivenPose(w, s->DrivingIndex, s->Offset, s->OffsetRotation, s->WorldLocation, s->WorldRotation, out var p, out var q);
                s->Location = mul(invSim, p - inst.SimPosition) * invSimScale;
                s->Rotation = mul(invSim, q);
            }
            for (int c = 0; c < inst.ConvexCount; c++)
            {
                var s = w.Convexes + inst.ConvexStart + c;
                if (s->DrivingIndex == KPDriving.SimSpace) continue;
                DrivenPose(w, s->DrivingIndex, s->Offset, s->OffsetRotation, s->WorldLocation, s->WorldRotation, out var p, out var q);
                s->Location = mul(invSim, p - inst.SimPosition) * invSimScale;
                s->Rotation = mul(invSim, q);
            }
        }

        static float3 DrivenPosition(in KPWorldPtrs w, int index, float3 offset)
        {
            var d = w.Reads[index];
            return d.Position + mul(d.Rotation, offset * d.LossyScale);
        }

        static void DrivenPose(in KPWorldPtrs w, int index, float3 offset, quaternion offsetRotation, float3 worldLocation,
            quaternion worldRotation, out float3 position, out quaternion rotation)
        {
            if (index >= 0)
            {
                var d = w.Reads[index];
                position = d.Position + mul(d.Rotation, offset * d.LossyScale);
                rotation = mul(d.Rotation, offsetRotation);
            }
            else
            {
                position = worldLocation;
                rotation = worldRotation;
            }
        }

        /// <summary>
        /// 對應原作 ApplySimulateResult，再換成各 Transform 的區域值並依 Alpha 跟動畫姿勢混合。
        /// 父骨頭只有一個子骨頭時，父的旋轉＝把姿勢方向轉到模擬方向；子骨頭的位置＝模擬位置。
        /// </summary>
        public static void ApplySimulateResult(ref KPInstance inst, in KPWorldPtrs w)
        {
            var bones = w.Bones;
            var writes = w.Writes;
            int begin = inst.BoneStart, end = inst.BoneStart + inst.BoneCount;

            // 第一輪：模擬空間的輸出，預設是動畫姿勢
            for (int i = begin; i < end; i++)
            {
                var b = bones + i;
                if (b->TransformIndex < 0) continue;
                var o = writes + b->TransformIndex;
                o->LocalRotation = b->PoseRotation;
                o->LocalPosition = b->PoseLocation;
                o->Mode = b->ParentIndex < 0 ? 1 : 2;
            }
            for (int i = begin; i < end; i++)
            {
                var b = bones + i;
                if (b->ParentIndex < 0) continue;
                var p = bones + b->ParentIndex;
                if (p->ChildCount <= 1 && p->TransformIndex >= 0)
                {
                    var poseVec = b->PoseLocation - p->PoseLocation;
                    var simVec = b->Location - p->Location;
                    if (all(SafeNormal(poseVec) == SafeNormal(simVec))) continue;
                    writes[p->TransformIndex].LocalRotation = mul(FindBetweenVectors(poseVec, simVec), p->PoseRotation);
                }
                if (b->TransformIndex >= 0) writes[b->TransformIndex].LocalPosition = b->Location;
            }

            // 第二輪：由子往父（索引遞減），父的值還是模擬空間時換算子的區域值，最後跟動畫姿勢混合
            var alpha = saturate(inst.Alpha);
            for (int i = end - 1; i >= begin; i--)
            {
                var b = bones + i;
                if (b->TransformIndex < 0) continue;
                var o = writes + b->TransformIndex;
                var r = w.Reads[b->TransformIndex];
                var worldRot = mul(inst.SimRotation, o->LocalRotation);
                var parentIndex = b->ParentIndex;
                while (parentIndex >= 0 && bones[parentIndex].TransformIndex < 0) parentIndex = bones[parentIndex].ParentIndex;
                if (parentIndex < 0)
                {
                    // 根：父 Transform 不在鏈裡，從讀到的世界／區域旋轉反推父的世界旋轉
                    var parentWorldRot = mul(r.Rotation, conjugate(r.LocalRotation));
                    var localRot = normalize(mul(conjugate(parentWorldRot), worldRot));
                    o->LocalRotation = SlerpUE(r.LocalRotation, localRot, alpha);
                    o->LocalPosition = r.LocalPosition;
                    o->Mode = 1;
                    continue;
                }
                var p = bones + parentIndex;
                var po = writes + p->TransformIndex;
                var worldPos = inst.SimPosition + mul(inst.SimRotation, o->LocalPosition * inst.SimScale);
                var pWorldRot = mul(inst.SimRotation, po->LocalRotation);
                var pWorldPos = inst.SimPosition + mul(inst.SimRotation, po->LocalPosition * inst.SimScale);
                var invP = conjugate(pWorldRot);
                var simLocalRot = normalize(mul(invP, worldRot));
                var parentScale = w.Reads[p->TransformIndex].LossyScale;
                parentScale = select(parentScale, new float3(1e-6f), abs(parentScale) < 1e-6f); // 父骨縮放 0 時不除以 0
                var simLocalPos = mul(invP, worldPos - pWorldPos) / parentScale;
                o->LocalRotation = alpha >= 1f ? simLocalRot : SlerpUE(r.LocalRotation, simLocalRot, alpha);
                o->LocalPosition = lerp(r.LocalPosition, simLocalPos, alpha);
            }
        }
    }
}
