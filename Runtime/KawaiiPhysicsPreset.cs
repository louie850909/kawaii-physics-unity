// 可重用的資料資產：碰撞組（LimitsDataAsset）、骨間約束組（BoneConstraintsDataAsset）、設定預設（PresetDataAsset）、
// 共用程序風（Shared Publisher 的風）。都用骨頭名稱指定，套到不同角色時用名稱在階層裡找。
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>
    /// 對應 UKawaiiPhysicsPresetDataAsset：存一組可在角色之間共用的設定。
    /// 欄位名跟 KawaiiPhysicsChain 一樣，套用時用 JSON 覆寫同名欄位。
    /// </summary>
    [CreateAssetMenu(menuName = "KawaiiPhysics/Preset")]
    public class KawaiiPhysicsPreset : ScriptableObject
    {
        /// <summary>預設的設定值（欄位名稱與 KawaiiPhysicsChain 相同）。</summary>
        [Serializable]
        public class Data
        {
            [Tooltip("同 KawaiiPhysicsChain.damping")]
            public float damping = 0.1f;
            [Tooltip("同 KawaiiPhysicsChain.stiffness")]
            public float stiffness = 0.05f;
            [Tooltip("同 KawaiiPhysicsChain.worldDampingLocation")]
            public float worldDampingLocation = 0.8f;
            [Tooltip("同 KawaiiPhysicsChain.worldDampingRotation")]
            public float worldDampingRotation = 0.8f;
            [Tooltip("同 KawaiiPhysicsChain.radius")]
            public float radius = 0.03f;
            [Tooltip("同 KawaiiPhysicsChain.limitAngle")]
            public float limitAngle;
            [Tooltip("同 KawaiiPhysicsChain.dampingCurve")]
            public AnimationCurve dampingCurve = new AnimationCurve();
            [Tooltip("同 KawaiiPhysicsChain.stiffnessCurve")]
            public AnimationCurve stiffnessCurve = new AnimationCurve();
            [Tooltip("同 KawaiiPhysicsChain.worldDampingLocationCurve")]
            public AnimationCurve worldDampingLocationCurve = new AnimationCurve();
            [Tooltip("同 KawaiiPhysicsChain.worldDampingRotationCurve")]
            public AnimationCurve worldDampingRotationCurve = new AnimationCurve();
            [Tooltip("同 KawaiiPhysicsChain.radiusCurve")]
            public AnimationCurve radiusCurve = new AnimationCurve();
            [Tooltip("同 KawaiiPhysicsChain.limitAngleCurve")]
            public AnimationCurve limitAngleCurve = new AnimationCurve();
            [Tooltip("同 KawaiiPhysicsChain.dummyBoneLength")]
            public float dummyBoneLength;
            [Tooltip("同 KawaiiPhysicsChain.planarConstraint")]
            public KPPlanarConstraint planarConstraint;
            [Tooltip("同 KawaiiPhysicsChain.targetFramerate")]
            public int targetFramerate = 60;
            [Tooltip("同 KawaiiPhysicsChain.gravity")]
            public Vector3 gravity;
            [Tooltip("同 KawaiiPhysicsChain.enableWind")]
            public bool enableWind;
            [Tooltip("同 KawaiiPhysicsChain.windScale")]
            public float windScale = 1f;
            [Tooltip("同 KawaiiPhysicsChain.windDirectionNoiseAngle")]
            public float windDirectionNoiseAngle;
            [Tooltip("同 KawaiiPhysicsChain.boneSubdivisionCount")]
            public int boneSubdivisionCount;
            [Tooltip("同 KawaiiPhysicsChain.boneSubdivisionCollisionOnly")]
            public bool boneSubdivisionCollisionOnly = true;
            [Tooltip("同 KawaiiPhysicsChain.boneConstraintSubdivisionCount")]
            public int boneConstraintSubdivisionCount;
            [Tooltip("同 KawaiiPhysicsChain.boneConstraintGlobalComplianceType")]
            public KPComplianceType boneConstraintGlobalComplianceType = KPComplianceType.Leather;
            [Tooltip("同 KawaiiPhysicsChain.boneConstraintIterationCountBeforeCollision")]
            public int boneConstraintIterationCountBeforeCollision = 1;
            [Tooltip("同 KawaiiPhysicsChain.boneConstraintIterationCountAfterCollision")]
            public int boneConstraintIterationCountAfterCollision = 1;
        }

        [Tooltip("預設的設定值")]
        public Data data = new Data();

        /// <summary>把設定套到 chain。只改了設定就在下一幀就地更新（不重置），改到結構（骨頭細分等）才重建這一條。</summary>
        public void ApplyTo(KawaiiPhysicsChain chain)
        {
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(data), chain);
            // 下一幀比對結構簽章：只改了設定就就地更新（不重置），結構變了才重建這一條
            chain.validateRequested = true;
        }

        /// <summary>把 chain 目前的設定記到這個預設。</summary>
        public void CaptureFrom(KawaiiPhysicsChain chain) => JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(chain), data);
    }
}
