// 測試用的角色與場景（Editor 與 PlayMode 測試共用）。
using System.Collections.Generic;
using KawaiiPhysicsUnity;
using UnityEngine;

namespace KawaiiPhysicsUnity.Tests
{
public static class KPTestRig
{
    public const int ChainsPerCharacter = 16;
    public const int BonesPerChain = 8;

    /// <summary>
    /// 16 條鏈 × 8 骨（＋末端假骨＝144 骨）的角色，根在 1.6 m 高往下垂，4 顆球＋6 個膠囊碰撞。
    /// variant 決定模擬空間（元件／世界／基準骨）。features 開滿其餘功能：五種外力、場景風、
    /// 簡易世界碰撞、世界碰撞、骨間約束、細分、錐形膠囊與箱碰撞、暖機。
    /// 物件先停用再設定，最後才啟用（Play 模式下 OnEnable 註冊時設定已經齊全）。
    /// </summary>
    public static KawaiiPhysicsChain BuildCharacter(string name, Vector3 position, int variant, bool features, bool active = true)
    {
        var root = new GameObject(name);
        root.SetActive(false);
        root.transform.position = position;
        var spine = new GameObject("spine").transform;
        spine.SetParent(root.transform, false);
        spine.localPosition = new Vector3(0f, 1.5f, 0f);

        var chain = root.AddComponent<KawaiiPhysicsChain>();
        chain.component = root.transform;
        chain.dummyBoneLength = 0.05f;
        chain.boneForwardAxis = KawaiiPhysicsChain.Axis.YNegative;
        chain.gravity = new Vector3(0f, -9.8f, 0f);
        chain.limitAngle = 60f;
        chain.radius = 0.01f;
        chain.simulationSpace = (KPSimulationSpace)(variant % 3);
        chain.simulationBaseBone = spine;

        var firstBones = new List<List<Transform>>();
        for (int k = 0; k < ChainsPerCharacter; k++)
        {
            var a = k * Mathf.PI * 2f / ChainsPerCharacter;
            var bones = new List<Transform>();
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
                bones.Add(t);
                prev = t;
            }
            firstBones.Add(bones);
        }
        for (int s = 0; s < 4; s++)
            chain.colliders.Add(Collider(root.transform, "ball", new Vector3(0.1f * s - 0.15f, 1.2f, 0.1f), KawaiiPhysicsCollider.Shape.Sphere, 0.08f, 0f));
        for (int s = 0; s < 6; s++)
            chain.colliders.Add(Collider(root.transform, "cap", new Vector3(0.08f * s - 0.2f, 0.9f, 0f), KawaiiPhysicsCollider.Shape.Capsule, 0.06f, 0.3f));

        if (features)
        {
            var tapered = Collider(root.transform, "tapered", new Vector3(0f, 1.1f, -0.1f), KawaiiPhysicsCollider.Shape.TaperedCapsule, 0.07f, 0.25f);
            tapered.radius1 = 0.03f;
            chain.colliders.Add(tapered);
            var box = Collider(root.transform, "box", new Vector3(0f, 1.0f, 0.15f), KawaiiPhysicsCollider.Shape.Box, 0f, 0f);
            box.extent = new Vector3(0.1f, 0.05f, 0.05f);
            chain.colliders.Add(box);

            chain.boneSubdivisionCount = 1;
            chain.warmUpFrames = 10;
            chain.needWarmUp = true;
            chain.enableWind = true;
            chain.windDirectionNoiseAngle = 10f;
            chain.useSimpleWorldCollision = true;
            chain.allowWorldCollision = true;
            for (int i = 1; i < BonesPerChain; i++)
                chain.boneConstraints.Add(new KawaiiPhysicsChain.BoneConstraint { bone1 = firstBones[0][i], bone2 = firstBones[1][i] });

            var curve = new KPForceCurve { space = KPForceSpace.WorldSpace };
            curve.forceX = AnimationCurve.EaseInOut(0f, -1f, 1f, 1f);
            curve.forceX.postWrapMode = WrapMode.PingPong;
            chain.externalForces.Add(new KPForceBasic { forceDir = new Vector3(0.5f, 0f, 0f), interval = 0.5f });
            chain.externalForces.Add(new KPForceGravity());
            chain.externalForces.Add(curve);
            chain.externalForces.Add(new KPForceWind { windDirectionNoiseAngle = 15f });
            chain.externalForces.Add(new KPForceProceduralWind
            {
                parameters = new KPProceduralWindParams { swayForce = 1f, rippleForce = 1f, randomForce = 0.5f, windDirectionNoiseAngle = 10f, seed = variant },
            });
        }
        if (active) root.SetActive(true);
        return chain;
    }

    static KawaiiPhysicsCollider Collider(Transform parent, string name, Vector3 local, KawaiiPhysicsCollider.Shape shape, float radius, float length)
    {
        var col = new GameObject(name).AddComponent<KawaiiPhysicsCollider>();
        col.transform.SetParent(parent, false);
        col.transform.localPosition = local;
        col.shape = shape;
        col.radius = radius;
        col.length = length;
        return col;
    }

    /// <summary>
    /// 軟組織：spine 底下左右胸（基部＋尖端各 1 骨，開傾斜與伸縮，手的球碰撞體）與骨盆底下左右臀，各一個 KawaiiPhysicsSoftBody。
    /// 元件放在先停用的子物件上，設定好才啟用（Play 模式下 OnEnable 註冊時設定已經齊全）。回傳 [胸, 臀]。
    /// </summary>
    public static KawaiiPhysicsSoftBody[] AddSoftBodies(Transform root)
    {
        static Transform NewChild(Transform parent, string name, Vector3 local)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = local;
            return t;
        }
        var spine = root.Find("spine");
        var holder = new GameObject("softbody");
        holder.SetActive(false);
        holder.transform.SetParent(root, false);

        var bust = holder.AddComponent<KawaiiPhysicsSoftBody>();
        bust.referenceRoot = root;
        bust.tilt = 0.5f;
        bust.stretchAxis = KawaiiPhysicsSoftBody.Axis.Z;
        bust.stretchAmount = 0.3f;
        bust.limitMin = new Vector3(-0.03f, -0.04f, -0.02f);
        bust.limitMax = new Vector3(0.03f, 0.03f, 0.03f);
        foreach (var side in new[] { -1f, 1f })
        {
            var b0 = NewChild(spine, side < 0f ? "bustL" : "bustR", new Vector3(0.08f * side, -0.2f, 0.1f));
            bust.bones.Add(b0);
            bust.bones.Add(NewChild(b0, b0.name + "_tip", new Vector3(0f, 0f, 0.04f)));
        }
        var hand = NewChild(root, "hand", new Vector3(0f, 1.25f, 0.2f)).gameObject.AddComponent<KawaiiPhysicsCollider>();
        hand.radius = 0.05f;
        bust.colliders.Add(hand);

        var hip = holder.AddComponent<KawaiiPhysicsSoftBody>();
        hip.referenceRoot = root;
        hip.frequency = 4f;
        hip.dampingRatio = 0.3f;
        hip.limitMin = new Vector3(-0.03f, -0.03f, -0.03f);
        hip.limitMax = new Vector3(0.03f, 0.03f, 0.03f);
        var pelvis = NewChild(root, "pelvis", new Vector3(0f, 0.95f, 0f));
        foreach (var side in new[] { -1f, 1f })
            hip.bones.Add(NewChild(pelvis, side < 0f ? "hipL" : "hipR", new Vector3(0.09f * side, -0.05f, -0.1f)));

        holder.SetActive(true);
        return new[] { bust, hip };
    }

    /// <summary>簡易世界碰撞與世界碰撞會碰到的場景物件：地面、球、膠囊、箱，以及 WindZone。</summary>
    public static List<GameObject> BuildEnvironment(Vector3 center)
    {
        var list = new List<GameObject>();
        var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "ground";
        ground.transform.position = center + new Vector3(0f, -0.5f, 0f);
        ground.transform.localScale = new Vector3(60f, 1f, 60f);
        list.Add(ground);
        var types = new[] { PrimitiveType.Sphere, PrimitiveType.Capsule, PrimitiveType.Cube };
        for (int i = 0; i < 6; i++)
        {
            var go = GameObject.CreatePrimitive(types[i % 3]);
            go.name = "obstacle" + i;
            go.transform.position = center + new Vector3(i * 2f + 0.3f, 1.2f, 0.25f);
            go.transform.localScale = Vector3.one * 0.3f;
            list.Add(go);
        }
        var wind = new GameObject("wind").AddComponent<WindZone>();
        wind.mode = WindZoneMode.Directional;
        wind.windMain = 2f;
        wind.windPulseMagnitude = 0.5f;
        wind.windPulseFrequency = 0.3f;
        list.Add(wind.gameObject);
        return list;
    }

    /// <summary>角色的根沿圓周移動並旋轉（幀號決定，可重現）。</summary>
    public static void Move(Transform root, int index, int frame, Vector3 basePosition)
    {
        var a = frame * 0.05f + index;
        root.SetPositionAndRotation(basePosition + new Vector3(Mathf.Cos(a) * 0.5f, 0f, Mathf.Sin(a) * 0.5f), Quaternion.Euler(0f, a * 30f, 0f));
    }

    /// <summary>鏈底下所有骨頭的位置都是有限值。</summary>
    public static bool AllFinite(KawaiiPhysicsChain chain, out string detail)
    {
        detail = "";
        if (chain == null || chain.rootBone == null) return true;
        foreach (var t in chain.transform.GetComponentsInChildren<Transform>(true))
        {
            var p = t.position;
            var q = t.rotation;
            if (!(IsFinite(p.x) && IsFinite(p.y) && IsFinite(p.z) && IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w)) || p.magnitude > 1e5f)
            {
                detail = $"{t.name} pos={p} rot={q}";
                return false;
            }
        }
        return true;
    }

    static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}
}
