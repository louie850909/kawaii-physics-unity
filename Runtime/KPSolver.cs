// KawaiiPhysics 核心解算的 Burst 版。每個函式的摘要註明對應原作的哪個函式，順序與分支照原作。
// 原作的位置是 UE 的 FVector（double），這裡用 float3，所以跟原作的 golden 值只會在 float 誤差內一致。
using Unity.Mathematics;
using static Unity.Mathematics.math;
using static KawaiiPhysicsUnity.KPMath;

namespace KawaiiPhysicsUnity
{
    /// <summary>解算用到的所有共用陣列。</summary>
    internal unsafe struct KPWorldPtrs
    {
        public KPBone* Bones;
        public KPSphere* Spheres;
        public KPCapsule* Capsules;
        public KPTaperedCapsule* Tapered;
        public KPBox* Boxes;
        public KPPlane* Planes;
        public KPConvex* Convexes;
        public float4* ConvexPlanes;
        public KPConstraint* Constraints;
        public KPSyncBone* SyncBones;
        public KPSyncTarget* SyncTargets;
        public KPForce* Forces;
        public float* Luts;
        public KPWindZone* WindZones;
        public int WindZoneCount;
        public KPReadPose* Reads;
        public KPWriteLocal* Writes;
    }

    internal static unsafe partial class KPSolver
    {
        static bool Has(KPBone* b, int flag) => (b->Flags & flag) != 0;

        // ===================================================================
        // 一幀（SimulateModifyBones 的純計算部分）
        // ===================================================================

        /// <summary>
        /// 前進一幀。前提：骨頭的 PoseLocation／PoseRotation 已是這一幀的姿勢（模擬空間），
        /// inst 的 SimSpaceGravity、SkelCompMove*、Teleport 已設定；外力已換到模擬空間。
        /// 對應原作 SimulateModifyBones（固定子步進與 legacy 兩條路徑）。
        /// </summary>
        public static void StepFrame(ref KPInstance inst, in KPWorldPtrs w, float frameDt)
        {
            if (frameDt <= 0f) return;
            var bones = w.Bones;
            int begin = inst.BoneStart, end = inst.BoneStart + inst.BoneCount;

            PrepareCollisionShapeCaches(ref inst, w);

            for (int i = begin; i < end; i++)
            {
                var b = bones + i;
                // 橋接假骨沒有縱向的父，但要當碰撞代理參加，所以不跳過；根骨頭跳過（跟著姿勢）
                var skip = !Has(b, KPBoneFlags.Bridge) && b->ParentIndex < 0;
                b->Flags = skip ? b->Flags | KPBoneFlags.SkipSimulate : b->Flags & ~KPBoneFlags.SkipSimulate;
                b->CurrentPoseLocation = b->PoseLocation;
                b->CurrentPoseRotation = b->PoseRotation;
                if (inst.PoseInitialized == 0)
                {
                    b->PrevPoseLocation = b->PoseLocation;
                    b->PrevPoseRotation = b->PoseRotation;
                }
            }
            inst.PoseInitialized = 1;

            if (inst.UseFixedSubstep == 0)
            {
                if (inst.DeltaTimeOld <= 0f) inst.DeltaTimeOld = 1f / max(1, inst.TargetFramerate);
                inst.StepDeltaTime = frameDt;
                SimulateOnce(ref inst, w, frameDt);
                inst.DeltaTimeOld = frameDt;
                inst.ConsumeFraction = 1f;
            }
            else
            {
                var fixedDt = 1f / max(1, inst.TargetFramerate);
                var rawElapsed = max(inst.SubstepAccumulator + frameDt, KindaSmallNumber);
                inst.SubstepAccumulator = min(rawElapsed, max(1, inst.MaxSubsteps) * fixedDt);
                var droppedTime = rawElapsed - inst.SubstepAccumulator;
                var numSteps = (int)floor(inst.SubstepAccumulator / fixedDt);
                inst.SubstepAccumulator -= numSteps * fixedDt;
                var moveFrac = fixedDt / rawElapsed;
                inst.ConsumeFraction = clamp((numSteps * fixedDt + droppedTime) / rawElapsed, 0f, 1f);
                var fullMove = inst.SkelCompMoveVector;
                var fullRot = inst.SkelCompMoveRotation;

                inst.StepDeltaTime = fixedDt;
                inst.DeltaTimeOld = fixedDt;
                for (int s = 0; s < numSteps; s++)
                {
                    var alpha = (float)(s + 1) / numSteps;
                    for (int i = begin; i < end; i++)
                    {
                        var b = bones + i;
                        b->PoseLocation = lerp(b->PrevPoseLocation, b->CurrentPoseLocation, alpha);
                        b->PoseRotation = SlerpUE(b->PrevPoseRotation, b->CurrentPoseRotation, alpha);
                    }
                    inst.SkelCompMoveVector = fullMove * moveFrac;
                    inst.SkelCompMoveRotation = SlerpUE(Unity.Mathematics.quaternion.identity, fullRot, moveFrac);
                    SimulateOnce(ref inst, w, fixedDt);
                }
                inst.SkelCompMoveVector = fullMove;
                inst.SkelCompMoveRotation = fullRot;

                for (int i = begin; i < end; i++)
                {
                    var b = bones + i;
                    b->PoseLocation = b->CurrentPoseLocation;
                    b->PoseRotation = b->CurrentPoseRotation;
                }
            }

            for (int i = begin; i < end; i++)
            {
                var b = bones + i;
                b->PrevPoseLocation = b->CurrentPoseLocation;
                b->PrevPoseRotation = b->CurrentPoseRotation;
            }
        }

        /// <summary>對應原作 SimulateOnce。</summary>
        static void SimulateOnce(ref KPInstance inst, in KPWorldPtrs w, float stepDt)
        {
            var bones = w.Bones;
            int begin = inst.BoneStart, end = inst.BoneStart + inst.BoneCount;
            var collisionOnly = inst.CollisionOnlySubdivision != 0;

            // 根骨頭跟著（內插後的）姿勢
            for (int i = begin; i < end; i++)
            {
                var b = bones + i;
                if (b->ParentIndex < 0 && !Has(b, KPBoneFlags.Bridge))
                {
                    b->PrevLocation = b->Location;
                    b->Location = b->PoseLocation;
                }
            }

            var exponent = inst.TargetFramerate * stepDt;
            for (int i = begin; i < end; i++)
            {
                var b = bones + i;
                if (Has(b, KPBoneFlags.SkipSimulate) || Has(b, KPBoneFlags.Bridge)) continue;
                if (collisionOnly && Has(b, KPBoneFlags.InterBone)) continue;
                Simulate(b, bones + b->ParentIndex, ref inst, w, stepDt, exponent);
            }

            // 只當碰撞用的細分假骨：實骨模擬完後放在兩端之間
            if (collisionOnly)
            {
                for (int i = begin; i < end; i++)
                {
                    var b = bones + i;
                    if (!Has(b, KPBoneFlags.InterBone)) continue;
                    b->PrevLocation = b->Location;
                    b->Location = lerp(bones[b->RealParentIndex].Location, bones[b->RealChildIndex].Location, b->InterBoneAlpha);
                }
            }

            // 橋接假骨：放在兩條鏈的端點之間；PoseLocation 暫存內插基準（量推移量用）
            for (int i = begin; i < end; i++)
            {
                var b = bones + i;
                if (!Has(b, KPBoneFlags.Bridge)) continue;
                b->PrevLocation = b->Location;
                b->Location = lerp(bones[b->RealParentIndex].Location, bones[b->RealChildIndex].Location, b->InterBoneAlpha);
                b->PoseLocation = b->Location;
            }

            if (inst.ConstraintItersBefore > 0 && inst.ConstraintCount > 0)
            {
                ResetConstraintLambda(ref inst, w);
                for (int k = 0; k < inst.ConstraintItersBefore; k++) AdjustByBoneConstraints(ref inst, w, stepDt);
            }

            for (int i = begin; i < end; i++)
            {
                var b = bones + i;
                if (Has(b, KPBoneFlags.SkipSimulate)) continue;
                for (int c = 0; c < inst.SphereCount; c++) AdjustBySphere(b, w.Spheres + inst.SphereStart + c);
                for (int c = 0; c < inst.CapsuleCount; c++) AdjustByCapsule(b, w.Capsules + inst.CapsuleStart + c);
                for (int c = 0; c < inst.TaperedCount; c++) AdjustByTaperedCapsule(b, w.Tapered + inst.TaperedStart + c);
                for (int c = 0; c < inst.BoxCount; c++) AdjustByBox(b, w.Boxes + inst.BoxStart + c);
                for (int c = 0; c < inst.PlaneCount; c++) AdjustByPlane(b, w.Planes + inst.PlaneStart + c);
                for (int c = 0; c < inst.ConvexCount; c++) AdjustByConvex(b, w.Convexes + inst.ConvexStart + c, w.ConvexPlanes);
            }

            ApplyBridgeDummyCollisionFeedback(ref inst, w);

            if (inst.ConstraintItersAfter > 0 && inst.ConstraintCount > 0)
            {
                ResetConstraintLambda(ref inst, w);
                for (int k = 0; k < inst.ConstraintItersAfter; k++) AdjustByBoneConstraints(ref inst, w, stepDt);
            }

            RestoreBoneLengthsAndLimits(ref inst, w);
        }

        /// <summary>
        /// 對應原作 Simulate：速度重建 → 減衰 → 風 → 重力 → 外力（速度）→ 積分 → 簡易外力 → 跟隨元件移動 → 外力（位置）→ 剛性拉回。
        /// </summary>
        static void Simulate(KPBone* b, KPBone* parent, ref KPInstance inst, in KPWorldPtrs w, float stepDt, float exponent)
        {
            var s = b->Settings;

            var wind = new float3(0f);
            if (inst.EnableWind != 0 && inst.WindScale != 0f)
                wind = SceneWindVelocity(ref inst, w, b->PoseLocation) * inst.TargetFramerate;

            // ComputeVerletStepVelocity
            var velocity = (b->Location - b->PrevLocation) / max(inst.DeltaTimeOld, KindaSmallNumber);
            b->PrevLocation = b->Location;
            velocity *= 1f - s.Damping;
            velocity += wind;
            if (inst.UseLegacyGravity == 0) velocity += inst.SimSpaceGravity * stepDt;
            else b->Location += 0.5f * inst.SimSpaceGravity * stepDt * stepDt;

            ApplyForcesToVelocity(b, ref inst, w, stepDt, ref velocity);

            b->Location += velocity * stepDt;

            // ApplySimpleExternalForce
            if (!IsNearlyZero(inst.SimSpaceSimpleExternalForce))
                b->Location += inst.SimSpaceSimpleExternalForce * stepDt;

            ApplyWorldMoveFollow(b, ref inst, w);

            ApplyForcesToLocation(b, ref inst, w, stepDt);

            // ApplyStiffnessPull
            var baseLocation = parent->Location + (b->PoseLocation - parent->PoseLocation);
            b->Location += (baseLocation - b->Location) * (1f - pow(1f - s.Stiffness, exponent));
        }

        /// <summary>
        /// 跟隨元件在世界中的移動：元件空間是原作 ApplyWorldMoveFollowNonBaseBone；
        /// 基準骨空間是 Simulate 裡 BaseBoneSpace 的分支；世界空間不需要。瞬移的那一幀不跟。
        /// </summary>
        static void ApplyWorldMoveFollow(KPBone* b, ref KPInstance inst, in KPWorldPtrs w)
        {
            if (inst.Teleport != 0) return;
            var s = b->Settings;
            if (inst.SimulationSpace == (int)KPSimulationSpace.ComponentSpace)
            {
                b->Location += inst.SkelCompMoveVector * (1f - s.WorldDampingLocation);
                b->Location += (mul(inst.SkelCompMoveRotation, b->PrevLocation) - b->PrevLocation) * (1f - s.WorldDampingRotation);
            }
            else if (inst.SimulationSpace == (int)KPSimulationSpace.BaseBoneSpace)
            {
                // 元件空間的移動量換到這一幀的基準骨空間
                var moveWorld = mul(inst.ComponentRotation, inst.SkelCompMoveVector * inst.ComponentScale);
                var moveSim = mul(conjugate(inst.SimRotation), moveWorld) / inst.SimScale;
                b->Location += moveSim * (1f - s.WorldDampingLocation);

                // 上一幀的位置（上一幀的基準骨空間）→ 上一幀的元件空間 → 轉 → 這一幀的基準骨空間
                var prevCS = inst.PrevSimPosition + mul(inst.PrevSimRotation, b->PrevLocation * inst.PrevSimScale);
                var rotatedCS = mul(inst.SkelCompMoveRotation, prevCS);
                var rotatedWorld = inst.ComponentPosition + mul(inst.ComponentRotation, rotatedCS * inst.ComponentScale);
                var rotatedSim = mul(conjugate(inst.SimRotation), rotatedWorld - inst.SimPosition) / inst.SimScale;
                b->Location += (rotatedSim - b->PrevLocation) * (1f - s.WorldDampingRotation);
            }
        }

        // ===================================================================
        // 外力（FKawaiiPhysics_ExternalForce 的 ApplyToVelocity／Apply）
        // ===================================================================

        static float ForceRate(KPForce* f, KPBone* b, in KPWorldPtrs w) => EvalLut(w.Luts, f->RateLut, b->LengthRate, 1f, 1f);

        /// <summary>BoneSpace 外力用的骨頭旋轉：假骨用延伸出它的實骨（ResolveExternalForceBoneTransform）。</summary>
        static quaternion BoneRotationForForce(KPBone* b, in KPWorldPtrs w)
        {
            if ((Has(b, KPBoneFlags.Dummy) || Has(b, KPBoneFlags.InterBone)) && b->RealParentIndex >= 0)
                return w.Bones[b->RealParentIndex].PoseRotation;
            return b->PoseRotation;
        }

        static void ApplyForcesToVelocity(KPBone* b, ref KPInstance inst, in KPWorldPtrs w, float stepDt, ref float3 velocity)
        {
            for (int k = 0; k < inst.ForceCount; k++)
            {
                if (k < 32 && ((b->ForceMask >> k) & 1u) == 0) continue;
                var f = w.Forces + inst.ForceStart + k;
                if (f->Type != KPForceType.Velocity) continue;
                velocity += f->Vector * ForceRate(f, b, w) * stepDt;
            }
        }

        static void ApplyForcesToLocation(KPBone* b, ref KPInstance inst, in KPWorldPtrs w, float stepDt)
        {
            for (int k = 0; k < inst.ForceCount; k++)
            {
                if (k < 32 && ((b->ForceMask >> k) & 1u) == 0) continue;
                var f = w.Forces + inst.ForceStart + k;
                var rate = ForceRate(f, b, w);
                switch (f->Type)
                {
                    case KPForceType.Position:
                    {
                        var v = f->Space == (int)KPForceSpace.BoneSpace ? mul(BoneRotationForForce(b, w), f->Vector) : f->Vector;
                        b->Location += v * rate * f->Scale * stepDt;
                        break;
                    }
                    case KPForceType.ProceduralWind:
                    {
                        var ripple = ProceduralRipple(f->RippleForce, f->RipplePeriod, f->RipplePhaseOffset, f->RippleTipPhaseDelay, f->Time, b->LengthRate);
                        var total = (f->SinesWithoutRipple + ripple) * f->StrengthCycle + f->RandomTerm + f->Gust;
                        var v = f->Space == (int)KPForceSpace.BoneSpace ? mul(BoneRotationForForce(b, w), f->Vector) : f->Vector;
                        b->Location += v * total * rate * stepDt;
                        break;
                    }
                    case KPForceType.SceneWind:
                    {
                        // Wind 外力：每骨頭的風向帶錐角雜訊（同一幀內固定，所以用幀種子＋骨頭索引）
                        SceneWindAt(ref inst, w, b->PoseLocation, out var dir, out var speed);
                        if (speed <= KindaSmallNumber) break;
                        if (f->NoiseAngle > 0f)
                        {
                            var rng = Unity.Mathematics.Random.CreateFromIndex((uint)f->Seed ^ (uint)(b - w.Bones) * 0x9E3779B9u);
                            dir = RandomInCone(ref rng, dir, radians(f->NoiseAngle));
                        }
                        b->Location += dir * speed * rate * f->Scale * stepDt;
                        break;
                    }
                }
            }
        }

        /// <summary>場景風在某個模擬空間位置的風向（模擬空間、單位向量）與風速。對應 FSceneInterface::GetWindParameters。</summary>
        static void SceneWindAt(ref KPInstance inst, in KPWorldPtrs w, float3 simLocation, out float3 dir, out float speed)
        {
            var world = inst.SimPosition + mul(inst.SimRotation, simLocation * inst.SimScale);
            var sum = new float3(0f);
            for (int z = 0; z < w.WindZoneCount; z++)
            {
                var zone = w.WindZones[z];
                if (zone.Spherical == 0)
                {
                    sum += zone.Direction * zone.Speed;
                }
                else
                {
                    var d = world - zone.Position;
                    var dist = length(d);
                    if (dist >= zone.Radius || zone.Radius <= 0f) continue;
                    sum += SafeNormal(d) * zone.Speed * (1f - dist / zone.Radius);
                }
            }
            speed = length(sum);
            dir = speed > 0f ? mul(conjugate(inst.SimRotation), sum / speed) : new float3(0f);
        }

        /// <summary>對應原作 GetWindVelocity：風向加上這一幀的錐角雜訊旋轉，乘風速、WindScale、突風係數 [0,2)。</summary>
        static float3 SceneWindVelocity(ref KPInstance inst, in KPWorldPtrs w, float3 simLocation)
        {
            SceneWindAt(ref inst, w, simLocation, out var dir, out var speed);
            if (inst.WindNoiseAngle > 0f) dir = mul(inst.WindNoiseRotation, dir);
            return dir * speed * inst.WindScale * inst.WindGustFactor;
        }

        // ===================================================================
        // 碰撞（AdjustBy*Collision）
        // ===================================================================

        public static void AdjustBySphere(KPBone* b, KPSphere* sphere)
        {
            if (sphere->Radius <= 0f) return;
            var r = b->Settings.Radius;
            if (sphere->Inner == 0)
            {
                var limit = sphere->Radius + r;
                var delta = b->Location - sphere->Location;
                var distSq = lengthsq(delta);
                if (distSq > limit * limit) return;
                var dist = sqrt(distSq);
                if (dist > KindaSmallNumber) b->Location += (limit - dist) * (delta / dist);
            }
            else
            {
                var limit = max(sphere->Radius - r, 0f);
                var delta = b->Location - sphere->Location;
                var distSq = lengthsq(delta);
                if (distSq < limit * limit) return;
                var dist = sqrt(distSq);
                b->Location = dist > KindaSmallNumber ? sphere->Location + limit * (delta / dist) : sphere->Location;
            }
        }

        public static void AdjustByCapsule(KPBone* b, KPCapsule* cap)
        {
            if (cap->Radius <= 0f || cap->Length <= 0f) return;
            var closest = ClosestPointOnSegment(b->Location, cap->Start, cap->End);
            var limit = b->Settings.Radius + cap->Radius;
            if (lengthsq(b->Location - closest) < limit * limit)
            {
                var pushDir = SafeNormal(b->Location - closest);
                if (IsNearlyZero(pushDir)) pushDir = cap->FallbackPushDir;
                b->Location = closest + pushDir * limit;
            }
        }

        /// <summary>對應 AdjustByTaperedCapsuleCollision（軸上最近點的半徑線性內插；一端包住另一端時退化成球）。</summary>
        public static void AdjustByTaperedCapsule(KPBone* b, KPTaperedCapsule* tc)
        {
            var r0 = max(tc->Radius0, 0f);
            var r1 = max(tc->Radius1, 0f);
            if (r0 <= 0f && r1 <= 0f) return;
            var len = max(tc->Length, 0f);
            float3 closest;
            float radius;
            if (len <= abs(r0 - r1) + KindaSmallNumber)
            {
                var dirSign = r0 >= r1 ? 1f : -1f;
                closest = tc->Location + mul(tc->Rotation, new float3(0f, 1f, 0f)) * len * 0.5f * dirSign;
                radius = max(r0, r1);
            }
            else
            {
                var t = clamp(dot(b->Location - tc->Start, tc->Segment) / tc->SegmentSizeSq, 0f, 1f);
                closest = tc->Start + tc->Segment * t;
                radius = max(lerp(r0, r1, t), 0f);
            }
            var limit = b->Settings.Radius + radius;
            if (lengthsq(b->Location - closest) < limit * limit)
            {
                var pushDir = SafeNormal(b->Location - closest);
                if (IsNearlyZero(pushDir)) pushDir = tc->FallbackPushDir;
                b->Location = closest + pushDir * limit;
            }
        }

        public static void AdjustByBox(KPBone* b, KPBox* box)
        {
            if (all(box->Extent <= 0f)) return; // 簡易世界碰撞的空槽
            var r = b->Settings.Radius;
            var local = mul(conjugate(box->Rotation), b->Location - box->Location);
            var ext = box->Extent;
            var closest = clamp(local, -ext, ext);
            if (lengthsq(local - closest) > r * r) return;
            var pushOut = local - closest;
            var distance = length(pushOut);
            var inside = all(local >= -ext & local <= ext) || distance <= KindaSmallNumber;
            if (inside)
            {
                // 中心在箱子裡：沿最小穿透面推出（穿透深度＋半徑）
                var pen = ext - abs(local);
                int axis = pen.x <= pen.y && pen.x <= pen.z ? 0 : (pen.y <= pen.z ? 1 : 2);
                var sign = local[axis] >= 0f ? 1f : -1f;
                var moved = local;
                moved[axis] = sign * (ext[axis] + r);
                b->Location = mul(box->Rotation, moved) + box->Location;
                return;
            }
            if (distance <= r)
            {
                var moved = closest + SafeNormal(pushOut) * r;
                b->Location = mul(box->Rotation, moved) + box->Location;
            }
        }

        public static void AdjustByPlane(KPBone* b, KPPlane* plane)
        {
            var n = plane->Normal;
            var w = dot(plane->Location, n);
            var planeDot = dot(b->Location, n) - w;
            var pointOnPlane = b->Location - planeDot * n;
            var r = b->Settings.Radius;

            // FMath::SegmentPlaneIntersection(Location, PrevLocation)
            var crossed = false;
            var seg = b->PrevLocation - b->Location;
            var denom = dot(seg, n);
            if (denom != 0f)
            {
                var t = (w - dot(b->Location, n)) / denom;
                crossed = t >= 0f && t <= 1f;
            }
            if (planeDot * planeDot < r * r || crossed)
                b->Location = pointOnPlane + n * r;
        }

        /// <summary>對應 AdjustByConvexCollision：有號距離最大的面就是推出方向（邊角附近略為多推，原作同樣的近似）。</summary>
        public static void AdjustByConvex(KPBone* b, KPConvex* cv, float4* planes)
        {
            if (cv->PlaneCount <= 0) return;
            var r = b->Settings.Radius;
            var local = mul(conjugate(cv->Rotation), b->Location - cv->Location);
            var d = local - clamp(local, cv->LocalBoundsMin, cv->LocalBoundsMax);
            if (lengthsq(d) > r * r) return;
            var maxDist = float.MinValue;
            var best = new float3(0f);
            for (int p = 0; p < cv->PlaneCount; p++)
            {
                var plane = planes[cv->PlaneStart + p];
                var dist = dot(plane.xyz, local) - plane.w;
                if (dist > maxDist)
                {
                    maxDist = dist;
                    best = plane.xyz;
                }
            }
            if (maxDist < r)
                b->Location = mul(cv->Rotation, local + best * (r - maxDist)) + cv->Location;
        }

        /// <summary>對應原作 AdjustByAngleLimit。</summary>
        public static void AdjustByAngleLimit(KPBone* b, KPBone* parent)
        {
            var limitAngle = b->Settings.LimitAngle;
            if (limitAngle == 0f) return;
            var boneDir = SafeNormal(b->Location - parent->Location);
            var poseDir = SafeNormal(b->PoseLocation - parent->PoseLocation);
            var axis = cross(poseDir, boneDir);
            var angle = atan2(length(axis), dot(poseDir, boneDir));
            var over = degrees(angle) - limitAngle;
            if (over > 0f)
            {
                var rotAxis = SafeNormal(axis);
                if (IsNearlyZero(rotAxis)) rotAxis = mul(parent->PoseRotation, new float3(1f, 0f, 0f));
                boneDir = RotateAngleAxisDeg(boneDir, -over, rotAxis);
                b->Location = boneDir * length(b->Location - parent->Location) + parent->Location;
            }
        }

        /// <summary>對應原作 AdjustByPlanarConstraint：投影到過父骨頭、法線為父姿勢某一軸的平面。</summary>
        static void AdjustByPlanarConstraint(KPBone* b, KPBone* parent, int planar)
        {
            if (planar == (int)KPPlanarConstraint.None) return;
            var axis = planar == (int)KPPlanarConstraint.X ? new float3(1f, 0f, 0f)
                : planar == (int)KPPlanarConstraint.Y ? new float3(0f, 1f, 0f) : new float3(0f, 0f, 1f);
            var n = mul(parent->PoseRotation, axis);
            b->Location -= dot(b->Location - parent->Location, n) * n;
        }

        // ===================================================================
        // 骨間約束（XPBD）與橋接假骨
        // ===================================================================

        static void ResetConstraintLambda(ref KPInstance inst, in KPWorldPtrs w)
        {
            for (int c = 0; c < inst.ConstraintCount; c++) w.Constraints[inst.ConstraintStart + c].Lambda = 0f;
        }

        /// <summary>對應原作 AdjustByBoneConstraints（XPBD，質量和固定為 2；根骨頭也會被推，下一步再跟回姿勢）。</summary>
        static void AdjustByBoneConstraints(ref KPInstance inst, in KPWorldPtrs w, float stepDt)
        {
            for (int c = 0; c < inst.ConstraintCount; c++)
            {
                var con = w.Constraints + inst.ConstraintStart + c;
                if (con->Length <= 0f) continue;
                var b1 = w.Bones + con->Bone1;
                var b2 = w.Bones + con->Bone2;
                var delta = b2->Location - b1->Location;
                var len = length(delta);
                if (len <= 0f) continue;
                var constraint = len - con->Length;
                var dt = max(stepDt, KindaSmallNumber);
                var compliance = con->Compliance / (dt * dt);
                var deltaLambda = (constraint - compliance * con->Lambda) / (2f + compliance);
                delta = delta / len * deltaLambda;
                b1->Location += delta;
                b2->Location -= delta;
                con->Lambda += deltaLambda;
            }
        }

        /// <summary>對應原作 ApplyBridgeDummyCollisionFeedback：橋接假骨被推開的量依距離比例轉給兩端的實骨。</summary>
        static void ApplyBridgeDummyCollisionFeedback(ref KPInstance inst, in KPWorldPtrs w)
        {
            if (inst.BridgeFeedbackScale <= 0f) return;
            var bones = w.Bones;
            int begin = inst.BoneStart, end = inst.BoneStart + inst.BoneCount;
            var any = false;
            for (int i = begin; i < end; i++)
            {
                var b = bones + i;
                b->FeedbackPush = new float3(0f);
                b->FeedbackWeight = 0f;
                any |= Has(b, KPBoneFlags.Bridge);
            }
            if (!any) return;

            var collisionOnly = inst.CollisionOnlySubdivision != 0;
            for (int i = begin; i < end; i++)
            {
                var b = bones + i;
                if (!Has(b, KPBoneFlags.Bridge) || Has(b, KPBoneFlags.SkipSimulate)) continue;
                var push = b->Location - b->PoseLocation;
                if (IsNearlyZero(push)) continue;
                AddEndpointPush(bones, b->RealParentIndex, 1f - b->InterBoneAlpha, push, collisionOnly);
                AddEndpointPush(bones, b->RealChildIndex, b->InterBoneAlpha, push, collisionOnly);
            }
            for (int i = begin; i < end; i++)
            {
                var b = bones + i;
                if (b->FeedbackWeight > 0f)
                    b->Location += b->FeedbackPush / max(1f, b->FeedbackWeight) * inst.BridgeFeedbackScale;
            }
        }

        static void AddEndpointPush(KPBone* bones, int endpoint, float weight, float3 push, bool collisionOnly)
        {
            var e = bones + endpoint;
            if (collisionOnly && Has(e, KPBoneFlags.InterBone))
            {
                // 會被重新內插的假骨：推移量照同樣比例分給它兩端的實骨
                var pw = weight * (1f - e->InterBoneAlpha);
                var cw = weight * e->InterBoneAlpha;
                bones[e->RealParentIndex].FeedbackPush += push * pw;
                bones[e->RealParentIndex].FeedbackWeight += pw;
                bones[e->RealChildIndex].FeedbackPush += push * cw;
                bones[e->RealChildIndex].FeedbackWeight += cw;
            }
            else
            {
                e->FeedbackPush += push * weight;
                e->FeedbackWeight += weight;
            }
        }

        /// <summary>對應原作 RestoreBoneLengthsAndLimits：角度限制、平面限制、骨長復原。</summary>
        public static void RestoreBoneLengthsAndLimits(ref KPInstance inst, in KPWorldPtrs w)
        {
            var bones = w.Bones;
            int begin = inst.BoneStart, end = inst.BoneStart + inst.BoneCount;
            var collisionOnly = inst.CollisionOnlySubdivision != 0;
            for (int i = begin; i < end; i++)
            {
                var b = bones + i;
                if (Has(b, KPBoneFlags.SkipSimulate) || Has(b, KPBoneFlags.Bridge)) continue;
                if (collisionOnly && Has(b, KPBoneFlags.InterBone)) continue;
                var parentIndex = b->ParentIndex;
                if (collisionOnly)
                    while (parentIndex >= 0 && Has(bones + parentIndex, KPBoneFlags.InterBone)) parentIndex = bones[parentIndex].ParentIndex;
                var parent = bones + parentIndex;
                AdjustByAngleLimit(b, parent);
                AdjustByPlanarConstraint(b, parent, inst.PlanarConstraint);
                var boneLength = length(b->PoseLocation - parent->PoseLocation);
                b->Location = SafeNormal(b->Location - parent->Location) * boneLength + parent->Location;
            }
            if (collisionOnly)
            {
                for (int i = begin; i < end; i++)
                {
                    var b = bones + i;
                    if (Has(b, KPBoneFlags.InterBone))
                        b->Location = lerp(bones[b->RealParentIndex].Location, bones[b->RealChildIndex].Location, b->InterBoneAlpha);
                }
            }
        }

        /// <summary>對應各 Limit 的 UpdateRuntimeCache。膠囊軸與平面法線用區域 +Y（Unity 慣例），推出備用方向用區域 +X。</summary>
        public static void PrepareCollisionShapeCaches(ref KPInstance inst, in KPWorldPtrs w)
        {
            var up = new float3(0f, 1f, 0f);
            var right = new float3(1f, 0f, 0f);
            for (int c = 0; c < inst.CapsuleCount; c++)
            {
                var cap = w.Capsules + inst.CapsuleStart + c;
                var axis = mul(cap->Rotation, up);
                cap->Start = cap->Location + axis * cap->Length * 0.5f;
                cap->End = cap->Location - axis * cap->Length * 0.5f;
                cap->FallbackPushDir = mul(cap->Rotation, right);
            }
            for (int c = 0; c < inst.TaperedCount; c++)
            {
                var tc = w.Tapered + inst.TaperedStart + c;
                var axis = mul(tc->Rotation, up);
                tc->Start = tc->Location + axis * tc->Length * 0.5f;
                tc->Segment = -axis * tc->Length;
                tc->SegmentSizeSq = lengthsq(tc->Segment);
                tc->FallbackPushDir = mul(tc->Rotation, right);
            }
            for (int c = 0; c < inst.PlaneCount; c++)
            {
                var plane = w.Planes + inst.PlaneStart + c;
                plane->Normal = mul(plane->Rotation, up);
            }
        }
    }
}
