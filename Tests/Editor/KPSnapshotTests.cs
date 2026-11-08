// 重構前後逐位元一致的快照（v0.5 陣列接口的 P0）：全功能的測試角色（3 種模擬空間）＋軟組織，
// 固定的動作與 dt，第 0／1／60 幀把「所有 Transform 的本地 TRS」與「KPWorld 的骨頭狀態」各雜湊成一個值，跟記錄下來的比。
// 記錄檔 Tests/Editor/KPSnapshots.txt 是在某一台機器上產生的（Burst 的浮點結果跟 CPU 有關）：換機器要在重構之前重新記錄。
// 記錄：環境變數 KP_SNAPSHOT_RECORD=1，或記錄檔不存在時，測試寫檔並回 Inconclusive。
// 雜湊欄可以用「|」列出幾個已知的值（軟組織在不同的 Editor 工作階段會落在兩個固定值之一，見 Matches）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor.PackageManager;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KawaiiPhysicsUnity.Tests
{
    public class KPSnapshotTests
    {
        const string FileName = "KPSnapshots.txt";
        static readonly int[] SnapshotFrames = { 0, 1, 60 };

        [SetUp]
        public void SetUp()
        {
            KPTestSettings.EnableSafety();
            Cleanup();
        }

        [TearDown]
        public void TearDown() => Cleanup();

        static void Cleanup()
        {
            KPSystem.Shutdown();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include))
                if (go != null && go.transform.parent == null) Object.DestroyImmediate(go);
        }

        /// <summary>每次執行都一樣的起點：全域主機的 frame seed（Shutdown 不重設）歸 1。</summary>
        static void ResetDeterminism() => KPSystem.ChainHost.FrameSeed = 1u;

        /// <summary>Chain 的亂數用 GetHashCode（每次執行的 instance id 不同）播種：建好之後改成固定的種子。</summary>
        static void PinRng(KawaiiPhysicsChain chain, uint seed)
        {
            Assert.IsNotNull(chain.runtime, "Chain 還沒建");
            chain.runtime.Rng = new Unity.Mathematics.Random(seed);
        }

        sealed class Scenario
        {
            public string Name;
            public int Variant;
            public bool SoftBodies;
            public bool CrossingColliders;
        }

        static readonly Scenario[] Scenarios =
        {
            new Scenario { Name = "chain_v0", Variant = 0 },
            new Scenario { Name = "chain_v1", Variant = 1 },
            new Scenario { Name = "chain_v2", Variant = 2 },
            new Scenario { Name = "soft_v0", Variant = 0, SoftBodies = true },
            new Scenario { Name = "collide_v0", Variant = 0, CrossingColliders = true }, // 根不動：3 種模擬空間的結果相同，只跑一種
        };

        /// <summary>
        /// 每種碰撞體各一個，放在鏈的第 3 節旁邊（往外 3 cm、半徑 4 cm），從側面推骨頭：
        /// 測試角色原本的球、膠囊碰撞體在 60 幀內碰不到骨頭，碰撞的程式路徑沒有被快照涵蓋。
        /// 這個情境關掉風與外力（會把鏈吹得幾乎水平、位置不可預期），鏈靠重力垂下，碰撞體才會一直碰到；
        /// 推的方向要跟骨頭垂直（以根為中心時沿骨頭方向推，接著骨長復原會整個抵銷，結果跟半徑無關）。
        /// </summary>
        static void AddCrossingColliders(KawaiiPhysicsChain chain)
        {
            chain.enableWind = false;
            chain.externalForces.Clear();
            var root = chain.transform;
            KawaiiPhysicsCollider Make(string name, int chainIndex, KawaiiPhysicsCollider.Shape shape)
            {
                var bone = root.GetComponentsInChildren<Transform>(true).First(t => t.name == $"c{chainIndex}_3");
                var go = new GameObject("cross_" + name);
                go.transform.SetParent(root, false);
                var p = root.InverseTransformPoint(bone.position);
                var outward = new Vector3(p.x, 0f, p.z).normalized;
                go.transform.localPosition = p + outward * 0.03f;
                var c = go.AddComponent<KawaiiPhysicsCollider>();
                c.shape = shape;
                c.radius = 0.04f;
                c.radius1 = 0.03f;
                c.length = 0.06f;
                c.extent = new Vector3(0.035f, 0.035f, 0.035f);
                chain.colliders.Add(c);
                return c;
            }
            Make("sphere", 0, KawaiiPhysicsCollider.Shape.Sphere);
            Make("capsule", 2, KawaiiPhysicsCollider.Shape.Capsule).direction = KawaiiPhysicsCollider.Axis.X;
            Make("tapered", 4, KawaiiPhysicsCollider.Shape.TaperedCapsule).direction = KawaiiPhysicsCollider.Axis.Z;
            // 箱：放在跟軸對齊的鏈（第 8 條，往 −X）旁，推的方向（X）上很薄，骨頭一直被往側面推
            var box = Make("box", 8, KawaiiPhysicsCollider.Shape.Box);
            box.extent = new Vector3(0.02f, 0.06f, 0.06f);
            box.transform.localPosition += new Vector3(0.015f, 0f, 0f);   // 中心離骨頭 1.5 cm（半邊寬 2 cm，骨頭在箱裡、最淺的是 X 方向）
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var convex = Make("convex", 6, KawaiiPhysicsCollider.Shape.Convex);
            convex.convexMesh = cube.GetComponent<MeshFilter>().sharedMesh;
            convex.transform.localScale = Vector3.one * 0.08f;
            Object.DestroyImmediate(cube);
            // 平面：在鏈的下半段高度，法線往上，把下面幾節頂上來
            var plane = new GameObject("cross_plane").AddComponent<KawaiiPhysicsCollider>();
            plane.transform.SetParent(root, false);
            plane.transform.localPosition = new Vector3(0f, 1.42f, 0f);
            plane.shape = KawaiiPhysicsCollider.Shape.Plane;
            chain.colliders.Add(plane);
        }

        /// <summary>跑一個情境，回傳 (幀, Transform 雜湊, 骨頭雜湊)。</summary>
        static List<(int frame, ulong transforms, ulong bones)> Run(Scenario s)
        {
            Cleanup();
            ResetDeterminism();
            KPTestRig.BuildEnvironment(new Vector3(0f, 0f, 0f));
            var basePos = Vector3.zero;
            var chain = KPTestRig.BuildCharacter("snap", basePos, s.Variant, features: true);
            if (s.CrossingColliders) AddCrossingColliders(chain);
            KPSystem.Register(chain); // 編輯模式不會呼叫 OnEnable
            if (s.SoftBodies)
                foreach (var b in KPTestRig.AddSoftBodies(chain.transform))
                    KPSystem.Register(b);
            var root = chain.transform;
            var result = new List<(int, ulong, ulong)>();
            for (int f = 0; f <= SnapshotFrames.Max(); f++)
            {
                KPSystem.RestorePhase();
                if (f == 0) PinRng(chain, 0x9E3779B9u ^ (uint)(s.Variant * 7919 + 1));
                // 碰撞情境的根不動：鏈靠重力一直壓在碰撞體上（根一動鏈就甩開 10 cm 以上，只有前幾幀碰得到）
                if (!s.CrossingColliders) KPTestRig.Move(root, 0, f, basePos);
                Physics.SyncTransforms();
                UnityEngine.Random.InitState(1000 + f);
                KPSystem.SimulatePhase(1f / 60f);
                if (Array.IndexOf(SnapshotFrames, f) >= 0)
                    result.Add((f, HashTransforms(root), HashBones()));
            }
            return result;
        }

        // ---- 雜湊（FNV-1a 64，逐位元）--------------------------------------

        const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;

        static ulong Mix(ulong h, uint v)
        {
            for (int i = 0; i < 4; i++) { h ^= (v >> (i * 8)) & 0xFF; h *= Prime; }
            return h;
        }

        static unsafe ulong Mix(ulong h, float v) => Mix(h, *(uint*)&v);

        static ulong Mix(ulong h, Vector3 v) => Mix(Mix(Mix(h, v.x), v.y), v.z);

        static ulong Mix(ulong h, Quaternion q) => Mix(Mix(Mix(Mix(h, q.x), q.y), q.z), q.w);

        /// <summary>根底下所有 Transform（階層順序）的名字長度與本地 TRS。</summary>
        static ulong HashTransforms(Transform root)
        {
            ulong h = Offset;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                h = Mix(h, (uint)t.name.Length);
                h = Mix(h, t.localPosition);
                h = Mix(h, t.localRotation);
                h = Mix(h, t.localScale);
            }
            return h;
        }

        /// <summary>KPWorld 裡所有骨頭（KPBone 整個結構的位元組）。</summary>
        static unsafe ulong HashBones()
        {
            ulong h = Offset;
            var world = KPSystem.World;
            if (world == null) return h;
            var bones = world.Bones;
            int used = 0;
            foreach (var seg in world.Segments) used = Math.Max(used, seg.At.Bone + seg.Size.Bone);
            h = Mix(h, (uint)used);
            var p = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(bones);
            long n = (long)used * UnsafeUtility.SizeOf<KPBone>();
            for (long i = 0; i < n; i++) { h ^= p[i]; h *= Prime; }
            return h;
        }

        // ---- 記錄檔 -------------------------------------------------------------

        static string SnapshotPath()
        {
            var root = PackageInfo.FindForAssembly(typeof(KPSystem).Assembly).resolvedPath;
            return Path.Combine(root, "Tests", "Editor", FileName);
        }

        static string Line(string scenario, (int frame, ulong transforms, ulong bones) r) =>
            $"{scenario} {r.frame} {r.transforms:X16} {r.bones:X16}";

        /// <summary>
        /// 記錄的一行跟這次的一行一樣。記錄的雜湊欄可以用「|」列出幾個值，符合其中一個就算：
        /// 軟組織的結果在同一個 Editor 工作階段裡完全確定（後端、工作執行緒數都不影響），但換一次工作階段
        ///（domain reload）偶爾會落在另一個固定的值，連 C# 後端也一樣，是引擎或 CPU 浮點狀態之類的外部輸入。
        /// 兩個值都記下來；重構改變了行為會出現第三個值，照樣抓得到。
        /// </summary>
        static bool Matches(string expected, string actual)
        {
            var e = expected.Split(' ');
            var a = actual.Split(' ');
            if (e.Length != a.Length) return false;
            for (int i = 0; i < e.Length; i++)
                if (Array.IndexOf(e[i].Split('|'), a[i]) < 0) return false;
            return true;
        }

        [Test(Description = "重構前後逐位元一致：全功能角色 3 種模擬空間＋軟組織，第 0／1／60 幀")]
        public void MatchesRecordedSnapshot()
        {
            var lines = new List<string>();
            foreach (var s in Scenarios)
                foreach (var r in Run(s))
                    lines.Add(Line(s.Name, r));

            var path = SnapshotPath();
            bool record = Environment.GetEnvironmentVariable("KP_SNAPSHOT_RECORD") == "1" || !File.Exists(path);
            if (record)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"# KPSnapshotTests 的記錄（{DateTime.Now:yyyy-MM-dd}，{SystemInfo.processorType}，Unity {Application.unityVersion}）");
                sb.AppendLine("# 情境 幀 Transform雜湊 骨頭雜湊");
                foreach (var l in lines) sb.AppendLine(l);
                File.WriteAllText(path, sb.ToString());
                Assert.Inconclusive($"已記錄 {lines.Count} 行到 {path}");
            }

            var expected = File.ReadAllLines(path).Where(l => l.Length > 0 && l[0] != '#').ToList();
            var diffs = new List<string>();
            for (int i = 0; i < Math.Max(expected.Count, lines.Count); i++)
            {
                var e = i < expected.Count ? expected[i] : "(沒有)";
                var a = i < lines.Count ? lines[i] : "(沒有)";
                if (!Matches(e, a)) diffs.Add($"記錄 {e}\n  現在 {a}");
            }
            Assert.IsEmpty(diffs, "跟記錄不一致：\n" + string.Join("\n", diffs));
        }

        [Test(Description = "同一次執行裡跑兩次結果一樣（快照本身是確定的）")]
        public void RunIsDeterministic()
        {
            var a = Run(Scenarios[0]);
            var b = Run(Scenarios[0]);
            CollectionAssert.AreEqual(a.Select(x => Line("x", x)).ToList(), b.Select(x => Line("x", x)).ToList());
        }
    }
}
