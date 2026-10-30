// 驗證與效能比較。NUnit 測試（KPBenchTests）呼叫各段；也可以批次產生報告：
//   Unity.exe -batchmode -nographics -releaseCodeOptimization -projectPath <proj> -executeMethod KawaiiPhysicsUnity.Tests.KPBench.RunAll -kpOut <報告路徑> -quit
//   （專案的 manifest 要有 "testables": ["com.louie.kawaii-physics"] 與 com.unity.test-framework）
// 1) Golden：KawaiiPhysics 自帶 golden 測試的四個情境（UE 實跑的骨頭位置）。
// 2) 原作單元測試移植：碰撞（球／膠囊／錐形膠囊／箱／凸包／平面／角度限制）、程序風（雜湊、雜訊、突風包絡、漣漪、決定性）。
// 3) 功能測試：真的 Transform 跑 KPSystem，逐一檢查新移植的功能。
// 4) 管線健全性與效能（Burst 平行／Burst 單執行緒／純 C#）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using KawaiiPhysicsUnity;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace KawaiiPhysicsUnity.Tests
{
public static unsafe class KPBench
{
    static readonly StringBuilder report = new StringBuilder();
    static int passCount, failCount;
    /// <summary>各段的檢查結果（NUnit 測試讀這個）。</summary>
    internal static readonly List<(string name, bool ok, string detail)> Results = new List<(string, bool, string)>();

    internal static void ResetResults()
    {
        Results.Clear();
        report.Clear();
        passCount = failCount = 0;
    }

    static void Log(string line)
    {
        report.AppendLine(line);
        Debug.Log("[KPBench] " + line);
    }

    static void Check(string name, bool ok, string detail)
    {
        if (ok) passCount++; else failCount++;
        Results.Add((name, ok, detail));
        Log($"| {(ok ? "PASS" : "**FAIL**")} | {name} | {detail} |");
    }

    public static void RunAll()
    {
        var outPath = ArgValue("-kpOut") ?? Path.Combine(Application.dataPath, "../kpbench_report.md");
        try
        {
            // 測試時開著工作除錯器與安全檢查（Play 模式的預設），量效能前才關
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Enabled;
            JobsUtility.JobDebuggerEnabled = true;
            BurstCompiler.Options.EnableBurstSafetyChecks = true;
            Log($"# KPBench v0.2 {DateTime.Now:yyyy-MM-dd HH:mm}");
            Log($"- Unity {Application.unityVersion}, {SystemInfo.processorType} ({SystemInfo.processorCount} threads), worker threads {JobsUtility.JobWorkerCount}");
            Log("");
            Golden();
            Log("## 2–3. 原作單元測試移植與功能測試");
            Log("");
            Log("| 結果 | 項目 | 數值 |");
            Log("|---|---|---|");
            CollisionUnitTests();
            ProceduralWindUnitTests();
            FeatureTests();
            Log("");
            Log($"合計 PASS {passCount}、FAIL {failCount}");
            Log("");
            PipelineSanity();
            if (ArgValue("-kpNoBench") == null)
            {
                NativeLeakDetection.Mode = NativeLeakDetectionMode.Disabled;
                JobsUtility.JobDebuggerEnabled = false;
                BurstCompiler.Options.EnableBurstSafetyChecks = false;
                SolverBenchmark();
                PipelineBenchmark();
            }
        }
        catch (Exception e)
        {
            Log("EXCEPTION: " + e);
        }
        finally
        {
            KPSystem.Shutdown();
            File.WriteAllText(outPath, report.ToString());
            Debug.Log("[KPBench] report written to " + outPath);
        }
    }

    static string ArgValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    // ===================================================================
    // 只有解算陣列的小世界（golden 與單元測試用）
    // ===================================================================

    sealed class TestWorld : IDisposable
    {
        public NativeArray<KPInstance> Instances;
        public NativeArray<KPBone> Bones;
        public NativeArray<KPSphere> Spheres;
        public NativeArray<KPCapsule> Capsules;
        public NativeArray<KPTaperedCapsule> Tapered;
        public NativeArray<KPBox> Boxes;
        public NativeArray<KPPlane> Planes;
        public NativeArray<KPConvex> Convexes;
        public NativeArray<float4> ConvexPlanes;
        public NativeArray<KPConstraint> Constraints;
        public NativeArray<float> Dummy;

        static NativeArray<T> N<T>(List<T> l) where T : struct
        {
            var a = new NativeArray<T>(Mathf.Max(1, l?.Count ?? 0), Allocator.Persistent);
            if (l != null) for (int i = 0; i < l.Count; i++) a[i] = l[i];
            return a;
        }

        public TestWorld(KPInstance inst, List<KPBone> bones, List<KPSphere> s = null, List<KPCapsule> c = null,
            List<KPTaperedCapsule> t = null, List<KPBox> b = null, List<KPPlane> p = null, List<KPConvex> cv = null,
            List<float4> cvp = null, List<KPConstraint> con = null)
        {
            inst.BoneCount = bones.Count;
            inst.SphereCount = s?.Count ?? 0;
            inst.CapsuleCount = c?.Count ?? 0;
            inst.TaperedCount = t?.Count ?? 0;
            inst.BoxCount = b?.Count ?? 0;
            inst.PlaneCount = p?.Count ?? 0;
            inst.ConvexCount = cv?.Count ?? 0;
            inst.ConstraintCount = con?.Count ?? 0;
            Instances = N(new List<KPInstance> { inst });
            Bones = N(bones); Spheres = N(s); Capsules = N(c); Tapered = N(t); Boxes = N(b); Planes = N(p);
            Convexes = N(cv); ConvexPlanes = N(cvp); Constraints = N(con);
            Dummy = new NativeArray<float>(1, Allocator.Persistent);
        }

        public KPWorldPtrs Ptrs() => new KPWorldPtrs
        {
            Bones = (KPBone*)Bones.GetUnsafePtr(), Spheres = (KPSphere*)Spheres.GetUnsafePtr(),
            Capsules = (KPCapsule*)Capsules.GetUnsafePtr(), Tapered = (KPTaperedCapsule*)Tapered.GetUnsafePtr(),
            Boxes = (KPBox*)Boxes.GetUnsafePtr(), Planes = (KPPlane*)Planes.GetUnsafePtr(),
            Convexes = (KPConvex*)Convexes.GetUnsafePtr(), ConvexPlanes = (float4*)ConvexPlanes.GetUnsafePtr(),
            Constraints = (KPConstraint*)Constraints.GetUnsafePtr(), Luts = (float*)Dummy.GetUnsafePtr(),
        };

        public ref KPInstance Inst => ref UnsafeUtility.ArrayElementAsRef<KPInstance>(Instances.GetUnsafePtr(), 0);

        public void Dispose()
        {
            Instances.Dispose(); Bones.Dispose(); Spheres.Dispose(); Capsules.Dispose(); Tapered.Dispose(); Boxes.Dispose();
            Planes.Dispose(); Convexes.Dispose(); ConvexPlanes.Dispose(); Constraints.Dispose(); Dummy.Dispose();
        }
    }

    static KPBone Bone(float3 loc, float radius, float3 prev, int parent = -1, int childCount = 0) => new KPBone
    {
        Location = loc, PrevLocation = prev, PoseLocation = loc, PrevPoseLocation = loc, CurrentPoseLocation = loc,
        PoseRotation = quaternion.identity, PrevPoseRotation = quaternion.identity, CurrentPoseRotation = quaternion.identity,
        Settings = new KPSettings { Radius = radius }, ParentIndex = parent, ChildCount = childCount, TransformIndex = -1,
        RealParentIndex = -1, RealChildIndex = -1,
    };

    // ===================================================================
    // 1) Golden（KawaiiPhysicsGoldenTest.cpp 的四個情境，單位 cm、Z 向上，跟 UE 一樣）
    // ===================================================================

    static readonly Dictionary<string, double[]> GoldenValues = new Dictionary<string, double[]>
    {
        ["Chain"] = new double[] {0.0, 0.0, 0.0, 1.5624128740451773, 0.0711072791718913, -9.876933216634942, 3.1138833839400015, 0.2115179158436166, -19.754849196741077, 4.655505064709694, 0.4219972168024064, -29.633062651473097, 6.184199108270746, 0.7021518672309404, -39.511554838696014, 7.697346790615052, 1.0515878487764172, -49.3902328163516, 9.194299564112692, 1.4683248200285646, -59.268768269074805, 10.670281871058222, 1.942226052631134, -69.14788229065192, 12.086805815460334, 2.461308196384814, -79.03342798337782, 13.37096743319239, 3.0856785766563117, -88.93095750178712, 14.628787114413692, 4.039756427295765, -98.80555243547688, 16.488226340872544, 5.451062301010474, -108.5292709368489},
        ["ChainLegacy"] = new double[] {0.0, 0.0, 0.0, 3.5876017068687864, 0.3795972239837022, -9.326576003036488, 7.097916825307923, 1.126654993891126, -18.660363683162846, 10.49228457304039, 2.2334984285375605, -28.00130395726447, 13.733973808813042, 3.6845806040680804, -37.34934179514967, 16.78828040313917, 5.460504893315058, -46.704410161907795, 19.623358593766827, 7.538632465603092, -56.0662324976672, 22.2119951270383, 9.891340627046288, -65.4344620639028, 24.526461483441018, 12.479134925365486, -74.8124451175592, 26.50754662090512, 15.261997330283478, -84.21090129468811, 28.05860404016085, 18.293392655045032, -93.61328733585796, 29.280811477229886, 21.80425902692142, -102.89660712457582},
        ["Constraint"] = new double[] {1.4999999226006326, 0.0, 0.0, 1.5000001102517617, 1.0510656091728412, -9.944609649715474, 1.5000001816267996, 2.0991001843141985, -19.88953918839539, 1.5000000998864222, 3.147042856489887, -29.834478411730707, 1.4999998919359074, 4.194979021735506, -39.779418320728155, 1.4999997990933271, 5.2431635053579315, -49.724332060338625, 1.5000000487833123, 6.292374257223531, -59.66913757870697, 1.4999998380648436, 7.340383142686271, -69.61406982462957, 13.500000077399369, 0.0, 0.0, 13.499999889748242, 1.0510656091728412, -9.944609649715474, 13.499999818373196, 2.0991001843141985, -19.88953918839539, 13.49999990011356, 3.147042856489887, -29.834478411730707, 13.500000108064073, 4.194979021735506, -39.779418320728155, 13.50000020090667, 5.2431635053579315, -49.724332060338625, 13.499999951216706, 6.292374257223531, -59.66913757870697, 13.500000161935194, 7.340383142686271, -69.61406982462957},
        ["Collision"] = new double[] {0.0, 0.0, 0.0, 0.0, 0.0, -10.0, 0.0, 0.0, -20.0, 0.0, -4.779984032614022, -28.78360704084347, -1.5242386334446347, -7.902456878880407, -38.16053937037662, -2.2417906235888863, -9.509197015131925, -48.004497175852954, -2.2002833464857994, -9.416126718418038, -58.00397791566225, -2.1994156744886584, -10.956254816031443, -67.88466638813657, -2.1991200654448155, -10.998838235629524, -77.88457571597502, -2.199895735449172, -10.99125620887549, -87.88457281153491, 2.0232022953883058, -13.668091241403785, -96.54482668948594, 1.802932329644579, -13.528718319420275, -106.54142892878751},
    };

    static KPSettings GoldenSettings(float radius, float limitAngle) => new KPSettings
    {
        Damping = 0.15f, WorldDampingLocation = 0.2f, WorldDampingRotation = 0.3f, Stiffness = 0.07f, Radius = radius, LimitAngle = limitAngle,
    };

    static List<KPBone> VerticalChain(int n, float spacing, float3 origin, KPSettings s, int offset)
    {
        var list = new List<KPBone>();
        for (int i = 0; i < n; i++)
        {
            var p = origin + new float3(0f, 0f, -spacing * i);
            var b = Bone(p, s.Radius, p, i > 0 ? offset + i - 1 : -1, i < n - 1 ? 1 : 0);
            b.Settings = s;
            list.Add(b);
        }
        return list;
    }

    static readonly quaternion YToZ = quaternion.RotateX(math.PI / 2f); // 膠囊軸與平面法線：Unity 版是區域 +Y，UE 是區域 +Z

    static TestWorld BuildGolden(string name)
    {
        var inst = KPInstance.Create(60);
        inst.MaxSubsteps = 4;
        inst.SimSpaceGravity = new float3(0f, 0f, -980f);
        var bones = new List<KPBone>();
        var spheres = new List<KPSphere>();
        var capsules = new List<KPCapsule>();
        var boxes = new List<KPBox>();
        var planes = new List<KPPlane>();
        var constraints = new List<KPConstraint>();
        switch (name)
        {
            case "Chain":
            case "ChainLegacy":
                bones.AddRange(VerticalChain(12, 10f, float3.zero, GoldenSettings(2f, 0f), 0));
                inst.UseFixedSubstep = name == "Chain" ? 1 : 0;
                inst.SkelCompMoveVector = new float3(0.3f, 0f, 0f);
                inst.SkelCompMoveRotation = quaternion.AxisAngle(new float3(0f, 0f, 1f), 0.01f);
                break;
            case "Constraint":
                bones.AddRange(VerticalChain(8, 10f, float3.zero, GoldenSettings(2f, 0f), 0));
                bones.AddRange(VerticalChain(8, 10f, new float3(15f, 0f, 0f), GoldenSettings(2f, 0f), 8));
                inst.ConstraintItersBefore = 2;
                inst.ConstraintItersAfter = 2;
                for (int d = 0; d < 8; d++)
                    constraints.Add(new KPConstraint { Bone1 = d, Bone2 = 8 + d, Length = 12f, Compliance = KPCompliance.Leather });
                inst.SkelCompMoveVector = new float3(0f, 0.2f, 0f);
                break;
            case "Collision":
                bones.AddRange(VerticalChain(12, 10f, float3.zero, GoldenSettings(3f, 30f), 0));
                spheres.Add(new KPSphere { Location = new float3(2f, 0f, -45f), Radius = 8f, DrivingIndex = KPDriving.SimSpace });
                capsules.Add(new KPCapsule { Location = new float3(0f, 2f, -55f), Rotation = YToZ, Radius = 4f, Length = 50f, DrivingIndex = KPDriving.SimSpace });
                boxes.Add(new KPBox { Location = new float3(0f, -2f, -75f), Rotation = quaternion.identity, Extent = new float3(6f, 6f, 8f), DrivingIndex = KPDriving.SimSpace });
                planes.Add(new KPPlane { Location = new float3(0f, 0f, -95f), Rotation = YToZ, DrivingIndex = KPDriving.SimSpace });
                break;
        }
        return new TestWorld(inst, bones, spheres, capsules, null, boxes, planes, null, null, constraints);
    }

    static float3[] RunGolden(string name, bool burst)
    {
        using var w = BuildGolden(name);
        const float dt = 1f / 90f;
        const int frames = 200;
        var ptrs = w.Ptrs();
        if (burst)
            new KPStepFrameJob { Instances = (KPInstance*)w.Instances.GetUnsafePtr(), InstanceCount = 1, World = ptrs, DeltaTime = dt, Frames = frames }.Run();
        else
            for (int f = 0; f < frames; f++) KPSolver.StepFrame(ref w.Inst, ptrs, dt);
        return w.Bones.Select(b => b.Location).ToArray();
    }

    internal static void Golden()
    {
        Log("## 1. Golden（UE 原版 200 幀 @90fps 的骨頭位置，單位 cm）");
        Log("");
        Log("| 情境 | 骨頭數 | Burst 最大誤差 | 純 C# 最大誤差 |");
        Log("|---|---|---|---|");
        foreach (var name in new[] { "Chain", "ChainLegacy", "Constraint", "Collision" })
        {
            var golden = GoldenValues[name];
            double Err(float3[] r)
            {
                double m = 0;
                for (int i = 0; i < r.Length; i++)
                    for (int k = 0; k < 3; k++) m = Math.Max(m, Math.Abs(r[i][k] - golden[i * 3 + k]));
                return m;
            }
            var eb = Err(RunGolden(name, true));
            var em = Err(RunGolden(name, false));
            Log($"| {name} | {golden.Length / 3} | {eb:0.000000} | {em:0.000000} |");
            var ok = eb <= 0.01 && em <= 0.01;
            if (ok) passCount++; else failCount++;
            Results.Add(($"Golden.{name}", ok, $"Burst {eb:0.000000} cm／C# {em:0.000000} cm"));
        }
        Log("");
    }

    // ===================================================================
    // 2) 原作單元測試（KawaiiPhysicsCollisionTest.cpp、KawaiiPhysicsProceduralWindTest.cpp）
    // ===================================================================

    const float Tol = 0.01f;

    static string V(float3 v) => $"({v.x:0.###}, {v.y:0.###}, {v.z:0.###})";

    internal static void CollisionUnitTests()
    {
        // SphereOuterPushOut
        var sphere = new KPSphere { Location = float3.zero, Radius = 10f };
        var b0 = Bone(new float3(5, 0, 0), 3f, new float3(5, 0, 0));
        KPSolver.AdjustBySphere(&b0, &sphere);
        Check("Collision.Sphere outer push-out", math.distance(b0.Location, new float3(13, 0, 0)) < Tol, V(b0.Location) + " 期望 (13,0,0)");
        var b1 = Bone(new float3(20, 0, 0), 3f, new float3(20, 0, 0));
        KPSolver.AdjustBySphere(&b1, &sphere);
        Check("Collision.Sphere outside untouched", math.distance(b1.Location, new float3(20, 0, 0)) < Tol, V(b1.Location));
        sphere.Inner = 1;
        var b2 = Bone(new float3(10, 0, 0), 3f, new float3(10, 0, 0));
        KPSolver.AdjustBySphere(&b2, &sphere);
        Check("Collision.Sphere inner pull-in", math.distance(b2.Location, new float3(7, 0, 0)) < Tol, V(b2.Location) + " 期望 (7,0,0)");

        // CapsulePushOut（UE 的膠囊軸是 Z）
        using (var w = new TestWorld(KPInstance.Create(), new List<KPBone> { Bone(new float3(2, 0, 0), 3f, new float3(2, 0, 0)) },
                   c: new List<KPCapsule> { new KPCapsule { Location = float3.zero, Rotation = YToZ, Length = 10f, Radius = 4f } }))
        {
            var p = w.Ptrs();
            KPSolver.PrepareCollisionShapeCaches(ref w.Inst, p);
            KPSolver.AdjustByCapsule(p.Bones, p.Capsules);
            Check("Collision.Capsule push-out", math.distance(p.Bones[0].Location, new float3(7, 0, 0)) < Tol, V(p.Bones[0].Location) + " 期望 (7,0,0)");
        }

        // TaperedCapsulePushOut
        void Tapered(string label, float3 start, float boneR, float len, float r0, float r1, float3 expected)
        {
            using var w = new TestWorld(KPInstance.Create(), new List<KPBone> { Bone(start, boneR, start) },
                t: new List<KPTaperedCapsule> { new KPTaperedCapsule { Location = float3.zero, Rotation = YToZ, Length = len, Radius0 = r0, Radius1 = r1 } });
            var p = w.Ptrs();
            KPSolver.PrepareCollisionShapeCaches(ref w.Inst, p);
            KPSolver.AdjustByTaperedCapsule(p.Bones, p.Tapered);
            Check("Collision.TaperedCapsule " + label, math.distance(p.Bones[0].Location, expected) < Tol, V(p.Bones[0].Location) + " 期望 " + V(expected));
        }
        Tapered("asymmetric t=0.25", new float3(1, 0, 2.5f), 3f, 10f, 6f, 2f, new float3(8, 0, 2.5f));
        Tapered("+Z clamp", new float3(0, 0, 10), 3f, 10f, 6f, 2f, new float3(0, 0, 14));
        Tapered("-Z clamp", new float3(0, 0, -8), 3f, 10f, 6f, 2f, new float3(0, 0, -10));
        Tapered("outside", new float3(0, 0, 20), 3f, 10f, 6f, 2f, new float3(0, 0, 20));
        Tapered("degenerate length", new float3(2, 0, 0), 3f, 0f, 6f, 2f, new float3(9, 0, 0));
        Tapered("axis fallback", new float3(0, 0, 0), 3f, 10f, 6f, 2f, new float3(7, 0, 0));
        Tapered("negative end radius", new float3(1, 0, 5), 3f, 10f, -2f, 6f, new float3(3, 0, 5));
        Tapered("strong taper sphere fallback", new float3(0, 0, -2.5f), 2f, 5f, 10f, 1f, new float3(0, 0, -9.5f));
        Tapered("boundary taper -Z sphere", new float3(0, 0, 2.5f), 1f, 5f, 1f, 6f, new float3(0, 0, 4.5f));

        // BoxInteriorPushOutToNearestFace
        void Box(string label, float3 start, float3 extent, quaternion rot, float3 expected)
        {
            var box = new KPBox { Location = float3.zero, Rotation = rot, Extent = extent };
            var b = Bone(start, 1f, start);
            KPSolver.AdjustByBox(&b, &box);
            Check("Collision.Box " + label, math.distance(b.Location, expected) < 1e-3f, V(b.Location) + " 期望 " + V(expected));
        }
        var thin = new float3(100, 100, 2);
        Box("interior +Z", new float3(30, -20, 0.5f), thin, quaternion.identity, new float3(30, -20, 3));
        Box("interior -Z", new float3(30, -20, -0.5f), thin, quaternion.identity, new float3(30, -20, -3));
        Box("thin box center", float3.zero, thin, quaternion.identity, new float3(0, 0, 3));
        Box("cube center tie chooses X", float3.zero, new float3(5, 5, 5), quaternion.identity, new float3(6, 0, 0));
        Box("rotated interior", new float3(0, 10, 1), thin, quaternion.AxisAngle(new float3(0, 0, 1), math.radians(90f)), new float3(0, 10, 3));
        Box("exterior overlapping", new float3(0, 0, 2.5f), thin, quaternion.identity, new float3(0, 0, 3));
        Box("exterior separated", new float3(0, 0, 3.5f), thin, quaternion.identity, new float3(0, 0, 3.5f));
        Box("nearest side face", new float3(99.5f, 0, 0), thin, quaternion.identity, new float3(101, 0, 0));

        // ConvexPushOut（單位立方體的 6 個面）
        var cubePlanes = new List<float4>
        {
            new float4(1, 0, 0, 1), new float4(-1, 0, 0, 1), new float4(0, 1, 0, 1),
            new float4(0, -1, 0, 1), new float4(0, 0, 1, 1), new float4(0, 0, -1, 1),
        };
        void Convex(string label, float3 start, float boneR, float3 loc, quaternion rot, float3 expected)
        {
            using var w = new TestWorld(KPInstance.Create(), new List<KPBone> { Bone(start, boneR, start) },
                cv: new List<KPConvex> { new KPConvex { Location = loc, Rotation = rot, LocalBoundsMin = -1f, LocalBoundsMax = 1f, PlaneStart = 0, PlaneCount = 6 } },
                cvp: cubePlanes);
            var p = w.Ptrs();
            KPSolver.AdjustByConvex(p.Bones, p.Convexes, p.ConvexPlanes);
            Check("Collision.Convex " + label, math.distance(p.Bones[0].Location, expected) < Tol, V(p.Bones[0].Location) + " 期望 " + V(expected));
        }
        Convex("face push-out", new float3(1.2f, 0, 0), 0.5f, float3.zero, quaternion.identity, new float3(1.5f, 0, 0));
        Convex("buried push-out", new float3(0.2f, 0, 0), 0.5f, float3.zero, quaternion.identity, new float3(1.5f, 0, 0));
        Convex("bounds reject", new float3(2f, 0, 0), 0.25f, float3.zero, quaternion.identity, new float3(2f, 0, 0));
        var rz = quaternion.AxisAngle(new float3(0, 0, 1), math.PI / 2f);
        Convex("rotated push-out", new float3(10, 0, 0) + math.mul(rz, new float3(0.8f, 0, 0)), 0.5f, new float3(10, 0, 0), rz,
            new float3(10, 0, 0) + math.mul(rz, new float3(1.5f, 0, 0)));
        Convex("equal distance keeps first plane", new float3(0.8f, 0.8f, 0), 0.5f, float3.zero, quaternion.identity, new float3(1.5f, 0.8f, 0));

        // PlanarPushOut（線段穿過平面）
        using (var w = new TestWorld(KPInstance.Create(), new List<KPBone> { Bone(new float3(0, 0, -5), 3f, new float3(0, 0, 5)) },
                   p: new List<KPPlane> { new KPPlane { Location = float3.zero, Rotation = YToZ } }))
        {
            var p = w.Ptrs();
            KPSolver.PrepareCollisionShapeCaches(ref w.Inst, p);
            KPSolver.AdjustByPlane(p.Bones, p.Planes);
            Check("Collision.Planar push-out", math.distance(p.Bones[0].Location, new float3(0, 0, 3)) < Tol, V(p.Bones[0].Location) + " 期望 (0,0,3)");
        }

        // AngleLimit
        var parent = Bone(float3.zero, 0f, float3.zero);
        var child = Bone(new float3(0, 10, 0), 0f, new float3(0, 10, 0));
        child.PoseLocation = new float3(10, 0, 0);
        child.Settings.LimitAngle = 30f;
        KPSolver.AdjustByAngleLimit(&child, &parent);
        var exp = new float3(10f * math.cos(math.radians(30f)), 10f * math.sin(math.radians(30f)), 0f);
        Check("Collision.AngleLimit", math.distance(child.Location, exp) < Tol && math.abs(math.length(child.Location) - 10f) < Tol, V(child.Location) + " 期望 " + V(exp));
        var within = Bone(new float3(10, 0, 0), 0f, new float3(10, 0, 0));
        within.Settings.LimitAngle = 30f;
        KPSolver.AdjustByAngleLimit(&within, &parent);
        Check("Collision.AngleLimit within untouched", math.distance(within.Location, new float3(10, 0, 0)) < Tol, V(within.Location));
    }

    internal static void ProceduralWindUnitTests()
    {
        // NoiseProperties：雜湊值要跟原作位元一致
        var hashes = new (int s, int g, int c, uint v)[] { (0, 0, 0, 672839204u), (123, 0, 0, 961409981u), (123, 1, 0, 688218621u), (123, -1, 0, 2476066305u), (-17, 42, 3, 1090092324u) };
        foreach (var h in hashes)
        {
            var got = KPMath.StableHash(h.s, h.g, h.c);
            Check($"ProceduralWind.StableHash({h.s},{h.g},{h.c})", got == h.v, $"{got} 期望 {h.v}");
        }
        var prev = KPMath.SampleSmoothNoise(-8f, 2468, 0);
        bool range = true, continuity = true;
        for (int i = 0; i <= 2000; i++)
        {
            var u = -8f + i * 0.01f;
            var v = KPMath.SampleSmoothNoise(u, 2468, 0);
            range &= v >= -1.0001f && v <= 1.0001f;
            if (i > 0) continuity &= math.abs(v - prev) <= 0.1f;
            prev = v;
        }
        Check("ProceduralWind.Noise range [-1,1]", range, "");
        Check("ProceduralWind.Noise continuity", continuity, "");
        var gridMatch = true;
        for (int g = -8; g <= 8; g++) gridMatch &= KPMath.SampleSmoothNoise(g, 2468, 1) == KPMath.SampleNoiseAt(g, 2468, 1);
        Check("ProceduralWind.Noise grid match", gridMatch, "");

        // GustEnvelope
        var st = new KPProceduralWindState { GustActive = true, GustStart = 0f, GustStrength = 5f, GustRise = 1f, GustDecay = 2f, GustHold = 2f };
        var expected = new (float t, float v)[] { (0.5f, 2.5f), (1f, 5f), (2f, 5f), (3f, 5f), (4f, 2.5f), (5f, 0f), (6f, 0f) };
        var ok = expected.All(e => math.abs(st.EvaluateGust(e.t) - e.v) < 1e-4f);
        st.GustHold = 0f;
        ok &= math.abs(st.EvaluateGust(1f) - 5f) < 1e-4f && math.abs(st.EvaluateGust(3f)) < 1e-4f;
        var step = new KPProceduralWindState { GustActive = true, GustStrength = 5f, GustHold = 1f };
        ok &= math.abs(step.EvaluateGust(0.5f) - 5f) < 1e-4f && math.abs(step.EvaluateGust(1.001f)) < 1e-4f;
        Check("ProceduralWind.GustEnvelope", ok, "起 1s／持 2s／落 2s、無持續、方波");

        // RipplePropagation：同一時間根與末端的漣漪反號
        var root = KPMath.ProceduralRipple(1f, 2f, 0f, 180f, 0.5f, 0f);
        var tip = KPMath.ProceduralRipple(1f, 2f, 0f, 180f, 0.5f, 1f);
        Check("ProceduralWind.RipplePropagation", root * tip < 0f, $"root={root:0.###} tip={tip:0.###}");

        // Determinism：同種子同結果、不同種子不同
        var pA = new KPProceduralWindParams { randomForce = 3f, randomForcePeriod = 0.37f, seed = 12345 };
        var pC = new KPProceduralWindParams { randomForce = 3f, randomForcePeriod = 0.37f, seed = 54321 };
        bool same = true, diff = false;
        for (int i = 0; i < 64; i++)
        {
            var sa = new KPProceduralWindState { Time = i * 0.071f };
            KPForceProceduralWind.Sample(pA, sa, out _, out _, out var ra, out _);
            KPForceProceduralWind.Sample(pA, sa, out _, out _, out var rb, out _);
            KPForceProceduralWind.Sample(pC, sa, out _, out _, out var rc, out _);
            same &= ra == rb;
            diff |= ra != rc;
        }
        Check("ProceduralWind.Determinism", same && diff, "");
    }

    // ===================================================================
    // 3) 功能測試（真的 Transform，跑 KPSystem）
    // ===================================================================

    sealed class Rig
    {
        public GameObject Root;
        public List<Transform> Bones = new List<Transform>();
        public KawaiiPhysicsChain Chain;
        public Transform Tip => Bones[Bones.Count - 1];
    }

    /// <summary>沿 dir 方向的一條鏈，根在 root 底下 (0,1,0)。</summary>
    static Rig MakeRig(string name, int count, float spacing, Vector3 dir, Vector3 rootPos = default, bool register = true)
    {
        var rig = new Rig { Root = new GameObject(name) };
        rig.Root.transform.position = rootPos;
        Transform prev = null;
        for (int i = 0; i < count; i++)
        {
            var t = new GameObject($"{name}_{i}").transform;
            t.SetParent(prev != null ? prev : rig.Root.transform, false);
            t.localPosition = prev == null ? new Vector3(0f, 1f, 0f) : dir * spacing;
            rig.Bones.Add(t);
            prev = t;
        }
        rig.Chain = rig.Root.AddComponent<KawaiiPhysicsChain>();
        rig.Chain.rootBone = rig.Bones[0];
        rig.Chain.component = rig.Root.transform;
        rig.Chain.radius = 0.02f;
        if (register) KPSystem.Register(rig.Chain);
        return rig;
    }

    static void Step(int frames, float dt = 1f / 60f, Action<int> before = null)
    {
        for (int f = 0; f < frames; f++)
        {
            KPSystem.RestorePhase();
            before?.Invoke(f);
            Physics.SyncTransforms();
            KPSystem.SimulatePhase(dt);
        }
    }

    static void Cleanup()
    {
        KPSystem.Shutdown();
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude))
            if (go != null && go.transform.parent == null) Object.DestroyImmediate(go);
    }

    internal static void FeatureTests()
    {
        Feature("PlanarConstraint", () =>
        {
            var a = MakeRig("planarA", 6, 0.05f, Vector3.right);
            a.Chain.gravity = new Vector3(0f, -9.8f, 0f);
            a.Chain.stiffness = 0f;
            a.Chain.simpleExternalForce = new Vector3(0f, 0f, 1f);
            a.Chain.useWorldSpaceSimpleExternalForce = true;
            var b = MakeRig("planarB", 6, 0.05f, Vector3.right, new Vector3(2f, 0f, 0f));
            b.Chain.gravity = a.Chain.gravity;
            b.Chain.stiffness = 0f;
            b.Chain.simpleExternalForce = a.Chain.simpleExternalForce;
            b.Chain.useWorldSpaceSimpleExternalForce = true;
            b.Chain.planarConstraint = KPPlanarConstraint.Z;
            Step(120);
            var za = a.Bones.Max(t => Mathf.Abs(t.position.z));
            var zb = b.Bones.Max(t => Mathf.Abs(t.position.z));
            return (zb < 1e-3f && za > 0.01f, $"無限制 |z|max={za:0.0000}、平面 Z 限制 |z|max={zb:0.00000}");
        });

        Feature("BoneSubdivision 碰撞", () =>
        {
            float Run(int subdiv)
            {
                var r = MakeRig("subdiv" + subdiv, 2, 0.5f, Vector3.right, new Vector3(subdiv * 3f, 0f, 0f));
                r.Chain.gravity = new Vector3(0f, -9.8f, 0f);
                r.Chain.stiffness = 0f;
                r.Chain.boneSubdivisionCount = subdiv;
                r.Chain.boneSubdivisionCollisionOnly = false; // 只當碰撞用的假骨，推移在骨長復原時丟掉（原作 SubdivisionCollisionOnlyRestore）
                var col = new GameObject("ball").AddComponent<KawaiiPhysicsCollider>();
                col.transform.SetParent(r.Root.transform, false);
                col.transform.localPosition = new Vector3(0.25f, 0.9f, 0f);
                col.radius = 0.07f;
                r.Chain.colliders.Add(col);
                KPSystem.Register(r.Chain);
                Step(90);
                return r.Tip.position.y;
            }
            var without = Run(0);
            Cleanup();
            var with = Run(5);
            return (with > without + 0.05f, $"球在兩骨之間：不細分末端 y={without:0.000}、細分 5（模擬的假骨）末端 y={with:0.000}（被球撐住）");
        });

        Feature("BoneConstraint＋橋接假骨", () =>
        {
            float Run(int bridges)
            {
                var root = new GameObject("bridgeRoot" + bridges);
                var chain = root.AddComponent<KawaiiPhysicsChain>();
                chain.component = root.transform;
                chain.radius = 0.02f;
                chain.stiffness = 0.2f;
                var cols = new List<Transform>[2];
                for (int c = 0; c < 2; c++)
                {
                    cols[c] = new List<Transform>();
                    Transform prev = null;
                    for (int i = 0; i < 4; i++)
                    {
                        var t = new GameObject($"col{c}_{i}").transform;
                        t.SetParent(prev != null ? prev : root.transform, false);
                        t.localPosition = prev == null ? new Vector3(c * 0.3f, 1f, 0f) : Vector3.down * 0.1f;
                        cols[c].Add(t);
                        prev = t;
                    }
                }
                chain.rootBone = cols[0][0];
                chain.additionalRootBones.Add(new KawaiiPhysicsChain.RootBoneSetting { rootBone = cols[1][0] });
                for (int i = 1; i < 4; i++) chain.boneConstraints.Add(new KawaiiPhysicsChain.BoneConstraint { bone1 = cols[0][i], bone2 = cols[1][i] });
                chain.boneConstraintSubdivisionCount = bridges;
                // 兩列中間的球（只有橋接假骨碰得到），往 +Z 偏一點，把兩列推向 -Z
                var col = new GameObject("pusher").AddComponent<KawaiiPhysicsCollider>();
                col.transform.SetParent(root.transform, false);
                col.transform.localPosition = new Vector3(0.15f, 0.75f, 0.03f);
                col.radius = 0.06f;
                chain.colliders.Add(col);
                KPSystem.Register(chain);
                Step(60);
                return cols[0][3].position.z;
            }
            var without = Run(0);
            Cleanup();
            var with = Run(3);
            return (with < without - 0.005f, $"球在兩列之間：無橋接 末端 z={without:0.0000}、橋接 3 個 z={with:0.0000}（被推開）");
        });

        Feature("SyncBone", () =>
        {
            var r = MakeRig("sync", 5, 0.08f, Vector3.down, register: false);
            r.Chain.stiffness = 1f;
            var thigh = new GameObject("thigh").transform;
            thigh.SetParent(r.Root.transform, false);
            thigh.localPosition = new Vector3(0f, 0.7f, 0.1f);
            r.Chain.syncBones.Add(new KawaiiPhysicsChain.SyncBone
            {
                bone = thigh,
                targetRoots = new List<KawaiiPhysicsChain.SyncTargetRoot> { new KawaiiPhysicsChain.SyncTargetRoot { bone = r.Bones[1] } },
            });
            KPSystem.Register(r.Chain);
            Step(10);
            var before = r.Tip.position.z;
            Step(60, before: f => thigh.localPosition = new Vector3(0f, 0.7f, 0.2f));
            var after = r.Tip.position.z;
            return (after > before + 0.03f, $"大腿往前 0.1m：裙尖 z {before:0.000} → {after:0.000}");
        });

        Feature("模擬空間（World／Component／BaseBone）", () =>
        {
            float Lag(KPSimulationSpace space, float wdl)
            {
                Cleanup();
                var r = MakeRig("space" + space + wdl, 5, 0.08f, Vector3.down, register: false);
                r.Chain.simulationSpace = space;
                r.Chain.worldDampingLocation = wdl;
                r.Chain.stiffness = 0.02f;
                if (space == KPSimulationSpace.BaseBoneSpace) r.Chain.simulationBaseBone = r.Bones[0];
                KPSystem.Register(r.Chain);
                Step(5);
                Step(1, before: f => r.Root.transform.position += new Vector3(0.5f, 0f, 0f));
                return r.Tip.position.x - r.Bones[0].position.x;
            }
            var comp1 = Lag(KPSimulationSpace.ComponentSpace, 1f);
            var comp0 = Lag(KPSimulationSpace.ComponentSpace, 0f);
            var world = Lag(KPSimulationSpace.WorldSpace, 1f);
            var baseBone = Lag(KPSimulationSpace.BaseBoneSpace, 1f);
            var ok = math.abs(comp1) < 1e-3f && comp0 < -0.05f && world < -0.05f && math.abs(baseBone) < 1e-3f;
            return (ok, $"元件移動 0.5m 後末端相對根的 x：Component(wdl=1) {comp1:0.000}、Component(wdl=0) {comp0:0.000}、World {world:0.000}、BaseBone {baseBone:0.000}");
        });

        Feature("Teleport", () =>
        {
            var r = MakeRig("teleport", 5, 0.08f, Vector3.down);
            r.Chain.worldDampingLocation = 0f;
            Step(5);
            Step(1, before: f => r.Root.transform.position += new Vector3(10f, 0f, 0f));
            var lag = r.Tip.position.x - r.Bones[0].position.x;
            return (math.abs(lag) < 1e-3f, $"一幀移動 10m（門檻 3m）：末端相對根 x={lag:0.0000}（不被拖）");
        });

        Feature("外力 Basic／Gravity／Curve＋骨頭篩選", () =>
        {
            var basic = MakeRig("forceBasic", 5, 0.08f, Vector3.down, register: false);
            basic.Chain.externalForces.Add(new KPForceBasic { forceDir = new Vector3(2f, 0f, 0f) });
            KPSystem.Register(basic.Chain);
            var grav = MakeRig("forceGravity", 5, 0.08f, Vector3.right, new Vector3(0f, 0f, 3f), register: false);
            grav.Chain.stiffness = 0f;
            grav.Chain.externalForces.Add(new KPForceGravity());
            KPSystem.Register(grav.Chain);
            var curve = MakeRig("forceCurve", 5, 0.08f, Vector3.down, new Vector3(0f, 0f, 6f), register: false);
            curve.Chain.externalForces.Add(new KPForceCurve { forceZ = AnimationCurve.Constant(0f, 1f, 2f) });
            KPSystem.Register(curve.Chain);
            var filtered = MakeRig("forceFilter", 5, 0.08f, Vector3.down, new Vector3(0f, 0f, 9f), register: false);
            var f2 = new KPForceBasic { forceDir = new Vector3(2f, 0f, 0f) };
            f2.ignoreBoneFilter.AddRange(filtered.Bones);
            filtered.Chain.externalForces.Add(f2);
            KPSystem.Register(filtered.Chain);
            Step(60);
            var bx = basic.Tip.position.x;
            var gy = grav.Tip.position.y - grav.Bones[0].position.y;
            var cz = curve.Tip.position.z - curve.Bones[0].position.z;
            var fx = filtered.Tip.position.x;
            var ok = bx > 0.02f && gy < -0.1f && cz > 0.02f && math.abs(fx) < 1e-3f;
            return (ok, $"Basic 末端 x={bx:0.000}、Gravity 下垂 {gy:0.000}、Curve 末端 z={cz:0.000}、全部忽略時 x={fx:0.0000}");
        });

        Feature("ProceduralWind（本地／共用頻道）", () =>
        {
            var local = MakeRig("pwLocal", 5, 0.08f, Vector3.down, register: false);
            local.Chain.externalForces.Add(new KPForceProceduralWind { parameters = new KPProceduralWindParams { windDirection = Vector3.right, constantForce = 2f, swayForce = 0.5f } });
            KPSystem.Register(local.Chain);
            var channel = ScriptableObject.CreateInstance<KawaiiPhysicsWindChannel>();
            channel.parameters.windDirection = Vector3.forward;
            channel.parameters.constantForce = 2f;
            var sharedA = MakeRig("pwSharedA", 5, 0.08f, Vector3.down, new Vector3(0f, 0f, 3f), register: false);
            var sharedB = MakeRig("pwSharedB", 5, 0.08f, Vector3.down, new Vector3(0f, 0f, 6f), register: false);
            foreach (var r in new[] { sharedA, sharedB })
            {
                r.Chain.externalForces.Add(new KPForceProceduralWind { windSource = KPWindSource.Shared, sharedChannel = channel });
                KPSystem.Register(r.Chain);
            }
            Step(60);
            var lx = local.Tip.position.x;
            var az = sharedA.Tip.position.z - 3f;
            var bz = sharedB.Tip.position.z - 6f;
            var clock = channel.State.Time;
            Object.DestroyImmediate(channel);
            return (lx > 0.02f && az > 0.02f && math.abs(az - bz) < 1e-4f && math.abs(clock - 1f) < 1e-3f,
                $"本地 末端 x={lx:0.000}；共用頻道 A z={az:0.0000}、B z={bz:0.0000}（一樣）；頻道時鐘 60 幀＝{clock:0.000}s");
        });

        Feature("場景風（WindZone）：Chain 的 enableWind 與 Wind 外力", () =>
        {
            var zone = new GameObject("wind").AddComponent<WindZone>();
            zone.mode = WindZoneMode.Directional;
            zone.transform.rotation = Quaternion.LookRotation(Vector3.right);
            zone.windMain = 1.5f;
            zone.windPulseMagnitude = 0f;
            var a = MakeRig("windChain", 5, 0.08f, Vector3.down);
            a.Chain.enableWind = true;
            var b = MakeRig("windForce", 5, 0.08f, Vector3.down, new Vector3(0f, 0f, 3f), register: false);
            b.Chain.externalForces.Add(new KPForceWind());
            KPSystem.Register(b.Chain);
            Step(60);
            var ax = a.Tip.position.x;
            var bx = b.Tip.position.x;
            return (ax > 0.01f && bx > 0.01f, $"風往 +X：enableWind 末端 x={ax:0.000}、Wind 外力 末端 x={bx:0.000}");
        });

        Feature("暫時突風（RequestTransientGust）與停止", () =>
        {
            var r = MakeRig("gust", 5, 0.08f, Vector3.down);
            Step(5);
            r.Chain.RequestTransientGust(3f, 0.1f, 0.3f, Vector3.right);
            var peak = 0f;
            for (int i = 0; i < 20; i++) { Step(1); peak = Mathf.Max(peak, r.Tip.position.x); }
            var during = r.Chain.TransientForceCount;
            Step(240);
            var after = r.Chain.TransientForceCount;
            var end = r.Tip.position.x;
            var h = r.Chain.RequestTransientGust(3f, 0.1f, 5f, Vector3.right);
            Step(10);
            r.Chain.RequestStopTransientExternalForce(h, 0f);
            var stopped = r.Chain.TransientForceCount;
            return (peak > 0.01f && during == 1 && after == 0 && stopped == 0 && math.abs(end) < 0.01f,
                $"最大 x={peak:0.000}、期間 {during} 個、結束後 {after} 個、回到 x={end:0.0000}、立即停止後 {stopped} 個");
        });

        Feature("設定倍率（Start／Push＋租約）", () =>
        {
            var r = MakeRig("mult", 5, 0.08f, Vector3.down);
            Step(1);
            var scale = KPSettingsMultiplier.One;
            scale.Stiffness = 0f;
            r.Chain.StartSettingsMultiplier(scale, 0.5f, 0.5f, 0.5f);
            float At(int frames) { Step(frames); return KPSystem.World.Instances[0].Multiplier.Stiffness; }
            var m1 = At(15);
            var m2 = At(30);
            var m3 = At(30);
            var m4 = At(30);
            r.Chain.PushSettingsMultiplier(12345L, scale, 0.25f, 3);
            var p1 = At(1);
            var p2 = At(5);
            var ok = math.abs(m1 - 0.5f) < 0.03f && math.abs(m2) < 1e-3f && math.abs(m3 - 0.5f) < 0.03f && math.abs(m4 - 1f) < 1e-3f &&
                     math.abs(p1 - 0.75f) < 1e-3f && p2 > 0.75f;
            return (ok, $"Stiffness 倍率 0.25s={m1:0.00}、0.75s={m2:0.00}、1.25s={m3:0.00}、1.75s={m4:0.00}；Push α0.25→{p1:0.00}、租約到期後={p2:0.00}");
        });

        Feature("Alpha 0＝動畫姿勢", () =>
        {
            var r = MakeRig("alpha", 5, 0.08f, Vector3.right);
            r.Chain.gravity = new Vector3(0f, -9.8f, 0f);
            r.Chain.stiffness = 0f;
            r.Chain.alpha = 0f;
            Step(60);
            var dy = r.Tip.position.y - r.Bones[0].position.y;
            return (math.abs(dy) < 1e-4f, $"有重力但 alpha=0：末端相對根 y={dy:0.00000}");
        });

        Feature("WarmUp", () =>
        {
            float Drop(bool warm)
            {
                Cleanup();
                var r = MakeRig("warm" + warm, 6, 0.05f, Vector3.right, register: false);
                r.Chain.gravity = new Vector3(0f, -9.8f, 0f);
                r.Chain.stiffness = 0f;
                r.Chain.damping = 0.2f;
                r.Chain.warmUpFrames = warm ? 180 : 0;
                r.Chain.needWarmUp = warm;
                KPSystem.Register(r.Chain);
                Step(1);
                return r.Bones[0].position.y - r.Tip.position.y;
            }
            var no = Drop(false);
            var yes = Drop(true);
            return (yes > 0.2f && no < 0.05f, $"第一幀的下垂：無暖機 {no:0.000}、暖機 180 幀 {yes:0.000}（完全下垂 0.25）");
        });

        Feature("World Collision（掃掠）", () =>
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.transform.position = new Vector3(0f, 0.85f, 0f);
            floor.transform.localScale = new Vector3(2f, 0.02f, 2f);
            var r = MakeRig("worldCol", 6, 0.05f, Vector3.right);
            r.Chain.gravity = new Vector3(0f, -9.8f, 0f);
            r.Chain.stiffness = 0f;
            r.Chain.allowWorldCollision = true;
            var hits = 0;
            for (int i = 0; i < 120; i++)
            {
                Step(1);
                var w = KPSystem.World;
                for (int k = 0; k < w.SweepHits.Length; k++) if (w.SweepHits[k].normal.sqrMagnitude > 0f) hits++;
            }
            var minY = r.Bones.Skip(1).Min(t => t.position.y);
            // 不穿過去即可：Unity 的掃掠不回報起點重疊，靜止時可能陷入表面一個半徑以內（UE 會用穿透深度推出）
            return (minY > 0.80f, $"地板上表面 y=0.86、骨半徑 0.02：骨頭最低 y={minY:0.000}（沒有掉到 0.75，殘留陷入 {Mathf.Max(0f, 0.88f - minY):0.000}）；120 幀命中 {hits} 次");
        });

        Feature("Simple World Collision（收集場景 Collider＋地面）", () =>
        {
            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.transform.position = new Vector3(0.15f, 0.9f, 0f);
            ball.transform.localScale = Vector3.one * 0.16f;
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.position = new Vector3(0f, 0.8f, 0f);
            var r = MakeRig("simpleWorld", 6, 0.05f, Vector3.right);
            r.Chain.gravity = new Vector3(0f, -9.8f, 0f);
            r.Chain.stiffness = 0f;
            r.Chain.useSimpleWorldCollision = true;
            r.Chain.simpleWorldCollisionGatherInterval = 0f;
            Step(120);
            var minBall = r.Bones.Skip(1).Min(t => Vector3.Distance(t.position, ball.transform.position));
            var minY = r.Bones.Skip(1).Min(t => t.position.y);
            return (minBall >= 0.1f - 2e-3f && minY >= 0.82f - 2e-3f, $"球（半徑 0.08）最近距離 {minBall:0.000}（≥ 0.10）、地面 0.80 上的最低 y={minY:0.000}（≥ 0.82）");
        });

        Feature("共用碰撞（Shared Collision）", () =>
        {
            var root = new GameObject("sharedRoot");
            var src = MakeRig("sharedSrc", 2, 0.05f, Vector3.down, register: false);
            src.Root.transform.SetParent(root.transform);
            var col = new GameObject("srcBall").AddComponent<KawaiiPhysicsCollider>();
            col.transform.SetParent(src.Root.transform, false);
            col.transform.localPosition = new Vector3(0.12f, 0.9f, 0f);
            col.radius = 0.06f;
            src.Chain.colliders.Add(col);
            src.Chain.sharedCollisionSource = true;
            src.Chain.sharedCollisionTag = "body";
            src.Chain.component = root.transform;
            var dst = MakeRig("sharedDst", 6, 0.05f, Vector3.right, register: false);
            dst.Root.transform.SetParent(root.transform);
            dst.Chain.component = root.transform;
            dst.Chain.gravity = new Vector3(0f, -9.8f, 0f);
            dst.Chain.stiffness = 0f;
            dst.Chain.useSharedCollision = true;
            dst.Chain.sharedCollisionTag = "body";
            KPSystem.Register(src.Chain);
            KPSystem.Register(dst.Chain);
            Step(90);
            var minD = dst.Bones.Skip(1).Min(t => Vector3.Distance(t.position, col.transform.position));
            return (minD >= 0.08f - 2e-3f, $"目標鏈離來源的球最近 {minD:0.000}（≥ 0.08）");
        });

        Feature("碰撞資產＋鏡像、約束資產＋正規表示式、預設", () =>
        {
            var r = MakeRig("assets", 3, 0.05f, Vector3.down, register: false);
            var l = new GameObject("leg_L").transform; l.SetParent(r.Root.transform, false);
            var rr = new GameObject("leg_R").transform; rr.SetParent(r.Root.transform, false);
            var limits = ScriptableObject.CreateInstance<KawaiiPhysicsLimitsAsset>();
            limits.entries.Add(new KawaiiPhysicsLimitsAsset.Entry { boneName = "leg_L", offset = new Vector3(0.1f, 0f, 0f), radius = 0.05f });
            limits.mirror = true;
            r.Chain.limitsAsset = limits;
            var cons = ScriptableObject.CreateInstance<KawaiiPhysicsConstraintsAsset>();
            cons.regexPatterns.Add(new KawaiiPhysicsConstraintsAsset.RegexPair { pattern1 = @"^a_\d+$", pattern2 = @"^b_\d+$" });
            var added = cons.ApplyRegex(new[] { "a_1", "b_1", "a_2", "b_2", "c_1" });
            var preset = ScriptableObject.CreateInstance<KawaiiPhysicsPreset>();
            preset.data.damping = 0.33f;
            preset.data.limitAngle = 45f;
            preset.ApplyTo(r.Chain);
            KPSystem.Register(r.Chain);
            Step(1);
            var spheres = KPSystem.World.Instances[0].SphereCount;
            var s1 = KPSystem.World.Spheres[1];
            var ok = spheres == 2 && math.abs(s1.Offset.x + 0.1f) < 1e-5f && added == 2 && cons.entries[1].boneName2 == "b_2" &&
                     math.abs(r.Chain.damping - 0.33f) < 1e-5f && math.abs(r.Chain.limitAngle - 45f) < 1e-5f;
            Object.DestroyImmediate(limits); Object.DestroyImmediate(cons); Object.DestroyImmediate(preset);
            return (ok, $"鏡像後球 {spheres} 個（反側 offset.x={s1.Offset.x:0.00}）、正規表示式配對 {added} 組、預設套用 damping={r.Chain.damping:0.00} limitAngle={r.Chain.limitAngle:0}");
        });

        Feature("錐形膠囊／凸包碰撞體（元件）", () =>
        {
            var r = MakeRig("shapes", 6, 0.05f, Vector3.right, register: false);
            r.Chain.gravity = new Vector3(0f, -9.8f, 0f);
            r.Chain.stiffness = 0f;
            var tc = new GameObject("tapered").AddComponent<KawaiiPhysicsCollider>();
            tc.transform.SetParent(r.Root.transform, false);
            tc.transform.localPosition = new Vector3(0.1f, 0.9f, 0f);
            tc.shape = KawaiiPhysicsCollider.Shape.TaperedCapsule;
            tc.direction = KawaiiPhysicsCollider.Axis.Z;
            tc.radius = 0.05f;
            tc.radius1 = 0.03f;
            tc.length = 0.2f;
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var cv = new GameObject("convex").AddComponent<KawaiiPhysicsCollider>();
            cv.transform.SetParent(r.Root.transform, false);
            cv.transform.localPosition = new Vector3(0.22f, 0.86f, 0f);
            cv.shape = KawaiiPhysicsCollider.Shape.Convex;
            cv.convexMesh = cube.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(cube);
            r.Chain.colliders.Add(tc);
            r.Chain.colliders.Add(cv);
            KPSystem.Register(r.Chain);
            Step(90);
            var inst = KPSystem.World.Instances[0];
            var planes = KPSystem.World.Convexes[0].PlaneCount;
            var minY = r.Bones.Skip(1).Min(t => t.position.y);
            return (inst.TaperedCount == 1 && inst.ConvexCount == 1 && planes == 6 && minY > 0.75f,
                $"錐形 {inst.TaperedCount}、凸包 {inst.ConvexCount}（{planes} 面）；鏈被撐住，最低 y={minY:0.000}");
        });

        Feature("AnimationEvents（事件預設：突風、倍率、外力）", () =>
        {
            var r = MakeRig("events", 4, 0.05f, Vector3.down);
            var ev = r.Root.AddComponent<KawaiiPhysicsAnimationEvents>();
            var gust = ScriptableObject.CreateInstance<KawaiiPhysicsEventPreset>();
            gust.kind = KawaiiPhysicsEventPreset.Kind.Gust;
            var mult = ScriptableObject.CreateInstance<KawaiiPhysicsEventPreset>();
            mult.kind = KawaiiPhysicsEventPreset.Kind.SettingsMultiplier;
            var force = ScriptableObject.CreateInstance<KawaiiPhysicsEventPreset>();
            force.kind = KawaiiPhysicsEventPreset.Kind.AddExternalForce;
            ev.KawaiiPhysicsEvent(gust);
            ev.KawaiiPhysicsEvent(mult);
            ev.KawaiiPhysicsEvent(force);
            ev.KawaiiPhysicsSetAlpha(0.5f);
            var transients = r.Chain.TransientForceCount;
            var mults = r.Chain.SettingsMultiplierCount;
            Object.DestroyImmediate(gust); Object.DestroyImmediate(mult); Object.DestroyImmediate(force);
            return (transients == 2 && mults == 1 && math.abs(r.Chain.alpha - 0.5f) < 1e-5f, $"暫時外力 {transients} 個、倍率 {mults} 個、alpha={r.Chain.alpha}");
        });
    }

    static void Feature(string name, Func<(bool ok, string detail)> body)
    {
        try
        {
            Cleanup();
            var (ok, detail) = body();
            Check(name, ok, detail);
        }
        catch (Exception e)
        {
            Check(name, false, "例外：" + e.GetType().Name + " " + e.Message.Replace("\n", " ") + " @ " + e.StackTrace?.Split('\n').FirstOrDefault()?.Trim());
        }
        finally
        {
            Cleanup();
        }
    }

    // ===================================================================
    // 4) 管線健全性與效能
    // ===================================================================

    internal static void PipelineSanity()
    {
        Log("## 4. 管線健全性（Transform 讀 → 解 → 寫）");
        Log("");
        var snaps = new Dictionary<KPBackend, Vector3[]>();
        foreach (var backend in new[] { KPBackend.BurstParallel, KPBackend.BurstSingleThread, KPBackend.Managed })
        {
            Cleanup();
            KPSystem.Backend = backend;
            var root = new GameObject("sanity");
            root.transform.SetPositionAndRotation(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 0f, 90f));
            var r = MakeRig("sanityChain", 6, 0.05f, Vector3.right, register: false);
            r.Root.transform.SetParent(root.transform, false);
            r.Chain.component = root.transform;
            r.Chain.gravity = new Vector3(0f, -9.8f, 0f);
            r.Chain.stiffness = 0f;
            KPSystem.Register(r.Chain);
            Step(500, before: f => { if (f < 200) root.transform.position = new Vector3(1f + 0.2f * Mathf.Sin(f * 0.1f), 0f, 2f); });
            var lenErr = 0f;
            for (int i = 1; i < r.Bones.Count; i++) lenErr = Mathf.Max(lenErr, Mathf.Abs((r.Bones[i].position - r.Bones[i - 1].position).magnitude - 0.05f));
            var drop = r.Bones[0].position.y - r.Tip.position.y;
            Log($"- {backend}: 骨長誤差最大 {lenErr * 1000f:0.000} mm；鏈尖下垂 {drop:0.0000} m（元件轉 90°、完全垂直＝0.2500）");
            snaps[backend] = r.Bones.Select(t => t.position).ToArray();
        }
        KPSystem.Backend = KPBackend.BurstParallel;
        float Diff(Vector3[] a, Vector3[] b) => a.Zip(b, (x, y) => (x - y).magnitude).Max();
        Log($"- 後端之間的最終位置差：Burst 平行 vs 單執行緒 {Diff(snaps[KPBackend.BurstParallel], snaps[KPBackend.BurstSingleThread]) * 1000f:0.0000} mm，Burst vs 純 C# {Diff(snaps[KPBackend.BurstParallel], snaps[KPBackend.Managed]) * 1000f:0.0000} mm");
        Log("");
        Cleanup();
    }

    const int ChainsPerCharacter = 16;
    const int BonesPerChain = 8;

    static List<GameObject> BuildCrowd(int n)
    {
        var gos = new List<GameObject>();
        for (int c = 0; c < n; c++)
        {
            var root = new GameObject("crowd" + c);
            root.transform.position = new Vector3(c * 2f, 0f, 0f);
            var chain = root.AddComponent<KawaiiPhysicsChain>();
            chain.component = root.transform;
            chain.dummyBoneLength = 0.05f;
            chain.boneForwardAxis = KawaiiPhysicsChain.Axis.YNegative;
            chain.gravity = new Vector3(0f, -9.8f, 0f);
            chain.limitAngle = 60f;
            chain.radius = 0.01f;
            for (int k = 0; k < ChainsPerCharacter; k++)
            {
                var a = k * Mathf.PI * 2f / ChainsPerCharacter;
                Transform prev = null;
                for (int i = 0; i < BonesPerChain; i++)
                {
                    var t = new GameObject($"c{k}_{i}").transform;
                    t.SetParent(prev != null ? prev : root.transform, false);
                    t.localPosition = prev == null ? new Vector3(Mathf.Cos(a) * 0.12f, 1.6f, Mathf.Sin(a) * 0.12f) : Vector3.down * 0.05f;
                    if (i == 0)
                    {
                        if (k == 0) chain.rootBone = t;
                        else chain.additionalRootBones.Add(new KawaiiPhysicsChain.RootBoneSetting { rootBone = t });
                    }
                    prev = t;
                }
            }
            for (int s = 0; s < 4; s++)
            {
                var col = new GameObject("ball").AddComponent<KawaiiPhysicsCollider>();
                col.transform.SetParent(root.transform, false);
                col.transform.localPosition = new Vector3(0.1f * s - 0.15f, 1.2f, 0.1f);
                col.radius = 0.08f;
                chain.colliders.Add(col);
            }
            for (int s = 0; s < 6; s++)
            {
                var col = new GameObject("cap").AddComponent<KawaiiPhysicsCollider>();
                col.transform.SetParent(root.transform, false);
                col.transform.localPosition = new Vector3(0.08f * s - 0.2f, 0.9f, 0f);
                col.shape = KawaiiPhysicsCollider.Shape.Capsule;
                col.radius = 0.06f;
                col.length = 0.3f;
                chain.colliders.Add(col);
            }
            KPSystem.Register(chain);
            gos.Add(root);
        }
        return gos;
    }

    static void MoveCrowd(List<GameObject> gos, int f)
    {
        for (int c = 0; c < gos.Count; c++)
        {
            var a = f * 0.05f + c;
            gos[c].transform.SetPositionAndRotation(new Vector3(c * 2f + Mathf.Cos(a) * 0.5f, 0f, Mathf.Sin(a) * 0.5f), Quaternion.Euler(0f, a * 30f, 0f));
        }
    }

    static void SolverBenchmark()
    {
        var perChar = ChainsPerCharacter * (BonesPerChain + 1);
        Log($"## 5. 解算（每角色 {ChainsPerCharacter} 鏈 × {BonesPerChain} 骨＋末端假骨＝{perChar} 骨、4 球＋6 膠囊，60fps 一步；含姿勢換算與輸出）");
        Log("");
        Log("| 角色數 | 骨頭數 | Burst 平行 (ms) | Burst 單執行緒 (ms) | 純 C# (ms) | Burst 單 ns/骨 | 純 C# ns/骨 |");
        Log("|---|---|---|---|---|---|---|");
        foreach (var n in new[] { 1, 16, 128 })
        {
            var t = new double[3];
            var backends = new[] { KPBackend.BurstParallel, KPBackend.BurstSingleThread, KPBackend.Managed };
            for (int b = 0; b < 3; b++)
            {
                Cleanup();
                KPSystem.Backend = backends[b];
                var gos = BuildCrowd(n);
                var frames = backends[b] == KPBackend.Managed ? (n >= 128 ? 20 : 60) : (n >= 128 ? 200 : 600);
                var sw = new Stopwatch();
                for (int f = -30; f < frames; f++)
                {
                    KPSystem.RestorePhase();
                    MoveCrowd(gos, f);
                    var world = KPSystem.World;
                    new KPReadJob { Reads = world.Reads }.ScheduleReadOnly(world.Transforms, 32).Complete();
                    if (f >= 0) sw.Start();
                    KPSystem.ScheduleSolve(1f / 60f, default).Complete();
                    if (f >= 0) sw.Stop();
                }
                t[b] = sw.Elapsed.TotalMilliseconds / frames;
            }
            var bones = n * perChar;
            Log($"| {n} | {bones} | {t[0]:0.000} | {t[1]:0.000} | {t[2]:0.000} | {t[1] * 1e6 / bones:0.0} | {t[2] * 1e6 / bones:0.0} |");
        }
        KPSystem.Backend = KPBackend.BurstParallel;
        Cleanup();
        Log("");
    }

    static void PipelineBenchmark()
    {
        Log("## 6. 整條管線（真的 GameObject；還原＋讀＋解＋寫，Burst 平行）");
        Log("");
        Log("| 角色數 | 還原 (ms) | 讀 (ms) | 解 (ms) | 寫 (ms) | 合計 (ms) |");
        Log("|---|---|---|---|---|---|");
        foreach (var n in new[] { 16, 128 })
        {
            Cleanup();
            var gos = BuildCrowd(n);
            const int frames = 200;
            var t = new double[4];
            var sw = new Stopwatch();
            for (int f = -30; f < frames; f++)
            {
                MoveCrowd(gos, f);
                sw.Restart(); KPSystem.RestorePhase(); var tr = sw.Elapsed.TotalMilliseconds;
                var world = KPSystem.World;
                sw.Restart(); new KPReadJob { Reads = world.Reads }.ScheduleReadOnly(world.Transforms, 32).Complete(); var tre = sw.Elapsed.TotalMilliseconds;
                sw.Restart(); KPSystem.ScheduleSolve(1f / 60f, default).Complete(); var ts = sw.Elapsed.TotalMilliseconds;
                sw.Restart(); new KPWriteJob { Writes = world.Writes }.Schedule(world.Transforms).Complete(); var tw = sw.Elapsed.TotalMilliseconds;
                if (f < 0) continue;
                t[0] += tr; t[1] += tre; t[2] += ts; t[3] += tw;
            }
            for (int k = 0; k < 4; k++) t[k] /= frames;
            Log($"| {n} | {t[0]:0.000} | {t[1]:0.000} | {t[2]:0.000} | {t[3]:0.000} | {t.Sum():0.000} |");
        }
        Cleanup();
        Log("");
    }
}
}
