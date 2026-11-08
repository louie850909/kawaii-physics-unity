// 骨頭來源（KPHierarchy）的 Transform 版：節點 id 的登記、查詢、回收。
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KawaiiPhysicsUnity.Tests
{
    public class KPHierarchyTests
    {
        readonly List<GameObject> created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
            created.Clear();
        }

        Transform New(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            else created.Add(go);
            return go.transform;
        }

        [Test]
        public void IdsAreStableAndQueriesFollowTransforms()
        {
            var h = new KPTransformHierarchy();
            var a = New("a");
            var b = New("b", a);
            var c = New("c", b);
            var ia = h.Id(a);
            var ib = h.Id(b);
            Assert.AreEqual(ia, h.Id(a), "同一個 Transform 同一個 id");
            Assert.AreEqual(KPHierarchy.None, h.Id(null));
            Assert.AreEqual(ia, h.Parent(ib));
            Assert.AreEqual(KPHierarchy.None, h.Parent(ia));
            Assert.AreEqual(1, h.ChildCount(ia));
            Assert.AreEqual(ib, h.Child(ia, 0));
            Assert.AreEqual(h.Id(c), h.FindDeep(ia, "c"));
            Assert.AreEqual(KPHierarchy.None, h.FindDeep(ia, "nothing"));
            Assert.AreEqual(ia, h.Root(h.Id(c)));
            Assert.AreSame(b, h.Get(ib));
        }

        [Test(Description = "刪掉的 Transform：沒有人在用的 id 回收重用，還在用的留著（不會把新的 Transform 接到舊的段上）")]
        public void FreeDeadKeepsReferencedIds()
        {
            var h = new KPTransformHierarchy();
            var root = New("root");
            var used = New("used", root);
            var unused = New("unused", root);
            var iUsed = h.Id(used);
            var iUnused = h.Id(unused);
            var countBefore = h.Count;
            Object.DestroyImmediate(used.gameObject);
            Object.DestroyImmediate(unused.gameObject);

            h.FreeDead(new HashSet<int> { iUsed });
            Assert.AreEqual(countBefore - 1, h.Count);
            Assert.IsFalse(h.IsAlive(iUsed));
            Assert.AreEqual(iUsed, h.Find(used), "還在用的 id 留著");

            var fresh = New("fresh");
            Assert.AreEqual(iUnused, h.Id(fresh), "回收的 id 重用");
            Assert.AreNotEqual(iUsed, h.Id(fresh));
        }
    }
}
