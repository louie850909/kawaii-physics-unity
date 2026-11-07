// 碰撞體的尺寸是世界單位（公尺）：元件（模擬空間）有縮放時，骨鏈碰到的碰撞體大小不變。
// 0.4.6 以前骨鏈把碰撞體的半徑當成模擬單位，元件縮放 0.5 時碰撞體只剩一半大。
using NUnit.Framework;
using UnityEngine;

namespace KawaiiPhysicsUnity.Tests
{
    public class KPColliderScaleTests
    {
        [SetUp]
        public void SetUp()
        {
            KPTestSettings.EnableSafety();
            KPSystem.Shutdown();
        }

        [TearDown]
        public void TearDown()
        {
            KPSystem.Shutdown();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include))
                if (go != null && go.transform.parent == null) Object.DestroyImmediate(go);
        }

        /// <summary>
        /// 縮放 scale 的元件底下一條往下垂的 3 根骨頭（每節 0.1 模擬單位），末端旁邊放一個碰撞體（世界尺寸），
        /// 剛性把末端拉回動畫姿勢、碰撞體把它推開，推進 120 幀後回傳末端到碰撞體表面的距離（世界，公尺）。
        /// </summary>
        static float RestingGap(float scale, KawaiiPhysicsCollider.Shape shape, out float boneRadiusWorld)
        {
            var root = new GameObject("root");
            root.transform.localScale = Vector3.one * scale;
            var b0 = new GameObject("b0").transform;
            b0.SetParent(root.transform, false);
            b0.localPosition = new Vector3(0f, 1f, 0f);
            var b1 = new GameObject("b1").transform;
            b1.SetParent(b0, false);
            b1.localPosition = new Vector3(0f, -0.1f, 0f);
            var b2 = new GameObject("b2").transform;
            b2.SetParent(b1, false);
            b2.localPosition = new Vector3(0f, -0.1f, 0f);

            var colGo = new GameObject("col");
            var kc = colGo.AddComponent<KawaiiPhysicsCollider>();
            kc.shape = shape;
            kc.radius = 0.1f;                              // 球：半徑 0.1 m
            kc.extent = new Vector3(0.1f, 0.1f, 0.1f);     // 箱：半邊長 0.1 m
            // 碰撞體放在末端的 +X 側、重疊 0.02·scale：表面在 x＝末端 x − 0.02·scale 的位置要把末端往 −X 推
            colGo.transform.position = b2.position + new Vector3(0.1f - 0.02f * scale, 0f, 0f);

            var chain = root.AddComponent<KawaiiPhysicsChain>();
            chain.rootBone = b0;
            chain.component = root.transform;
            chain.damping = 0.5f;
            chain.stiffness = 0.3f;
            chain.worldDampingLocation = 1f;
            chain.worldDampingRotation = 1f;
            chain.radius = 0.02f;                          // 骨頭半徑是模擬單位（世界＝0.02·scale）
            chain.colliders.Add(kc);
            KPSystem.Register(chain);
            for (int f = 0; f < 120; f++)
            {
                KPSystem.RestorePhase();
                KPSystem.SimulatePhase(1f / 60f);
            }
            boneRadiusWorld = 0.02f * scale;
            var surfaceX = colGo.transform.position.x - 0.1f;
            return surfaceX - b2.position.x;
        }

        [TestCase(1f)]
        [TestCase(0.5f)]
        [TestCase(2f)]
        public void SphereColliderKeepsWorldSize(float scale)
        {
            var gap = RestingGap(scale, KawaiiPhysicsCollider.Shape.Sphere, out var r);
            // 末端停在碰撞體表面外一個骨頭半徑（骨長復原在碰撞之後，容許少量陷入）
            Assert.AreEqual(r, gap, 0.15f * r + 1e-3f, $"縮放 {scale}：離表面 {gap * 1000f:F2} mm，骨頭半徑 {r * 1000f:F2} mm");
        }

        [TestCase(1f)]
        [TestCase(0.5f)]
        [TestCase(2f)]
        public void BoxColliderKeepsWorldSize(float scale)
        {
            var gap = RestingGap(scale, KawaiiPhysicsCollider.Shape.Box, out var r);
            Assert.AreEqual(r, gap, 0.15f * r + 1e-3f, $"縮放 {scale}：離表面 {gap * 1000f:F2} mm，骨頭半徑 {r * 1000f:F2} mm");
        }
    }
}
