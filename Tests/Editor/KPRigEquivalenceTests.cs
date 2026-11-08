// 陣列接口（KPRig）跟 Transform 版（KawaiiPhysicsChain＋KPSystem）在同一個角色、同樣的輸入下結果一致：
// 結構（骨頭數、骨長、骨長比例、旗標、父子、約束、碰撞體數）一樣；動態前 10 幀 ≤ 1e-5 m、120 幀內 ≤ 1 mm。
// 另外是 KPRig 自己的行為：父子順序的檢查、會寫的骨頭清單、工作還在跑時 Dispose、三種後端、穩態 0 GC。
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KawaiiPhysicsUnity.Tests
{
    public class KPRigEquivalenceTests
    {
        const float Dt = 1f / 60f;
        const int Frames = 120;
        const int RandomSeed = 1234;

        [SetUp]
        public void SetUp()
        {
            KPTestSettings.EnableSafety();
            Cleanup();
        }

        [TearDown]
        public void TearDown()
        {
            Cleanup();
            KPSystem.Backend = KPBackend.BurstParallel;
        }

        static void Cleanup()
        {
            KPSystem.Shutdown();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include))
                if (go != null && go.transform.parent == null) Object.DestroyImmediate(go);
        }

        // ---- 從 Transform 角色做出同一個骨架與設定 -----------------------------------------

        /// <summary>root 底下的 Transform（不含 root）照深度優先列出：父一定在子之前，兄弟照 GetChild 的順序。</summary>
        sealed class Skeleton
        {
            public readonly List<Transform> Bones = new List<Transform>();
            public int[] Parents;
            public string[] Names;
            public Vector3[] Positions, Scales;
            public Quaternion[] Rotations;
            public bool[] Colliders;
            public float4x4[] Model;
            public quaternion[] ModelRotation;

            public Skeleton(Transform root)
            {
                void Walk(Transform t)
                {
                    for (int i = 0; i < t.childCount; i++)
                    {
                        Bones.Add(t.GetChild(i));
                        Walk(t.GetChild(i));
                    }
                }
                Walk(root);
                var index = new Dictionary<Transform, int>();
                for (int i = 0; i < Bones.Count; i++) index[Bones[i]] = i;
                var n = Bones.Count;
                Parents = new int[n];
                Names = new string[n];
                Positions = new Vector3[n];
                Scales = new Vector3[n];
                Rotations = new Quaternion[n];
                Colliders = new bool[n];
                Model = new float4x4[n];
                ModelRotation = new quaternion[n];
                for (int i = 0; i < n; i++)
                {
                    var t = Bones[i];
                    Parents[i] = t.parent == root ? -1 : index[t.parent];
                    Names[i] = t.name;
                    Positions[i] = t.localPosition;
                    Rotations[i] = t.localRotation;
                    Scales[i] = t.localScale;
                    Colliders[i] = t.TryGetComponent<KawaiiPhysicsCollider>(out _);
                    var local = float4x4.TRS(Positions[i], Rotations[i], Scales[i]);
                    Model[i] = Parents[i] < 0 ? local : math.mul(Model[Parents[i]], local);
                    ModelRotation[i] = Parents[i] < 0 ? (quaternion)Rotations[i] : math.mul(ModelRotation[Parents[i]], Rotations[i]);
                }
            }

            public KPRig CreateRig() => new KPRig(Parents, Names, Positions, Rotations, Scales, Colliders);
        }

        /// <summary>元件的設定換成這個骨架的索引（元件＝骨架的根 → -1），外力複製成獨立的一份。要在模擬之前做。</summary>
        static KPRigChain ToRigChain(KawaiiPhysicsChain chain, Skeleton sk)
        {
            var h = new KPTransformHierarchy();
            foreach (var t in sk.Bones) h.Id(t); // 節點 id＝骨架的索引
            chain.CopyToSettings(h, true);
            var r = new KPRigChain();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(chain.settings), r);
            if (r.component >= sk.Bones.Count) r.component = -1;
            r.externalForces = chain.externalForces
                .Select(f => f == null ? null : (KPExternalForce)JsonUtility.FromJson(JsonUtility.ToJson(f), f.GetType())).ToList();
            r.rngSeed = chain.GetHashCode();
            r.needWarmUp = chain.needWarmUp;
            r.enabled = true;
            return r;
        }

        /// <summary>Output 套回靜止姿勢、從根算到世界：每根骨頭的世界位置。</summary>
        static Vector3[] WorldPositions(KPRig rig, Skeleton sk, Matrix4x4 rootToWorld)
        {
            var n = sk.Bones.Count;
            var model = new Matrix4x4[n];
            var result = new Vector3[n];
            var output = rig.Output;
            for (int i = 0; i < n; i++)
            {
                var o = output[i];
                var pos = o.Mode == 2 ? (Vector3)o.LocalPosition : sk.Positions[i];
                var rot = o.Mode != 0 ? (Quaternion)o.LocalRotation : sk.Rotations[i];
                var local = Matrix4x4.TRS(pos, rot, sk.Scales[i]);
                model[i] = sk.Parents[i] < 0 ? local : model[sk.Parents[i]] * local;
                result[i] = rootToWorld.MultiplyPoint3x4(model[i].GetColumn(3));
            }
            return result;
        }

        static KPRigFrame Frame(Transform root, NativeArray<float4x4> model, NativeArray<quaternion> rotation, float dt = Dt) => new KPRigFrame
        {
            DeltaTime = dt,
            RootToWorld = root.localToWorldMatrix,
            RootRotation = root.rotation,
            Model = model,
            ModelRotation = rotation,
        };

        sealed class Run
        {
            public List<Vector3[]> Positions = new List<Vector3[]>();
            public KPBone[] Bones;
            public int Constraints, Spheres, Capsules, Boxes;
        }

        static Run Capture(KPWorld w, Run run)
        {
            var inst = w.Instances[0];
            run.Bones = new KPBone[inst.BoneCount];
            for (int i = 0; i < inst.BoneCount; i++) run.Bones[i] = w.Bones[inst.BoneStart + i];
            run.Constraints = inst.ConstraintCount;
            run.Spheres = inst.SphereCount;
            run.Capsules = inst.CapsuleCount;
            run.Boxes = inst.BoxCount;
            return run;
        }

        /// <summary>同一個角色先用 Transform 版跑 Frames 幀，再用 KPRig 跑一次（全域亂數、frame seed 都從同一個起點）。</summary>
        static (Run transform, Run rig, Skeleton sk) RunBoth(int variant, bool features,
            Func<KawaiiPhysicsChain, List<KawaiiPhysicsChain>> extra = null)
        {
            KPTestRig.BuildEnvironment(Vector3.zero);
            var chain = KPTestRig.BuildCharacter("A", Vector3.zero, variant, features);
            var root = chain.transform;
            var chains = extra != null ? extra(chain) : new List<KawaiiPhysicsChain> { chain };
            var sk = new Skeleton(root);
            var rigChains = chains.Select(c => ToRigChain(c, sk)).ToList();
            var basePos = Vector3.zero;

            var a = new Run();
            UnityEngine.Random.InitState(RandomSeed);
            KPSystem.ChainHost.FrameSeed = 1;
            foreach (var c in chains) KPSystem.Register(c);
            for (int f = 0; f < Frames; f++)
            {
                KPSystem.RestorePhase();
                KPTestRig.Move(root, 0, f, basePos);
                Physics.SyncTransforms();
                KPSystem.SimulatePhase(Dt);
                if (f == 0) Capture(KPSystem.World, a);
                a.Positions.Add(sk.Bones.Select(t => t.position).ToArray());
            }
            KPSystem.Shutdown();

            var b = new Run();
            UnityEngine.Random.InitState(RandomSeed);
            using var model = new NativeArray<float4x4>(sk.Model, Allocator.Persistent);
            using var rotation = new NativeArray<quaternion>(sk.ModelRotation, Allocator.Persistent);
            using (var rig = sk.CreateRig())
            {
                rig.Owner = root;
                foreach (var c in rigChains) rig.AddChain(c);
                for (int f = 0; f < Frames; f++)
                {
                    KPTestRig.Move(root, 0, f, basePos);
                    Physics.SyncTransforms();
                    rig.ScheduleChains(Frame(root, model, rotation)).Complete();
                    if (f == 0) Capture(rig.World, b);
                    b.Positions.Add(WorldPositions(rig, sk, root.localToWorldMatrix));
                }
            }
            return (a, b, sk);
        }

        static void AssertStructureEqual(Run a, Run b)
        {
            Assert.That(b.Bones.Length, Is.EqualTo(a.Bones.Length), "骨頭數");
            Assert.That(b.Constraints, Is.EqualTo(a.Constraints), "約束數");
            Assert.That((b.Spheres, b.Capsules, b.Boxes), Is.EqualTo((a.Spheres, a.Capsules, a.Boxes)), "碰撞體數");
            for (int i = 0; i < a.Bones.Length; i++)
            {
                var x = a.Bones[i];
                var y = b.Bones[i];
                Assert.That((y.ParentIndex - b.Bones[0].ParentIndex, y.Flags, y.ChildCount), Is.EqualTo((x.ParentIndex - a.Bones[0].ParentIndex, x.Flags, x.ChildCount)), $"骨頭 {i} 的父子、旗標");
                Assert.That(y.BoneLength, Is.EqualTo(x.BoneLength).Within(1e-5f), $"骨頭 {i} 的骨長");
                Assert.That(y.LengthRate, Is.EqualTo(x.LengthRate).Within(1e-5f), $"骨頭 {i} 的骨長比例");
                Assert.That(y.BaseSettings.Radius, Is.EqualTo(x.BaseSettings.Radius).Within(1e-6f), $"骨頭 {i} 的半徑");
                Assert.That(y.ForceMask, Is.EqualTo(x.ForceMask), $"骨頭 {i} 的外力遮罩");
            }
        }

        static (float early, float late, int worstFrame) MaxDiff(Run a, Run b)
        {
            float early = 0f, late = 0f;
            var worst = -1;
            for (int f = 0; f < Frames; f++)
            {
                var d = 0f;
                for (int i = 0; i < a.Positions[f].Length; i++) d = Mathf.Max(d, Vector3.Distance(a.Positions[f][i], b.Positions[f][i]));
                Assert.That(float.IsNaN(d), Is.False, $"第 {f} 幀有 NaN");
                if (f < 10) early = Mathf.Max(early, d);
                if (d > late) { late = d; worst = f; }
            }
            return (early, late, worst);
        }

        [TestCase(0, false, TestName = "KPRig 等價：基本角色（元件空間）")]
        [TestCase(0, true, TestName = "KPRig 等價：全功能（元件空間）")]
        [TestCase(1, true, TestName = "KPRig 等價：全功能（世界空間）")]
        [TestCase(2, true, TestName = "KPRig 等價：全功能（基準骨空間）")]
        public void MatchesTransformVersion(int variant, bool features)
        {
            var (a, b, _) = RunBoth(variant, features);
            AssertStructureEqual(a, b);
            var (early, late, worst) = MaxDiff(a, b);
            var line = $"KPRig 等價（模擬空間 {variant}、{(features ? "全功能" : "基本")}）最大差：前 10 幀 {early * 1000f:F5} mm、{Frames} 幀內 {late * 1000f:F5} mm（第 {worst} 幀）";
            TestContext.WriteLine(line);
            Debug.Log(line);
            Assert.That(early, Is.LessThanOrEqualTo(1e-5f), "前 10 幀");
            Assert.That(late, Is.LessThanOrEqualTo(1e-3f), $"{Frames} 幀內");
        }

        /// <summary>
        /// 全功能的角色再加上其餘的功能：排除骨、依半徑加密、橋接假骨、碰撞資產、約束資產、平面與凸包碰撞、
        /// SyncBone、外力的名稱篩選、共用碰撞（同一個根底下另一條提供碰撞體的鏈）。
        /// </summary>
        static List<KawaiiPhysicsChain> AddRemainingFeatures(KawaiiPhysicsChain chain)
        {
            var root = chain.transform;
            Transform Bone(string name) => root.GetComponentsInChildren<Transform>(true).First(t => t.name == name);
            chain.excludeBones.Add(Bone("c3_5"));
            chain.boneSubdivisionDensifyByRadius = true;
            chain.radius = 0.02f;
            chain.boneConstraintSubdivisionCount = 1;

            var plane = new GameObject("plane").AddComponent<KawaiiPhysicsCollider>();
            plane.transform.SetParent(root, false);
            plane.transform.localPosition = new Vector3(0f, 0.95f, 0f);
            plane.shape = KawaiiPhysicsCollider.Shape.Plane;
            chain.colliders.Add(plane);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var src = cube.GetComponent<MeshFilter>().sharedMesh;
            var mesh = new Mesh { vertices = src.vertices, triangles = src.triangles };
            Object.DestroyImmediate(cube);
            var convex = new GameObject("convex").AddComponent<KawaiiPhysicsCollider>();
            convex.transform.SetParent(root, false);
            convex.transform.localPosition = new Vector3(0.15f, 1.3f, 0.15f);
            convex.transform.localScale = Vector3.one * 0.1f;
            convex.shape = KawaiiPhysicsCollider.Shape.Convex;
            convex.convexMesh = mesh;
            chain.colliders.Add(convex);

            var limits = ScriptableObject.CreateInstance<KawaiiPhysicsLimitsAsset>();
            limits.entries.Add(new KawaiiPhysicsLimitsAsset.Entry { boneName = "spine", shape = KawaiiPhysicsCollider.Shape.Capsule, offset = new Vector3(0f, -0.3f, 0f), radius = 0.1f, length = 0.2f, direction = KawaiiPhysicsCollider.Axis.X });
            chain.limitsAsset = limits;
            var constraints = ScriptableObject.CreateInstance<KawaiiPhysicsConstraintsAsset>();
            for (int i = 1; i < KPTestRig.BonesPerChain; i++)
                constraints.entries.Add(new KawaiiPhysicsConstraintsAsset.Entry { boneName1 = $"c4_{i}", boneName2 = $"c5_{i}" });
            chain.boneConstraintsAsset = constraints;

            var thigh = new GameObject("thigh").transform;
            thigh.SetParent(root, false);
            thigh.localPosition = new Vector3(0.1f, 1.0f, 0.1f);
            chain.syncBones.Add(new KawaiiPhysicsChain.SyncBone
            {
                bone = thigh,
                scaleCurveByDeltaDistance = AnimationCurve.Linear(0f, 1f, 0.5f, 0.5f),
                targetRoots = { new KawaiiPhysicsChain.SyncTargetRoot { bone = Bone("c6_0"), scaleCurveByBoneLengthRate = AnimationCurve.Linear(0f, 0f, 1f, 1f) } },
            });
            foreach (var f in chain.externalForces) f.applyBoneNameFilter.AddRange(new[] { "c0_3", "c1_4", "c2_5" });
            chain.useSharedCollision = true;

            var holder = new GameObject("source");
            holder.SetActive(false);
            holder.transform.SetParent(root, false);
            chain.additionalRootBones.RemoveAll(r => r.rootBone == Bone("c7_0")); // c7 這一束改由提供碰撞體的鏈模擬（同一根骨頭不給兩條鏈）
            var source = holder.AddComponent<KawaiiPhysicsChain>();
            source.component = root;
            source.rootBone = Bone("c7_0");
            source.sharedCollisionSource = true;
            source.gravity = new Vector3(0f, -9.8f, 0f);
            var shared = new GameObject("sharedBall").AddComponent<KawaiiPhysicsCollider>();
            shared.transform.SetParent(root, false);
            shared.transform.localPosition = new Vector3(-0.1f, 1.35f, 0f);
            shared.radius = 0.06f;
            source.colliders.Add(shared);
            holder.SetActive(true);
            return new List<KawaiiPhysicsChain> { chain, source };
        }

        [Test(Description = "KPRig 等價：其餘的功能（排除骨、加密、橋接、資產、平面與凸包、SyncBone、名稱篩選、共用碰撞）")]
        public void MatchesTransformVersionWithRemainingFeatures()
        {
            var (a, b, _) = RunBoth(0, true, AddRemainingFeatures);
            Assert.That(a.Bones.Count(x => (x.Flags & KPBoneFlags.Bridge) != 0), Is.GreaterThan(0), "有橋接假骨");
            AssertStructureEqual(a, b);
            var (early, late, worst) = MaxDiff(a, b);
            var line = $"KPRig 等價（其餘的功能）最大差：前 10 幀 {early * 1000f:F5} mm、{Frames} 幀內 {late * 1000f:F5} mm（第 {worst} 幀）";
            TestContext.WriteLine(line);
            Debug.Log(line);
            Assert.That(early, Is.LessThanOrEqualTo(1e-5f), "前 10 幀");
            Assert.That(late, Is.LessThanOrEqualTo(1e-3f), $"{Frames} 幀內");
        }

        // ---- KPRig 自己的行為 -------------------------------------------------------------

        [Test]
        public void RejectsParentAfterChild()
        {
            var p = new[] { 1, -1 };
            Assert.Throws<ArgumentException>(() => new KPRig(p, new[] { "a", "b" }, new Vector3[2], new[] { Quaternion.identity, Quaternion.identity }));
            Assert.Throws<ArgumentException>(() => new KPRig(new[] { -1 }, new[] { "a", "b" }, new Vector3[1], new[] { Quaternion.identity }));
        }

        [Test(Description = "骨頭索引超出骨架的範圍：排程時丟出例外並指出欄位")]
        public void RejectsOutOfRangeBone()
        {
            using var rig = new KPRig(new[] { -1, 0 }, new[] { "a", "b" }, new[] { Vector3.zero, Vector3.down }, new[] { Quaternion.identity, Quaternion.identity });
            rig.AddChain(new KPRigChain { rootBone = 0, excludeBones = { 5 } });
            using var model = new NativeArray<float4x4>(new[] { float4x4.identity, float4x4.Translate(new float3(0, -1, 0)) }, Allocator.Persistent);
            using var rot = new NativeArray<quaternion>(new[] { quaternion.identity, quaternion.identity }, Allocator.Persistent);
            var root = new GameObject("root").transform;
            var e = Assert.Throws<ArgumentException>(() => rig.ScheduleChains(Frame(root, model, rot)));
            StringAssert.Contains("excludeBones", e.Message);
        }

        [Test(Description = "會寫的骨頭：骨鏈的實骨（根除外沒有關係，假骨沒有骨頭），排序；沒有骨鏈時清空")]
        public void AffectedBonesListsChainBones()
        {
            var chain = KPTestRig.BuildCharacter("A", Vector3.zero, 0, false);
            var sk = new Skeleton(chain.transform);
            var rc = ToRigChain(chain, sk);
            using var model = new NativeArray<float4x4>(sk.Model, Allocator.Persistent);
            using var rot = new NativeArray<quaternion>(sk.ModelRotation, Allocator.Persistent);
            using var rig = sk.CreateRig();
            rig.AddChain(rc);
            rig.ScheduleChains(Frame(chain.transform, model, rot)).Complete();
            var expected = Enumerable.Range(0, sk.Bones.Count).Where(i => System.Text.RegularExpressions.Regex.IsMatch(sk.Names[i], @"^c\d+_\d+$")).ToArray();
            Assert.That(rig.AffectedBones.ToArray(), Is.EqualTo(expected));
            Assert.That(rig.ChainCount, Is.EqualTo(1));
            Assert.That(rig.SimulatedBoneCount, Is.EqualTo(KPTestRig.ChainsPerCharacter * (KPTestRig.BonesPerChain + 1)));
            Assert.That(rig.Output.ToArray().Count(o => o.Mode != 0), Is.GreaterThan(0));
            rig.RemoveChain(rc);
            rig.ScheduleChains(Frame(chain.transform, model, rot)).Complete();
            Assert.That(rig.AffectedBones.Length, Is.EqualTo(0));
            Assert.That(rig.Output.ToArray().All(o => o.Mode == 0), "拿掉骨鏈後不再有物理結果");
        }

        [Test(Description = "工作還在跑時 Dispose：先等工作完成，不會有安全檢查的錯誤")]
        public void DisposeWhileRunning()
        {
            var chain = KPTestRig.BuildCharacter("A", Vector3.zero, 0, true);
            var sk = new Skeleton(chain.transform);
            var rc = ToRigChain(chain, sk);
            using var model = new NativeArray<float4x4>(sk.Model, Allocator.Persistent);
            using var rot = new NativeArray<quaternion>(sk.ModelRotation, Allocator.Persistent);
            var rig = sk.CreateRig();
            rig.AddChain(rc);
            rig.ScheduleChains(Frame(chain.transform, model, rot)).Complete();
            rig.ScheduleChains(Frame(chain.transform, model, rot)); // 不等
            Assert.DoesNotThrow(() => rig.Dispose());
            Assert.DoesNotThrow(() => rig.Dispose(), "重複 Dispose");
            Assert.Throws<ObjectDisposedException>(() => rig.ScheduleChains(Frame(chain.transform, model, rot)));
        }

        [TestCase(KPBackend.BurstSingleThread)]
        [TestCase(KPBackend.Managed)]
        public void BackendsMatchParallel(KPBackend backend)
        {
            Vector3[] RunWith(KPBackend b)
            {
                Cleanup();
                KPSystem.Backend = b;
                var chain = KPTestRig.BuildCharacter("A", Vector3.zero, 0, false);
                var sk = new Skeleton(chain.transform);
                var rc = ToRigChain(chain, sk);
                using var model = new NativeArray<float4x4>(sk.Model, Allocator.Persistent);
                using var rot = new NativeArray<quaternion>(sk.ModelRotation, Allocator.Persistent);
                using var rig = sk.CreateRig();
                rig.AddChain(rc);
                for (int f = 0; f < 60; f++)
                {
                    KPTestRig.Move(chain.transform, 0, f, Vector3.zero);
                    rig.ScheduleChains(Frame(chain.transform, model, rot)).Complete();
                }
                return WorldPositions(rig, sk, chain.transform.localToWorldMatrix);
            }
            var parallel = RunWith(KPBackend.BurstParallel);
            var other = RunWith(backend);
            var d = parallel.Zip(other, Vector3.Distance).Max();
            TestContext.WriteLine($"{backend} 跟 BurstParallel 的最大差 {d * 1000f:F5} mm");
            Debug.Log($"KPRig 後端 {backend} 跟 BurstParallel 的最大差 {d * 1000f:F5} mm");
            Assert.That(d, Is.LessThanOrEqualTo(1e-4f));
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(16)]
        public void Performance(int characters)
        {
            const int warm = 60, measured = 240;
            KPTestRig.BuildEnvironment(Vector3.zero);
            var comps = new List<KawaiiPhysicsChain>();
            for (int c = 0; c < characters; c++) comps.Add(KPTestRig.BuildCharacter($"char{c}", new Vector3(c * 2f, 0f, 30f), c, false));
            var sks = comps.Select(c => new Skeleton(c.transform)).ToList();
            var rigChains = comps.Select((c, i) => ToRigChain(c, sks[i])).ToList();

            // Transform 版：兩個階段在主執行緒的時間（含等工作）
            foreach (var c in comps) KPSystem.Register(c);
            var sw = new System.Diagnostics.Stopwatch();
            double host = 0;
            for (int f = 0; f < warm + measured; f++)
            {
                for (int i = 0; i < comps.Count; i++) KPTestRig.Move(comps[i].transform, i, f, new Vector3(i * 2f, 0f, 30f));
                sw.Restart();
                KPSystem.RestorePhase();
                KPSystem.SimulatePhase(Dt);
                if (f >= warm) host += sw.Elapsed.TotalMilliseconds;
            }
            KPSystem.Shutdown();

            // KPRig：排程（主執行緒的準備）與等工作完成分開量
            var rigs = new List<(KPRig rig, Transform root, NativeArray<float4x4> m, NativeArray<quaternion> r)>();
            double schedule = 0, wait = 0;
            try
            {
                for (int i = 0; i < characters; i++)
                {
                    var rig = sks[i].CreateRig();
                    rig.AddChain(rigChains[i]);
                    rigs.Add((rig, comps[i].transform, new NativeArray<float4x4>(sks[i].Model, Allocator.Persistent),
                        new NativeArray<quaternion>(sks[i].ModelRotation, Allocator.Persistent)));
                }
                for (int f = 0; f < warm + measured; f++)
                {
                    for (int i = 0; i < rigs.Count; i++) KPTestRig.Move(rigs[i].root, i, f, new Vector3(i * 2f, 0f, 30f));
                    sw.Restart();
                    foreach (var r in rigs) r.rig.ScheduleChains(Frame(r.root, r.m, r.r));
                    var t1 = sw.Elapsed.TotalMilliseconds;
                    foreach (var r in rigs) r.rig.Complete();
                    var t2 = sw.Elapsed.TotalMilliseconds;
                    if (f >= warm) { schedule += t1; wait += t2 - t1; }
                }
            }
            finally
            {
                foreach (var r in rigs) { r.rig.Dispose(); r.m.Dispose(); r.r.Dispose(); }
            }
            var line = $"KPRig 效能（{characters} 角色 × {KPTestRig.ChainsPerCharacter} 鏈）：每幀 Transform 版 {host / measured:F3} ms；" +
                       $"KPRig 排程 {schedule / measured:F3} ms（每角色 {schedule / measured / characters:F3}）＋等工作 {wait / measured:F3} ms";
            TestContext.WriteLine(line);
            Debug.Log(line);
        }

        [Test(Description = "穩態每幀 0 GC 配置（16 個 KPRig，含一個全功能的）")]
        public void ZeroAllocationSteadyState()
        {
            KPTestAlloc.Init();
            Assume.That(KPTestAlloc.Precise, "這個環境量不到小配置：" + KPTestAlloc.Describe());
            KPTestRig.BuildEnvironment(Vector3.zero);
            var rigs = new List<(KPRig rig, Transform root, NativeArray<float4x4> model, NativeArray<quaternion> rot)>();
            try
            {
                for (int c = 0; c < 16; c++)
                {
                    var chain = KPTestRig.BuildCharacter($"char{c}", new Vector3(c * 2f, 0f, 0f), c, c == 0);
                    var sk = new Skeleton(chain.transform);
                    var rc = ToRigChain(chain, sk);
                    var rig = sk.CreateRig();
                    rig.Owner = chain.transform;
                    rig.AddChain(rc);
                    rigs.Add((rig, chain.transform, new NativeArray<float4x4>(sk.Model, Allocator.Persistent),
                        new NativeArray<quaternion>(sk.ModelRotation, Allocator.Persistent)));
                }
                long[] Measure()
                {
                    var perFrame = new long[300];
                    for (int f = 0; f < 360; f++)
                    {
                        for (int i = 0; i < rigs.Count; i++) KPTestRig.Move(rigs[i].root, i, f, new Vector3(i * 2f, 0f, 0f));
                        Physics.SyncTransforms();
                        var a0 = KPTestAlloc.Now();
                        foreach (var r in rigs) r.rig.ScheduleChains(Frame(r.root, r.model, r.rot));
                        foreach (var r in rigs) r.rig.Complete();
                        var a1 = KPTestAlloc.Now();
                        if (f >= 60) perFrame[f - 60] = Math.Max(0, a1 - a0);
                    }
                    return perFrame;
                }
                // 「GC Allocated In Frame」是全域計數：量兩次、每幀取較小的（KPProductionTests.C1 的做法）
                var w = Measure().Zip(Measure(), Math.Min).Max();
                TestContext.WriteLine($"每幀最大配置 {w} B（{KPTestAlloc.Describe()}）");
                Assert.That(w, Is.EqualTo(0));
            }
            finally
            {
                foreach (var r in rigs)
                {
                    r.rig.Dispose();
                    r.model.Dispose();
                    r.rot.Dispose();
                }
            }
        }
    }
}
