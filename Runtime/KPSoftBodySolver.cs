// 軟組織的解算：每根骨頭是以動畫姿勢（錨點）為中心的阻尼彈簧（三軸同一個頻率與阻尼比）。
// 彈簧用解析解推進：任何時間步都穩定，頻率與衰減跟參數一致；子步進只影響碰撞與位移上限的精度。
// 錨點的移動拆成「角色根的移動」與「動畫」兩部分，各自乘慣性倍率：1＝真實慣性，0＝跟著走、不晃。
// 讀 → 解（一個群組一份：骨頭有祖先關係的元件同一組，依階層深度解）→ 寫，跟 Chain 一樣用 PlayerLoop 驅動（KPSoftBodySystem）。
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Jobs;
using static Unity.Mathematics.math;

namespace KawaiiPhysicsUnity
{
    internal static class KPSoftBodySolver
    {
        /// <summary>
        /// 阻尼彈簧推進 h 秒的係數：x' = pp·x + pv·v、v' = vp·x + vv·v（x 是離平衡位置的位移）。
        /// omega＝角頻率（2π × 頻率），zeta＝阻尼比。
        /// </summary>
        public static void SpringCoefficients(float omega, float zeta, float h, out float pp, out float pv, out float vp, out float vv)
        {
            const float eps = 1e-4f;
            if (!(h > 0f))
            {
                pp = 1f;
                pv = 0f;
                vp = 0f;
                vv = 1f;
                return;
            }
            if (zeta > 1f + eps)
            {
                // 過阻尼：x = c1·e^(z1·t) + c2·e^(z2·t)。z2 用有理化的式子，阻尼比很大時不會相消
                var root = sqrt(zeta * zeta - 1f);
                var z1 = -omega * (zeta + root);
                var z2 = -omega / (zeta + root);
                var e1 = exp(z1 * h);
                var e2 = exp(z2 * h);
                var inv = 1f / (z2 - z1);
                pp = (z2 * e1 - z1 * e2) * inv;
                pv = (e2 - e1) * inv;
                vp = z1 * z2 * (e1 - e2) * inv;
                vv = (z2 * e2 - z1 * e1) * inv;
            }
            else if (zeta < 1f - eps)
            {
                // 欠阻尼：x = e^(-ζωt)·(x0·cos(αt) + (v0 + ζω·x0)/α·sin(αt))，α = ω√(1-ζ²)
                var wz = omega * zeta;
                var alpha = omega * sqrt(1f - zeta * zeta);
                var e = exp(-wz * h);
                sincos(alpha * h, out var s, out var c);
                var es = e * s / alpha;
                pp = e * c + wz * es;
                pv = es;
                vp = -e * s * alpha - wz * wz * es;
                vv = e * c - wz * es;
            }
            else
            {
                // 臨界阻尼：x = e^(-ωt)·(x0 + (v0 + ω·x0)·t)
                var e = exp(-omega * h);
                var te = h * e;
                pp = e + omega * te;
                pv = te;
                vp = -omega * omega * te;
                vv = e - omega * te;
            }
        }

        /// <summary>
        /// 一個群組：先準備每個元件的這一幀（角色根、重置、彈簧係數），再依階層深度解每根骨頭（祖先先解，
        /// 子孫的錨點套上祖先這一幀的位移，元件不同也一樣），最後記下角色根與這一幀的輸出設定。
        /// 骨頭的軸向一律從世界矩陣取（正規化的欄），不用 Unity 的世界旋轉：負縮放（鏡像）的階層裡兩者不一致。
        /// </summary>
        public static void SolveGroup(int index, NativeArray<KPSoftGroup> groups, NativeArray<int> groupBodies, NativeArray<int> groupBones,
            NativeArray<KPSoftBodyData> bodies, NativeArray<KPSoftBone> bones, NativeArray<KPSoftRead> reads,
            NativeArray<KPSoftLocal> writes, NativeArray<KPSoftCollider> colliders, float dt)
        {
            if (!(dt > 0f && dt < float.PositiveInfinity)) dt = 0f; // 負值、NaN、無限大：當成暫停
            var g = groups[index];
            for (int k = 0; k < g.BodyCount; k++)
            {
                var bi = groupBodies[g.BodyStart + k];
                var body = bodies[bi];
                BeginBody(ref body, reads, dt);
                bodies[bi] = body;
            }
            for (int k = 0; k < g.BoneCount; k++) SolveBone(groupBones[g.BoneStart + k], bodies, bones, reads, writes, colliders, dt);
            for (int k = 0; k < g.BodyCount; k++)
            {
                var bi = groupBodies[g.BodyStart + k];
                var body = bodies[bi];
                if (reads[body.RootIndex].Valid != 0)
                {
                    body.PrevRootPosition = body.RootPosition;
                    body.PrevRootFrame = body.RootFrame;
                    body.RootInitialized = 1;
                    body.LastDeltaTime = dt;
                    body.PrevAlpha = body.Alpha;
                    body.PrevTilt = body.Tilt;
                    body.PrevStretchAxis = body.StretchAxis;
                    body.PrevStretchAmount = body.StretchAmount;
                    body.PrevVolumePreservation = body.VolumePreservation;
                    body.PrevLimitMin = body.LimitMin;
                    body.PrevLimitMax = body.LimitMax;
                }
                bodies[bi] = body;
            }
        }

        static void BeginBody(ref KPSoftBodyData body, NativeArray<KPSoftRead> reads, float dt)
        {
            var root = reads[body.RootIndex];
            if (root.Valid == 0)
            {
                // 角色根不能用（已刪除等）：整個元件這一幀不寫，下次重新初始化
                body.FrameSkip = 1;
                body.RootInitialized = 0;
                return;
            }
            body.FrameSkip = body.Enabled == 0 ? 1 : 0;
            body.RootPosition = root.World.c3.xyz;
            body.RootFrame = Frame(root.World);
            var started = body.RootInitialized != 0;
            var reset = body.ResetRequested != 0 || !started;
            body.ResetRequested = 0;
            body.MoveRotation = RotationOf(mul(body.RootFrame, transpose(body.PrevRootFrame)));
            // 角色根的縮放正負翻了（左右翻轉角色）：前後的框架沒有旋轉可以接上，當成瞬移（不受瞬移門檻影響）
            if (!reset && determinant(body.RootFrame) * determinant(body.PrevRootFrame) < 0f) reset = true;
            if (!reset && body.TeleportDistance > 0f && distance(body.RootPosition, body.PrevRootPosition) > body.TeleportDistance) reset = true;
            if (!reset && body.TeleportAngle > 0f && Angle(body.MoveRotation) > body.TeleportAngle) reset = true;
            body.FrameReset = reset ? 1 : 0;
            // 輸出的設定改了（alpha、傾斜、伸縮）：子孫用上一幀的設定量祖先的速度，那一段跳動精確扣掉
            body.FrameOutputChanged = started && (body.Alpha != body.PrevAlpha || body.Tilt != body.PrevTilt || body.StretchAxis != body.PrevStretchAxis ||
                                                  body.StretchAmount != body.PrevStretchAmount || body.VolumePreservation != body.PrevVolumePreservation) ? 1 : 0;
            // 位移上限改了：位移可能一幀內被夾住，子孫重新取基準
            body.FrameLimitChanged = started && (any(body.LimitMin != body.PrevLimitMin) || any(body.LimitMax != body.PrevLimitMax)) ? 1 : 0;
            body.Steps = dt > 0f ? clamp((int)ceil(dt * body.TargetFramerate - 1e-3f), 1, max(1, body.MaxSubsteps)) : 0;
            SpringCoefficients(body.Omega, body.DampingRatio, body.Steps > 0 ? dt / body.Steps : 0f, out body.Pp, out body.Pv, out body.Vp, out body.Vv);
            body.CarryAnim = saturate(1f - body.AnimationInertia);
            body.CarryMove = saturate(1f - body.MovementInertia);
        }

        static void SolveBone(int bi, NativeArray<KPSoftBodyData> bodies, NativeArray<KPSoftBone> bones, NativeArray<KPSoftRead> reads,
            NativeArray<KPSoftLocal> writes, NativeArray<KPSoftCollider> colliders, float dt)
        {
            var b = bones[bi];
            var body = bodies[b.Body];
            var r = reads[b.TransformIndex];
            if (body.FrameSkip != 0 || r.Valid == 0)
            {
                Skip(bones, writes, bi);
                return;
            }
            var identity = Unity.Mathematics.quaternion.identity;

            // 祖先軟組織骨頭這一幀的變化（中間沒模擬的骨頭跟著一起動；位置、旋轉、伸縮都在這個仿射變換裡）
            var delta = Unity.Mathematics.float4x4.identity;
            var deltaInv = Unity.Mathematics.float4x4.identity;
            var deltaContinuous = Unity.Mathematics.float4x4.identity;
            var ancestorJump = false;
            var ancestorSettingsChanged = false;
            if (b.ParentSoft >= 0)
            {
                var p = bones[b.ParentSoft];
                delta = p.Delta;
                deltaInv = p.DeltaInverse;
                ancestorJump = p.Discontinuity != 0;
                ancestorSettingsChanged = p.ChainChanged != 0;
                deltaContinuous = ancestorSettingsChanged ? p.DeltaContinuous : p.Delta;
            }
            var basePos = r.World.c3.xyz;
            var baseFrame = Frame(r.World);
            var anchorMatrix = mul(delta, r.World);
            var anchor = anchorMatrix.c3.xyz;
            var anchorFrame = Frame(anchorMatrix);
            var parentMatrix = mul(delta, r.ParentMatrix);
            var parentFrame = Frame(parentMatrix);
            var rootPos = body.RootPosition;
            var rootFrame = body.RootFrame;
            var invRoot = transpose(rootFrame);

            // 重力參考還沒記（啟用時矩陣退化，例如縮放 0）：第一次能用的時候用現在的父骨記
            if (b.RestPending != 0)
            {
                b.RestParentInRoot = mul(invRoot, Frame(r.ParentMatrix));
                b.RestPending = 0;
            }

            // 平衡位置：重力＋額外加速度。重力相對靜止姿勢時扣掉靜止姿勢已經包含的那份
            var force = body.Gravity + body.ExternalAcceleration;
            if (body.GravityRelativeToRest != 0) force -= mul(mul(parentFrame, transpose(b.RestParentInRoot)), body.Gravity);
            var eq = force / (body.Omega * body.Omega);
            var baseInRoot = mul(invRoot, basePos - rootPos);
            var baseFrameInRoot = mul(invRoot, baseFrame);
            var jiggle = anchor - basePos;
            // 祖先這一幀的輸出設定改了：用上一幀的設定算的錨點量速度（設定造成的那一段跳動不算加速）
            var jiggleForVelocity = ancestorSettingsChanged ? transform(deltaContinuous, basePos) - basePos : jiggle;

            // 上一幀沒在模擬（新加入、剛略過）：自己對子孫的變化本來是 I，初始化造成的跳動量已知（子孫精確扣掉）。
            // 重建時被重置、但上一幀還在模擬的（WasRunning）不算：跳動量不知道，跟重置一樣
            var newInit = b.Initialized == 0 && b.WasRunning == 0;
            b.WasRunning = 0;
            var reinit = body.FrameReset != 0 || b.Initialized == 0;
            // 骨頭自己的軸向正負翻了（縮放的正負變了）：前後接不上，重新初始化
            if (!reinit && determinant(baseFrameInRoot) * determinant(b.PrevBaseFrameInRoot) < 0f) reinit = true;
            // 祖先這一幀重置、初始化、略過或上限改了（跳動量不知道）：祖先晃動那一份重新取基準
            if (reinit || ancestorJump) b.JiggleStage = 0;
            if (reinit)
            {
                // 跟著錨點一起動、停在平衡位置。錨點這時的速度不知道，下一幀只記錄、不當成加速
                b.Offset = all(isfinite(eq)) ? eq : new float3(0f);
                b.Velocity = new float3(0f);
                Limit(ref b.Offset, ref b.Velocity, anchorFrame, body.LimitMin, body.LimitMax);
                b.Initialized = 1;
            }
            else if (dt > 0f)
            {
                // 錨點這一幀的速度拆三份：角色根的移動（predicted）、動畫、祖先的晃動
                var predicted = rootPos + mul(rootFrame, b.PrevBaseInRoot);
                var vMove = (predicted - b.PrevBase) / dt;
                var vAnim = (basePos - predicted) / dt;
                var vJiggle = (jiggleForVelocity - b.PrevJiggle) / dt;
                if (b.Initialized == 2)
                {
                    // 不跟慣性的部分：位移與速度跟著錨點一起轉（角色轉向、動畫轉動）
                    if (body.CarryAnim > 0f || body.CarryMove > 0f)
                    {
                        var animRotation = RotationOf(mul(baseFrame, transpose(mul(rootFrame, b.PrevBaseFrameInRoot))));
                        var carry = mul(slerp(identity, animRotation, body.CarryAnim), slerp(identity, body.MoveRotation, body.CarryMove));
                        b.Offset = mul(carry, b.Offset);
                        b.Velocity = mul(carry, b.Velocity);
                    }
                    // 錨點的速度變了多少，質點相對錨點的速度就反向變多少（慣性）
                    b.Velocity -= body.MovementInertia * (vMove - b.PrevMoveVelocity) + body.AnimationInertia * (vAnim - b.PrevAnimVelocity);
                }
                // 祖先的晃動一律是真實慣性；重新取基準之後的第一幀只記錄速度
                if (b.JiggleStage == 2) b.Velocity -= vJiggle - b.PrevJiggleVelocity;
                if (b.JiggleStage >= 1)
                {
                    b.PrevJiggleVelocity = vJiggle;
                    b.JiggleStage = 2;
                }
                b.PrevMoveVelocity = vMove;
                b.PrevAnimVelocity = vAnim;
                b.Initialized = 2;
            }
            b.Velocity += b.PendingImpulse;
            b.PendingImpulse = new float3(0f);

            for (int s = 0; s < body.Steps; s++)
            {
                var x = b.Offset - eq;
                b.Offset = body.Pp * x + body.Pv * b.Velocity + eq;
                b.Velocity = body.Vp * x + body.Vv * b.Velocity;
                // 碰撞優先：上限之後再推出碰撞體（骨頭可以被推到上限外）；否則上限最後（碰撞體可能穿進骨頭）
                if (body.CollisionPriority != 0) Limit(ref b.Offset, ref b.Velocity, anchorFrame, body.LimitMin, body.LimitMax);
                if (body.ColliderCount > 0)
                {
                    var pos = anchor + b.Offset;
                    for (int c = 0; c < body.ColliderCount; c++) Collide(ref pos, ref b.Velocity, body.Radius, colliders[body.ColliderStart + c]);
                    b.Offset = pos - anchor;
                }
                if (body.CollisionPriority == 0) Limit(ref b.Offset, ref b.Velocity, anchorFrame, body.LimitMin, body.LimitMax);
            }

            if (!all(isfinite(b.Offset)) || !all(isfinite(b.Velocity)))
            {
                // 極端輸入（NaN 的衝量、加速度等）：回到平衡位置重來
                b.Offset = all(isfinite(eq)) ? eq : new float3(0f);
                b.Velocity = new float3(0f);
                Limit(ref b.Offset, ref b.Velocity, anchorFrame, body.LimitMin, body.LimitMax);
                b.Initialized = 1;
                b.JiggleStage = 0;
                reinit = true;
            }
            b.PrevBase = basePos;
            b.PrevBaseInRoot = baseInRoot;
            b.PrevBaseFrameInRoot = baseFrameInRoot;
            b.PrevJiggle = jiggle;
            if (b.JiggleStage == 0) b.JiggleStage = 1;
            // 跳動量不知道的事件（模擬中的骨頭重置、上限改了，或祖先有這種事件）：子孫也要重新取基準
            b.Discontinuity = (reinit && !newInit) || ancestorJump || body.FrameLimitChanged != 0 ? 1 : 0;

            // 輸出：平移、依位移傾斜、沿軸壓扁與拉伸
            var local = Output(r, anchor, anchorFrame, parentMatrix, parentFrame, deltaInv, b.Offset * body.Alpha,
                body.Tilt, body.StretchAxis, body.StretchAmount, body.VolumePreservation, body.LimitMin, body.LimitMax);
            writes[b.TransformIndex] = local;

            // 子孫用：讀取時的世界 → 寫回後的世界（4×4 仿射，含伸縮）。上一幀的也留著（重建時換祖先用）
            b.PrevDelta = b.Delta;
            b.PrevDeltaInverse = b.DeltaInverse;
            var trsRead = Unity.Mathematics.float4x4.TRS(r.LocalPosition, r.LocalRotation, r.LocalScale);
            var invTrsRead = InverseTRS(r.LocalPosition, r.LocalRotation, r.LocalScale);
            b.Delta = mul(parentMatrix, mul(Unity.Mathematics.float4x4.TRS(local.LocalPosition, local.LocalRotation, local.LocalScale), mul(invTrsRead, r.ParentInverse)));
            b.DeltaInverse = mul(r.ParentMatrix, mul(trsRead, mul(InverseTRS(local.LocalPosition, local.LocalRotation, local.LocalScale), mul(r.ParentInverse, deltaInv))));

            // 這根骨頭或祖先的輸出設定這一幀改了、或這根骨頭剛開始模擬：另外算「照上一幀」的變換，給子孫量速度用
            var chainChanged = body.FrameOutputChanged != 0 || ancestorSettingsChanged || newInit;
            b.ChainChanged = chainChanged && b.Discontinuity == 0 ? 1 : 0;
            if (b.ChainChanged != 0 && newInit) b.DeltaContinuous = deltaContinuous; // 上一幀自己的變化是 I
            else if (b.ChainChanged != 0)
            {
                var contInv = inverse(deltaContinuous);
                var contAnchorMatrix = mul(deltaContinuous, r.World);
                var contParentMatrix = mul(deltaContinuous, r.ParentMatrix);
                var cont = Output(r, contAnchorMatrix.c3.xyz, Frame(contAnchorMatrix), contParentMatrix, Frame(contParentMatrix), contInv,
                    b.Offset * body.PrevAlpha, body.PrevTilt, body.PrevStretchAxis, body.PrevStretchAmount, body.PrevVolumePreservation,
                    body.LimitMin, body.LimitMax);
                b.DeltaContinuous = mul(contParentMatrix, mul(Unity.Mathematics.float4x4.TRS(cont.LocalPosition, cont.LocalRotation, cont.LocalScale),
                    mul(invTrsRead, r.ParentInverse)));
            }
            bones[bi] = b;
        }

        /// <summary>骨頭寫回的區域姿勢：錨點＋位移 d，依位移傾斜（tilt），沿軸伸縮（stretch）。</summary>
        static KPSoftLocal Output(in KPSoftRead r, float3 anchor, float3x3 anchorFrame, float4x4 parentMatrix, float3x3 parentFrame, float4x4 deltaInv,
            float3 d, float tilt, int stretchAxis, float stretchAmount, float volumePreservation, float3 limitMin, float3 limitMax)
        {
            var localRot = r.LocalRotation;
            if (tilt > 0f)
            {
                var arm = anchor - parentMatrix.c3.xyz;
                if (lengthsq(arm) > 1e-10f)
                {
                    // 世界空間的傾斜換到父骨的軸向（鏡像時也成立）：區域旋轉 ← Fᵀ·傾斜·F × 原本的區域旋轉
                    var t = new float3x3(slerp(Unity.Mathematics.quaternion.identity, KPMath.FindBetweenVectors(arm, arm + d), tilt));
                    localRot = normalize(mul(RotationOf(mul(transpose(parentFrame), mul(t, parentFrame))), localRot));
                }
            }
            var scale = r.LocalScale;
            if (stretchAmount != 0f && stretchAxis >= 0 && stretchAxis < 3)
            {
                var along = dot(d, anchorFrame[stretchAxis]);
                var lim = along >= 0f ? limitMax[stretchAxis] : -limitMin[stretchAxis];
                var ratio = lim > 1e-6f ? clamp(along / lim, -1f, 1f) : 0f;
                var s = max(1f + stretchAmount * ratio, 0.01f);
                var factor = new float3(pow(s, -0.5f * volumePreservation));
                factor[stretchAxis] = s;
                scale *= factor;
            }
            // 父 Transform 這一幀也跟著祖先動了：先退回讀取時的父空間，再換成區域座標
            return new KPSoftLocal
            {
                LocalPosition = transform(r.ParentInverse, transform(deltaInv, anchor + d)),
                LocalRotation = localRot,
                LocalScale = scale,
                Mode = 1,
            };
        }

        /// <summary>TRS(p, q, s) 的反矩陣（s 不能有 0）。</summary>
        public static float4x4 InverseTRS(float3 p, quaternion q, float3 s)
        {
            var rt = new float3x3(conjugate(q));
            var inv = 1f / s;
            // diag(1/s) × Rᵀ：第 i 列乘 1/s[i]
            var m = new float3x3(rt.c0 * inv, rt.c1 * inv, rt.c2 * inv);
            return new float4x4(m, -mul(m, p));
        }

        /// <summary>
        /// 矩陣的軸向：前兩欄依序做 Gram–Schmidt 正規化，第三軸是 ±x×y（正負跟原矩陣的行列式一樣，鏡像時取反），
        /// 所以 X、Y、Z 就是這個 Transform 實際的區域軸在世界的方向。
        /// </summary>
        public static float3x3 Frame(float4x4 m)
        {
            var x = normalizesafe(m.c0.xyz, new float3(1f, 0f, 0f));
            var y = m.c1.xyz - dot(m.c1.xyz, x) * x;
            y = normalizesafe(y, normalizesafe(cross(m.c2.xyz, x), new float3(0f, 1f, 0f)));
            var z = cross(x, y);
            if (determinant(new float3x3(m)) < 0f) z = -z;
            return new float3x3(x, y, z);
        }

        /// <summary>矩陣是不是退化（某一欄接近 0，或三欄接近共面）：軸向與鏡像的正負都不可靠。</summary>
        public static bool Degenerate(float3x3 m)
        {
            var l0 = length(m.c0);
            var l1 = length(m.c1);
            var l2 = length(m.c2);
            if (!(l0 > 1e-12f && l1 > 1e-12f && l2 > 1e-12f)) return true;
            return !(abs(determinant(m)) > 1e-6f * l0 * l1 * l2);
        }

        /// <summary>正交矩陣的旋轉；鏡像（行列式為負）或退化時當成不轉。</summary>
        static quaternion RotationOf(float3x3 m) =>
            determinant(m) > 0.5f ? normalize(new quaternion(m)) : Unity.Mathematics.quaternion.identity;

        /// <summary>
        /// 這一幀不寫，下次重新初始化。沒寫回的骨頭照樣跟著祖先動：給子孫的變化是祖先的（自己的那一份是 I），
        /// 子孫重新取基準（自己的位移消失了）。恢復那一幀「上一幀自己的變化是 I」就成立。
        /// </summary>
        static void Skip(NativeArray<KPSoftBone> bones, NativeArray<KPSoftLocal> writes, int bi)
        {
            var b = bones[bi];
            writes[b.TransformIndex] = default;
            b.Initialized = 0;
            b.WasRunning = 0;
            b.JiggleStage = 0;
            b.Discontinuity = 1;
            b.ChainChanged = 0;
            b.PendingImpulse = new float3(0f);
            b.PrevDelta = b.Delta;
            b.PrevDeltaInverse = b.DeltaInverse;
            if (b.ParentSoft >= 0)
            {
                var p = bones[b.ParentSoft];
                b.Delta = p.Delta;
                b.DeltaInverse = p.DeltaInverse;
            }
            else
            {
                b.Delta = Unity.Mathematics.float4x4.identity;
                b.DeltaInverse = Unity.Mathematics.float4x4.identity;
            }
            bones[bi] = b;
        }

        /// <summary>旋轉的角度（弧度）。</summary>
        static float Angle(quaternion q) => 2f * acos(min(abs(q.value.w), 1f));

        /// <summary>
        /// 位移限制在錨點區域軸向（frame 的三欄）的橢球裡：各象限的半軸取 limitMax（正向）或 -limitMin（負向），
        /// 半軸為 0 的軸鎖住。超出時沿徑向拉回，往外的速度去掉。
        /// </summary>
        public static void Limit(ref float3 offset, ref float3 velocity, float3x3 frame, float3 limitMin, float3 limitMax)
        {
            var inv = transpose(frame);
            var o = mul(inv, offset);
            var v = mul(inv, velocity);
            var axes = select(-limitMin, limitMax, o >= 0f);
            var locked = axes <= 1e-6f;
            o = select(o, new float3(0f), locked);
            v = select(v, new float3(0f), locked);
            var safe = select(axes, new float3(1f), locked);
            var len2 = lengthsq(o / safe);
            if (len2 > 1f)
            {
                o /= sqrt(len2);
                var n = normalizesafe(o / (safe * safe));
                var vn = dot(v, n);
                if (vn > 0f) v -= vn * n;
            }
            offset = mul(frame, o);
            velocity = mul(frame, v);
        }

        /// <summary>質點（半徑 r）推出碰撞體；推出方向上往內的速度去掉。</summary>
        public static void Collide(ref float3 p, ref float3 v, float r, in KPSoftCollider c)
        {
            switch (c.Shape)
            {
                case KPSoftCollider.Sphere:
                {
                    var d = p - c.A;
                    var dist = length(d);
                    if (c.Inner != 0)
                    {
                        var limit = max(c.RadiusA - r, 0f);
                        if (dist > limit) PushOut(ref p, ref v, d / dist * (limit - dist));
                    }
                    else
                    {
                        var limit = c.RadiusA + r;
                        if (dist < limit) PushOut(ref p, ref v, (dist > 1e-6f ? d / dist : new float3(0f, 1f, 0f)) * (limit - dist));
                    }
                    break;
                }
                case KPSoftCollider.Capsule:
                {
                    var seg = c.A - c.B;
                    var lsq = lengthsq(seg);
                    var t = lsq > 1e-12f ? saturate(dot(p - c.B, seg) / lsq) : 0f;
                    var d = p - (c.B + seg * t);
                    var dist = length(d);
                    var limit = lerp(c.RadiusB, c.RadiusA, t) + r;
                    if (dist < limit) PushOut(ref p, ref v, (dist > 1e-6f ? d / dist : Perpendicular(seg)) * (limit - dist));
                    break;
                }
                case KPSoftCollider.Box:
                {
                    var q = mul(conjugate(c.Rotation), p - c.A);
                    var e = c.Extent;
                    var closest = clamp(q, -e, e);
                    var d = q - closest;
                    var dsq = lengthsq(d);
                    float3 target;
                    if (dsq > 1e-12f)
                    {
                        if (dsq >= r * r) break;
                        target = closest + d * (r / sqrt(dsq));
                    }
                    else
                    {
                        // 中心在箱子裡：從最近的面推出去
                        var gap = e - abs(q);
                        target = q;
                        var k = gap.x <= gap.y && gap.x <= gap.z ? 0 : gap.y <= gap.z ? 1 : 2;
                        target[k] = (q[k] >= 0f ? 1f : -1f) * (e[k] + r);
                    }
                    PushOut(ref p, ref v, mul(c.Rotation, target - q));
                    break;
                }
                case KPSoftCollider.Plane:
                {
                    var dist = dot(p - c.A, c.B);
                    if (dist < r) PushOut(ref p, ref v, c.B * (r - dist));
                    break;
                }
            }
        }

        static void PushOut(ref float3 p, ref float3 v, float3 delta)
        {
            var len = length(delta);
            if (!(len > 0f)) return;
            p += delta;
            var n = delta / len;
            var vn = dot(v, n);
            if (vn < 0f) v -= vn * n;
        }

        static float3 Perpendicular(float3 axis)
        {
            var c = cross(axis, new float3(0f, 1f, 0f));
            if (lengthsq(c) < 1e-12f) c = cross(axis, new float3(1f, 0f, 0f));
            return normalizesafe(c, new float3(1f, 0f, 0f));
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct KPSoftReadJob : IJobParallelForTransform
    {
        [WriteOnly] public NativeArray<KPSoftRead> Reads;

        public void Execute(int index, TransformAccess t)
        {
            if (!t.isValid)
            {
                Reads[index] = default;
                return;
            }
            var l2w = (float4x4)t.localToWorldMatrix;
            var lp = (float3)t.localPosition;
            var lr = (quaternion)t.localRotation;
            var ls = (float3)t.localScale;
            var read = new KPSoftRead
            {
                World = l2w,
                LocalPosition = lp,
                LocalRotation = lr,
                LocalScale = ls,
            };
            // 父的矩陣用自己的矩陣與區域 TRS 反推：L2W = 父 × TRS → 父 = L2W × TRS⁻¹、父⁻¹ = TRS × W2L
            // 退化（某一欄接近 0、三欄接近共面）的矩陣軸向不可靠：這一幀不用（相對門檻，等比的小縮放不算）
            var valid = all(abs(ls) > 1e-8f) && !KPSoftBodySolver.Degenerate(new float3x3(l2w));
            if (valid)
            {
                read.ParentMatrix = mul(l2w, KPSoftBodySolver.InverseTRS(lp, lr, ls));
                read.ParentInverse = mul(Unity.Mathematics.float4x4.TRS(lp, lr, ls), (float4x4)t.worldToLocalMatrix);
                valid = Finite(l2w) && all(isfinite(lr.value)) && Finite(read.ParentMatrix) && Finite(read.ParentInverse);
            }
            read.Valid = valid ? 1 : 0;
            Reads[index] = read;
        }

        static bool Finite(float4x4 m) => all(isfinite(m.c0)) && all(isfinite(m.c1)) && all(isfinite(m.c2)) && all(isfinite(m.c3));
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct KPSoftSolveJob : Unity.Jobs.IJobParallelFor
    {
        [ReadOnly] public NativeArray<KPSoftGroup> Groups;
        [ReadOnly] public NativeArray<int> GroupBodies;
        [ReadOnly] public NativeArray<int> GroupBones;
        [NativeDisableParallelForRestriction] public NativeArray<KPSoftBodyData> Bodies;
        [NativeDisableParallelForRestriction] public NativeArray<KPSoftBone> Bones;
        [ReadOnly] public NativeArray<KPSoftRead> Reads;
        [NativeDisableParallelForRestriction] public NativeArray<KPSoftLocal> Writes;
        [ReadOnly] public NativeArray<KPSoftCollider> Colliders;
        public float DeltaTime;

        public void Execute(int index) =>
            KPSoftBodySolver.SolveGroup(index, Groups, GroupBodies, GroupBones, Bodies, Bones, Reads, Writes, Colliders, DeltaTime);
    }

    /// <summary>Mode 1 的寫回區域位置、旋轉、縮放（寫回與還原共用）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct KPSoftApplyJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<KPSoftLocal> Locals;

        public void Execute(int index, TransformAccess t)
        {
            var w = Locals[index];
            if (w.Mode == 0 || !t.isValid) return;
            t.SetLocalPositionAndRotation(w.LocalPosition, w.LocalRotation);
            t.localScale = w.LocalScale;
        }
    }
}
