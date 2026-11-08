// 陣列接口的設定類別（KPRigChain、KPRigCollider、KPRigSoftBody）跟元件的欄位不能分歧：
// 名稱、型別（Transform → int）、預設值一樣；元件每幀的複製沒有漏掉欄位。另外是依名稱的外力篩選、預設與事件的多載。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KawaiiPhysicsUnity.Tests
{
    public class KPRigSettingsTests
    {
        const BindingFlags Fields = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        [SetUp]
        public void SetUp() => Cleanup();

        [TearDown]
        public void TearDown() => Cleanup();

        static void Cleanup()
        {
            KPSystem.Shutdown();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include))
                if (go != null && go.transform.parent == null) Object.DestroyImmediate(go);
        }

        // ---- 鏡像：欄位名稱、型別、預設值 ------------------------------------------

        /// <summary>元件的欄位型別 → 設定類別應有的型別。</summary>
        static Type Expected(Type componentType)
        {
            if (componentType == typeof(Transform)) return typeof(int);
            if (componentType == typeof(List<Transform>)) return typeof(List<int>);
            if (componentType == typeof(List<KawaiiPhysicsCollider>)) return typeof(List<KPRigCollider>);
            if (componentType == typeof(List<KawaiiPhysicsChain.RootBoneSetting>)) return typeof(List<KPRigChain.RootBoneSetting>);
            if (componentType == typeof(List<KawaiiPhysicsChain.BoneConstraint>)) return typeof(List<KPRigChain.BoneConstraint>);
            if (componentType == typeof(List<KawaiiPhysicsChain.SyncBone>)) return typeof(List<KPRigChain.SyncBone>);
            if (componentType == typeof(List<KawaiiPhysicsChain.SyncTargetRoot>)) return typeof(List<KPRigChain.SyncTargetRoot>);
            return componentType;
        }

        /// <summary>預設值的比較用表示（曲線看 key 數、清單看長度、Transform／int 參照是「沒有」）。</summary>
        static object DefaultKey(object v)
        {
            switch (v)
            {
                case null: return "none";
                case Transform _: return "none";
                case int i when i == -1: return "none";
                case AnimationCurve c: return "curve" + c.length;
                case IList l: return "list" + l.Count;
                case LayerMask m: return m.value;
                default: return v;
            }
        }

        static void AssertMirror(Type component, object componentDefaults, Type rig, object rigDefaults, string[] componentOnly, string[] rigOnly)
        {
            var c = component.GetFields(Fields).Where(f => !componentOnly.Contains(f.Name)).ToDictionary(f => f.Name);
            var r = rig.GetFields(Fields).Where(f => !rigOnly.Contains(f.Name)).ToDictionary(f => f.Name);
            CollectionAssert.AreEquivalent(c.Keys, r.Keys, $"{component.Name} 與 {rig.Name} 的欄位名稱不一致");
            foreach (var kv in c)
            {
                var rf = r[kv.Key];
                Assert.That(rf.FieldType, Is.EqualTo(Expected(kv.Value.FieldType)), $"{rig.Name}.{kv.Key} 的型別");
                if (componentDefaults == null) continue;
                var cv = DefaultKey(kv.Value.GetValue(componentDefaults));
                var rv = DefaultKey(rf.GetValue(rigDefaults));
                Assert.That(rv, Is.EqualTo(cv), $"{rig.Name}.{kv.Key} 的預設值");
            }
        }

        [Test]
        public void ChainSettingsMirrorComponent()
        {
            var go = new GameObject("c");
            var chain = go.AddComponent<KawaiiPhysicsChain>();
            AssertMirror(typeof(KawaiiPhysicsChain), chain, typeof(KPRigChain), new KPRigChain(), new[] { "preset" }, new[] { "enabled" });
            AssertMirror(typeof(KawaiiPhysicsChain.RootBoneSetting), new KawaiiPhysicsChain.RootBoneSetting(),
                typeof(KPRigChain.RootBoneSetting), new KPRigChain.RootBoneSetting(), new string[0], new string[0]);
            AssertMirror(typeof(KawaiiPhysicsChain.BoneConstraint), new KawaiiPhysicsChain.BoneConstraint(),
                typeof(KPRigChain.BoneConstraint), new KPRigChain.BoneConstraint(), new string[0], new string[0]);
            AssertMirror(typeof(KawaiiPhysicsChain.SyncBone), new KawaiiPhysicsChain.SyncBone(),
                typeof(KPRigChain.SyncBone), new KPRigChain.SyncBone(), new string[0], new string[0]);
            AssertMirror(typeof(KawaiiPhysicsChain.SyncTargetRoot), new KawaiiPhysicsChain.SyncTargetRoot(),
                typeof(KPRigChain.SyncTargetRoot), new KPRigChain.SyncTargetRoot(), new string[0], new string[0]);
        }

        [Test]
        public void ColliderAndSoftBodySettingsMirrorComponents()
        {
            var go = new GameObject("c");
            AssertMirror(typeof(KawaiiPhysicsCollider), go.AddComponent<KawaiiPhysicsCollider>(), typeof(KPRigCollider), new KPRigCollider(),
                new string[0], new[] { "bone" });
            AssertMirror(typeof(KawaiiPhysicsSoftBody), go.AddComponent<KawaiiPhysicsSoftBody>(), typeof(KPRigSoftBody), new KPRigSoftBody(),
                new string[0], new[] { "enabled" });
        }

        // ---- 複製沒有漏掉欄位 -------------------------------------------------------

        /// <summary>每個欄位改成跟預設不同的值（Transform 參照指到 bone）。</summary>
        static void Scramble(object target, Transform bone, int salt)
        {
            foreach (var f in target.GetType().GetFields(Fields))
            {
                var t = f.FieldType;
                var k = salt + f.Name.GetHashCode() % 97;
                if (t == typeof(float)) f.SetValue(target, 0.123f + Mathf.Abs(k) * 0.001f);
                else if (t == typeof(int)) f.SetValue(target, 2 + Mathf.Abs(k) % 5);
                else if (t == typeof(bool)) f.SetValue(target, !(bool)f.GetValue(target));
                else if (t == typeof(string)) f.SetValue(target, "tag" + k);
                else if (t == typeof(Vector3)) f.SetValue(target, new Vector3(0.1f, 0.2f + k * 1e-3f, 0.3f));
                else if (t == typeof(Vector2)) f.SetValue(target, new Vector2(0.4f, 0.5f));
                else if (t.IsEnum)
                {
                    var values = Enum.GetValues(t);
                    var cur = f.GetValue(target);
                    foreach (var v in values) if (!v.Equals(cur)) { f.SetValue(target, v); break; }
                }
                else if (t == typeof(LayerMask)) f.SetValue(target, (LayerMask)(1 << 3));
                else if (t == typeof(AnimationCurve)) f.SetValue(target, AnimationCurve.Linear(0f, 0.5f, 1f, 2f));
                else if (t == typeof(Transform)) f.SetValue(target, bone);
                else if (t == typeof(List<Transform>)) f.SetValue(target, new List<Transform> { bone });
            }
        }

        static object ValueKey(object v, KPTransformHierarchy h)
        {
            switch (v)
            {
                case Transform t: return t == null ? -1 : h.Id(t);
                case List<Transform> l: return string.Join(",", l.Select(t => t == null ? -1 : h.Id(t)));
                case List<int> l: return string.Join(",", l);
                case LayerMask m: return m.value;
                default: return v;
            }
        }

        static void AssertCopied(object component, object settings, KPTransformHierarchy h, string[] skip)
        {
            foreach (var f in component.GetType().GetFields(Fields))
            {
                if (skip.Contains(f.Name)) continue;
                var rf = settings.GetType().GetField(f.Name, Fields);
                var t = f.FieldType;
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) && t != typeof(List<Transform>) && t != typeof(List<KPExternalForce>))
                {
                    Assert.That(((IList)rf.GetValue(settings)).Count, Is.EqualTo(((IList)f.GetValue(component)).Count), $"{f.Name} 的筆數");
                    continue;
                }
                Assert.That(ValueKey(rf.GetValue(settings), h), Is.EqualTo(ValueKey(f.GetValue(component), h)), $"{f.Name} 沒有複製");
            }
        }

        [Test(Description = "元件的每個欄位都複製進 KPRigChain（巢狀的設定與碰撞體也是）")]
        public void ChainCopiesEveryField()
        {
            var go = new GameObject("c");
            var bone = new GameObject("bone").transform;
            bone.SetParent(go.transform, false);
            var chain = go.AddComponent<KawaiiPhysicsChain>();
            Scramble(chain, bone, 1);
            chain.component = go.transform;
            var col = bone.gameObject.AddComponent<KawaiiPhysicsCollider>();
            Scramble(col, bone, 2);
            chain.colliders = new List<KawaiiPhysicsCollider> { col };
            var root = new KawaiiPhysicsChain.RootBoneSetting();
            Scramble(root, bone, 3);
            chain.additionalRootBones = new List<KawaiiPhysicsChain.RootBoneSetting> { root };
            var con = new KawaiiPhysicsChain.BoneConstraint();
            Scramble(con, bone, 4);
            chain.boneConstraints = new List<KawaiiPhysicsChain.BoneConstraint> { con };
            var sync = new KawaiiPhysicsChain.SyncBone();
            Scramble(sync, bone, 5);
            var target = new KawaiiPhysicsChain.SyncTargetRoot();
            Scramble(target, bone, 6);
            sync.targetRoots = new List<KawaiiPhysicsChain.SyncTargetRoot> { target };
            chain.syncBones = new List<KawaiiPhysicsChain.SyncBone> { sync };
            chain.externalForces.Add(new KPForceBasic());

            var h = new KPTransformHierarchy();
            chain.CopyToSettings(h, true);
            var s = chain.settings;
            AssertCopied(chain, s, h, new[] { "preset" });
            AssertCopied(col, s.colliders[0], h, new string[0]);
            Assert.That(s.colliders[0].bone, Is.EqualTo(h.Id(bone)));
            AssertCopied(root, s.additionalRootBones[0], h, new string[0]);
            AssertCopied(con, s.boneConstraints[0], h, new string[0]);
            AssertCopied(sync, s.syncBones[0], h, new string[0]);
            AssertCopied(target, s.syncBones[0].targetRoots[0], h, new string[0]);
            Assert.That(s.externalForces, Is.SameAs(chain.externalForces), "外力清單共用同一份（PostApply 的移除要反映到元件）");
            Assert.That(s.enabled, Is.EqualTo(chain.isActiveAndEnabled));
        }

        [Test(Description = "軟組織每幀的設定值都複製進 KPRigSoftBody（骨頭、角色根、碰撞體清單之後的版本才換算）")]
        public void SoftBodyCopiesEverySetting()
        {
            var go = new GameObject("s");
            var body = go.AddComponent<KawaiiPhysicsSoftBody>();
            Scramble(body, go.transform, 7);
            body.CopyToSettings();
            AssertCopied(body, body.settings, new KPTransformHierarchy(), new[] { "bones", "referenceRoot", "colliders" });
        }

        // ---- 依名稱的外力篩選 ----------------------------------------------------------

        [Test]
        public void ForceNameFilterSelectsBones()
        {
            var f = new KPForceBasic();
            Assert.IsTrue(f.CanApplyToName("a"));
            f.applyBoneNameFilter.Add("a");
            Assert.IsTrue(f.CanApplyToName("a"));
            Assert.IsFalse(f.CanApplyToName("b"));
            Assert.IsFalse(f.CanApplyToName(null), "假骨（沒有名字）不在 apply 清單裡");
            f.applyBoneNameFilter.Clear();
            f.ignoreBoneNameFilter.Add("a");
            Assert.IsFalse(f.CanApplyToName("a"));
            Assert.IsTrue(f.CanApplyToName(null));
        }

        [Test(Description = "名稱篩選套到模擬：只有名稱在 apply 清單裡的骨頭的外力遮罩有這個力")]
        public void ForceNameFilterAppliesInSimulation()
        {
            var chain = KPTestRig.BuildCharacter("A", Vector3.zero, 0, false);
            var force = new KPForceBasic { forceDir = new Vector3(1f, 0f, 0f) };
            force.applyBoneNameFilter.Add("c0_3");
            chain.externalForces.Add(force);
            KPSystem.Register(chain);
            KPSystem.RestorePhase();
            KPSystem.SimulatePhase(1f / 60f);
            var w = KPSystem.World;
            var inst = w.Instances[chain.runtime.InstanceIndex];
            var hits = new List<string>();
            for (int i = 0; i < inst.BoneCount; i++)
            {
                var node = chain.runtime.BoneNodes[i];
                if ((w.Bones[inst.BoneStart + i].ForceMask & 1u) != 0) hits.Add(node >= 0 ? w.Hierarchy.Name(node) : "dummy");
            }
            Assert.That(hits, Is.EqualTo(new[] { "c0_3" }));
        }

        // ---- 預設與事件的多載 ----------------------------------------------------------

        [Test]
        public void PresetAndEventOverloadsWorkOnRigChain()
        {
            var preset = ScriptableObject.CreateInstance<KawaiiPhysicsPreset>();
            preset.data.damping = 0.42f;
            preset.data.boneSubdivisionCount = 3;
            var rig = new KPRigChain();
            rig.validateRequested = false;
            preset.ApplyTo(rig);
            Assert.That(rig.damping, Is.EqualTo(0.42f));
            Assert.That(rig.boneSubdivisionCount, Is.EqualTo(3));
            Assert.IsTrue(rig.validateRequested);
            rig.stiffness = 0.7f;
            preset.CaptureFrom(rig);
            Assert.That(preset.data.stiffness, Is.EqualTo(0.7f));
            Object.DestroyImmediate(preset);

            var ev = ScriptableObject.CreateInstance<KawaiiPhysicsEventPreset>();
            ev.kind = KawaiiPhysicsEventPreset.Kind.SettingsMultiplier;
            ev.Apply(rig);
            Assert.That(rig.SettingsMultiplierCount, Is.EqualTo(1));
            ev.kind = KawaiiPhysicsEventPreset.Kind.Gust;
            ev.Apply(rig);
            Assert.That(rig.TransientForceCount, Is.EqualTo(1));
            Object.DestroyImmediate(ev);
        }
    }
}
