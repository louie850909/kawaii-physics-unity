// 範例：執行時生成幾個「頭＋頭髮」的角色，繞圈移動；每隔幾秒加入或移除一個角色（其他角色的頭髮不受影響）。
// 骨頭本身看不見，頭髮用 LineRenderer 畫：在 KPSystem.AfterSimulate（物理寫回 Transform 之後）更新。
// 胸前的兩顆球是 KawaiiPhysicsSoftBody（平移式軟組織）的骨頭：繞圈與轉身的慣性讓它晃，每隔 bumpInterval 秒再加一次往下的衝量。
using System.Collections.Generic;
using UnityEngine;

namespace KawaiiPhysicsUnity.Samples
{
    public class KawaiiPhysicsSample : MonoBehaviour
    {
        [Tooltip("一開始的角色數")]
        public int characters = 4;
        [Tooltip("角色數在這個範圍內來回：每隔 spawnInterval 秒加入或移除一個（0＝不做）")]
        public int maxCharacters = 6;
        [Tooltip("加入或移除角色的間隔（秒）")]
        public float spawnInterval = 3f;
        [Tooltip("每個角色的頭髮束數")]
        public int strandsPerCharacter = 12;
        [Tooltip("每束頭髮的骨頭數")]
        public int bonesPerStrand = 6;
        [Tooltip("加上程序風（KPForceProceduralWind）")]
        public bool proceduralWind = true;
        [Tooltip("軟組織（胸）加往下衝量的間隔（秒，0＝不加）")]
        public float bumpInterval = 2f;

        readonly List<GameObject> spawned = new List<GameObject>();
        readonly List<(List<Transform> bones, LineRenderer line)> strands = new List<(List<Transform>, LineRenderer)>();
        readonly List<KawaiiPhysicsSoftBody> softBodies = new List<KawaiiPhysicsSoftBody>();
        float timer;
        float bumpTimer;
        bool adding = true;
        int nextIndex;
        Material material;

        void OnEnable() => KPSystem.AfterSimulate += UpdateLines;
        void OnDisable() => KPSystem.AfterSimulate -= UpdateLines;

        void Start()
        {
            material = CreateMaterial();
            var wind = new GameObject("Wind").AddComponent<WindZone>();
            wind.transform.SetParent(transform, false);
            wind.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            wind.windMain = 1.5f;
            wind.windPulseMagnitude = 0.5f;
            wind.windPulseFrequency = 0.2f;
            for (int i = 0; i < characters; i++) Spawn();
        }

        void Update()
        {
            var t = Time.time;
            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] == null) continue;
                var a = t * 0.8f + i * 1.7f;
                var center = new Vector3((i - (spawned.Count - 1) * 0.5f) * 1.6f, 0f, 0f);
                spawned[i].transform.SetPositionAndRotation(center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.35f,
                    Quaternion.Euler(0f, Mathf.Sin(a * 0.7f) * 60f, 0f));
            }

            if (bumpInterval > 0f && (bumpTimer += Time.deltaTime) >= bumpInterval)
            {
                bumpTimer = 0f;
                foreach (var soft in softBodies)
                    if (soft != null) soft.AddImpulse(new Vector3(0f, -0.6f, 0f));
            }

            if (spawnInterval <= 0f || (timer += Time.deltaTime) < spawnInterval) return;
            timer = 0f;
            if (adding && spawned.Count >= maxCharacters) adding = false;
            if (!adding && spawned.Count <= 1) adding = true;
            if (adding) Spawn();
            else Despawn();
        }

        void Spawn()
        {
            var index = nextIndex++;
            var root = new GameObject($"Character {index}");
            root.SetActive(false); // 設定齊全之後才啟用（OnEnable 時註冊）
            root.transform.SetParent(transform, false);

            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(head.GetComponent<Collider>());
            head.name = "Head (visual)";
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            head.transform.localScale = Vector3.one * 0.32f;

            var headCollider = new GameObject("Head (KawaiiPhysicsCollider)").AddComponent<KawaiiPhysicsCollider>();
            headCollider.transform.SetParent(root.transform, false);
            headCollider.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            headCollider.radius = 0.17f;
            var body = new GameObject("Body (KawaiiPhysicsCollider)").AddComponent<KawaiiPhysicsCollider>();
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 1.15f, 0f);
            body.shape = KawaiiPhysicsCollider.Shape.Capsule;
            body.radius = 0.15f;
            body.length = 0.5f;

            var chain = root.AddComponent<KawaiiPhysicsChain>();
            chain.component = root.transform;
            chain.damping = 0.12f;
            chain.stiffness = 0.04f;
            chain.radius = 0.02f;
            chain.limitAngle = 70f;
            chain.gravity = new Vector3(0f, -9.8f, 0f);
            chain.dummyBoneLength = 0.06f;
            chain.boneForwardAxis = KawaiiPhysicsChain.Axis.YNegative;
            chain.enableWind = true;
            chain.colliders.Add(headCollider);
            chain.colliders.Add(body);
            if (proceduralWind)
                chain.externalForces.Add(new KPForceProceduralWind
                {
                    parameters = new KPProceduralWindParams { windDirection = Vector3.right, swayForce = 1.5f, rippleForce = 1f, randomForce = 0.5f, seed = index },
                });

            for (int k = 0; k < strandsPerCharacter; k++)
            {
                // 頭的後半圈往下垂
                var a = Mathf.PI * (0.15f + 0.7f * k / Mathf.Max(1, strandsPerCharacter - 1));
                var dir = new Vector3(Mathf.Cos(a), 0.3f, -Mathf.Sin(a)).normalized;
                var bones = new List<Transform>();
                Transform prev = null;
                for (int i = 0; i < bonesPerStrand; i++)
                {
                    var bone = new GameObject($"hair{k}_{i}").transform;
                    bone.SetParent(prev != null ? prev : root.transform, false);
                    bone.localPosition = prev == null ? new Vector3(0f, 1.6f, 0f) + dir * 0.17f : Vector3.down * 0.07f;
                    bones.Add(bone);
                    prev = bone;
                }
                if (k == 0) chain.rootBone = bones[0];
                else chain.additionalRootBones.Add(new KawaiiPhysicsChain.RootBoneSetting { rootBone = bones[0] });

                // LineRenderer 放在角色根底下（不能放在骨頭底下：骨頭的子物件也會被當成骨頭）
                var line = new GameObject($"hair{k} (line)").AddComponent<LineRenderer>();
                line.transform.SetParent(root.transform, false);
                line.positionCount = bones.Count;
                line.widthMultiplier = 0.025f;
                line.sharedMaterial = material;
                line.startColor = line.endColor = Color.HSVToRGB(index * 0.17f % 1f, 0.6f, 0.9f);
                strands.Add((bones, line));
            }

            // 胸：KawaiiPhysicsSoftBody。骨頭底下的球只是外觀，跟著骨頭平移、傾斜、伸縮
            var chest = new GameObject("Chest").transform;
            chest.SetParent(root.transform, false);
            chest.localPosition = new Vector3(0f, 1.25f, 0f);
            var soft = root.AddComponent<KawaiiPhysicsSoftBody>();
            soft.referenceRoot = root.transform;
            soft.frequency = 3f;
            soft.dampingRatio = 0.2f;
            soft.tilt = 0.5f;
            soft.stretchAxis = KawaiiPhysicsSoftBody.Axis.Z;
            soft.stretchAmount = 0.25f;
            soft.limitMin = new Vector3(-0.04f, -0.05f, -0.02f);
            soft.limitMax = new Vector3(0.04f, 0.04f, 0.04f);
            foreach (var side in new[] { -1f, 1f })
            {
                var bone = new GameObject(side < 0f ? "BustL" : "BustR").transform;
                bone.SetParent(chest, false);
                bone.localPosition = new Vector3(0.08f * side, 0.02f, 0.16f);
                var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(ball.GetComponent<Collider>());
                ball.name = "Bust (visual)";
                ball.transform.SetParent(bone, false);
                ball.transform.localScale = Vector3.one * 0.13f;
                soft.bones.Add(bone);
            }
            softBodies.Add(soft);

            root.SetActive(true);
            spawned.Add(root);
        }

        void Despawn()
        {
            var root = spawned[0];
            spawned.RemoveAt(0);
            strands.RemoveAll(s => s.line == null || s.line.transform.parent == root.transform);
            softBodies.RemoveAll(s => s == null || s.gameObject == root);
            Destroy(root);
        }

        void UpdateLines()
        {
            foreach (var (bones, line) in strands)
            {
                if (line == null) continue;
                for (int i = 0; i < bones.Count; i++) line.SetPosition(i, bones[i].position);
            }
        }

        void OnGUI()
        {
            GUI.Label(new Rect(10f, 10f, 900f, 22f),
                $"KawaiiPhysics: {KPSystem.ChainCount} chains, {KPSystem.BoneCount} bones, {KPSystem.SoftBodyCount} soft bodies ({KPSystem.SoftBoneCount} bones), " +
                $"simulate {KPSystem.LastSimulateMs:0.000} ms, last sync {KPSystem.LastSyncMs:0.000} ms");
        }

        static Material CreateMaterial()
        {
            // URP、內建管線都有的不受光 shader
            foreach (var name in new[] { "Universal Render Pipeline/Unlit", "Sprites/Default", "Unlit/Color" })
            {
                var shader = Shader.Find(name);
                if (shader != null) return new Material(shader);
            }
            return null;
        }
    }
}
