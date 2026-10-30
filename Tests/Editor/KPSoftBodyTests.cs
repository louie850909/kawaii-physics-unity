// 軟組織（KawaiiPhysicsSoftBody）：物理正確性（平衡、頻率與衰減、上限、慣性、衝量、體積、碰撞、傾斜、階層）
// 與生命週期（加減互不影響、停用還原、瞬移、極端輸入、0 GC、後端一致、Inspector、事件）。
// 「審查後追加」那一段是獨立審查指出的缺口：期望值另外算、移動中的重置、跨元件階層等。
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace KawaiiPhysicsUnity.Tests
{
    public class KPSoftBodyTests
    {
        const float Dt = 1f / 60f;
        readonly List<GameObject> created = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            KPSystem.Shutdown();
            KPSystem.Backend = KPBackend.BurstParallel;
        }

        [TearDown]
        public void TearDown()
        {
            KPSystem.Shutdown();
            KPSystem.Backend = KPBackend.BurstParallel;
            foreach (var go in created)
                if (go != null) Object.DestroyImmediate(go);
            created.Clear();
        }

        // ---- 測試用的角色 ----------------------------------------------------

        public sealed class Rig
        {
            public Transform Root, Chest, BustL, BustR;
            public KawaiiPhysicsSoftBody Body;
        }

        /// <summary>角色根 → chest（1.3 m）→ 左右胸（前方 0.1 m）。元件放在根上，限制放寬到 ±0.1 m。</summary>
        Rig Build(string name, Vector3 position, bool register = true)
        {
            var root = new GameObject(name);
            created.Add(root);
            root.transform.position = position;
            var rig = new Rig { Root = root.transform };
            rig.Chest = Child(rig.Root, "chest", new Vector3(0f, 1.3f, 0f));
            rig.BustL = Child(rig.Chest, "bustL", new Vector3(-0.08f, 0f, 0.1f));
            rig.BustR = Child(rig.Chest, "bustR", new Vector3(0.08f, 0f, 0.1f));
            var body = root.AddComponent<KawaiiPhysicsSoftBody>();
            body.referenceRoot = rig.Root;
            body.bones.Add(rig.BustL);
            body.bones.Add(rig.BustR);
            body.limitMin = new Vector3(-0.1f, -0.1f, -0.1f);
            body.limitMax = new Vector3(0.1f, 0.1f, 0.1f);
            rig.Body = body;
            if (register) KPSystem.Register(body);
            return rig;
        }

        static Transform Child(Transform parent, string name, Vector3 localPosition)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = localPosition;
            return t;
        }

        static KawaiiPhysicsCollider AddCollider(Transform parent, KawaiiPhysicsCollider.Shape shape, Vector3 worldPosition)
        {
            var go = new GameObject("collider_" + shape);
            go.transform.SetParent(parent, false);
            go.transform.position = worldPosition;
            var c = go.AddComponent<KawaiiPhysicsCollider>();
            c.shape = shape;
            return c;
        }

        /// <summary>一幀：還原 →（動畫）→ 模擬。</summary>
        static void Frame(int f, Action<int> animate = null, float dt = Dt)
        {
            KPSystem.RestorePhase();
            animate?.Invoke(f);
            KPSystem.SimulatePhase(dt);
        }

        static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        static bool Finite(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w);

        static float Omega(float hz) => 2f * Mathf.PI * hz;

        // ---- 平衡 -------------------------------------------------------------

        [Test(Description = "重力相對靜止姿勢、沒有動作：600 幀位移一直是 0、骨頭留在靜止姿勢")]
        public void RestingBodyDoesNotDrift()
        {
            var rig = Build("rest", Vector3.zero);
            var rest = rig.BustL.localPosition;
            var maxOffset = 0f;
            var maxPose = 0f;
            for (int f = 0; f < 600; f++)
            {
                Frame(f);
                maxOffset = Mathf.Max(maxOffset, rig.Body.GetOffset(0).magnitude, rig.Body.GetOffset(1).magnitude);
                maxPose = Mathf.Max(maxPose, (rig.BustL.localPosition - rest).magnitude);
            }
            TestContext.WriteLine($"最大位移 {maxOffset:E2} m、骨頭離靜止姿勢最大 {maxPose:E2} m");
            Assert.That(rig.Body.IsSimulating(0) && rig.Body.IsSimulating(1));
            Assert.That(maxOffset, Is.LessThan(1e-6f));
            Assert.That(maxPose, Is.LessThan(1e-6f));
        }

        [Test(Description = "絕對重力：一開始就在平衡位置（下垂 g/ω²），之後不漂移")]
        public void AbsoluteGravitySagMatchesSpring()
        {
            var rig = Build("sag", Vector3.zero);
            rig.Body.gravityRelativeToRestPose = false;
            rig.Body.frequency = 3f;
            rig.Body.dampingRatio = 0.3f;
            var expected = -9.81f / (Omega(3f) * Omega(3f));
            var anchor = rig.BustL.position;
            Frame(0);
            var first = rig.Body.GetOffset(0);
            var maxDrift = 0f;
            for (int f = 1; f < 600; f++)
            {
                Frame(f);
                maxDrift = Mathf.Max(maxDrift, (rig.Body.GetOffset(0) - first).magnitude);
            }
            TestContext.WriteLine($"下垂 {first.y:F6} m（期望 {expected:F6}）、之後的漂移最大 {maxDrift:E2} m");
            Assert.That(first.y, Is.EqualTo(expected).Within(1e-5f));
            Assert.That(new Vector2(first.x, first.z).magnitude, Is.LessThan(1e-6f));
            Assert.That(maxDrift, Is.LessThan(1e-6f));
            Assert.That((rig.BustL.position - (anchor + first)).magnitude, Is.LessThan(1e-5f), "寫回的位置＝錨點＋位移");
        }

        // ---- 頻率與衰減 ---------------------------------------------------------

        [TestCase(0f)]
        [TestCase(0.1f)]
        [TestCase(0.3f)]
        [TestCase(1f)]
        [TestCase(3f)]
        public void TrajectoryMatchesAnalyticSpring(float zeta)
        {
            var rig = Build("spring", Vector3.zero);
            rig.Body.gravity = Vector3.zero;
            rig.Body.frequency = 2f;
            rig.Body.dampingRatio = zeta;
            rig.Body.limitMin = -Vector3.one;
            rig.Body.limitMax = Vector3.one;
            for (int f = 0; f < 5; f++) Frame(f);
            const float v0 = 0.5f;
            rig.Body.AddImpulse(0, new Vector3(0f, 0f, v0));
            var w = Omega(2f);
            var maxErr = 0f;
            var peak = 0f;
            var minZ = 0f;
            var crossings = new List<float>();
            var prev = 0f;
            for (int n = 1; n <= 300; n++)
            {
                Frame(n + 5);
                var z = rig.Body.GetOffset(0).z;
                var t = n * Dt;
                double expected;
                if (zeta < 1f)
                {
                    var a = w * Math.Sqrt(1 - zeta * zeta);
                    expected = v0 / a * Math.Exp(-zeta * w * t) * Math.Sin(a * t);
                }
                else if (zeta == 1f) expected = v0 * t * Math.Exp(-w * t);
                else
                {
                    var r = Math.Sqrt(zeta * zeta - 1);
                    double z1 = -w * (zeta + r), z2 = -w * (zeta - r);
                    expected = v0 / (z2 - z1) * (Math.Exp(z2 * t) - Math.Exp(z1 * t));
                }
                maxErr = Mathf.Max(maxErr, Mathf.Abs(z - (float)expected));
                peak = Mathf.Max(peak, z);
                minZ = Mathf.Min(minZ, z);
                if (n > 1 && prev > 0f && z <= 0f) crossings.Add((n - 1 + prev / (prev - z)) * Dt);
                prev = z;
            }
            TestContext.WriteLine($"ζ={zeta}：與解析解的最大差 {maxErr:E2} m（峰值 {peak:F4} m）、最小值 {minZ:E2}");
            Assert.That(maxErr, Is.LessThan(1e-5f));
            if (zeta < 1f)
            {
                // 由正到負的零交越間隔＝一個週期
                var used = crossings.Take(4).ToList();
                var period = (used.Last() - used.First()) / (used.Count - 1);
                var expectedPeriod = 2f * Mathf.PI / (w * Mathf.Sqrt(1f - zeta * zeta));
                TestContext.WriteLine($"量到的週期 {period:F5} s、期望 {expectedPeriod:F5} s");
                Assert.That(period, Is.EqualTo(expectedPeriod).Within(expectedPeriod * 0.005f));
            }
            else Assert.That(minZ, Is.GreaterThan(-1e-6f), "臨界與過阻尼不回彈");
        }

        // ---- 位移上限 ------------------------------------------------------------

        [TestCase(false, TestName = "位移不超過非對稱上限")]
        [TestCase(true, TestName = "位移不超過上限（X 軸鎖住）")]
        public void OffsetNeverExceedsLimit(bool lockX)
        {
            var rig = Build("limit", Vector3.zero, register: false);
            rig.Chest.localRotation = Quaternion.Euler(20f, 30f, 10f);
            rig.BustL.localRotation = Quaternion.Euler(0f, 0f, 15f);
            KPSystem.Register(rig.Body); // 靜止姿勢在註冊時記錄：姿勢先擺好
            var min = new Vector3(lockX ? 0f : -0.01f, -0.04f, -0.02f);
            var max = new Vector3(lockX ? 0f : 0.03f, 0.01f, 0.02f);
            rig.Body.limitMin = min;
            rig.Body.limitMax = max;
            var anchor = rig.BustL.position;
            var anchorRot = rig.BustL.rotation;
            var rand = new System.Random(1);
            var maxNorm = 0f;
            var maxLockedX = 0f;
            var maxWriteErr = 0f;
            for (int f = 0; f < 600; f++)
            {
                if (f % 7 == 0)
                    rig.Body.AddImpulse(0, new Vector3((float)rand.NextDouble() - 0.5f, (float)rand.NextDouble() - 0.5f, (float)rand.NextDouble() - 0.5f) * 10f);
                Frame(f);
                var o = Quaternion.Inverse(anchorRot) * rig.Body.GetOffset(0);
                var norm = 0f;
                for (int k = 0; k < 3; k++)
                {
                    var axis = o[k] >= 0f ? max[k] : -min[k];
                    if (axis > 0f) norm += (o[k] / axis) * (o[k] / axis);
                }
                maxNorm = Mathf.Max(maxNorm, Mathf.Sqrt(norm));
                maxLockedX = Mathf.Max(maxLockedX, Mathf.Abs(o.x));
                maxWriteErr = Mathf.Max(maxWriteErr, (rig.BustL.position - anchor - rig.Body.GetOffset(0)).magnitude);
            }
            TestContext.WriteLine($"橢球正規化長度最大 {maxNorm:F6}、X 位移最大 {maxLockedX:E2} m、寫回誤差最大 {maxWriteErr:E2} m");
            Assert.That(maxNorm, Is.LessThanOrEqualTo(1f + 1e-4f));
            Assert.That(maxNorm, Is.GreaterThan(0.99f), "有撞到上限（測試有意義）");
            Assert.That(maxWriteErr, Is.LessThan(1e-5f));
            if (lockX) Assert.That(maxLockedX, Is.LessThan(1e-6f));
        }

        // ---- 慣性 ---------------------------------------------------------------

        /// <summary>走 1 秒（1.5 m/s）→ 停 → 原地轉 180°/s 一秒。</summary>
        static void WalkAndTurn(Transform root, int f)
        {
            var x = Mathf.Clamp(f - 30, 0, 60) * Dt * 1.5f;
            var yaw = Mathf.Clamp(f - 120, 0, 60) * Dt * 180f;
            root.SetPositionAndRotation(new Vector3(x, 0f, 0f), Quaternion.Euler(0f, yaw, 0f));
        }

        [TestCase(0f)]
        [TestCase(1f)]
        public void MovementInertiaScale(float inertia)
        {
            var rig = Build("move", Vector3.zero);
            rig.Body.movementInertia = inertia;
            var maxOffset = 0f;
            for (int f = 0; f < 240; f++)
            {
                Frame(f, k => WalkAndTurn(rig.Root, k));
                maxOffset = Mathf.Max(maxOffset, rig.Body.GetOffset(0).magnitude);
            }
            TestContext.WriteLine($"角色移動慣性 {inertia}：最大位移 {maxOffset * 1000f:F3} mm");
            if (inertia == 0f) Assert.That(maxOffset, Is.LessThan(1e-4f));
            else Assert.That(maxOffset, Is.GreaterThan(5e-3f));
        }

        [TestCase(0f)]
        [TestCase(1f)]
        public void AnimationInertiaScale(float inertia)
        {
            var rig = Build("anim", Vector3.zero);
            rig.Body.animationInertia = inertia;
            var maxOffset = 0f;
            for (int f = 0; f < 240; f++)
            {
                // chest 上下擺動＋扭轉（動畫），角色根不動
                Frame(f, k =>
                {
                    var t = k * Dt;
                    rig.Chest.localPosition = new Vector3(0f, 1.3f + 0.04f * Mathf.Sin(2f * Mathf.PI * 2f * t), 0f);
                    rig.Chest.localRotation = Quaternion.Euler(0f, 20f * Mathf.Sin(2f * Mathf.PI * 1f * t), 0f);
                });
                maxOffset = Mathf.Max(maxOffset, rig.Body.GetOffset(0).magnitude);
            }
            TestContext.WriteLine($"動畫慣性 {inertia}：最大位移 {maxOffset * 1000f:F3} mm");
            if (inertia == 0f) Assert.That(maxOffset, Is.LessThan(1e-4f));
            else Assert.That(maxOffset, Is.GreaterThan(5e-3f));
        }

        // ---- 衝量 ---------------------------------------------------------------

        [Test(Description = "衝量：整組、指定骨頭、撞擊點依距離衰減（dt=0 的幀只套衝量、不推進）")]
        public void ImpulsesApplyAndFallOffWithDistance()
        {
            var rig = Build("impulse", Vector3.zero);
            for (int f = 0; f < 3; f++) Frame(f);
            var v = new Vector3(0f, 0f, 1f);

            rig.Body.AddImpulseAtPosition(rig.BustL.position, v, 0.1f); // 左：距離 0 → 權重 1；右：0.16 > 0.1 → 0
            Frame(3, dt: 0f);
            Assert.That((rig.Body.GetVelocity(0) - v).magnitude, Is.LessThan(1e-6f));
            Assert.That(rig.Body.GetVelocity(1).magnitude, Is.LessThan(1e-6f));

            rig.Body.ResetDynamics();
            Frame(4, dt: 0f);
            var mid = (rig.BustL.position + rig.BustR.position) * 0.5f;
            rig.Body.AddImpulseAtPosition(mid, v, 0.16f); // 兩邊距離 0.08 → (1 − 0.5)² = 0.25
            Frame(5, dt: 0f);
            Assert.That(rig.Body.GetVelocity(0).z, Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(rig.Body.GetVelocity(1).z, Is.EqualTo(0.25f).Within(1e-5f));

            rig.Body.ResetDynamics();
            Frame(6, dt: 0f);
            rig.Body.AddImpulse(v);
            rig.Body.AddImpulse(1, v);
            Frame(7, dt: 0f);
            Assert.That(rig.Body.GetVelocity(0).z, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(rig.Body.GetVelocity(1).z, Is.EqualTo(2f).Within(1e-6f));

            Assert.Throws<ArgumentOutOfRangeException>(() => rig.Body.AddImpulse(2, v));
            rig.Body.enabled = false; // 停用中的元件忽略
            rig.Body.AddImpulse(v);
            Assert.That(rig.Body.pendingUniformImpulse, Is.EqualTo(Vector3.zero));
        }

        // ---- 壓扁與拉伸 -----------------------------------------------------------

        [TestCase(1f)]
        [TestCase(0f)]
        public void SquashStretchVolume(float volumePreservation)
        {
            var rig = Build("stretch", Vector3.zero);
            rig.Body.gravity = Vector3.zero;
            rig.Body.stretchAxis = KawaiiPhysicsSoftBody.Axis.Y;
            rig.Body.stretchAmount = 0.5f;
            rig.Body.volumePreservation = volumePreservation;
            rig.Body.limitMin = new Vector3(-0.05f, -0.05f, -0.05f);
            rig.Body.limitMax = new Vector3(0.05f, 0.05f, 0.05f);
            var maxStretch = 1f;
            var minSquash = 1f;
            var maxVolumeErr = 0f;
            for (int f = 0; f < 300; f++)
            {
                if (f % 60 == 10) rig.Body.AddImpulse(0, new Vector3(0f, 2f, 0f));
                Frame(f);
                var s = rig.BustL.localScale;
                maxStretch = Mathf.Max(maxStretch, s.y);
                minSquash = Mathf.Min(minSquash, s.y);
                maxVolumeErr = Mathf.Max(maxVolumeErr, Mathf.Abs(s.x * s.y * s.z - 1f));
            }
            TestContext.WriteLine($"維持體積 {volumePreservation}：Y 縮放 {minSquash:F3}–{maxStretch:F3}、體積誤差最大 {maxVolumeErr:E2}");
            Assert.That(maxStretch, Is.GreaterThan(1.3f));
            Assert.That(minSquash, Is.LessThan(0.8f));
            if (volumePreservation == 1f) Assert.That(maxVolumeErr, Is.LessThan(1e-4f));
            else Assert.That(maxVolumeErr, Is.GreaterThan(0.3f));
            Assert.That(rig.BustR.localScale, Is.EqualTo(Vector3.one), "沒有位移的骨頭不縮放");
        }

        // ---- 碰撞 ---------------------------------------------------------------

        [TestCase(KawaiiPhysicsCollider.Shape.Sphere)]
        [TestCase(KawaiiPhysicsCollider.Shape.Capsule)]
        [TestCase(KawaiiPhysicsCollider.Shape.TaperedCapsule)]
        [TestCase(KawaiiPhysicsCollider.Shape.Box)]
        [TestCase(KawaiiPhysicsCollider.Shape.Plane)]
        public void CollidersAreNotPenetrated(KawaiiPhysicsCollider.Shape shape)
        {
            float RunMaxZ(bool withCollider, out float minClearance)
            {
                KPSystem.Shutdown();
                var rig = Build("collide", Vector3.zero);
                rig.Body.radius = 0.01f;
                rig.Body.gravity = Vector3.zero;
                var anchor = rig.BustL.position;
                // 錨點前方 0.08 m 的碰撞體，表面在 +0.05 m → 骨頭（半徑 0.01）最多到 +0.04 m
                var c = AddCollider(rig.Root, shape, anchor + new Vector3(0f, 0f, 0.08f));
                c.radius = 0.03f;
                c.radius1 = 0.03f;
                c.length = 0.2f;
                c.direction = KawaiiPhysicsCollider.Axis.X;
                c.extent = new Vector3(0.1f, 0.1f, 0.03f);
                if (shape == KawaiiPhysicsCollider.Shape.Plane)
                {
                    c.transform.position = anchor + new Vector3(0f, 0f, 0.05f);
                    c.transform.rotation = Quaternion.FromToRotation(Vector3.up, Vector3.back);
                }
                if (withCollider) rig.Body.colliders.Add(c);
                var maxZ = 0f;
                minClearance = float.MaxValue;
                for (int f = 0; f < 120; f++)
                {
                    if (f == 5) rig.Body.AddImpulse(0, new Vector3(0f, 0f, 3f));
                    Frame(f);
                    var z = rig.Body.GetOffset(0).z;
                    maxZ = Mathf.Max(maxZ, z);
                    minClearance = Mathf.Min(minClearance, 0.05f - (rig.BustL.position.z - anchor.z));
                }
                return maxZ;
            }
            var free = RunMaxZ(false, out _);
            var blocked = RunMaxZ(true, out var clearance);
            TestContext.WriteLine($"{shape}：沒有碰撞體最遠 {free:F4} m、有碰撞體最遠 {blocked:F4} m、離表面最近 {clearance:F5} m（半徑 0.01）");
            Assert.That(free, Is.GreaterThan(0.06f), "沒有碰撞體時會越過表面（測試有意義）");
            Assert.That(blocked, Is.LessThanOrEqualTo(0.04f + 1e-4f));
            Assert.That(clearance, Is.GreaterThanOrEqualTo(0.01f - 1e-4f));
        }

        // ---- 傾斜與階層 -----------------------------------------------------------

        [Test(Description = "傾斜 1：骨頭轉向『父骨 → 位移後的位置』")]
        public void TiltRotatesTowardOffset()
        {
            var rig = Build("tilt", Vector3.zero);
            rig.Body.tilt = 1f;
            rig.Body.gravity = Vector3.zero;
            var restRot = rig.BustL.rotation;
            var arm = rig.BustL.position - rig.Chest.position;
            var maxErr = 0f;
            var maxAngle = 0f;
            for (int f = 0; f < 120; f++)
            {
                if (f == 5) rig.Body.AddImpulse(0, new Vector3(0f, 1f, 0.3f));
                Frame(f);
                var d = rig.Body.GetOffset(0);
                var expected = Quaternion.FromToRotation(arm, arm + d) * restRot;
                maxErr = Mathf.Max(maxErr, Quaternion.Angle(expected, rig.BustL.rotation));
                maxAngle = Mathf.Max(maxAngle, Quaternion.Angle(restRot, rig.BustL.rotation));
            }
            TestContext.WriteLine($"傾斜角最大 {maxAngle:F2}°、與期望的差最大 {maxErr:F4}°");
            Assert.That(maxAngle, Is.GreaterThan(5f));
            Assert.That(maxErr, Is.LessThan(0.05f));
        }

        [Test(Description = "階層：子骨的錨點跟著父骨的位移，子骨的位置＝靜止位置＋父位移＋子位移")]
        public void ChildFollowsParentSoftBone()
        {
            var rig = Build("hier", Vector3.zero, register: false);
            var tip = Child(rig.BustL, "bustL_tip", new Vector3(0f, 0f, 0.05f));
            rig.Body.bones.Clear();
            rig.Body.bones.Add(tip); // 清單順序跟階層相反也要對
            rig.Body.bones.Add(rig.BustL);
            rig.Body.gravity = Vector3.zero;
            KPSystem.Register(rig.Body);
            var tipRest = tip.position;
            var maxErr = 0f;
            var maxChild = 0f;
            for (int f = 0; f < 180; f++)
            {
                if (f == 5) rig.Body.AddImpulse(1, new Vector3(0f, 1f, 0f)); // 只推父骨
                Frame(f);
                var expected = tipRest + rig.Body.GetOffset(1) + rig.Body.GetOffset(0);
                maxErr = Mathf.Max(maxErr, (tip.position - expected).magnitude);
                maxChild = Mathf.Max(maxChild, rig.Body.GetOffset(0).magnitude);
            }
            TestContext.WriteLine($"子骨位置與期望的差最大 {maxErr:E2} m、子骨自己的位移最大 {maxChild * 1000f:F2} mm");
            Assert.That(maxErr, Is.LessThan(1e-5f));
            Assert.That(maxChild, Is.GreaterThan(1e-3f), "子骨感受到父骨的晃動（慣性）");
        }

        [Test(Description = "alpha 0：完全照動畫（有衝量也不動）")]
        public void AlphaZeroLeavesAnimationUntouched()
        {
            var rig = Build("alpha", Vector3.zero);
            rig.Body.alpha = 0f;
            rig.Body.stretchAmount = 0.5f;
            rig.Body.tilt = 1f;
            var restPos = rig.BustL.localPosition;
            var restRot = rig.BustL.localRotation;
            var maxErr = 0f;
            for (int f = 0; f < 60; f++)
            {
                rig.Body.AddImpulse(new Vector3(0f, 1f, 1f));
                Frame(f);
                maxErr = Mathf.Max(maxErr, (rig.BustL.localPosition - restPos).magnitude, Quaternion.Angle(restRot, rig.BustL.localRotation) * 1e-3f,
                    (rig.BustL.localScale - Vector3.one).magnitude);
            }
            Assert.That(rig.Body.GetOffset(0).magnitude, Is.GreaterThan(1e-3f), "模擬照跑");
            Assert.That(maxErr, Is.LessThan(1e-6f));
        }

        // ---- 重力相對靜止姿勢 ------------------------------------------------------

        [Test(Description = "重力相對靜止姿勢：轉向不下垂；躺下時往重力方向偏 (g − R·g)/ω²")]
        public void GravityRelativeToRestPose()
        {
            var rig = Build("lie", Vector3.zero);
            rig.Body.limitMin = new Vector3(-0.2f, -0.2f, -0.2f);
            rig.Body.limitMax = new Vector3(0.2f, 0.2f, 0.2f);
            rig.Body.dampingRatio = 0.5f;
            rig.Body.teleportRotationThreshold = 0f;
            rig.Root.rotation = Quaternion.Euler(0f, 90f, 0f);
            for (int f = 0; f < 300; f++) Frame(f);
            var yawed = rig.Body.GetOffset(0);
            rig.Root.rotation = Quaternion.Euler(-90f, 0f, 0f); // 仰躺（正面朝上）
            for (int f = 300; f < 900; f++) Frame(f);
            var g = rig.Body.gravity;
            var expected = (g - rig.Root.rotation * g) / (Omega(3f) * Omega(3f));
            var lying = rig.Body.GetOffset(0);
            TestContext.WriteLine($"轉向 {yawed.magnitude:E2} m；仰躺 {lying}（期望 {expected}）");
            Assert.That(yawed.magnitude, Is.LessThan(1e-5f));
            Assert.That((lying - expected).magnitude, Is.LessThan(1e-4f));
        }

        // ---- 瞬移、重置 ---------------------------------------------------------

        [Test]
        public void TeleportResetsButSmallMoveDoesNot()
        {
            var rig = Build("teleport", Vector3.zero);
            rig.Body.teleportDistanceThreshold = 1f;
            for (int f = 0; f < 10; f++) Frame(f);
            rig.Body.AddImpulse(new Vector3(0f, 0f, 2f));
            Frame(10);
            Frame(11, k => rig.Root.position = new Vector3(0.5f, 0f, 0f)); // 門檻以下：照慣性
            Assert.That(rig.Body.GetOffset(0).magnitude, Is.GreaterThan(1e-2f));
            Frame(12, k => rig.Root.position = new Vector3(10f, 0f, 0f));  // 瞬移：重置
            Assert.That(rig.Body.GetOffset(0).magnitude, Is.LessThan(1e-6f));
            Assert.That(rig.Body.GetVelocity(0).magnitude, Is.LessThan(1e-6f));
            Assert.That((rig.BustL.position - rig.Chest.TransformPoint(new Vector3(-0.08f, 0f, 0.1f))).magnitude, Is.LessThan(1e-5f));

            rig.Body.AddImpulse(new Vector3(0f, 0f, 2f));
            Frame(13);
            Assert.That(rig.Body.GetOffset(0).magnitude, Is.GreaterThan(1e-2f));
            rig.Body.ResetDynamics();
            Frame(14);
            Assert.That(rig.Body.GetOffset(0).magnitude, Is.LessThan(1e-6f));
        }

        // ---- 生命週期 -----------------------------------------------------------

        /// <summary>mode 0：只有 A；1：第 60 幀加入 B、第 120 幀移除。回傳 A 每幀的位移。</summary>
        List<Vector3> RunIsolation(int mode)
        {
            KPSystem.Shutdown();
            foreach (var go in created)
                if (go != null) Object.DestroyImmediate(go);
            created.Clear();
            var a = Build("A", Vector3.zero);
            Rig b = null;
            var result = new List<Vector3>();
            for (int f = 0; f < 200; f++)
            {
                if (mode == 1 && f == 60) b = Build("B", new Vector3(3f, 0f, 0f));
                if (mode == 1 && f == 120) KPSystem.Unregister(b.Body);
                if (f % 20 == 3) a.Body.AddImpulse(0, new Vector3(0.3f, 0.5f, -0.4f));
                if (b != null && f % 10 == 0) b.Body.AddImpulse(new Vector3(0f, 1f, 0f));
                Frame(f, k => WalkAndTurn(a.Root, k));
                result.Add(a.Body.GetOffset(0));
            }
            return result;
        }

        [Test(Description = "加入、移除其他軟組織元件不影響 A 的模擬（逐幀一致）")]
        public void AddRemoveDoesNotAffectOthers()
        {
            var control = RunIsolation(0);
            var withB = RunIsolation(1);
            var maxDiff = control.Zip(withB, (x, y) => (x - y).magnitude).Max();
            TestContext.WriteLine($"A 與對照組的最大差 {maxDiff:E2} m（A 的最大位移 {control.Max(v => v.magnitude):F4} m）");
            Assert.That(maxDiff, Is.EqualTo(0f));
        }

        [Test(Description = "停用：下一幀動畫前骨頭回到靜止姿勢（位置、旋轉、縮放）")]
        public void UnregisterRestoresRestPose()
        {
            var rig = Build("restore", Vector3.zero);
            rig.Body.tilt = 1f;
            rig.Body.stretchAmount = 0.5f;
            var pos = rig.BustL.localPosition;
            var rot = rig.BustL.localRotation;
            for (int f = 0; f < 30; f++)
            {
                rig.Body.AddImpulse(new Vector3(0.5f, 1f, 0f));
                Frame(f);
            }
            Assert.That((rig.BustL.localPosition - pos).magnitude, Is.GreaterThan(1e-3f), "有在動");
            KPSystem.Unregister(rig.Body);
            KPSystem.RestorePhase();
            Assert.That(rig.BustL.localPosition, Is.EqualTo(pos));
            Assert.That(rig.BustL.localRotation, Is.EqualTo(rot));
            Assert.That(rig.BustL.localScale, Is.EqualTo(Vector3.one));
            Assert.That(KPSystem.SoftBodyCount, Is.EqualTo(0));
            Assert.That(KPSoftBodySystem.RestCacheCount, Is.EqualTo(0), "離開模擬的骨頭不留靜止姿勢記錄");
        }

        [Test(Description = "應用端改了靜止姿勢後 CaptureRestPose：之後還原到新的姿勢")]
        public void CaptureRestPoseUpdatesRestore()
        {
            var rig = Build("capture", Vector3.zero);
            for (int f = 0; f < 5; f++) Frame(f);
            var changed = new Vector3(-0.09f, 0.02f, 0.12f);
            KPSystem.RestorePhase();
            rig.BustL.localPosition = changed; // 體型調整（在 Update 裡）
            rig.Body.CaptureRestPose();
            KPSystem.SimulatePhase(Dt);
            for (int f = 6; f < 10; f++) Frame(f);
            KPSystem.RestorePhase();
            Assert.That((rig.BustL.localPosition - changed).magnitude, Is.LessThan(1e-6f));
        }

        [Test(Description = "Inspector：改設定不重建、就地生效；改骨頭清單才重建")]
        public void ValidateRebuildsOnlyWhenBonesChange()
        {
            var rig = Build("validate", Vector3.zero);
            rig.Body.gravity = Vector3.zero;
            for (int f = 0; f < 5; f++) Frame(f);
            rig.Body.AddImpulse(0, new Vector3(0f, 0f, 0.5f));
            for (int f = 5; f < 10; f++) Frame(f);
            var syncs = KPSoftBodySystem.SyncCount;
            // 振幅（位移與速度合起來；零交越附近只看位移會誤判）
            float Amplitude()
            {
                var w = Omega(rig.Body.frequency);
                return Mathf.Sqrt(rig.Body.GetOffset(0).sqrMagnitude + rig.Body.GetVelocity(0).sqrMagnitude / (w * w));
            }
            Assert.That(Amplitude(), Is.GreaterThan(0.01f)); // 3 Hz、ζ 0.25：衝量 0.5 m/s 五幀後約 0.019 m
            var onValidate = typeof(KawaiiPhysicsSoftBody).GetMethod("OnValidate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            rig.Body.frequency = 5f;
            onValidate.Invoke(rig.Body, null);
            Frame(10);
            Assert.That(KPSoftBodySystem.SyncCount, Is.EqualTo(syncs), "改設定不重建");
            Assert.That(Amplitude(), Is.GreaterThan(5e-3f), "狀態沒有重置");

            var extra = Child(rig.Chest, "belly", new Vector3(0f, -0.25f, 0.12f));
            rig.Body.bones.Add(extra);
            onValidate.Invoke(rig.Body, null);
            Frame(11);
            Assert.That(KPSoftBodySystem.SyncCount, Is.EqualTo(syncs + 1));
            Assert.That(rig.Body.IsSimulating(2), Is.True);
            Assert.That(Amplitude(), Is.GreaterThan(1e-3f), "重建時同一根骨頭的狀態沿用");
        }

        [Test(Description = "同一根骨頭給兩個軟組織元件：後面的略過並警告")]
        public void DuplicateBoneAcrossBodiesIsSkipped()
        {
            var rig = Build("dup", Vector3.zero);
            var other = rig.Root.gameObject.AddComponent<KawaiiPhysicsSoftBody>();
            other.bones.Add(rig.BustL);
            KPSystem.Register(other);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("bustL"));
            Frame(0);
            Assert.That(rig.Body.IsSimulating(0), Is.True);
            Assert.That(other.IsSimulating(0), Is.False);
        }

        // ---- 審查後追加：慣性的大小、移動中的重置、階層、碰撞優先、生命週期 -----------------------

        /// <summary>
        /// 阻尼彈簧推進 dt 的轉移矩陣，用 double 的 RK4（2000 子步）另外算（不用套件的解析解）。
        /// 回傳 [pp, pv, vp, vv]。
        /// </summary>
        static double[] Rk4Transition(double omega, double zeta, double dt)
        {
            double[] Step(double x0, double v0)
            {
                const int n = 2000;
                var h = dt / n;
                double x = x0, v = v0;
                for (int i = 0; i < n; i++)
                {
                    double Ax(double xx, double vv) => vv;
                    double Av(double xx, double vv) => -omega * omega * xx - 2 * zeta * omega * vv;
                    var k1x = Ax(x, v); var k1v = Av(x, v);
                    var k2x = Ax(x + h / 2 * k1x, v + h / 2 * k1v); var k2v = Av(x + h / 2 * k1x, v + h / 2 * k1v);
                    var k3x = Ax(x + h / 2 * k2x, v + h / 2 * k2v); var k3v = Av(x + h / 2 * k2x, v + h / 2 * k2v);
                    var k4x = Ax(x + h * k3x, v + h * k3v); var k4v = Av(x + h * k3x, v + h * k3v);
                    x += h / 6 * (k1x + 2 * k2x + 2 * k3x + k4x);
                    v += h / 6 * (k1v + 2 * k2v + 2 * k3v + k4v);
                }
                return new[] { x, v };
            }
            var a = Step(1, 0);
            var b = Step(0, 1);
            return new[] { a[0], b[0], a[1], b[1] };
        }

        [TestCase(true, TestName = "慣性的大小：角色根等加速度時的穩態位移＝離散不動點（RK4 另外算）")]
        [TestCase(false, TestName = "慣性的大小：動畫等加速度時的穩態位移＝離散不動點（RK4 另外算）")]
        public void InertiaMagnitudeMatchesIndependentFixedPoint(bool movement)
        {
            const float a = 2f;
            const float zeta = 0.5f;
            var rig = Build("accel", Vector3.zero);
            rig.Body.dampingRatio = zeta;
            for (int f = 0; f < 200; f++)
            {
                Frame(f, k =>
                {
                    var t = k * Dt;
                    var s = 0.5f * a * t * t;
                    if (movement) rig.Root.position = new Vector3(s, 0f, 0f);
                    else rig.Chest.localPosition = new Vector3(0f, 1.3f + s, 0f);
                });
            }
            // 每幀：u ← u − a·dt（錨點的速度每幀多 a·dt），再 [x; u] ← M [x; u]。不動點：(I − M) z = −M [0; a·dt]
            var m = Rk4Transition(2 * Math.PI * 3, zeta, Dt);
            var q = a * (double)Dt;
            double r0 = -m[1] * q, r1 = -m[3] * q;
            var det = (1 - m[0]) * (1 - m[3]) - m[1] * m[2];
            var expected = (r0 * (1 - m[3]) + m[1] * r1) / det;
            var offset = rig.Body.GetOffset(0);
            var along = movement ? offset.x : offset.y;
            var across = movement ? new Vector2(offset.y, offset.z).magnitude : new Vector2(offset.x, offset.z).magnitude;
            TestContext.WriteLine($"{(movement ? "角色根" : "動畫")}等加速度 {a} m/s²：位移 {along * 1000f:F4} mm、期望 {expected * 1000:F4} mm（連續解 −a/ω² {-a / Math.Pow(2 * Math.PI * 3, 2) * 1000:F4} mm）、其他軸 {across:E2} m");
            Assert.That(along, Is.EqualTo(expected).Within(2e-5));
            Assert.That(across, Is.LessThan(1e-5f));
        }

        [TestCase("none", TestName = "移動中：對照組（不重置）")]
        [TestCase("reset", TestName = "移動中 ResetDynamics 不反衝")]
        [TestCase("teleport", TestName = "移動中瞬移後繼續移動不反衝")]
        [TestCase("enable", TestName = "移動中才啟用不反衝")]
        [TestCase("anim-reset", TestName = "動畫等速移動中 ResetDynamics 不反衝")]
        public void ResetWhileMovingHasNoKick(string mode)
        {
            var rig = Build("moving", Vector3.zero, register: mode != "enable");
            var shift = 0f;
            var maxAfter = 0f;
            for (int f = 0; f < 240; f++)
            {
                if (f == 120)
                {
                    if (mode == "reset" || mode == "anim-reset") rig.Body.ResetDynamics();
                    if (mode == "teleport") shift = 10f;
                    if (mode == "enable") KPSystem.Register(rig.Body);
                }
                Frame(f, k =>
                {
                    var t = k * Dt;
                    if (mode == "anim-reset") rig.Chest.localPosition = new Vector3(0f, 1.3f + 0.5f * t, 0f);
                    else rig.Root.position = new Vector3(1.5f * t + shift, 0f, 0f);
                });
                if (f >= 120) maxAfter = Mathf.Max(maxAfter, rig.Body.GetOffset(0).magnitude);
            }
            TestContext.WriteLine($"{mode}：第 120 幀之後的最大位移 {maxAfter * 1000f:F4} mm");
            Assert.That(rig.Body.IsSimulating(0));
            Assert.That(maxAfter, Is.LessThan(1e-4f));
        }

        [Test(Description = "移動中生成（等速平移）：一開始就跟著走，600 幀都不晃")]
        public void SpawnedWhileMovingDoesNotDrift()
        {
            var rig = Build("spawnMoving", Vector3.zero);
            var max = 0f;
            for (int f = 0; f < 600; f++)
            {
                Frame(f, k => rig.Root.position = new Vector3(1f, 0f, 0.7f) * (k * Dt));
                max = Mathf.Max(max, rig.Body.GetOffset(0).magnitude, rig.Body.GetOffset(1).magnitude);
            }
            TestContext.WriteLine($"最大位移 {max * 1000f:F4} mm");
            Assert.That(max, Is.LessThan(1e-4f));
        }

        [Test(Description = "父骨伸縮：子軟組織骨頭的位置＝父骨寫回後的矩陣 × 子骨的區域位置＋子骨的位移")]
        public void ParentStretchCarriesChild()
        {
            var rig = Build("stretchParent", Vector3.zero, register: false);
            var tip = Child(rig.BustL, "tip", new Vector3(0.01f, 0.02f, 0.05f));
            rig.Body.bones.Clear();
            rig.Body.bones.Add(rig.BustL);
            rig.Body.bones.Add(tip);
            rig.Body.gravity = Vector3.zero;
            rig.Body.stretchAxis = KawaiiPhysicsSoftBody.Axis.Z;
            rig.Body.stretchAmount = 0.5f;
            rig.Body.tilt = 0.5f;
            KPSystem.Register(rig.Body);
            var tipLocal = tip.localPosition;
            var maxErr = 0f;
            var maxScale = 1f;
            for (int f = 0; f < 180; f++)
            {
                if (f == 5) rig.Body.AddImpulse(0, new Vector3(0.3f, 0.5f, 2f));
                Frame(f);
                var expected = rig.BustL.localToWorldMatrix.MultiplyPoint3x4(tipLocal) + rig.Body.GetOffset(1);
                maxErr = Mathf.Max(maxErr, (tip.position - expected).magnitude);
                maxScale = Mathf.Max(maxScale, rig.BustL.localScale.z);
            }
            TestContext.WriteLine($"父骨 Z 縮放最大 {maxScale:F3}、子骨位置誤差最大 {maxErr:E2} m");
            Assert.That(maxScale, Is.GreaterThan(1.2f), "父骨有伸縮（測試有意義）");
            Assert.That(maxErr, Is.LessThan(1e-5f));
        }

        [TestCase(true, TestName = "階層：不同元件的父子骨，子骨也感受父骨的晃動")]
        [TestCase(false, TestName = "階層：animationInertia 0 時子骨還是感受父骨的晃動")]
        public void HierarchyInertiaAcrossComponentsAndAnimationInertia(bool crossComponent)
        {
            var rig = Build("hier2", Vector3.zero, register: false);
            var tip = Child(rig.BustL, "tip", new Vector3(0f, 0f, 0.05f));
            rig.Body.bones.Clear();
            rig.Body.bones.Add(rig.BustL);
            rig.Body.gravity = Vector3.zero;
            var child = rig.Body;
            if (crossComponent)
            {
                child = rig.Chest.gameObject.AddComponent<KawaiiPhysicsSoftBody>();
                child.referenceRoot = rig.Root;
                child.gravity = Vector3.zero;
                child.limitMin = rig.Body.limitMin;
                child.limitMax = rig.Body.limitMax;
                KPSystem.Register(child); // 子骨的元件先註冊：解算順序要靠階層，不靠註冊順序
            }
            else rig.Body.animationInertia = 0f;
            child.bones.Add(tip);
            KPSystem.Register(rig.Body);
            var tipIndex = child.bones.Count - 1;
            var tipLocal = tip.localPosition;
            var maxErr = 0f;
            var maxChild = 0f;
            for (int f = 0; f < 180; f++)
            {
                if (f == 5) rig.Body.AddImpulse(0, new Vector3(0f, 1f, 0f));
                Frame(f);
                maxErr = Mathf.Max(maxErr, (tip.position - (rig.BustL.localToWorldMatrix.MultiplyPoint3x4(tipLocal) + child.GetOffset(tipIndex))).magnitude);
                maxChild = Mathf.Max(maxChild, child.GetOffset(tipIndex).magnitude);
            }
            TestContext.WriteLine($"子骨自己的位移最大 {maxChild * 1000f:F2} mm、位置誤差最大 {maxErr:E2} m");
            Assert.That(child.IsSimulating(tipIndex));
            Assert.That(maxChild, Is.GreaterThan(1e-3f));
            Assert.That(maxErr, Is.LessThan(1e-5f));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void CollisionPriorityOverLimit(bool collisionPriority)
        {
            var rig = Build("priority", Vector3.zero);
            rig.Body.gravity = Vector3.zero;
            rig.Body.radius = 0.01f;
            rig.Body.limitMin = new Vector3(-0.1f, -0.1f, -0.01f);
            rig.Body.collisionPriority = collisionPriority;
            var anchor = rig.BustL.position;
            // 前方的球：表面在錨點後 0.01 m（超過 limitMin.z），骨頭（半徑 0.01）要退到 −0.02 m 才不穿透
            var c = AddCollider(rig.Root, KawaiiPhysicsCollider.Shape.Sphere, anchor + new Vector3(0f, 0f, 0.03f));
            c.radius = 0.04f;
            rig.Body.colliders.Add(c);
            for (int f = 0; f < 60; f++) Frame(f);
            var dist = (rig.BustL.position - c.transform.position).magnitude;
            var z = rig.Body.GetOffset(0).z;
            TestContext.WriteLine($"碰撞優先 {collisionPriority}：位移 z {z:F4} m、到球心 {dist:F4} m（不穿透要 ≥ 0.05）");
            if (collisionPriority) Assert.That(dist, Is.GreaterThanOrEqualTo(0.05f - 1e-4f));
            else
            {
                Assert.That(z, Is.GreaterThanOrEqualTo(-0.01f - 1e-5f));
                Assert.That(dist, Is.LessThan(0.05f - 1e-3f));
            }
        }

        [TestCase(true, TestName = "停用再啟用（同一幀）：重新開始")]
        [TestCase(false, TestName = "停用再啟用（跨幀）：重新開始")]
        public void DisableEnableAlwaysResets(bool sameFrame)
        {
            var rig = Build("reenable", Vector3.zero);
            rig.Body.gravity = Vector3.zero;
            for (int f = 0; f < 5; f++) Frame(f);
            rig.Body.AddImpulse(0, new Vector3(0f, 0f, 1f));
            Frame(5);
            Assert.That(rig.Body.GetVelocity(0).magnitude, Is.GreaterThan(0.1f));
            KPSystem.Unregister(rig.Body);
            if (!sameFrame) Frame(6);
            KPSystem.Register(rig.Body);
            Frame(7);
            TestContext.WriteLine($"重新啟用後 位移 {rig.Body.GetOffset(0).magnitude:E2} m、速度 {rig.Body.GetVelocity(0).magnitude:E2} m/s");
            Assert.That(rig.Body.GetOffset(0).magnitude, Is.LessThan(1e-6f));
            Assert.That(rig.Body.GetVelocity(0).magnitude, Is.LessThan(1e-6f));
        }

        [Test(Description = "referenceRoot 被刪掉：退回上層的 Animator 或自己，繼續模擬；刪掉的骨頭 IsSimulating 是 false")]
        public void DestroyedReferenceRootFallsBack()
        {
            var rig = Build("refRoot", Vector3.zero, register: false);
            var reference = Child(rig.Root, "reference", Vector3.zero);
            rig.Body.referenceRoot = reference;
            rig.Body.gravity = Vector3.zero;
            KPSystem.Register(rig.Body);
            for (int f = 0; f < 5; f++) Frame(f);
            Object.DestroyImmediate(reference.gameObject);
            var max = 0f;
            for (int f = 5; f < 125; f++)
            {
                if (f % 20 == 10) rig.Body.AddImpulse(0, new Vector3(0f, 0f, 1f));
                Frame(f);
                max = Mathf.Max(max, rig.Body.GetOffset(0).magnitude);
            }
            TestContext.WriteLine($"角色根刪掉後的最大位移 {max * 1000f:F2} mm");
            Assert.That(max, Is.GreaterThan(1e-3f));
            Assert.That(rig.Body.IsSimulating(0));
            Object.DestroyImmediate(rig.BustR.gameObject);
            Frame(125);
            Assert.That(rig.Body.IsSimulating(1), Is.False);
            Assert.That(rig.Body.IsSimulating(0));
        }

        [Test(Description = "CaptureRestPose：別的元件模擬中的骨頭不動（靜止姿勢歸那個元件管）")]
        public void CaptureRestPoseSkipsBonesOwnedByOthers()
        {
            var rig = Build("captureOther", Vector3.zero);
            var other = rig.Chest.gameObject.AddComponent<KawaiiPhysicsSoftBody>();
            other.referenceRoot = rig.Root;
            other.bones.Add(rig.BustL);
            KPSystem.Register(other);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("bustL"));
            Frame(0);
            var rest = rig.BustL.localPosition;
            KPSystem.RestorePhase();
            rig.BustL.localPosition = rest + new Vector3(0f, 0.03f, 0f);
            other.CaptureRestPose();                 // 這根骨頭歸 rig.Body：不採用
            KPSystem.SimulatePhase(Dt);
            KPSystem.RestorePhase();
            Assert.That((rig.BustL.localPosition - rest).magnitude, Is.LessThan(1e-6f), "還原到原本的靜止姿勢");
            rig.BustL.localPosition = rest + new Vector3(0f, 0.03f, 0f);
            rig.Body.CaptureRestPose();              // 擁有者：採用
            KPSystem.SimulatePhase(Dt);
            KPSystem.RestorePhase();
            Assert.That((rig.BustL.localPosition - (rest + new Vector3(0f, 0.03f, 0f))).magnitude, Is.LessThan(1e-6f));
        }

        // ---- 第二次審查後追加：祖先單獨跳動、CaptureRestPose 的對應、鏡像的軸向 ------------------------

        [TestCase("late-enable", TestName = "祖先的元件晚啟用：子骨不被甩")]
        [TestCase("parent-disable", TestName = "祖先的元件停用：子骨不被甩")]
        [TestCase("parent-reset", TestName = "只重置祖先的元件：子骨不被甩")]
        [TestCase("add-to-same", TestName = "把祖先骨頭加進同一個元件：子骨不被甩")]
        public void AncestorJumpDoesNotKickChild(string mode)
        {
            var rig = Build("ancestor", Vector3.zero, register: false);
            var tip = Child(rig.BustL, "tip", new Vector3(0f, 0f, 0.05f));
            var parent = rig.Body;                               // 父：bustL，位移不是 0（下垂或額外加速度）
            parent.bones.Clear();
            parent.bones.Add(rig.BustL);
            parent.gravityRelativeToRestPose = false;            // 下垂 27.6 mm
            if (mode == "parent-reset") parent.externalAcceleration = new Vector3(0f, 0f, 8f);
            KawaiiPhysicsSoftBody child;
            if (mode == "add-to-same")
            {
                child = parent;
                parent.bones.Clear();
                parent.bones.Add(tip);
            }
            else
            {
                child = rig.Chest.gameObject.AddComponent<KawaiiPhysicsSoftBody>();
                child.referenceRoot = rig.Root;
                child.gravity = Vector3.zero;
                child.limitMin = parent.limitMin;
                child.limitMax = parent.limitMax;
                child.bones.Add(tip);
                KPSystem.Register(child);
            }
            if (mode != "late-enable" && mode != "add-to-same") KPSystem.Register(parent);
            if (mode == "add-to-same") KPSystem.Register(parent);
            var tipIndex = child.bones.IndexOf(tip);
            Vector3 before = default;
            var maxDev = 0f;
            for (int f = 0; f < 300; f++)
            {
                if (f == 120)
                {
                    before = child.GetOffset(child.bones.IndexOf(tip));
                    switch (mode)
                    {
                        case "late-enable": KPSystem.Register(parent); break;
                        case "parent-disable": KPSystem.Unregister(parent); break;
                        case "parent-reset":
                            parent.externalAcceleration = Vector3.zero; // 平衡位置變了，同一幀重置：父骨一幀內跳回去
                            parent.ResetDynamics();
                            break;
                        case "add-to-same":
                            parent.bones.Add(rig.BustL);
                            parent.MarkDirty();
                            break;
                    }
                }
                Frame(f);
                tipIndex = child.bones.IndexOf(tip);
                if (f >= 120) maxDev = Mathf.Max(maxDev, (child.GetOffset(tipIndex) - before).magnitude);
            }
            TestContext.WriteLine($"{mode}：事件後子骨位移的變化最大 {maxDev * 1000f:F4} mm（事件前 {before.magnitude * 1000f:F2} mm）");
            Assert.That(child.IsSimulating(tipIndex));
            Assert.That(maxDev, Is.LessThan(1e-4f));
        }

        [TestCase(true, TestName = "CaptureRestPose：同一幀插入骨頭再呼叫，記到對的骨頭")]
        [TestCase(false, TestName = "CaptureRestPose：同一幀移除骨頭再呼叫，記到對的骨頭")]
        public void CaptureRestPoseAfterListChangeUsesTransforms(bool insert)
        {
            var rig = Build("captureList", Vector3.zero);
            var belly = Child(rig.Chest, "belly", new Vector3(0f, -0.25f, 0.12f));
            for (int f = 0; f < 5; f++) Frame(f);
            var restL = rig.BustL.localPosition;
            var restR = rig.BustR.localPosition;
            var changedR = restR + new Vector3(0f, 0.03f, 0f);
            KPSystem.RestorePhase();
            rig.BustR.localPosition = changedR;                 // 體型調整（Update 裡）
            if (insert) rig.Body.bones.Insert(0, belly);
            else rig.Body.bones.RemoveAt(0);                     // 拿掉 bustL
            rig.Body.MarkDirty();
            rig.Body.CaptureRestPose();
            KPSystem.SimulatePhase(Dt);
            for (int f = 6; f < 10; f++) Frame(f);
            KPSystem.RestorePhase();
            TestContext.WriteLine($"bustR 還原到 {rig.BustR.localPosition}（期望 {changedR}）；bustL {rig.BustL.localPosition}（期望 {restL}）");
            Assert.That((rig.BustR.localPosition - changedR).magnitude, Is.LessThan(1e-6f), "bustR 用新的靜止姿勢");
            Assert.That((rig.BustL.localPosition - restL).magnitude, Is.LessThan(1e-6f), "bustL 留在自己的靜止姿勢");
            Assert.That(rig.Body.IsSimulating(rig.Body.bones.IndexOf(rig.BustR)));
            Assert.That(rig.Body.IsSimulating(rig.Body.bones.IndexOf(rig.BustL)), Is.EqualTo(insert));
        }

        [Test(Description = "鏡像（父骨縮放 −1）：重力相對靜止姿勢時站著不動不偏；傾斜轉向『父骨 → 位移後的位置』（用實際的軸向比）")]
        public void MirroredHierarchyKeepsRestAndTiltDirection()
        {
            var rig = Build("mirror", Vector3.zero, register: false);
            rig.Chest.localScale = new Vector3(-1f, 1f, 1f);
            rig.BustL.localRotation = Quaternion.Euler(0f, 0f, 15f);
            rig.BustR.localRotation = Quaternion.Euler(20f, -10f, 0f);
            rig.Body.tilt = 1f;
            KPSystem.Register(rig.Body);
            var maxRest = 0f;
            for (int f = 0; f < 300; f++)
            {
                Frame(f);
                maxRest = Mathf.Max(maxRest, rig.Body.GetOffset(0).magnitude, rig.Body.GetOffset(1).magnitude);
            }

            // 傾斜：骨頭實際的軸（世界矩陣正規化的欄）＝ FromTo(arm, arm+d) × 靜止時的軸
            KPSystem.RestorePhase();
            var restAxes = new[] { rig.BustL.localToWorldMatrix.GetColumn(0), rig.BustL.localToWorldMatrix.GetColumn(1), rig.BustL.localToWorldMatrix.GetColumn(2) }
                .Select(c => ((Vector3)c).normalized).ToArray();
            var arm = rig.BustL.position - rig.Chest.position;
            KPSystem.SimulatePhase(Dt);
            var maxAxisErr = 0f;
            var maxTilt = 0f;
            for (int f = 300; f < 420; f++)
            {
                if (f == 305) rig.Body.AddImpulse(0, new Vector3(0.3f, 1f, 0.2f));
                Frame(f);
                var t = Quaternion.FromToRotation(arm, arm + rig.Body.GetOffset(0));
                var m = rig.BustL.localToWorldMatrix;
                for (int k = 0; k < 3; k++)
                    maxAxisErr = Mathf.Max(maxAxisErr, Vector3.Angle(((Vector3)m.GetColumn(k)).normalized, t * restAxes[k]));
                maxTilt = Mathf.Max(maxTilt, Vector3.Angle(((Vector3)m.GetColumn(1)).normalized, restAxes[1]));
            }
            TestContext.WriteLine($"鏡像：靜止時位移最大 {maxRest * 1000f:F4} mm；傾斜最大 {maxTilt:F2}°、軸向誤差最大 {maxAxisErr:F4}°");
            Assert.That(maxRest, Is.LessThan(1e-6f));
            Assert.That(maxTilt, Is.GreaterThan(5f), "有傾斜（測試有意義）");
            Assert.That(maxAxisErr, Is.LessThan(0.05f));
        }

        // ---- 第三次審查後追加：三層的上層祖先離開、清單重複、alpha 跳動、角色根翻轉、元件間搬骨頭 ---------------

        /// <summary>某根骨頭在事件（第 120 幀）之後，位移相對事件前的最大變化。</summary>
        static float MaxDeviationAfter(List<Vector3> offsets, int eventFrame)
        {
            var before = offsets[eventFrame - 1];
            var max = 0f;
            for (int f = eventFrame; f < offsets.Count; f++) max = Mathf.Max(max, (offsets[f] - before).magnitude);
            return max;
        }

        [TestCase("disable-same", TestName = "三層：上層祖先的元件停用（中、下層同一個元件），孫骨不被甩")]
        [TestCase("disable-split", TestName = "三層：上層祖先的元件停用（中、下層分開），孫骨不被甩")]
        [TestCase("remove", TestName = "三層：上層祖先從清單拿掉，孫骨不被甩")]
        public void UpperAncestorLeavingDoesNotKickGrandchild(string mode)
        {
            var rig = Build("grand", Vector3.zero, register: false);
            var mid = Child(rig.BustL, "mid", new Vector3(0f, 0f, 0.04f));
            var tip = Child(mid, "tip", new Vector3(0f, 0f, 0.04f));
            var top = rig.Body;                                   // 上層：bustL，下垂 27.6 mm
            top.bones.Clear();
            top.bones.Add(rig.BustL);
            top.gravityRelativeToRestPose = false;
            KawaiiPhysicsSoftBody NewBody(string name)
            {
                var go = new GameObject(name);
                go.transform.SetParent(rig.Root, false);
                var sb = go.AddComponent<KawaiiPhysicsSoftBody>();
                sb.referenceRoot = rig.Root;
                sb.gravity = Vector3.zero;
                sb.limitMin = top.limitMin;
                sb.limitMax = top.limitMax;
                return sb;
            }
            var lower = NewBody("lower");
            lower.bones.Add(mid);
            var tipBody = lower;
            if (mode == "disable-split") tipBody = NewBody("tipBody");
            tipBody.bones.Add(tip);
            KPSystem.Register(top);
            KPSystem.Register(lower);
            if (tipBody != lower) KPSystem.Register(tipBody);
            var midOffsets = new List<Vector3>();
            var tipOffsets = new List<Vector3>();
            for (int f = 0; f < 300; f++)
            {
                if (f == 120)
                {
                    if (mode == "remove")
                    {
                        top.bones.Remove(rig.BustL);
                        top.MarkDirty();
                    }
                    else KPSystem.Unregister(top);
                }
                Frame(f);
                midOffsets.Add(lower.GetOffset(lower.bones.IndexOf(mid)));
                tipOffsets.Add(tipBody.GetOffset(tipBody.bones.IndexOf(tip)));
            }
            var dMid = MaxDeviationAfter(midOffsets, 120);
            var dTip = MaxDeviationAfter(tipOffsets, 120);
            TestContext.WriteLine($"{mode}：事件後 中間骨 {dMid * 1000f:F4} mm、孫骨 {dTip * 1000f:F4} mm");
            Assert.That(dMid, Is.LessThan(1e-4f));
            Assert.That(dTip, Is.LessThan(1e-4f));
        }

        [Test(Description = "同一根骨頭在清單裡出現兩次：只算第一次（衝量不加倍、第二個 IsSimulating 是 false）")]
        public void DuplicateBoneInSameListCountsOnce()
        {
            var rig = Build("dupList", Vector3.zero, register: false);
            rig.Body.bones.Insert(1, rig.BustL);                   // [bustL, bustL, bustR]
            rig.Body.gravity = Vector3.zero;
            KPSystem.Register(rig.Body);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("bustL"));
            for (int f = 0; f < 3; f++) Frame(f);
            var v = new Vector3(0f, 0f, 1f);
            rig.Body.AddImpulse(v);
            rig.Body.AddImpulseAtPosition((rig.BustL.position + rig.BustR.position) * 0.5f, v, 0.16f);
            rig.Body.AddImpulse(1, v * 10f);                      // 重複的那一格：忽略
            Frame(3, dt: 0f);
            var l = rig.Body.GetVelocity(0).z;
            var r = rig.Body.GetVelocity(2).z;
            TestContext.WriteLine($"bustL {l:F4}、bustR {r:F4} m/s；IsSimulating(1)＝{rig.Body.IsSimulating(1)}");
            Assert.That(l, Is.EqualTo(1.25f).Within(1e-5f));
            Assert.That(r, Is.EqualTo(1.25f).Within(1e-5f));
            Assert.That(rig.Body.IsSimulating(0), Is.True);
            Assert.That(rig.Body.IsSimulating(1), Is.False);
            Assert.That(rig.Body.GetVelocity(1), Is.EqualTo(Vector3.zero));
        }

        [TestCase(0f, TestName = "祖先的 alpha 一幀內 1→0：子骨不被甩")]
        [TestCase(0.5f, TestName = "祖先的 alpha 一幀內 1→0.5：子骨不被甩")]
        [TestCase(-1f, TestName = "祖先的 alpha 漸變（每幀 1/60）：跟一次跳一樣，子骨不當成加速")]
        public void AncestorAlphaJump(float target)
        {
            var rig = Build("alphaJump", Vector3.zero, register: false);
            var tip = Child(rig.BustL, "tip", new Vector3(0f, 0f, 0.05f));
            var parent = rig.Body;
            parent.bones.Clear();
            parent.bones.Add(rig.BustL);
            parent.gravityRelativeToRestPose = false;
            var child = rig.Chest.gameObject.AddComponent<KawaiiPhysicsSoftBody>();
            child.referenceRoot = rig.Root;
            child.gravity = Vector3.zero;
            child.bones.Add(tip);
            KPSystem.Register(parent);
            KPSystem.Register(child);
            var offsets = new List<Vector3>();
            for (int f = 0; f < 300; f++)
            {
                if (target >= 0f && f == 120) parent.alpha = target;
                if (target < 0f && f >= 120) parent.alpha = Mathf.Max(0f, 1f - (f - 119) / 60f);
                Frame(f);
                offsets.Add(child.GetOffset(0));
            }
            var dev = MaxDeviationAfter(offsets, 120);
            TestContext.WriteLine($"alpha {(target < 0f ? "漸變" : target.ToString())}：子骨位移的變化最大 {dev * 1000f:F4} mm");
            // alpha 是動畫與物理的混合比例，不是力：漸變或一次跳，祖先輸出因此的移動都不給子孫慣性
            Assert.That(dev, Is.LessThan(1e-4f));
        }

        [TestCase(false, TestName = "角色根的縮放正負翻轉（站著）：當成瞬移，不被甩")]
        [TestCase(true, TestName = "角色根的縮放正負翻轉（走路中）：當成瞬移，不被甩")]
        public void RootMirrorFlipDoesNotKick(bool walking)
        {
            var rig = Build("flip", Vector3.zero);
            var max = 0f;
            for (int f = 0; f < 240; f++)
            {
                if (f == 120) rig.Root.localScale = new Vector3(-1f, 1f, 1f);
                Frame(f, k => { if (walking) rig.Root.position = new Vector3(1.5f * k * Dt, 0f, 0f); });
                if (f >= 120) max = Mathf.Max(max, rig.Body.GetOffset(0).magnitude, rig.Body.GetOffset(1).magnitude);
            }
            TestContext.WriteLine($"翻轉後的最大位移 {max * 1000f:F4} mm");
            Assert.That(max, Is.LessThan(1e-4f));
        }

        [Test(Description = "同一幀把骨頭從 A 搬到 B、改靜止姿勢、B 呼叫 CaptureRestPose：下次重建採用新姿勢")]
        public void CaptureRestPoseWhenMovingBoneBetweenComponents()
        {
            var rig = Build("moveBone", Vector3.zero);
            var b = rig.Chest.gameObject.AddComponent<KawaiiPhysicsSoftBody>();
            b.referenceRoot = rig.Root;
            KPSystem.Register(b);
            for (int f = 0; f < 5; f++) Frame(f);
            var changed = rig.BustR.localPosition + new Vector3(0f, 0.03f, 0f);
            KPSystem.RestorePhase();
            rig.Body.bones.Remove(rig.BustR);
            rig.Body.MarkDirty();
            b.bones.Add(rig.BustR);
            b.MarkDirty();
            rig.BustR.localPosition = changed;
            b.CaptureRestPose();
            KPSystem.SimulatePhase(Dt);
            for (int f = 6; f < 10; f++) Frame(f);
            KPSystem.RestorePhase();
            TestContext.WriteLine($"bustR 還原到 {rig.BustR.localPosition}（期望 {changed}）");
            Assert.That((rig.BustR.localPosition - changed).magnitude, Is.LessThan(1e-6f));
            Assert.That(b.IsSimulating(0), Is.True);
            Assert.That(rig.Body.IsSimulating(rig.Body.bones.IndexOf(rig.BustL)), Is.True);
        }

        [Test(Description = "Frame()：某一軸的縮放很小（1e-7）時也保留鏡像的正負，三軸正交")]
        public void FrameKeepsMirrorWithTinyAxis()
        {
            foreach (var s in new[] { new Unity.Mathematics.float3(-1f, 1f, 1e-7f), new Unity.Mathematics.float3(1f, -2f, 1e-7f), new Unity.Mathematics.float3(1f, 1f, 1e-7f) })
            {
                var m = Unity.Mathematics.float4x4.TRS(new Unity.Mathematics.float3(1f, 2f, 3f), Unity.Mathematics.quaternion.EulerXYZ(0.3f, 0.2f, 0.1f), s);
                var f = KPSoftBodySolver.Frame(m);
                var det = Unity.Mathematics.math.determinant(f);
                var expected = s.x * s.y * s.z < 0f ? -1f : 1f;
                Assert.That(det, Is.EqualTo(expected).Within(1e-4f), $"縮放 {s}");
                Assert.That(Unity.Mathematics.math.dot(f.c0, f.c1), Is.EqualTo(0f).Within(1e-5f));
                Assert.That(Unity.Mathematics.math.dot(f.c0, f.c2), Is.EqualTo(0f).Within(1e-5f));
                Assert.That(Unity.Mathematics.math.dot(f.c1, f.c2), Is.EqualTo(0f).Within(1e-5f));
            }
        }

        /// <summary>兩層（父 bustL 晃動中、子 tip 在另一個元件），mode 1 時第 60 幀加入、第 120 幀移除一個無關的角色。回傳子骨每幀的位移。</summary>
        List<Vector3> RunHierarchyIsolation(int mode, float alpha)
        {
            KPSystem.Shutdown();
            foreach (var go in created)
                if (go != null) Object.DestroyImmediate(go);
            created.Clear();
            var rig = Build("H", Vector3.zero, register: false);
            var tip = Child(rig.BustL, "tip", new Vector3(0f, 0f, 0.05f));
            rig.Body.bones.Clear();
            rig.Body.bones.Add(rig.BustL);
            rig.Body.alpha = alpha;
            var child = rig.Chest.gameObject.AddComponent<KawaiiPhysicsSoftBody>();
            child.referenceRoot = rig.Root;
            child.bones.Add(tip);
            KPSystem.Register(rig.Body);
            KPSystem.Register(child);
            Rig other = null;
            var result = new List<Vector3>();
            for (int f = 0; f < 200; f++)
            {
                if (mode == 1 && f == 60) other = Build("other", new Vector3(3f, 0f, 0f));
                if (mode == 1 && f == 120) KPSystem.Unregister(other.Body);
                if (f % 25 == 3) rig.Body.AddImpulse(0, new Vector3(0.3f, 0.6f, -0.4f));
                Frame(f, k => WalkAndTurn(rig.Root, k));
                result.Add(child.GetOffset(0));
            }
            return result;
        }

        [TestCase(1f, TestName = "無關的元件加入、移除造成重建：晃動中的兩層階層，子骨逐幀一致")]
        [TestCase(0.6f, TestName = "無關的元件加入、移除造成重建：父 alpha 0.6 時子骨逐幀一致")]
        public void RebuildKeepsHierarchyState(float alpha)
        {
            var control = RunHierarchyIsolation(0, alpha);
            var withRebuild = RunHierarchyIsolation(1, alpha);
            var maxDiff = control.Zip(withRebuild, (x, y) => (x - y).magnitude).Max();
            TestContext.WriteLine($"alpha {alpha}：子骨與對照組的最大差 {maxDiff:E2} m（子骨的最大位移 {control.Max(v => v.magnitude) * 1000f:F2} mm）");
            Assert.That(control.Max(v => v.magnitude), Is.GreaterThan(1e-3f), "子骨有在晃（測試有意義）");
            Assert.That(maxDiff, Is.EqualTo(0f));
        }

        // ---- 第四次審查後追加：祖先在晃時的事件、幀率、其他設定、縮放 0 時啟用、退化的矩陣 -------------------

        /// <summary>
        /// G（bustL）在晃：角色上下擺動走路＋每 40 幀一次衝量。P（mid，G 底下）幾乎是剛體（1000 Hz、臨界阻尼），
        /// 用額外加速度固定在 10 mm 的位移；C（tip，P 底下）是一般的彈簧。G 的位移只有平移，所以 P 的輸出改成任何常數，
        /// C 的錨點運動都跟對照組一樣：C 的位移應該逐幀一致。回傳 C 每幀的位移。
        /// </summary>
        List<Vector3> RunJigglingChain(int eventFrame, Action<KawaiiPhysicsSoftBody> onEvent, bool registerP = true, Action<KawaiiPhysicsSoftBody> setupP = null)
        {
            KPSystem.Shutdown();
            foreach (var go in created)
                if (go != null) Object.DestroyImmediate(go);
            created.Clear();
            var rig = Build("chain", Vector3.zero, register: false);
            var mid = Child(rig.BustL, "mid", new Vector3(0f, 0f, 0.04f));
            var tip = Child(mid, "tip", new Vector3(0f, 0f, 0.04f));
            var g = rig.Body;
            g.bones.Clear();
            g.bones.Add(rig.BustL);
            KawaiiPhysicsSoftBody NewBody(string name)
            {
                var go = new GameObject(name);
                go.transform.SetParent(rig.Root, false);
                var sb = go.AddComponent<KawaiiPhysicsSoftBody>();
                sb.referenceRoot = rig.Root;
                sb.gravity = Vector3.zero;
                sb.limitMin = g.limitMin;
                sb.limitMax = g.limitMax;
                return sb;
            }
            var p = NewBody("P");
            p.frequency = 1000f;
            p.dampingRatio = 1f;
            p.externalAcceleration = new Vector3(0.01f, 0f, 0f) * Mathf.Pow(2f * Mathf.PI * 1000f, 2f); // 往側邊 10 mm（傾斜才有作用）
            p.bones.Add(mid);
            setupP?.Invoke(p);
            var c = NewBody("C");
            c.bones.Add(tip);
            KPSystem.Register(g);
            KPSystem.Register(c);
            if (registerP) KPSystem.Register(p);
            var result = new List<Vector3>();
            for (int f = 0; f < 240; f++)
            {
                if (f % 40 == 5) g.AddImpulse(0, new Vector3(0f, 0.6f, 0.2f));
                if (f == eventFrame) onEvent?.Invoke(p);
                Frame(f, k => rig.Root.position = new Vector3(0f, 0.03f * Mathf.Sin(k * Dt * 2f * Mathf.PI * 1.7f), k * Dt * 1.2f));
                result.Add(c.GetOffset(0));
            }
            return result;
        }

        [TestCase("alpha-0.9", TestName = "祖先在晃時，中間層 alpha 1→0.9：孫骨逐幀一致")]
        [TestCase("alpha-0", TestName = "祖先在晃時，中間層 alpha 1→0：孫骨逐幀一致")]
        [TestCase("tilt", TestName = "祖先在晃時，中間層 tilt 0→1：孫骨逐幀一致")]
        [TestCase("stretch", TestName = "祖先在晃時，中間層 stretchAmount 0→1：孫骨逐幀一致")]
        [TestCase("join", TestName = "祖先在晃時，中間層的元件才啟用：孫骨逐幀一致")]
        public void AncestorEventWhileJigglingKeepsInertia(string mode)
        {
            var maxDev = 0f;
            var maxC = 0f;
            foreach (var at in new[] { 112, 119, 126, 133 })
            {
                List<Vector3> control, run;
                if (mode == "join")
                {
                    // 對照：P 從頭就在（位移 10 mm 是常數）；事件：P 在 at 才啟用
                    control = RunJigglingChain(-1, null);
                    run = RunJigglingChain(at, p => KPSystem.Register(p), registerP: false);
                    // 啟用前 C 的錨點少了 P 的 10 mm 常數：只比事件之後
                    for (int f = at; f < control.Count; f++) maxDev = Mathf.Max(maxDev, (control[f] - run[f]).magnitude);
                }
                else
                {
                    control = RunJigglingChain(-1, null, setupP: p => { if (mode == "stretch") p.stretchAxis = KawaiiPhysicsSoftBody.Axis.X; });
                    run = RunJigglingChain(at, p =>
                    {
                        switch (mode)
                        {
                            case "alpha-0.9": p.alpha = 0.9f; break;
                            case "alpha-0": p.alpha = 0f; break;
                            case "tilt": p.tilt = 1f; break;
                            case "stretch": p.stretchAmount = 1f; break;
                        }
                    }, setupP: p => { if (mode == "stretch") p.stretchAxis = KawaiiPhysicsSoftBody.Axis.X; });
                    for (int f = 0; f < control.Count; f++) maxDev = Mathf.Max(maxDev, (control[f] - run[f]).magnitude);
                }
                maxC = Mathf.Max(maxC, control.Max(v => v.magnitude));
            }
            TestContext.WriteLine($"{mode}：孫骨與對照組的最大差 {maxDev * 1000f:F4} mm（孫骨的最大位移 {maxC * 1000f:F2} mm，事件在 4 個相位）");
            Assert.That(maxC, Is.GreaterThan(3e-3f), "孫骨有在晃（測試有意義）");
            Assert.That(maxDev, Is.LessThan(1e-4f));
        }

        [Test(Description = "祖先在晃時，停用位移幾乎是 0 的中間層：孫骨跟『中間層從來沒模擬』的對照組一致")]
        public void RemovingZeroOffsetMiddleWhileJiggling()
        {
            var maxDev = 0f;
            foreach (var at in new[] { 112, 119, 126, 133 })
            {
                var control = RunJigglingChain(-1, null, registerP: false);
                var run = RunJigglingChain(at, p => KPSystem.Unregister(p), setupP: p => p.externalAcceleration = Vector3.zero);
                for (int f = 0; f < control.Count; f++) maxDev = Mathf.Max(maxDev, (control[f] - run[f]).magnitude);
            }
            TestContext.WriteLine($"孫骨與對照組的最大差 {maxDev * 1000f:F4} mm");
            Assert.That(maxDev, Is.LessThan(1e-4f));
        }

        [Test(Description = "同一段 0.25 秒的 alpha 漸變：30／60／120 fps 都一樣，子骨不當成加速（不依幀率，沒有門檻）")]
        public void AlphaFadeIsFramerateIndependent()
        {
            float Run(float dt)
            {
                KPSystem.Shutdown();
                var rig = Build("fade" + dt, Vector3.zero, register: false);
                var tip = Child(rig.BustL, "tip", new Vector3(0f, 0f, 0.05f));
                rig.Body.bones.Clear();
                rig.Body.bones.Add(rig.BustL);
                rig.Body.gravityRelativeToRestPose = false;
                var child = rig.Chest.gameObject.AddComponent<KawaiiPhysicsSoftBody>();
                child.referenceRoot = rig.Root;
                child.gravity = Vector3.zero;
                child.bones.Add(tip);
                KPSystem.Register(rig.Body);
                KPSystem.Register(child);
                var steps = Mathf.RoundToInt(2f / dt);
                var start = Mathf.RoundToInt(1f / dt);
                var max = 0f;
                for (int f = 0; f < steps; f++)
                {
                    var t = (f - start + 1) * dt;
                    if (f >= start) rig.Body.alpha = Mathf.Clamp01(1f - t / 0.25f);
                    Frame(f, dt: dt);
                    if (f >= start) max = Mathf.Max(max, child.GetOffset(0).magnitude);
                }
                return max;
            }
            var at60 = Run(1f / 60f);
            var at120 = Run(1f / 120f);
            var at30 = Run(1f / 30f);
            TestContext.WriteLine($"子骨的最大位移：30 fps {at30 * 1000f:F3}、60 fps {at60 * 1000f:F3}、120 fps {at120 * 1000f:F3} mm");
            Assert.That(at30, Is.LessThan(1e-4f));
            Assert.That(at60, Is.LessThan(1e-4f));
            Assert.That(at120, Is.LessThan(1e-4f));
        }

        [TestCase("tilt", TestName = "祖先的 tilt 一幀內改變：子骨不被甩")]
        [TestCase("stretch", TestName = "祖先的 stretchAmount 一幀內改變：子骨不被甩")]
        [TestCase("limit", TestName = "祖先的位移上限一幀內改變：子骨不被甩")]
        public void AncestorSettingJump(string mode)
        {
            var rig = Build("settingJump", Vector3.zero, register: false);
            var tip = Child(rig.BustL, "tip", new Vector3(0f, 0f, 0.05f));
            var parent = rig.Body;
            parent.bones.Clear();
            parent.bones.Add(rig.BustL);
            parent.gravityRelativeToRestPose = false;               // 下垂 27.6 mm
            parent.stretchAxis = KawaiiPhysicsSoftBody.Axis.Y;
            var child = rig.Chest.gameObject.AddComponent<KawaiiPhysicsSoftBody>();
            child.referenceRoot = rig.Root;
            child.gravity = Vector3.zero;
            child.bones.Add(tip);
            KPSystem.Register(parent);
            KPSystem.Register(child);
            var offsets = new List<Vector3>();
            for (int f = 0; f < 300; f++)
            {
                if (f == 120)
                {
                    if (mode == "tilt") parent.tilt = 1f;
                    if (mode == "stretch") parent.stretchAmount = 1f;
                    if (mode == "limit") parent.limitMin = new Vector3(-0.1f, -0.01f, -0.1f);
                }
                Frame(f);
                offsets.Add(child.GetOffset(0));
            }
            var dev = MaxDeviationAfter(offsets, 120);
            TestContext.WriteLine($"{mode}：子骨位移的變化最大 {dev * 1000f:F4} mm");
            Assert.That(dev, Is.LessThan(1e-4f));
        }

        [Test(Description = "角色在縮放 0 時啟用：重力參考等到第一次能用的幀再記，之後站著不動不偏")]
        public void EnabledAtZeroScaleCapturesGravityReferenceLater()
        {
            var rig = Build("zeroScale", Vector3.zero, register: false);
            rig.Chest.localRotation = Quaternion.Euler(-90f, 0f, 0f);  // 胸的軸相對角色根轉 90°（匯入的骨架常見）
            rig.Root.localScale = Vector3.zero;
            KPSystem.Register(rig.Body);
            Frame(0);
            rig.Root.localScale = Vector3.one;
            var max = 0f;
            for (int f = 1; f < 300; f++)
            {
                Frame(f);
                if (f > 2) max = Mathf.Max(max, rig.Body.GetOffset(0).magnitude, rig.Body.GetOffset(1).magnitude);
            }
            TestContext.WriteLine($"站著不動的最大位移 {max * 1000f:F4} mm");
            Assert.That(rig.Body.IsSimulating(0));
            Assert.That(max, Is.LessThan(1e-6f));
        }

        [Test(Description = "父骨某一軸縮放剛好是 0（秩不足）：那幾幀不模擬，不產生 NaN，也不會一直重新初始化；恢復後照常")]
        public void RankDeficientParentIsSkipped()
        {
            var rig = Build("rank", Vector3.zero);
            for (int f = 0; f < 10; f++) Frame(f);
            rig.Chest.localScale = new Vector3(1f, 0f, 1f);
            for (int f = 10; f < 70; f++)
            {
                Frame(f, k => rig.Root.rotation = Quaternion.Euler(0f, k * 0.5f, 0f));
                Assert.That(Finite(rig.BustL.position) && Finite(rig.Body.GetOffset(0)), $"第 {f} 幀");
            }
            rig.Chest.localScale = Vector3.one;
            rig.Body.AddImpulse(0, new Vector3(0f, 0f, 1f));
            var max = 0f;
            for (int f = 70; f < 100; f++)
            {
                Frame(f);
                max = Mathf.Max(max, rig.Body.GetOffset(0).magnitude);
            }
            Assert.That(max, Is.GreaterThan(1e-3f), "恢復後照常模擬");
        }

        // ---- 第五次審查後追加：祖先在重建時被重置、祖先只略過一幀 ---------------------------------------

        KawaiiPhysicsSoftBody NewSoftBody(Transform parent, string name, KawaiiPhysicsSoftBody like)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var sb = go.AddComponent<KawaiiPhysicsSoftBody>();
            sb.referenceRoot = like.referenceRoot;
            sb.gravity = like.gravity;
            sb.gravityRelativeToRestPose = like.gravityRelativeToRestPose;
            sb.limitMin = like.limitMin;
            sb.limitMax = like.limitMax;
            return sb;
        }

        [TestCase("reenable-same", TestName = "父骨的元件同一幀停用再啟用（重建時重置）：另一個元件的子骨不被甩")]
        [TestCase("root-change", TestName = "改父骨元件的 referenceRoot（重建時重置）：子骨不被甩")]
        [TestCase("move-bone", TestName = "父骨搬到另一個元件（重建時重置）：子骨不被甩")]
        public void AncestorReinitThroughRebuildDoesNotKick(string mode)
        {
            var rig = Build("rebuildReset", Vector3.zero, register: false);
            var tip = Child(rig.BustL, "tip", new Vector3(0f, 0f, 0.05f));
            var parent = rig.Body;
            parent.bones.Clear();
            parent.bones.Add(rig.BustL);
            parent.gravityRelativeToRestPose = false;             // 下垂 27.6 mm
            var spare = NewSoftBody(rig.Root, "spare", parent);
            var child = NewSoftBody(rig.Root, "child", parent);
            child.gravity = Vector3.zero;
            child.bones.Add(tip);
            KPSystem.Register(parent);
            KPSystem.Register(child);
            KPSystem.Register(spare);
            var offsets = new List<Vector3>();
            for (int f = 0; f < 300; f++)
            {
                if (f == 120)
                {
                    switch (mode)
                    {
                        case "reenable-same":
                            KPSystem.Unregister(parent);
                            KPSystem.Register(parent);
                            break;
                        case "root-change":
                            parent.referenceRoot = rig.Chest;
                            parent.MarkDirty();
                            break;
                        case "move-bone":
                            parent.bones.Remove(rig.BustL);
                            parent.MarkDirty();
                            spare.bones.Add(rig.BustL);
                            spare.MarkDirty();
                            break;
                    }
                }
                Frame(f);
                offsets.Add(child.GetOffset(0));
            }
            var dev = MaxDeviationAfter(offsets, 120);
            TestContext.WriteLine($"{mode}：子骨位移的變化最大 {dev * 1000f:F4} mm");
            Assert.That(dev, Is.LessThan(1e-4f));
        }

        [TestCase("enabled", TestName = "中間層只略過一幀（元件 enabled 關一幀）：孫骨不被甩")]
        [TestCase("root-scale0", TestName = "中間層只略過一幀（它的 referenceRoot 縮放 0 一幀）：孫骨不被甩")]
        public void MiddleSkippedOneFrameDoesNotKick(string mode)
        {
            var rig = Build("skipOne", Vector3.zero, register: false);
            var mid = Child(rig.BustL, "mid", new Vector3(0f, 0f, 0.04f));
            var tip = Child(mid, "tip", new Vector3(0f, 0f, 0.04f));
            var rootB = Child(rig.Root, "rootB", Vector3.zero);
            var g = rig.Body;
            g.bones.Clear();
            g.bones.Add(rig.BustL);
            g.gravityRelativeToRestPose = false;                  // 下垂 27.6 mm
            var p = NewSoftBody(rig.Root, "P", g);
            p.referenceRoot = rootB;
            p.gravity = Vector3.zero;                             // 位移 0
            p.bones.Add(mid);
            var c = NewSoftBody(rig.Root, "C", g);
            c.gravity = Vector3.zero;
            c.bones.Add(tip);
            KPSystem.Register(g);
            KPSystem.Register(c);
            KPSystem.Register(p);
            var offsets = new List<Vector3>();
            var syncs = 0;
            for (int f = 0; f < 300; f++)
            {
                if (f == 120)
                {
                    syncs = KPSoftBodySystem.SyncCount;
                    if (mode == "enabled") p.enabled = false;     // 編輯模式下不呼叫 OnDisable：沒有重建，那一幀略過
                    else rootB.localScale = Vector3.zero;
                }
                if (f == 121)
                {
                    if (mode == "enabled") p.enabled = true;
                    else rootB.localScale = Vector3.one;
                }
                Frame(f);
                offsets.Add(c.GetOffset(0));
            }
            var dev = MaxDeviationAfter(offsets, 120);
            TestContext.WriteLine($"{mode}：孫骨位移的變化最大 {dev * 1000f:F4} mm（重建 {KPSoftBodySystem.SyncCount - syncs} 次）");
            Assert.That(KPSoftBodySystem.SyncCount, Is.EqualTo(syncs), "沒有重建（只是略過）");
            Assert.That(dev, Is.LessThan(1e-4f));
        }

        [Test(Description = "走路中父骨在晃：同一幀停用再啟用，跟 ResetDynamics 的結果逐幀一樣（父、子骨）")]
        public void ReenableSameFrameMatchesResetDynamicsWhileWalking()
        {
            var maxChild = 0f;
            var maxParent = 0f;
            var maxC = 0f;
            foreach (var at in new[] { 112, 119, 126, 133 })
            {
                var runs = new List<(List<Vector3> parent, List<Vector3> child)>();
                foreach (var reenable in new[] { false, true })
                {
                    KPSystem.Shutdown();
                    foreach (var go in created)
                        if (go != null) Object.DestroyImmediate(go);
                    created.Clear();
                    var rig = Build("walkReset", Vector3.zero, register: false);
                    var tip = Child(rig.BustL, "tip", new Vector3(0f, 0f, 0.05f));
                    var parent = rig.Body;
                    parent.bones.Clear();
                    parent.bones.Add(rig.BustL);
                    var child = NewSoftBody(rig.Root, "child", parent);
                    child.bones.Add(tip);
                    KPSystem.Register(parent);
                    KPSystem.Register(child);
                    var po = new List<Vector3>();
                    var co = new List<Vector3>();
                    for (int f = 0; f < 240; f++)
                    {
                        if (f % 40 == 5) parent.AddImpulse(0, new Vector3(0f, 0.6f, 0.2f));
                        if (f == at)
                        {
                            if (reenable)
                            {
                                KPSystem.Unregister(parent);
                                KPSystem.Register(parent);
                            }
                            else parent.ResetDynamics();
                        }
                        Frame(f, k => rig.Root.position = new Vector3(0f, 0.03f * Mathf.Sin(k * Dt * 2f * Mathf.PI * 1.7f), k * Dt * 1.2f));
                        po.Add(parent.GetOffset(0));
                        co.Add(child.GetOffset(0));
                    }
                    runs.Add((po, co));
                }
                maxParent = Mathf.Max(maxParent, runs[0].parent.Zip(runs[1].parent, (x, y) => (x - y).magnitude).Max());
                maxChild = Mathf.Max(maxChild, runs[0].child.Zip(runs[1].child, (x, y) => (x - y).magnitude).Max());
                maxC = Mathf.Max(maxC, runs[0].child.Max(v => v.magnitude));
            }
            TestContext.WriteLine($"停用再啟用 vs ResetDynamics：父骨差 {maxParent * 1000f:F4} mm、子骨差 {maxChild * 1000f:F4} mm（子骨最大位移 {maxC * 1000f:F2} mm，4 個相位）");
            Assert.That(maxC, Is.GreaterThan(3e-3f), "子骨有在晃（測試有意義）");
            Assert.That(maxParent, Is.LessThan(1e-5f));
            Assert.That(maxChild, Is.LessThan(1e-4f));
        }

        // ---- 極端輸入 -----------------------------------------------------------

        static IEnumerable<TestCaseData> ExtremeCases()
        {
            TestCaseData Case(string name, Action<Rig> setup, Func<int, float> dt = null) =>
                new TestCaseData(setup, dt).SetName("軟組織極端輸入：" + name);
            yield return Case("dt 0、1e-7、1、10、負值、NaN、無限大", r => { }, f => new[] { 0f, 1e-7f, 1f, 10f, -1f, float.NaN, float.PositiveInfinity, Dt }[f % 8]);
            yield return Case("頻率 0", r => r.Body.frequency = 0f);
            yield return Case("頻率 1e6", r => r.Body.frequency = 1e6f);
            yield return Case("阻尼比 0", r => r.Body.dampingRatio = 0f);
            yield return Case("阻尼比 1e6", r => r.Body.dampingRatio = 1e6f);
            yield return Case("上限全 0", r => { r.Body.limitMin = Vector3.zero; r.Body.limitMax = Vector3.zero; });
            yield return Case("上限 1e6", r => { r.Body.limitMin = -Vector3.one * 1e6f; r.Body.limitMax = Vector3.one * 1e6f; });
            yield return Case("重力 1e6", r => r.Body.gravity = new Vector3(0f, -1e6f, 0f));
            yield return Case("重力 NaN", r => r.Body.gravity = new Vector3(float.NaN, 0f, 0f));
            yield return Case("額外加速度無限大", r => r.Body.externalAcceleration = new Vector3(0f, float.PositiveInfinity, 0f));
            yield return Case("骨頭縮放 0", r => r.BustL.localScale = Vector3.zero);
            yield return Case("父骨縮放 0", r => r.Chest.localScale = Vector3.zero);
            yield return Case("骨頭與父骨同位置（傾斜）", r => { r.BustL.localPosition = Vector3.zero; r.Body.tilt = 1f; });
            yield return Case("錨點在球碰撞體裡", r =>
            {
                var c = AddCollider(r.Root, KawaiiPhysicsCollider.Shape.Sphere, r.BustL.position);
                c.radius = 0.05f;
                r.Body.colliders.Add(c);
            });
            yield return Case("內側球比骨頭還小", r =>
            {
                var c = AddCollider(r.Root, KawaiiPhysicsCollider.Shape.Sphere, r.BustL.position);
                c.radius = 0.001f;
                c.inner = true;
                r.Body.colliders.Add(c);
            });
            yield return Case("箱子中心＝錨點", r =>
            {
                var c = AddCollider(r.Root, KawaiiPhysicsCollider.Shape.Box, r.BustL.position);
                c.extent = Vector3.zero;
                r.Body.colliders.Add(c);
            });
            yield return Case("伸長 −1、體積 1", r => { r.Body.stretchAmount = -1f; r.Body.volumePreservation = 1f; });
            yield return Case("慣性 2、子步進 16", r => { r.Body.animationInertia = 2f; r.Body.movementInertia = 2f; r.Body.maxSubsteps = 16; r.Body.targetFramerate = 100000; });
        }

        [TestCaseSource(nameof(ExtremeCases))]
        public void ExtremeInputsProduceNoNaN(Action<Rig> setup, Func<int, float> dt)
        {
            var rig = Build("extreme", Vector3.zero, register: false);
            setup(rig);
            KPSystem.Register(rig.Body);
            var bad = 0;
            for (int f = 0; f < 120; f++)
            {
                if (f % 10 == 0) rig.Body.AddImpulse(new Vector3(1e6f, -1e6f, 0f));
                if (f == 50) rig.Body.AddImpulse(0, new Vector3(float.NaN, 0f, 0f));
                if (f == 70) rig.Body.AddImpulseAtPosition(rig.BustL.position, new Vector3(0f, float.PositiveInfinity, 0f), 1f);
                Frame(f, k =>
                {
                    WalkAndTurn(rig.Root, k);
                    if (k == 90) rig.Root.position = new Vector3(1e5f, 0f, 0f);
                }, dt?.Invoke(f) ?? Dt);
                foreach (var t in new[] { rig.BustL, rig.BustR })
                    if (!Finite(t.localPosition) || !Finite(t.localRotation) || !Finite(t.localScale) || !Finite(t.position)) bad++;
                for (int i = 0; i < 2; i++)
                    if (!Finite(rig.Body.GetOffset(i)) || !Finite(rig.Body.GetVelocity(i))) bad++;
            }
            Assert.That(bad, Is.EqualTo(0));
        }

        // ---- 0 GC、後端一致 -------------------------------------------------------

        /// <summary>胸（2 骨，兩顆球碰撞體、傾斜、伸縮）＋臀（2 骨，一顆球）兩個元件的角色。</summary>
        (Rig rig, KawaiiPhysicsSoftBody hip) BuildBustAndHip(string name, Vector3 position)
        {
            var rig = Build(name, position);
            rig.Body.tilt = 0.5f;
            rig.Body.stretchAmount = 0.3f;
            for (int k = 0; k < 2; k++)
            {
                var col = AddCollider(rig.Root, KawaiiPhysicsCollider.Shape.Sphere, rig.Root.position + new Vector3(k * 0.2f - 0.1f, 1.2f, 0.25f));
                rig.Body.colliders.Add(col);
            }
            var hipGo = new GameObject("hip");
            hipGo.transform.SetParent(rig.Root, false);
            var pelvis = Child(rig.Root, "pelvis", new Vector3(0f, 0.9f, 0f));
            var hip = hipGo.AddComponent<KawaiiPhysicsSoftBody>();
            hip.referenceRoot = rig.Root;
            hip.bones.Add(Child(pelvis, "hipL", new Vector3(-0.09f, -0.05f, -0.1f)));
            hip.bones.Add(Child(pelvis, "hipR", new Vector3(0.09f, -0.05f, -0.1f)));
            hip.colliders.Add(rig.Body.colliders[0]);
            KPSystem.Register(hip);
            return (rig, hip);
        }

        /// <summary>16 個角色 × 胸＋臀兩個元件，每幀加衝量。回傳每幀的 GC 配置。</summary>
        long[] MeasureAllocations()
        {
            KPSystem.Shutdown();
            var rigs = new List<Rig>();
            var hips = new List<KawaiiPhysicsSoftBody>();
            for (int c = 0; c < 16; c++)
            {
                var (rig, hip) = BuildBustAndHip($"gc{c}", new Vector3(c * 2f, 0f, 0f));
                rigs.Add(rig);
                hips.Add(hip);
            }
            var perFrame = new long[600];
            for (int f = 0; f < 660; f++)
            {
                var a0 = KPTestAlloc.Now();
                for (int i = 0; i < rigs.Count; i++)
                {
                    rigs[i].Body.AddImpulseAtPosition(rigs[i].BustL.position, new Vector3(0f, 0.1f, 0f), 0.2f);
                    hips[i].AddImpulse(0, new Vector3(0f, 0f, 0.05f));
                    hips[i].AddImpulse(new Vector3(0f, 0.01f, 0f));
                }
                KPSystem.RestorePhase();
                var a1 = KPTestAlloc.Now();
                for (int i = 0; i < rigs.Count; i++) WalkAndTurn(rigs[i].Root, f % 240);
                var a2 = KPTestAlloc.Now();
                KPSystem.SimulatePhase(Dt);
                var a3 = KPTestAlloc.Now();
                if (f >= 60) perFrame[f - 60] = Math.Max(0, a1 - a0) + Math.Max(0, a3 - a2);
            }
            return perFrame;
        }

        [Test(Description = "軟組織穩態每幀 0 GC（16 角色 × 2 元件，含衝量 API）。量兩次、每幀取較小的（排除編輯器其他執行緒的雜訊）")]
        public void ZeroAllocationSteadyState()
        {
            KPTestAlloc.Init();
            Assume.That(KPTestAlloc.Precise, "這個環境量不到小配置：" + KPTestAlloc.Describe());
            var first = MeasureAllocations();
            var second = MeasureAllocations();
            var both = first.Zip(second, Math.Min).ToArray();
            var frames = both.Select((v, i) => (v, i)).Where(p => p.v > 0).Select(p => $"{p.i}:{p.v}B").Take(10).ToList();
            TestContext.WriteLine($"第一次合計 {first.Sum()} B、第二次 {second.Sum()} B；兩次都有配置的幀：{string.Join(", ", frames)}");
            Assert.That(KPSystem.SoftBodyCount, Is.EqualTo(32));
            Assert.That(both.Sum(), Is.EqualTo(0), "兩次都在同一幀配置：" + string.Join(", ", frames));
        }

        [TestCase(16)]
        [TestCase(128)]
        [Category("Performance")]
        public void SoftBodyPipelineCost(int characters)
        {
            KPTestSettings.DisableSafetyForTiming();
            try
            {
                var rigs = new List<Rig>();
                for (int c = 0; c < characters; c++) rigs.Add(BuildBustAndHip($"perf{c}", new Vector3(c * 2f, 0f, 0f)).rig);
                var ms = new List<double>();
                var sw = new System.Diagnostics.Stopwatch();
                for (int f = 0; f < 360; f++)
                {
                    for (int i = 0; i < rigs.Count; i++) WalkAndTurn(rigs[i].Root, (f + i) % 240);
                    sw.Restart();
                    KPSystem.RestorePhase();
                    KPSystem.SimulatePhase(Dt);
                    if (f >= 60) ms.Add(sw.Elapsed.TotalMilliseconds);
                }
                // 加入／移除一個角色那一幀（軟組織整批重建，跟元件總數成正比）
                var add = new List<double>();
                var remove = new List<double>();
                for (int k = 0; k < 11; k++)
                {
                    var extra = BuildBustAndHip($"extra{k}", new Vector3(0f, 0f, -6f));
                    sw.Restart();
                    KPSystem.RestorePhase();
                    if (k > 0) add.Add(sw.Elapsed.TotalMilliseconds);
                    KPSystem.SimulatePhase(Dt);
                    KPSystem.Unregister(extra.rig.Body);
                    KPSystem.Unregister(extra.hip);
                    sw.Restart();
                    KPSystem.RestorePhase();
                    if (k > 0) remove.Add(sw.Elapsed.TotalMilliseconds);
                    KPSystem.SimulatePhase(Dt);
                }
                ms.Sort();
                TestContext.WriteLine($"{characters} 角色 × 2 元件（{KPSystem.SoftBoneCount} 骨）：每幀平均 {ms.Average():0.000} ms、中位 {ms[ms.Count / 2]:0.000} ms、" +
                                      $"p99 {ms[(int)(ms.Count * 0.99)]:0.000} ms；加入一個角色 平均 {add.Average():0.000} ms、最大 {add.Max():0.000} ms；" +
                                      $"移除 平均 {remove.Average():0.000} ms、最大 {remove.Max():0.000} ms");
                Assert.That(KPSystem.SoftBodyCount, Is.EqualTo(characters * 2));
                Assert.That(ms[ms.Count / 2], Is.LessThan(characters <= 16 ? 0.5 : 2.0));
            }
            finally
            {
                KPTestSettings.EnableSafety();
            }
        }

        List<Vector3> RunBackend(KPBackend backend)
        {
            KPSystem.Shutdown();
            KPSystem.Backend = backend;
            var rig = Build("backend_" + backend, Vector3.zero);
            rig.Body.tilt = 0.5f;
            rig.Body.stretchAmount = 0.3f;
            var col = AddCollider(rig.Root, KawaiiPhysicsCollider.Shape.Capsule, rig.BustL.position + new Vector3(0f, -0.06f, 0.02f));
            col.direction = KawaiiPhysicsCollider.Axis.X;
            col.radius = 0.02f;
            rig.Body.colliders.Add(col);
            var result = new List<Vector3>();
            for (int f = 0; f < 300; f++)
            {
                if (f % 30 == 0) rig.Body.AddImpulse(new Vector3(0.2f, -1f, 0.5f));
                Frame(f, k => WalkAndTurn(rig.Root, k));
                result.Add(rig.BustL.position);
                result.Add(rig.BustR.position);
            }
            return result;
        }

        [Test(Description = "Burst 平行、Burst 單執行緒、C# 三種後端的結果一致")]
        public void BackendsAgree()
        {
            var parallel = RunBackend(KPBackend.BurstParallel);
            var single = RunBackend(KPBackend.BurstSingleThread);
            var managed = RunBackend(KPBackend.Managed);
            var ds = parallel.Zip(single, (a, b) => (a - b).magnitude).Max();
            var dm = parallel.Zip(managed, (a, b) => (a - b).magnitude).Max();
            TestContext.WriteLine($"平行 vs 單執行緒 {ds:E2} m、平行 vs C# {dm:E2} m");
            Assert.That(ds, Is.EqualTo(0f));
            Assert.That(dm, Is.LessThan(1e-5f));
        }

        // ---- 事件 ---------------------------------------------------------------

        [Test(Description = "事件預設 SoftBodyImpulse：角色根區域空間的速度變化，依名稱篩選，Chain 不受影響")]
        public void EventPresetAppliesSoftBodyImpulse()
        {
            var rig = Build("events", Vector3.zero, register: false);
            Object.DestroyImmediate(rig.Body);
            var bustGo = new GameObject("BustBody");
            bustGo.transform.SetParent(rig.Root, false);
            var bust = bustGo.AddComponent<KawaiiPhysicsSoftBody>();
            bust.referenceRoot = rig.Root;
            bust.bones.Add(rig.BustL);
            var hipGo = new GameObject("HipBody");
            hipGo.transform.SetParent(rig.Root, false);
            var hip = hipGo.AddComponent<KawaiiPhysicsSoftBody>();
            hip.referenceRoot = rig.Root;
            hip.bones.Add(rig.BustR);
            KPSystem.Register(bust);
            KPSystem.Register(hip);
            rig.Root.rotation = Quaternion.Euler(0f, 90f, 0f);
            var events = rig.Root.gameObject.AddComponent<KawaiiPhysicsAnimationEvents>();
            for (int f = 0; f < 3; f++) Frame(f);

            var preset = ScriptableObject.CreateInstance<KawaiiPhysicsEventPreset>();
            try
            {
                preset.kind = KawaiiPhysicsEventPreset.Kind.SoftBodyImpulse;
                preset.softBodyImpulse = new Vector3(0f, 0f, 1f);
                preset.softBodyNameFilter = "Bust";
                events.KawaiiPhysicsEvent(preset);
                Frame(3, dt: 0f);
                Assert.That((bust.GetVelocity(0) - new Vector3(1f, 0f, 0f)).magnitude, Is.LessThan(1e-5f), "角色根轉 90° → 世界 +X");
                Assert.That(hip.GetVelocity(0).magnitude, Is.LessThan(1e-6f), "名稱不符的不受影響");

                events.KawaiiPhysicsSetAlpha(0.25f);
                Assert.That(bust.alpha, Is.EqualTo(0.25f));
                events.KawaiiPhysicsResetDynamics();
                Frame(4, dt: 0f);
                Assert.That(bust.GetVelocity(0).magnitude, Is.LessThan(1e-6f));
            }
            finally
            {
                Object.DestroyImmediate(preset);
            }
        }
    }
}
