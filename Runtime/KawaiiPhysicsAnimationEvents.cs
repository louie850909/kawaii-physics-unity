using System.Collections.Generic;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>
    /// 原作 AnimNotify／AnimNotifyState（SetAlpha、AddExternalForce、TriggerGust、SettingsMultiplier）的 Unity 版：
    /// 放在有 Animator 的物件上，動畫片段的 AnimationEvent 呼叫這裡的方法，作用到底下所有的 KawaiiPhysicsChain
    /// 與 KawaiiPhysicsSoftBody。
    /// </summary>
    public class KawaiiPhysicsAnimationEvents : MonoBehaviour
    {
        // 事件時才找（元件會增減），清單重用：事件密集時也不每次配置陣列
        readonly List<KawaiiPhysicsChain> chains = new List<KawaiiPhysicsChain>();
        readonly List<KawaiiPhysicsSoftBody> softBodies = new List<KawaiiPhysicsSoftBody>();

        List<KawaiiPhysicsChain> Chains
        {
            get
            {
                GetComponentsInChildren(chains);
                return chains;
            }
        }

        List<KawaiiPhysicsSoftBody> SoftBodies
        {
            get
            {
                GetComponentsInChildren(softBodies);
                return softBodies;
            }
        }

        /// <summary>AnimationEvent：floatParameter＝新的 alpha（Chain 與 SoftBody）。</summary>
        public void KawaiiPhysicsSetAlpha(float alpha)
        {
            foreach (var c in Chains) c.alpha = Mathf.Clamp01(alpha);
            foreach (var s in SoftBodies) s.alpha = Mathf.Clamp01(alpha);
        }

        /// <summary>AnimationEvent：底下所有 Chain 與 SoftBody 重新開始物理（ResetDynamics）。</summary>
        public void KawaiiPhysicsResetDynamics()
        {
            foreach (var c in Chains) c.ResetDynamics();
            foreach (var s in SoftBodies) s.ResetDynamics();
        }

        /// <summary>
        /// AnimationEvent：objectReferenceParameter＝KawaiiPhysicsEventPreset。
        /// SoftBodyImpulse 作用到名稱符合 softBodyNameFilter 的 SoftBody，其他種類作用到名稱符合 chainNameFilter 的 Chain。
        /// </summary>
        public void KawaiiPhysicsEvent(Object preset)
        {
            if (!(preset is KawaiiPhysicsEventPreset p)) return;
            if (p.kind == KawaiiPhysicsEventPreset.Kind.SoftBodyImpulse)
            {
                foreach (var s in SoftBodies)
                    if (string.IsNullOrEmpty(p.softBodyNameFilter) || s.name.Contains(p.softBodyNameFilter)) p.Apply(s);
                return;
            }
            foreach (var c in Chains)
                if (string.IsNullOrEmpty(p.chainNameFilter) || c.name.Contains(p.chainNameFilter)) p.Apply(c);
        }
    }
}
