// 原作的 golden、原作單元測試的移植、功能測試（KPBench 的各段），一段一個測試。
using System;
using System.Linq;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs.LowLevel.Unsafe;

namespace KawaiiPhysicsUnity.Tests
{
    public class KPBenchTests
    {
        [OneTimeSetUp]
        public void SetUp() => KPTestSettings.EnableSafety();

        [TearDown]
        public void TearDown() => KPSystem.Shutdown();

        static void Run(Action section, int expected)
        {
            KPBench.ResetResults();
            section();
            foreach (var r in KPBench.Results) TestContext.WriteLine($"{(r.ok ? "PASS" : "FAIL")} {r.name} {r.detail}");
            var failed = KPBench.Results.Where(r => !r.ok).Select(r => $"{r.name}: {r.detail}").ToList();
            Assert.That(failed, Is.Empty, string.Join("\n", failed));
            Assert.That(KPBench.Results.Count, Is.EqualTo(expected), "檢查項目的數量");
        }

        /// <summary>UE 原版 200 幀 @90fps 的骨頭位置，4 個情境，誤差 &lt; 0.01 cm。</summary>
        [Test]
        public void Golden() => Run(KPBench.Golden, 4);

        /// <summary>KawaiiPhysicsCollisionTest.cpp 的移植。</summary>
        [Test]
        public void CollisionUnitTests() => Run(KPBench.CollisionUnitTests, 29);

        /// <summary>KawaiiPhysicsProceduralWindTest.cpp 的移植。</summary>
        [Test]
        public void ProceduralWindUnitTests() => Run(KPBench.ProceduralWindUnitTests, 11);

        /// <summary>真的 Transform 跑 KPSystem，逐一檢查功能。</summary>
        [Test]
        public void FeatureTests() => Run(KPBench.FeatureTests, 19);
    }

    /// <summary>測試時開著工作除錯器、安全檢查、洩漏偵測；Burst 同步編譯（背景編譯會在量測中途配置記憶體）。</summary>
    public static class KPTestSettings
    {
        public static void EnableSafety()
        {
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
            JobsUtility.JobDebuggerEnabled = true;
            BurstCompiler.Options.EnableBurstSafetyChecks = true;
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
        }

        public static void DisableSafetyForTiming()
        {
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Disabled;
            JobsUtility.JobDebuggerEnabled = false;
            BurstCompiler.Options.EnableBurstSafetyChecks = false;
        }
    }
}
