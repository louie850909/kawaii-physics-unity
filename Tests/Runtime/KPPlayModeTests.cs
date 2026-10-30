// Play 模式：元件的 OnEnable／OnDisable 自動註冊、執行中的破壞性操作、切場景。
// 有任何例外或錯誤記錄時 Test Framework 會判定失敗（LogAssert）。
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace KawaiiPhysicsUnity.Tests
{
    public class KPPlayModeTests
    {
        readonly List<KawaiiPhysicsChain> chains = new List<KawaiiPhysicsChain>();
        readonly List<Vector3> bases = new List<Vector3>();
        readonly List<Scene> scenes = new List<Scene>();
        Scene testScene;
        int frame;

        [SetUp]
        public void SetUp()
        {
            Time.captureDeltaTime = 1f / 60f;
            chains.Clear();
            bases.Clear();
            frame = 0;
            // 測試自己的場景（測試框架的場景不動）
            testScene = SceneManager.GetActiveScene();
            UseNewScene("KPPlayModeTests_A");
        }

        void UseNewScene(string name)
        {
            var s = SceneManager.CreateScene(name + "_" + scenes.Count);
            scenes.Add(s);
            SceneManager.SetActiveScene(s);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.captureDeltaTime = 0f;
            if (testScene.IsValid()) SceneManager.SetActiveScene(testScene);
            foreach (var s in scenes)
                if (s.IsValid() && s.isLoaded) yield return SceneManager.UnloadSceneAsync(s);
            scenes.Clear();
        }

        void Spawn(string name, Vector3 pos, int variant, bool features)
        {
            chains.Add(KPTestRig.BuildCharacter(name, pos, variant, features));
            bases.Add(pos);
        }

        IEnumerator Frames(int n)
        {
            for (int k = 0; k < n; k++, frame++)
            {
                for (int i = 0; i < chains.Count; i++)
                    if (chains[i] != null && chains[i].gameObject.activeSelf)
                        KPTestRig.Move(chains[i].transform, i, frame, bases[i]);
                yield return null;
            }
        }

        void AssertFinite()
        {
            foreach (var c in Object.FindObjectsByType<KawaiiPhysicsChain>(FindObjectsInactive.Exclude))
                Assert.That(KPTestRig.AllFinite(c, out var d), Is.True, $"{c.name}: {d}");
        }

        [UnityTest]
        public IEnumerator RegistersThroughComponentLifecycle()
        {
            Spawn("a", Vector3.zero, 0, false);
            Spawn("b", new Vector3(2f, 0f, 0f), 1, false);
            yield return Frames(5);
            Assert.That(KPSystem.ChainCount, Is.EqualTo(2));
            Assert.That(KPSystem.BoneCount, Is.EqualTo(2 * KPTestRig.ChainsPerCharacter * (KPTestRig.BonesPerChain + 1)));
            chains[1].enabled = false;
            yield return Frames(2);
            Assert.That(KPSystem.ChainCount, Is.EqualTo(1));
            chains[1].enabled = true;
            yield return Frames(2);
            Assert.That(KPSystem.ChainCount, Is.EqualTo(2));
            AssertFinite();
        }

        // 軟組織：OnEnable／OnDisable 註冊、角色移動時有位移、停用還原、執行中刪骨頭／元件／角色
        [UnityTest]
        public IEnumerator SoftBodyRunsInPlayerLoop()
        {
            Spawn("s0", Vector3.zero, 0, false);
            Spawn("s1", new Vector3(2f, 0f, 0f), 1, false);
            var soft0 = KPTestRig.AddSoftBodies(chains[0].transform);
            var soft1 = KPTestRig.AddSoftBodies(chains[1].transform);
            var restPos = soft1[0].bones[0].localPosition;
            yield return Frames(30);
            Assert.That(KPSystem.SoftBodyCount, Is.EqualTo(4));
            Assert.That(KPSystem.SoftBoneCount, Is.EqualTo(2 * (4 + 2)));

            var maxOffset = 0f;
            for (int k = 0; k < 60; k++)
            {
                if (k == 30) soft0[0].AddImpulseAtPosition(soft0[0].bones[0].position, Vector3.down, 0.2f);
                yield return Frames(1);
                maxOffset = Mathf.Max(maxOffset, soft0[0].GetOffset(0).magnitude);
            }
            Assert.That(maxOffset, Is.GreaterThan(1e-3f), "角色移動與衝量造成位移");

            soft1[0].enabled = false;
            yield return Frames(2);
            Assert.That(KPSystem.SoftBodyCount, Is.EqualTo(3));
            Assert.That((soft1[0].bones[0].localPosition - restPos).magnitude, Is.LessThan(1e-6f), "停用後回到靜止姿勢");
            soft1[0].enabled = true;
            yield return Frames(2);
            Assert.That(KPSystem.SoftBodyCount, Is.EqualTo(4));

            Object.Destroy(soft0[1].bones[0].gameObject); // 執行中刪掉一根臀部骨頭
            yield return Frames(10);
            Object.Destroy(soft0[0]);                     // 刪掉胸的元件
            yield return Frames(5);
            Assert.That(KPSystem.SoftBodyCount, Is.EqualTo(3));
            Object.Destroy(chains[1].gameObject);         // 整個角色
            chains[1] = null;
            yield return Frames(5);
            Assert.That(KPSystem.SoftBodyCount, Is.EqualTo(1));
            AssertFinite();
        }

        [UnityTest]
        public IEnumerator SurvivesDestructiveOperationsAndSceneSwitch()
        {
            KPTestRig.BuildEnvironment(Vector3.zero);
            for (int c = 0; c < 6; c++) Spawn($"char{c}", new Vector3(c * 2f, 0f, 0f), c, c == 0);
            yield return Frames(60);

            Object.Destroy(chains[1].transform.Find("c0_0/c0_1/c0_2/c0_3").gameObject); // 鏈中段的骨頭
            yield return Frames(10);
            chains[2].gameObject.SetActive(false);
            yield return Frames(10);
            chains[2].gameObject.SetActive(true);
            yield return Frames(10);
            Object.Destroy(chains[3].gameObject);                                        // 整個角色
            yield return Frames(10);
            Object.Destroy(chains[4].colliders[0].gameObject);                           // 碰撞體
            yield return Frames(10);
            Object.Destroy(Object.FindAnyObjectByType<WindZone>().gameObject);
            yield return Frames(10);
            chains[5].stiffness = 0.5f;                                                  // 設定（就地更新）
            chains[5].boneSubdivisionCount = 1;                                          // 結構（只重建這一條）
            chains[5].MarkDirty();
            yield return Frames(10);
            chains[0].transform.position += new Vector3(10f, 0f, 0f);                  // 瞬移
            bases[0] += new Vector3(10f, 0f, 0f);
            chains[0].ResetDynamics(KPTeleportType.Teleport);
            yield return Frames(10);
            AssertFinite();

            // 切場景：新場景 → 卸載舊場景（舊場景的角色全部刪掉）→ 新場景生角色
            var old = SceneManager.GetActiveScene();
            UseNewScene("KPPlayModeTests_B");
            yield return SceneManager.UnloadSceneAsync(old);
            chains.Clear();
            bases.Clear();
            for (int c = 0; c < 3; c++) Spawn($"b{c}", new Vector3(c * 2f, 0f, 0f), c, false);
            yield return Frames(30);
            Assert.That(KPSystem.ChainCount, Is.EqualTo(3));
            AssertFinite();
        }
    }
}
