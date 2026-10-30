// Inspector：外力的型別選單、「新增外力」、清單複製出的重複參照、Chain 的自訂 Inspector。
using System.Linq;
using KawaiiPhysicsUnity.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KawaiiPhysicsUnity.Tests
{
    public class KPInspectorTests
    {
        KawaiiPhysicsChain chain;

        [SetUp]
        public void SetUp() => chain = new GameObject("chain").AddComponent<KawaiiPhysicsChain>();

        [TearDown]
        public void TearDown()
        {
            if (chain != null) Object.DestroyImmediate(chain.gameObject);
            KPSystem.Shutdown();
        }

        [Test]
        public void MenuListsAllForceTypes()
        {
            var types = KPExternalForceMenu.ForceTypes;
            Assert.That(types, Is.SupersetOf(new[]
            {
                typeof(KPForceBasic), typeof(KPForceGravity), typeof(KPForceCurve), typeof(KPForceWind), typeof(KPForceProceduralWind),
            }));
            Assert.That(types.Any(t => t.IsAbstract), Is.False);
        }

        [Test]
        public void AddForceFromMenu()
        {
            KPExternalForceMenu.Add(new Object[] { chain }, typeof(KPForceWind));
            KPExternalForceMenu.Add(new Object[] { chain }, typeof(KPForceProceduralWind));
            Assert.That(chain.externalForces.Select(f => f?.GetType()), Is.EqualTo(new[] { typeof(KPForceWind), typeof(KPForceProceduralWind) }));
            // 序列化後也是這兩個型別（存進場景、重開還在）
            var so = new SerializedObject(chain);
            var list = so.FindProperty(nameof(KawaiiPhysicsChain.externalForces));
            Assert.That(list.arraySize, Is.EqualTo(2));
            StringAssert.Contains(nameof(KPForceWind), list.GetArrayElementAtIndex(0).managedReferenceFullTypename);
        }

        [Test(Description = "多選兩條外力數不同的 Chain 按「新增外力」：各自加在最後，原本的外力不被截掉")]
        public void AddForceToMultipleChainsKeepsEachList()
        {
            var other = new GameObject("other").AddComponent<KawaiiPhysicsChain>();
            try
            {
                chain.externalForces.Add(new KPForceGravity());
                other.externalForces.Add(new KPForceBasic());
                other.externalForces.Add(new KPForceCurve());
                other.externalForces.Add(new KPForceWind());
                KPExternalForceMenu.Add(new Object[] { chain, other }, typeof(KPForceProceduralWind));
                Assert.That(chain.externalForces.Select(f => f?.GetType()), Is.EqualTo(new[] { typeof(KPForceGravity), typeof(KPForceProceduralWind) }));
                Assert.That(other.externalForces.Select(f => f?.GetType()),
                    Is.EqualTo(new[] { typeof(KPForceBasic), typeof(KPForceCurve), typeof(KPForceWind), typeof(KPForceProceduralWind) }));
                Assert.That(ReferenceEquals(chain.externalForces[1], other.externalForces[3]), Is.False, "各自是新的實例");
            }
            finally
            {
                Object.DestroyImmediate(other.gameObject);
            }
        }

        [Test(Description = "多選時換型別：每個 Chain 各自一個新實例")]
        public void ChangeForceTypeOnMultipleChains()
        {
            var other = new GameObject("other").AddComponent<KawaiiPhysicsChain>();
            try
            {
                chain.externalForces.Add(new KPForceBasic());
                other.externalForces.Add(new KPForceBasic());
                other.externalForces.Add(new KPForceCurve());
                var path = nameof(KawaiiPhysicsChain.externalForces) + ".Array.data[0]";
                KPExternalForceMenu.SetType(new Object[] { chain, other }, path, typeof(KPForceWind));
                Assert.That(chain.externalForces[0], Is.TypeOf<KPForceWind>());
                Assert.That(other.externalForces[0], Is.TypeOf<KPForceWind>());
                Assert.That(other.externalForces[1], Is.TypeOf<KPForceCurve>(), "其他元素不受影響");
                Assert.That(ReferenceEquals(chain.externalForces[0], other.externalForces[0]), Is.False, "各自是新的實例");
            }
            finally
            {
                Object.DestroyImmediate(other.gameObject);
            }
        }

        [Test]
        public void ChangeAndClearForceType()
        {
            KPExternalForceMenu.Add(new Object[] { chain }, typeof(KPForceBasic));
            var path = nameof(KawaiiPhysicsChain.externalForces) + ".Array.data[0]";
            KPExternalForceMenu.SetType(new Object[] { chain }, path, typeof(KPForceGravity));
            Assert.That(chain.externalForces[0], Is.TypeOf<KPForceGravity>());
            KPExternalForceMenu.SetType(new Object[] { chain }, path, null);
            Assert.That(chain.externalForces[0], Is.Null);
        }

        [Test]
        public void DuplicatedReferenceIsSplit()
        {
            var f = new KPForceBasic { forceDir = new Vector3(1f, 2f, 3f) };
            chain.externalForces.Add(f);
            chain.externalForces.Add(f); // 清單按「+」複製出來的情況
            typeof(KawaiiPhysicsChain).GetMethod("OnValidate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(chain, null);
            Assert.That(ReferenceEquals(chain.externalForces[0], chain.externalForces[1]), Is.False);
            Assert.That(((KPForceBasic)chain.externalForces[1]).forceDir, Is.EqualTo(f.forceDir));
        }

        [Test]
        public void ChainUsesCustomInspector()
        {
            var editor = UnityEditor.Editor.CreateEditor(chain);
            try
            {
                Assert.That(editor, Is.TypeOf<KawaiiPhysicsChainEditor>());
            }
            finally
            {
                Object.DestroyImmediate(editor);
            }
        }

        [Test]
        public void ForceDrawerIsRegisteredForAllForceTypes()
        {
            var attr = typeof(KPExternalForceDrawer).GetCustomAttributes(typeof(CustomPropertyDrawer), false).Cast<CustomPropertyDrawer>().Single();
            var type = (System.Type)typeof(CustomPropertyDrawer).GetField("m_Type", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(attr);
            var useForChildren = (bool?)typeof(CustomPropertyDrawer).GetField("m_UseForChildren", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(attr);
            Assert.That(type, Is.EqualTo(typeof(KPExternalForce)));
            Assert.That(useForChildren, Is.True);
        }
    }
}
