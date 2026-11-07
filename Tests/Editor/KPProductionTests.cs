// production ready 的要求：加減角色互不影響（B2）、極端輸入不產生 NaN（B5）、穩態每幀 0 GC 配置（C1）、
// 管線耗時（C2）、加入／移除一個角色的耗時（C3）。編輯模式下手動推進 KPSystem 的兩個階段。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KawaiiPhysicsUnity.Tests
{
    public class KPProductionTests
    {
        static readonly Regex BoneName = new Regex(@"^c\d+_\d+$");

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

        static KawaiiPhysicsChain Spawn(string name, Vector3 pos, int variant, bool features)
        {
            var c = KPTestRig.BuildCharacter(name, pos, variant, features);
            KPSystem.Register(c); // 編輯模式不會呼叫 OnEnable
            return c;
        }

        static void Frame(int f, IList<KawaiiPhysicsChain> chains, IList<Vector3> bases, float dt = 1f / 60f)
        {
            KPSystem.RestorePhase();
            for (int i = 0; i < chains.Count; i++)
                if (chains[i] != null) KPTestRig.Move(chains[i].transform, i, f, bases[i]);
            Physics.SyncTransforms();
            KPSystem.SimulatePhase(dt);
        }

        // ---- B2 -------------------------------------------------------------

        /// <summary>mode 0：A、B；1：第 60 幀加入 C；2：C 一開始就在、第 60 幀移除。回傳 A 每幀的骨頭位置。</summary>
        static List<Vector3[]> RunAddRemove(int mode)
        {
            Cleanup();
            var chains = new List<KawaiiPhysicsChain> { Spawn("A", Vector3.zero, 0, false), Spawn("B", new Vector3(2f, 0f, 0f), 1, false) };
            var bases = new List<Vector3> { Vector3.zero, new Vector3(2f, 0f, 0f) };
            if (mode == 2)
            {
                chains.Add(Spawn("C", new Vector3(4f, 0f, 0f), 2, false));
                bases.Add(new Vector3(4f, 0f, 0f));
            }
            var aBones = chains[0].GetComponentsInChildren<Transform>().Where(t => BoneName.IsMatch(t.name)).ToArray();
            var traj = new List<Vector3[]>();
            for (int f = 0; f < 180; f++)
            {
                if (f == 60 && mode == 1)
                {
                    chains.Add(Spawn("C", new Vector3(4f, 0f, 0f), 2, false));
                    bases.Add(new Vector3(4f, 0f, 0f));
                }
                if (f == 60 && mode == 2)
                {
                    KPSystem.Unregister(chains[2]);
                    Object.DestroyImmediate(chains[2].gameObject);
                    chains.RemoveAt(2);
                    bases.RemoveAt(2);
                }
                Frame(f, chains, bases);
                traj.Add(aBones.Select(t => t.position).ToArray());
            }
            return traj;
        }

        [TestCase(1, TestName = "B2 加入一個角色不影響其他角色")]
        [TestCase(2, TestName = "B2 移除一個角色不影響其他角色")]
        public void AddRemoveIsolation(int mode)
        {
            var control = RunAddRemove(0);
            var test = RunAddRemove(mode);
            var maxDiff = 0f;
            for (int f = 0; f < control.Count; f++)
                maxDiff = Mathf.Max(maxDiff, control[f].Zip(test[f], (a, b) => (a - b).magnitude).Max());
            TestContext.WriteLine($"A 與對照組的最大差 {maxDiff * 1000f:0.000} mm");
            Assert.That(maxDiff, Is.LessThan(1e-5f));
        }

        [Test(Description = "設定改了就地更新：不同步、不重建（同一個段，模擬狀態保留），下一幀起用新的值")]
        public void SettingsChangeKeepsState()
        {
            var chains = new List<KawaiiPhysicsChain> { Spawn("A", Vector3.zero, 0, false) };
            var bases = new List<Vector3> { Vector3.zero };
            for (int f = 0; f < 60; f++) Frame(f, chains, bases);
            var syncs = KPSystem.World.SyncCount;
            var segment = chains[0].runtime.Segment;
            chains[0].stiffness = 0.5f;
            // 改值之後的兩幀（還原＋模擬各兩次）：同步次數不變、還是同一個段
            for (int k = 0; k < 2; k++)
            {
                KPSystem.RestorePhase();
                Physics.SyncTransforms();
                KPSystem.SimulatePhase(1f / 60f);
            }
            Assert.That(KPSystem.ChainCount, Is.EqualTo(1));
            Assert.That(KPSystem.World.SyncCount, Is.EqualTo(syncs), "改 stiffness 不應觸發同步");
            Assert.That(chains[0].runtime.Segment, Is.SameAs(segment), "段沒有重建");
            var inst = KPSystem.World.Instances[chains[0].runtime.InstanceIndex];
            Assert.That(KPSystem.World.Bones[inst.BoneStart + 1].BaseSettings.Stiffness, Is.EqualTo(0.5f).Within(1e-6f));
        }

        [Test(Description = "停用的鏈：骨頭放回靜止姿勢")]
        public void UnregisterRestoresRestPose()
        {
            var chains = new List<KawaiiPhysicsChain> { Spawn("A", Vector3.zero, 0, false) };
            var bases = new List<Vector3> { Vector3.zero };
            var bone = chains[0].GetComponentsInChildren<Transform>().First(t => t.name == "c0_3");
            var rest = bone.localRotation;
            for (int f = 0; f < 60; f++) Frame(f, chains, bases);
            Assert.That(Quaternion.Angle(bone.localRotation, rest), Is.GreaterThan(0.01f), "物理有動到骨頭");
            KPSystem.Unregister(chains[0]);
            KPSystem.RestorePhase();
            Assert.That(Quaternion.Angle(bone.localRotation, rest), Is.LessThan(1e-3f));
        }

        [Test(Description = "在 LateUpdate 這類兩個階段之間停用：當場放回靜止姿勢，同一幀的解算也不寫回")]
        public void UnregisterBetweenPhasesRestoresImmediately()
        {
            var chains = new List<KawaiiPhysicsChain> { Spawn("A", Vector3.zero, 0, false) };
            var bases = new List<Vector3> { Vector3.zero };
            var bone = chains[0].GetComponentsInChildren<Transform>().First(t => t.name == "c0_3");
            var rest = bone.localRotation;
            for (int f = 0; f < 60; f++) Frame(f, chains, bases);
            KPSystem.RestorePhase();
            KPTestRig.Move(chains[0].transform, 0, 60, bases[0]);
            KPSystem.SimulatePhase(1f / 60f);
            Assert.That(Quaternion.Angle(bone.localRotation, rest), Is.GreaterThan(0.01f), "物理有動到骨頭");
            KPSystem.Unregister(chains[0]);
            Assert.That(Quaternion.Angle(bone.localRotation, rest), Is.LessThan(1e-3f), "停用當下");
            KPSystem.SimulatePhase(1f / 60f);
            Assert.That(Quaternion.Angle(bone.localRotation, rest), Is.LessThan(1e-3f), "同一幀的解算之後");
            Assert.That(KPSystem.ChainCount, Is.EqualTo(0));
        }

        [Test(Description = "停用後改了骨頭的區域姿勢（換裝、體型）：不會被舊的靜止姿勢蓋掉，再啟用時用新的姿勢當靜止姿勢")]
        public void UnregisterForgetsRestPose()
        {
            var chains = new List<KawaiiPhysicsChain> { Spawn("A", Vector3.zero, 0, false) };
            var bases = new List<Vector3> { Vector3.zero };
            var bone = chains[0].GetComponentsInChildren<Transform>().First(t => t.name == "c0_2");
            for (int f = 0; f < 30; f++) Frame(f, chains, bases);
            KPSystem.Unregister(chains[0]);
            var changed = bone.localPosition * 1.3f;
            bone.localPosition = changed;
            KPSystem.RestorePhase();
            Assert.That(Vector3.Distance(bone.localPosition, changed), Is.LessThan(1e-6f), "停用後的下一幀不再寫回舊的靜止姿勢");

            KPSystem.Register(chains[0]);
            for (int f = 30; f < 60; f++) Frame(f, chains, bases);
            KPSystem.Unregister(chains[0]);
            Assert.That(Vector3.Distance(bone.localPosition, changed), Is.LessThan(1e-6f), "再啟用時記的是新的姿勢");
        }

        // ---- 空洞與整理（Repack）：有外力的角色 ------------------------------

        /// <summary>每個角色三種外力（結果與時間只由幀號決定，兩次執行可以逐幀比對）。</summary>
        static KawaiiPhysicsChain SpawnWithForces(int id)
        {
            var c = KPTestRig.BuildCharacter($"char{id}", new Vector3(id * 2f, 0f, 0f), id, false);
            c.externalForces.Add(new KPForceBasic { forceDir = new Vector3(0.3f, 0f, 0.1f) });
            c.externalForces.Add(new KPForceGravity());
            var curve = new KPForceCurve();
            curve.forceZ = AnimationCurve.Linear(0f, -0.5f, 1f, 0.5f);
            c.externalForces.Add(curve);
            KPSystem.Register(c);
            return c;
        }

        /// <summary>外力的槽位：每條鏈都在 Forces 範圍內、互不重疊。</summary>
        static void AssertForceSlotsValid()
        {
            var w = KPSystem.World;
            var ranges = new List<(int start, int end)>();
            for (int i = 0; i < w.InstanceCount; i++)
            {
                var start = w.Instances[i].ForceStart;
                var end = start + w.Segments[i].Size.Force;
                Assert.That(start, Is.GreaterThanOrEqualTo(0), $"第 {i} 條的 ForceStart");
                Assert.That(end, Is.LessThanOrEqualTo(w.Forces.Length), $"第 {i} 條的外力槽位超出 Forces");
                ranges.Add((start, end));
            }
            ranges.Sort();
            for (int i = 1; i < ranges.Count; i++)
                Assert.That(ranges[i].start, Is.GreaterThanOrEqualTo(ranges[i - 1].end), "外力槽位重疊");
        }

        /// <summary>removeOthers：8 個角色、第 60 幀移除 0–4 號（空洞過半 → 整理）；否則只有 5–7 號（對照組）。回傳 5–7 號每幀的骨頭位置。</summary>
        static List<Vector3[]> RunCompaction(bool removeOthers, out bool compacted)
        {
            Cleanup();
            compacted = false;
            var alive = new Dictionary<int, KawaiiPhysicsChain>();
            for (int id = 0; id < 8; id++)
                if (removeOthers || id >= 5) alive[id] = SpawnWithForces(id);
            var watched = new[] { 5, 6, 7 }.SelectMany(id => alive[id].GetComponentsInChildren<Transform>().Where(t => BoneName.IsMatch(t.name))).ToArray();
            var traj = new List<Vector3[]>();
            for (int f = 0; f < 180; f++)
            {
                if (removeOthers && f == 60)
                    for (int id = 0; id < 5; id++)
                    {
                        KPSystem.Unregister(alive[id]);
                        Object.DestroyImmediate(alive[id].gameObject);
                        alive.Remove(id);
                    }
                KPSystem.RestorePhase();
                if (removeOthers && f == 60)
                {
                    compacted = KPSystem.World.HoleCount == 0;
                    AssertForceSlotsValid();
                }
                foreach (var kv in alive) KPTestRig.Move(kv.Value.transform, kv.Key, f, new Vector3(kv.Key * 2f, 0f, 0f));
                Physics.SyncTransforms();
                KPSystem.SimulatePhase(1f / 60f);
                traj.Add(watched.Select(t => t.position).ToArray());
            }
            return traj;
        }

        [Test(Description = "B2：移除過半觸發整理（Repack）時，有外力的其他角色不受影響、外力槽位正確")]
        public void CompactionWithForcesKeepsOthers()
        {
            var control = RunCompaction(false, out _);
            var test = RunCompaction(true, out var compacted);
            Assert.That(compacted, Is.True, "第 60 幀應該整理過（空洞清空）");
            var maxDiff = 0f;
            for (int f = 0; f < control.Count; f++)
                maxDiff = Mathf.Max(maxDiff, control[f].Zip(test[f], (a, b) => (a - b).magnitude).Max());
            TestContext.WriteLine($"倖存者與對照組的最大差 {maxDiff * 1000f:0.000} mm");
            Assert.That(maxDiff, Is.LessThan(1e-5f));
        }

        [Test(Description = "範例的節奏：反覆加入、移除（舊的先移除），走過空洞重用、接在最後、整理三條路徑")]
        public void SpawnDespawnCyclesWithForces()
        {
            var alive = new List<(int id, KawaiiPhysicsChain chain)>();
            var nextId = 0;
            for (int k = 0; k < 4; k++) alive.Add((nextId, SpawnWithForces(nextId++)));
            var adding = true;
            int compactions = 0, holeReuses = 0;
            for (int f = 0; f < 40 * 30; f++)
            {
                var holesBefore = KPSystem.World?.HoleCount ?? 0;
                var changed = f > 0 && f % 30 == 0;
                if (changed)
                {
                    if (adding && alive.Count >= 6) adding = false;
                    if (!adding && alive.Count <= 1) adding = true;
                    if (adding)
                    {
                        alive.Add((nextId, SpawnWithForces(nextId++)));
                    }
                    else
                    {
                        KPSystem.Unregister(alive[0].chain);
                        Object.DestroyImmediate(alive[0].chain.gameObject);
                        alive.RemoveAt(0);
                    }
                }
                KPSystem.RestorePhase();
                if (changed)
                {
                    AssertForceSlotsValid();
                    if (adding && KPSystem.World.HoleCount < holesBefore) holeReuses++;
                    if (!adding && KPSystem.World.HoleCount == 0) compactions++;
                }
                foreach (var (id, chain) in alive) KPTestRig.Move(chain.transform, id, f, new Vector3((id % 6) * 2f, 0f, 0f));
                Physics.SyncTransforms();
                KPSystem.SimulatePhase(1f / 60f);
                foreach (var (_, chain) in alive) Assert.That(KPTestRig.AllFinite(chain, out var d), Is.True, $"第 {f} 幀：{d}");
            }
            TestContext.WriteLine($"整理 {compactions} 次、空洞重用 {holeReuses} 次");
            Assert.That(compactions, Is.GreaterThan(0), "要走到整理（Repack）");
            Assert.That(holeReuses, Is.GreaterThan(0), "要走到空洞重用");
        }

        // ---- 其他審查指出的情境 --------------------------------------------

        [Test(Description = "一開始停用的 WindZone，之後啟用也會吹")]
        public void InactiveWindZoneActivatedLater()
        {
            var wind = new GameObject("wind").AddComponent<WindZone>();
            wind.gameObject.SetActive(false);
            var chains = new List<KawaiiPhysicsChain> { Spawn("A", Vector3.zero, 0, false) };
            chains[0].enableWind = true;
            var bases = new List<Vector3> { Vector3.zero };
            for (int f = 0; f < 3; f++) Frame(f, chains, bases);
            Assert.That(KPSystem.World.WindZoneCount, Is.EqualTo(0));
            wind.gameObject.SetActive(true);
            Frame(3, chains, bases);
            Assert.That(KPSystem.World.WindZoneCount, Is.EqualTo(1));
        }

        [TestCase(true, TestName = "外力的骨頭篩選與曲線：Inspector 改（OnValidate）就地生效、不重建")]
        [TestCase(false, TestName = "外力的骨頭篩選與曲線：程式改＋MarkSettingsDirty 就地生效、不重建")]
        public void ForceFilterAndCurveChangeAppliesInPlace(bool fromInspector)
        {
            var c = KPTestRig.BuildCharacter("A", Vector3.zero, 0, false);
            var force = new KPForceBasic { forceDir = Vector3.right };
            c.externalForces.Add(force);
            KPSystem.Register(c);
            var chains = new List<KawaiiPhysicsChain> { c };
            var bases = new List<Vector3> { Vector3.zero };
            for (int f = 0; f < 5; f++) Frame(f, chains, bases);
            Assert.That(force.lut, Is.LessThan(0), "曲線沒設時沒有曲線表");
            var syncs = KPSystem.World.SyncCount;
            var segment = c.runtime.Segment;

            force.applyBoneFilter.Add(c.transform.Find("c0_0/c0_1/c0_2/c0_3"));
            force.forceRateByBoneLengthRate = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            if (fromInspector)
                // Inspector 改欄位時 Unity 會呼叫 OnValidate
                typeof(KawaiiPhysicsChain).GetMethod("OnValidate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(c, null);
            else c.MarkSettingsDirty();
            for (int f = 5; f < 8; f++) Frame(f, chains, bases);

            var w = KPSystem.World;
            var inst = w.Instances[c.runtime.InstanceIndex];
            var withForce = 0;
            for (int i = inst.BoneStart; i < inst.BoneStart + inst.BoneCount; i++)
                if ((w.Bones[i].ForceMask & 1u) != 0) withForce++;
            Assert.That(withForce, Is.EqualTo(1), "只有篩選的那根骨頭受力");
            Assert.That(force.lut, Is.GreaterThanOrEqualTo(0), "新的曲線取進曲線表");
            Assert.That(w.SyncCount, Is.EqualTo(syncs), "沒有同步（沒有重建）");
            Assert.That(c.runtime.Segment, Is.SameAs(segment), "還是同一個段（模擬狀態保留）");
        }

        [Test(Description = "曲線表依內容共用：反覆停用啟用、暫時外力重複觸發、生成同樣的角色，表都不會變大")]
        public void CurveTableDoesNotGrow()
        {
            KawaiiPhysicsChain Make(int id)
            {
                var ch = KPTestRig.BuildCharacter($"c{id}", new Vector3(id * 2f, 0f, 0f), 0, false);
                ch.externalForces.Add(new KPForceBasic { forceDir = Vector3.right, forceRateByBoneLengthRate = AnimationCurve.Linear(0f, 0.5f, 1f, 1f) });
                ch.syncBones.Add(new KawaiiPhysicsChain.SyncBone
                {
                    bone = ch.transform.Find("c0_0"),
                    targetRoots = { new KawaiiPhysicsChain.SyncTargetRoot { bone = ch.transform.Find("c1_0") } },
                    scaleCurveByDeltaDistance = AnimationCurve.Linear(0f, 1f, 0.2f, 0f),
                });
                KPSystem.Register(ch);
                return ch;
            }
            var chains = new List<KawaiiPhysicsChain> { Make(0) };
            var bases = new List<Vector3> { Vector3.zero };
            for (int f = 0; f < 3; f++) Frame(f, chains, bases);
            var baseline = KPSystem.World.LutData.Count;
            Assert.That(baseline, Is.GreaterThan(0), "外力曲線與 SyncBone 曲線都進了曲線表");

            var frame = 3;
            for (int k = 0; k < 10; k++)
            {
                KPSystem.Unregister(chains[0]);                 // 停用 → 啟用
                Frame(frame++, chains, bases);
                KPSystem.Register(chains[0]);
                Frame(frame++, chains, bases);
                chains[0].MarkDirty();                          // 結構重建
                Frame(frame++, chains, bases);
                var clone = (KPForceBasic)chains[0].externalForces[0].CloneForTransient();
                clone.lutBaked = false;                         // 事件預設每次觸發都是新的外力物件
                chains[0].RequestTransientExternalForce(clone, 0.05f);
                Frame(frame++, chains, bases);
            }
            for (int id = 1; id <= 5; id++)                     // 生成同樣的角色
            {
                chains.Add(Make(id));
                bases.Add(new Vector3(id * 2f, 0f, 0f));
                Frame(frame++, chains, bases);
            }
            TestContext.WriteLine($"曲線表 {baseline} → {KPSystem.World.LutData.Count} 個 float");
            Assert.That(KPSystem.World.LutData.Count, Is.EqualTo(baseline));
        }

        [Test(Description = "停用後才刪除的角色：靜止姿勢快取不會一直變大")]
        public void RestCacheStaysBoundedWhenDestroyedAfterDisable()
        {
            var keep = Spawn("keep", Vector3.zero, 0, false);
            var chains = new List<KawaiiPhysicsChain> { keep };
            var bases = new List<Vector3> { Vector3.zero };
            var frame = 0;
            for (int k = 0; k < 30; k++)
            {
                var extra = Spawn($"extra{k}", new Vector3(4f, 0f, 0f), k, false);
                Frame(frame++, chains, bases);
                KPSystem.Unregister(extra);                     // 先停用（同步時 Transform 還在）
                Frame(frame++, chains, bases);
                Object.DestroyImmediate(extra.gameObject);      // 之後才刪除
            }
            Frame(frame, chains, bases);
            TestContext.WriteLine($"靜止姿勢快取 {KPSystem.RestCacheCount} 筆（不清理的話約 {31 * 128} 筆）");
            Assert.That(KPSystem.RestCacheCount, Is.LessThan(31 * 128 / 2));
        }

        [Test(Description = "執行中打開簡易世界碰撞（沒呼叫 MarkDirty）：自動重建這一條，不會寫到相鄰角色的碰撞")]
        public void EnablingSimpleWorldCollisionAtRuntime()
        {
            KPTestRig.BuildEnvironment(Vector3.zero);
            var a = Spawn("A", Vector3.zero, 0, false);
            var b = Spawn("B", new Vector3(2f, 0f, 0f), 1, true); // 有箱子碰撞體
            var chains = new List<KawaiiPhysicsChain> { a, b };
            var bases = new List<Vector3> { Vector3.zero, new Vector3(2f, 0f, 0f) };
            for (int f = 0; f < 5; f++) Frame(f, chains, bases);
            var w = KPSystem.World;
            var bInst = w.Instances[b.runtime.InstanceIndex];
            var bBoxExtent = w.Boxes[bInst.BoxStart].Extent;
            a.useSimpleWorldCollision = true;
            for (int f = 5; f < 10; f++) Frame(f, chains, bases);
            Assert.That(a.runtime.SimpleCapacity, Is.GreaterThan(0), "A 重建並保留了簡易世界碰撞的槽位");
            bInst = KPSystem.World.Instances[b.runtime.InstanceIndex];
            Assert.That(KPSystem.World.Boxes[bInst.BoxStart].Extent, Is.EqualTo(bBoxExtent), "B 的箱子碰撞沒被覆蓋");
        }

        [Test(Description = "半徑決定假骨數量時，只改 radiusCurve＋MarkSettingsDirty 也會重建（假骨數量跟著變）")]
        public void RadiusCurveChangeRebuildsWhenStructural()
        {
            var c = KPTestRig.BuildCharacter("A", Vector3.zero, 0, false);
            c.boneSubdivisionDensifyByRadius = true;
            c.radius = 0.01f;
            KPSystem.Register(c);
            var chains = new List<KawaiiPhysicsChain> { c };
            var bases = new List<Vector3> { Vector3.zero };
            for (int f = 0; f < 3; f++) Frame(f, chains, bases);
            var bonesBefore = KPSystem.BoneCount;
            c.radiusCurve = AnimationCurve.Constant(0f, 1f, 4f); // 半徑變 4 倍 → 加密的假骨變少
            c.MarkSettingsDirty();
            for (int f = 3; f < 6; f++) Frame(f, chains, bases);
            TestContext.WriteLine($"骨頭數 {bonesBefore} → {KPSystem.BoneCount}");
            Assert.That(KPSystem.BoneCount, Is.LessThan(bonesBefore));
        }

        // ---- B5 -------------------------------------------------------------

        public static IEnumerable<TestCaseData> ExtremeCases()
        {
            TestCaseData C(string name, Action<KawaiiPhysicsChain> setup, Func<int, float> dt = null, Action<Transform, int> move = null) =>
                new TestCaseData(setup, dt, move).SetName("B5 " + name);
            yield return C("dt = 0", null, f => 0f);
            yield return C("dt = 1 s", null, f => 1f);
            yield return C("dt = 10 s", null, f => 10f);
            yield return C("dt = 1e-6 s", null, f => 1e-6f);
            yield return C("dt 在 0 與 1／60 間交替", null, f => f % 2 == 0 ? 0f : 1f / 60f);
            yield return C("骨長 0", c =>
            {
                foreach (var t in c.GetComponentsInChildren<Transform>().Where(t => BoneName.IsMatch(t.name) && !t.name.EndsWith("_0")))
                    t.localPosition = Vector3.zero;
            });
            yield return C("只有一根骨頭", c =>
            {
                c.rootBone = c.transform.Find("c0_0/c0_1/c0_2/c0_3/c0_4/c0_5/c0_6/c0_7");
                c.additionalRootBones.Clear();
                c.boneConstraints.Clear();
                c.dummyBoneLength = 0f;
            });
            yield return C("縮放 0.01", c => c.transform.localScale = Vector3.one * 0.01f);
            yield return C("縮放 100", c => c.transform.localScale = Vector3.one * 100f);
            yield return C("非等比縮放", c => c.transform.localScale = new Vector3(1f, 3f, 0.5f));
            yield return C("縮放 0", c => c.transform.localScale = Vector3.zero);
            yield return C("參數全 0", c => c.damping = c.stiffness = c.worldDampingLocation = c.worldDampingRotation = c.radius = c.limitAngle = c.alpha = 0f);
            yield return C("參數上限", c =>
            {
                c.damping = c.stiffness = c.worldDampingLocation = c.worldDampingRotation = c.alpha = 1f;
                c.limitAngle = 180f;
                c.radius = 1f;
            });
            yield return C("參數超出範圍", c =>
            {
                c.damping = -1f; c.stiffness = 2f; c.worldDampingLocation = c.worldDampingRotation = 5f; c.alpha = 2f; c.radius = -1f;
            });
            yield return C("targetFramerate 1、maxSubsteps 0", c => { c.targetFramerate = 1; c.maxSubsteps = 0; });
            yield return C("targetFramerate 1000、maxSubsteps 100", c => { c.targetFramerate = 1000; c.maxSubsteps = 100; });
            yield return C("targetFramerate 0、maxSubsteps 負值", c => { c.targetFramerate = 0; c.maxSubsteps = -5; });
            yield return C("重力與外力 1e6", c => { c.gravity = new Vector3(0f, -1e6f, 0f); c.simpleExternalForce = new Vector3(1e6f, 0f, 0f); });
            yield return C("瞬移門檻 0", c => { c.teleportDistanceThreshold = 0f; c.teleportRotationThreshold = 0f; });
            yield return C("細分 10＋加密＋約束細分 5", c =>
            {
                c.boneSubdivisionCount = 10; c.boneSubdivisionDensifyByRadius = true; c.boneSubdivisionCollisionOnly = false;
                c.boneConstraintSubdivisionCount = 5;
            });
            yield return C("碰撞體尺寸 0", c =>
            {
                foreach (var col in c.colliders) { col.radius = 0f; col.radius1 = 0f; col.length = 0f; col.extent = Vector3.zero; }
            });
            yield return C("根物件每幀瞬移 1000 m", null, null, (t, f) => t.position = new Vector3((f % 2) * 1000f, 0f, 0f));
        }

        [TestCaseSource(nameof(ExtremeCases))]
        public void ExtremeInputs(Action<KawaiiPhysicsChain> setup, Func<int, float> dt, Action<Transform, int> move)
        {
            KPTestRig.BuildEnvironment(Vector3.zero);
            var c = KPTestRig.BuildCharacter("x", Vector3.zero, 0, true);
            setup?.Invoke(c);
            KPSystem.Register(c);
            for (int f = 0; f < 120; f++)
            {
                KPSystem.RestorePhase();
                if (move != null) move(c.transform, f);
                else KPTestRig.Move(c.transform, 0, f, Vector3.zero);
                Physics.SyncTransforms();
                KPSystem.SimulatePhase(dt?.Invoke(f) ?? 1f / 60f);
                Assert.That(KPTestRig.AllFinite(c, out var d), Is.True, $"第 {f} 幀 NaN／無限大：{d}");
            }
        }

        // ---- C1 -------------------------------------------------------------

        /// <summary>每幀套件兩個階段的 GC 配置（600 幀）。</summary>
        static long[] MeasureAllocations(bool feature)
        {
            Cleanup();
            KPTestRig.BuildEnvironment(Vector3.zero);
            var chains = new List<KawaiiPhysicsChain>();
            var bases = new List<Vector3>();
            for (int c = 0; c < 16; c++)
            {
                chains.Add(Spawn($"char{c}", new Vector3(c * 2f, 0f, 0f), c, false));
                bases.Add(new Vector3(c * 2f, 0f, 0f));
            }
            if (feature)
            {
                var f0 = Spawn("feature", new Vector3(-3f, 0f, 0f), 0, true);
                chains.Add(f0);
                bases.Add(new Vector3(-3f, 0f, 0f));
                f0.RequestTransientGust(2f, 1f, 2f, holdTime: 3f);
                f0.RequestTransientExternalForce(new KPForceBasic { forceDir = Vector3.forward }, 30f);
                var m = KPSettingsMultiplier.One;
                m.Stiffness = 0.5f;
                f0.StartSettingsMultiplier(m, 0.5f, 0f, 0.5f, infiniteHold: true);
            }
            var perFrame = new long[600];
            for (int f = 0; f < 660; f++)
            {
                var a0 = KPTestAlloc.Now();
                KPSystem.RestorePhase();
                var a1 = KPTestAlloc.Now();
                for (int i = 0; i < chains.Count; i++) KPTestRig.Move(chains[i].transform, i, f, bases[i]);
                Physics.SyncTransforms();
                var a2 = KPTestAlloc.Now();
                KPSystem.SimulatePhase(1f / 60f);
                var a3 = KPTestAlloc.Now();
                if (f >= 60) perFrame[f - 60] = Math.Max(0, a1 - a0) + Math.Max(0, a3 - a2);
            }
            return perFrame;
        }

        /// <summary>
        /// 「GC Allocated In Frame」是全域計數，編輯器其他執行緒偶爾的配置也會算進來。量兩次、每幀取較小的：
        /// 套件自己的配置每次都在同一幀出現（例如每幀或每 N 幀），取較小的也會留下來；隨機的雜訊兩次剛好同一幀的機會極小。
        /// </summary>
        [TestCase(false, TestName = "C1 穩態每幀 0 GC 配置（16 個基本角色）")]
        [TestCase(true, TestName = "C1 穩態每幀 0 GC 配置（＋開滿功能的角色）")]
        public void ZeroAllocationSteadyState(bool feature)
        {
            KPTestAlloc.Init();
            Assume.That(KPTestAlloc.Precise, "這個環境量不到小配置：" + KPTestAlloc.Describe());
            var first = MeasureAllocations(feature);
            var second = MeasureAllocations(feature);
            var both = first.Zip(second, Math.Min).ToArray();
            var frames = both.Select((v, i) => (v, i)).Where(p => p.v > 0).Select(p => $"{p.i}:{p.v}B").Take(10).ToList();
            TestContext.WriteLine($"第一次合計 {first.Sum()} B、第二次 {second.Sum()} B；兩次都有配置的幀：{string.Join(", ", frames)}");
            Assert.That(both.Sum(), Is.EqualTo(0), "兩次都在同一幀配置：" + string.Join(", ", frames));
        }

        // ---- C2／C3（效能；門檻放寬給編輯器，正式數字在 Player 量） -------------

        [Test, Category("Performance")]
        public void C2_PipelineCost16Characters()
        {
            KPTestSettings.DisableSafetyForTiming();
            try
            {
                var chains = new List<KawaiiPhysicsChain>();
                var bases = new List<Vector3>();
                for (int c = 0; c < 16; c++)
                {
                    chains.Add(Spawn($"char{c}", new Vector3(c * 2f, 0f, 0f), c, false));
                    bases.Add(new Vector3(c * 2f, 0f, 0f));
                }
                var ms = new List<double>();
                var sw = new Stopwatch();
                for (int f = 0; f < 360; f++)
                {
                    for (int i = 0; i < chains.Count; i++) KPTestRig.Move(chains[i].transform, i, f, bases[i]);
                    Physics.SyncTransforms();
                    sw.Restart();
                    KPSystem.RestorePhase();
                    KPSystem.SimulatePhase(1f / 60f);
                    if (f >= 60) ms.Add(sw.Elapsed.TotalMilliseconds);
                }
                ms.Sort();
                TestContext.WriteLine($"16 × 144 骨：平均 {ms.Average():0.000} ms、中位 {ms[ms.Count / 2]:0.000} ms、p99 {ms[(int)(ms.Count * 0.99)]:0.000} ms");
                Assert.That(ms[ms.Count / 2], Is.LessThan(0.5), "中位數 < 0.5 ms");
            }
            finally
            {
                KPTestSettings.EnableSafety();
            }
        }

        [Test, Category("Performance")]
        public void C3_AddRemoveOneCharacter()
        {
            KPTestSettings.DisableSafetyForTiming();
            try
            {
                var chains = new List<KawaiiPhysicsChain>();
                var bases = new List<Vector3>();
                for (int c = 0; c < 16; c++)
                {
                    chains.Add(Spawn($"char{c}", new Vector3(c * 2f, 0f, 0f), c, false));
                    bases.Add(new Vector3(c * 2f, 0f, 0f));
                }
                var f = 0;
                for (; f < 30; f++) Frame(f, chains, bases);
                var add = new List<double>();
                var remove = new List<double>();
                var sw = new Stopwatch();
                for (int trial = -1; trial < 10; trial++) // 第一次是暖身（新程式路徑的 JIT）
                {
                    var extra = KPTestRig.BuildCharacter("extra", new Vector3(40f, 0f, 0f), trial + 1, false);
                    sw.Restart(); KPSystem.Register(extra); KPSystem.RestorePhase(); var a = sw.Elapsed.TotalMilliseconds;
                    KPSystem.SimulatePhase(1f / 60f);
                    for (int k = 0; k < 5; k++, f++) Frame(f, chains, bases);
                    Object.DestroyImmediate(extra.gameObject);
                    sw.Restart(); KPSystem.Unregister(extra); KPSystem.RestorePhase(); var r = sw.Elapsed.TotalMilliseconds;
                    KPSystem.SimulatePhase(1f / 60f);
                    for (int k = 0; k < 5; k++, f++) Frame(f, chains, bases);
                    if (trial < 0) continue;
                    add.Add(a);
                    remove.Add(r);
                }
                TestContext.WriteLine($"加入：平均 {add.Average():0.000} ms、最大 {add.Max():0.000} ms；移除：平均 {remove.Average():0.000} ms、最大 {remove.Max():0.000} ms");
                Assert.That(remove.Average(), Is.LessThan(1.0), "移除平均 < 1 ms");
                Assert.That(add.Average(), Is.LessThan(2.0), "加入平均 < 2 ms（編輯器；IL2CPP Player 約 0.3 ms）");
            }
            finally
            {
                KPTestSettings.EnableSafety();
            }
        }
    }
}
