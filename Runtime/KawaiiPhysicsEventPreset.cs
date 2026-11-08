using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>AnimationEvent 用的事件內容。</summary>
    [CreateAssetMenu(menuName = "KawaiiPhysics/Event Preset")]
    public class KawaiiPhysicsEventPreset : ScriptableObject
    {
        /// <summary>事件種類。</summary>
        public enum Kind
        {
            /// <summary>突風（RequestTransientGust）</summary>
            Gust,
            /// <summary>暫時外力（RequestTransientExternalForce）</summary>
            AddExternalForce,
            /// <summary>設定倍率（StartSettingsMultiplier）</summary>
            SettingsMultiplier,
            /// <summary>軟組織的衝量（KawaiiPhysicsSoftBody.AddImpulse；作用到 SoftBody，不作用到 Chain）</summary>
            SoftBodyImpulse,
        }

        [Tooltip("事件種類")]
        public Kind kind = Kind.Gust;
        [Tooltip("空的話作用到全部 Chain；否則只作用到名稱含這個字串的")]
        public string chainNameFilter = "";

        [Header("Gust")]
        [Tooltip("突風強度")]
        public float strength = 1f;
        [Tooltip("突風升起的時間（秒）")]
        public float riseTime = 0.1f;
        [Tooltip("突風維持的時間（秒）")]
        public float holdTime;
        [Tooltip("突風落下的時間（秒）")]
        public float decayTime = 0.5f;
        [Tooltip("突風方向（零向量＝沿用程序風的方向）")]
        public Vector3 direction;
        [Tooltip("突風的包絡用真實時間（不受程序風 timeScale 影響）")]
        public bool realTimeEnvelope;

        [Header("AddExternalForce")]
        [Tooltip("要加的暫時外力")]
        [SerializeReference] public KPExternalForce force = new KPForceBasic();
        public float lifetime = 0.5f;

        [Header("SettingsMultiplier（總長 duration；起落時間會被壓進總長）")]
        [Tooltip("設定倍率")]
        public KPSettingsMultiplier multiplier = KPSettingsMultiplier.One;
        [Tooltip("倍率的總時間（秒）")]
        public float duration = 1f;
        [Tooltip("倍率升起的時間（秒）")]
        public float blendIn = 0.1f;
        [Tooltip("倍率落下的時間（秒）")]
        public float blendOut = 0.2f;

        [Header("SoftBodyImpulse")]
        [Tooltip("速度變化（公尺/秒），角色根（SoftBody 的 referenceRoot）的區域空間；例：(0,-0.6,0)＝往下一頓")]
        public Vector3 softBodyImpulse = new Vector3(0f, -0.6f, 0f);
        [Tooltip("空的話作用到全部 SoftBody；否則只作用到名稱含這個字串的")]
        public string softBodyNameFilter = "";

        /// <summary>把這個事件套到一條鏈。</summary>
        public void Apply(KawaiiPhysicsChain chain) => Apply(chain.settings.control, chain.externalForces);

        /// <summary>把這個事件套到陣列接口的一條鏈。</summary>
        public void Apply(KPRigChain chain) => Apply(chain.control, chain.externalForces);

        void Apply(KPChainControl control, System.Collections.Generic.List<KPExternalForce> externalForces)
        {
            switch (kind)
            {
                case Kind.Gust:
                    control.RequestTransientGust(externalForces, strength, riseTime, decayTime, direction, -1, holdTime, realTimeEnvelope);
                    break;
                case Kind.AddExternalForce:
                    if (force != null) control.RequestTransientExternalForce(force.CloneForTransient(), lifetime);
                    break;
                case Kind.SettingsMultiplier:
                    KPMath.ResolveEnvelope(duration, blendIn, blendOut, out var rise, out var hold, out var decay);
                    control.StartSettingsMultiplier(multiplier, rise, hold, decay, false);
                    break;
            }
        }

        /// <summary>把這個事件套到一個軟組織元件（只有 SoftBodyImpulse 有作用）。</summary>
        public void Apply(KawaiiPhysicsSoftBody softBody)
        {
            if (kind == Kind.SoftBodyImpulse) softBody.AddImpulse(softBody.ResolveRoot().TransformDirection(softBodyImpulse));
        }
    }
}
