// 一條鏈的執行時操作：暫時外力、設定倍率、重置與瞬移的要求（原作 AnimNode 的 Request* 與 ResetDynamics）。
// KawaiiPhysicsChain 與 KPRigChain 都轉給它，每幀由主機推進。
using System.Collections.Generic;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    internal sealed class KPChainControl
    {
        internal bool PendingTeleport;
        internal bool PendingReset;

        static long nextHandle = 1;
        internal static long NewHandle() => nextHandle++;

        internal readonly List<KPTransientForce> transientForces = new List<KPTransientForce>();
        internal readonly List<KPActiveMultiplier> multipliers = new List<KPActiveMultiplier>();

        public long RequestTransientExternalForce(KPExternalForce force, float lifetime)
        {
            var h = NewHandle();
            force.oneShot = false;
            transientForces.Add(new KPTransientForce { Force = force, RemainingLifetime = lifetime, Handle = h });
            TrimTransients();
            return h;
        }

        /// <summary>externalForces：這條鏈的外力（找程序風當突風的底）。</summary>
        public long RequestTransientGust(List<KPExternalForce> externalForces, float strength, float riseTime, float decayTime, Vector3 direction,
            int inheritForceIndex, float holdTime, bool realTimeEnvelope)
        {
            var h = NewHandle();
            var sources = new List<KPForceProceduralWind>();
            if (inheritForceIndex == KawaiiPhysicsChain.TransientGustInheritAllWinds)
            {
                foreach (var f in externalForces)
                    if (f is KPForceProceduralWind pw && pw.enabled) sources.Add(pw);
            }
            else
            {
                KPForceProceduralWind src = null;
                if (inheritForceIndex >= 0 && inheritForceIndex < externalForces.Count) src = externalForces[inheritForceIndex] as KPForceProceduralWind;
                if (src == null)
                    foreach (var f in externalForces)
                        if (f is KPForceProceduralWind pw && pw.enabled) { src = pw; break; }
                sources.Add(src);
            }
            if (sources.Count == 0) sources.Add(null);
            foreach (var src in sources)
            {
                var wind = src != null ? (KPForceProceduralWind)src.CloneForTransient() : new KPForceProceduralWind();
                if (realTimeEnvelope) wind.parameters.timeScale = 1f;
                if (direction.sqrMagnitude > 1e-8f)
                {
                    // 原作：指定方向時不繼承來源的方向與方向雜訊
                    wind.space = KPForceSpace.WorldSpace;
                    wind.parameters.windDirection = direction;
                    wind.parameters.windDirectionNoiseAngle = 0f;
                }
                // 突風本身以外的成分歸零：只留下突風
                var p = wind.parameters;
                p.constantForce = p.swayForce = p.rippleForce = p.randomForce = 0f;
                p.strengthCycleRange = Vector2.one;
                var ts = Mathf.Max(p.timeScale, KPMath.KindaSmallNumber);
                var lifetime = (Mathf.Max(riseTime, 0f) + Mathf.Max(holdTime, 0f) + Mathf.Max(decayTime, 0f)) / ts + 0.2f;
                wind.RequestGust(strength, riseTime, decayTime, holdTime, realTimeEnvelope);
                transientForces.Add(new KPTransientForce { Force = wind, RemainingLifetime = lifetime, Handle = h });
            }
            TrimTransients();
            return h;
        }

        public void RequestStopTransientExternalForce(long handle, float blendOutTime)
        {
            for (int i = transientForces.Count - 1; i >= 0; i--)
            {
                var t = transientForces[i];
                if (t.Handle != handle) continue;
                if (blendOutTime <= KPMath.KindaSmallNumber) { transientForces.RemoveAt(i); continue; }
                if (t.Force is KPForceProceduralWind pw)
                {
                    pw.RequestGustStop(blendOutTime);
                    t.RemainingLifetime = blendOutTime / Mathf.Max(pw.parameters.timeScale, KPMath.KindaSmallNumber) + 0.2f;
                }
                else t.RemainingLifetime = Mathf.Min(t.RemainingLifetime, Mathf.Max(blendOutTime, 0f));
            }
        }

        void TrimTransients()
        {
            while (transientForces.Count > KawaiiPhysicsChain.MaxTransientExternalForces) transientForces.RemoveAt(0);
        }

        public long StartSettingsMultiplier(KPSettingsMultiplier scale, float riseTime, float holdTime, float decayTime, bool infiniteHold)
        {
            var h = NewHandle();
            multipliers.Add(new KPActiveMultiplier
            {
                Handle = h, Scale = scale, RiseTime = riseTime, HoldTime = holdTime, DecayTime = decayTime,
                InfiniteHold = infiniteHold, PeakAlpha = 1f,
            });
            TrimMultipliers();
            return h;
        }

        public void PushSettingsMultiplier(long handle, KPSettingsMultiplier scale, float pushAlpha, int leaseEvaluations, float leaseExpireBlendOut)
        {
            if (handle == 0) return;
            // 逐幀呼叫的 API：用迴圈找（lambda 會捕捉 handle，每次呼叫都配置）
            KPActiveMultiplier m = null;
            foreach (var x in multipliers)
                if (x.Handle == handle)
                {
                    m = x;
                    break;
                }
            if (m == null)
            {
                m = new KPActiveMultiplier { Handle = handle };
                multipliers.Add(m);
            }
            m.Scale = scale;
            m.DrivenAlpha = Mathf.Clamp01(pushAlpha);
            m.LeaseEvaluations = Mathf.Max(0, leaseEvaluations);
            m.LeaseRemaining = m.LeaseEvaluations;
            m.LeaseExpireBlendOut = Mathf.Max(0f, leaseExpireBlendOut);
            m.InfiniteHold = false;
            m.ExternallyDriven = true;
            m.PeakAlpha = 1f;
            m.RiseTime = m.HoldTime = m.DecayTime = m.ElapsedTime = 0f;
            TrimMultipliers();
        }

        public void StopSettingsMultiplier(long handle, float blendOutTime)
        {
            for (int i = multipliers.Count - 1; i >= 0; i--)
                if (multipliers[i].Handle == handle) StopMultiplierAt(i, blendOutTime);
        }

        internal void StopMultiplierAt(int index, float blendOutTime)
        {
            var m = multipliers[index];
            if (blendOutTime <= KPMath.KindaSmallNumber) { multipliers.RemoveAt(index); return; }
            m.PeakAlpha = m.ExternallyDriven ? m.DrivenAlpha : m.PeakAlpha * m.CurrentEnvelope();
            m.InfiniteHold = false;
            m.ExternallyDriven = false;
            m.DrivenAlpha = 0f;
            m.LeaseEvaluations = m.LeaseRemaining = 0;
            m.RiseTime = 0f;
            m.HoldTime = 0f;
            m.DecayTime = blendOutTime;
            m.ElapsedTime = 0f;
        }

        void TrimMultipliers()
        {
            while (multipliers.Count > KawaiiPhysicsChain.MaxPhysicsSettingsMultipliers) multipliers.RemoveAt(0);
        }

        /// <summary>原作 ComputeEffectivePhysicsSettingsMultiplierScale：每個倍率依包絡從 1 內插過去，彼此相乘。</summary>
        internal KPSettingsMultiplier EffectiveMultiplier()
        {
            var e = KPSettingsMultiplier.One;
            foreach (var m in multipliers)
            {
                var a = m.ExternallyDriven ? m.DrivenAlpha : m.PeakAlpha * m.CurrentEnvelope();
                e.Damping *= Mathf.Lerp(1f, m.Scale.Damping, a);
                e.Stiffness *= Mathf.Lerp(1f, m.Scale.Stiffness, a);
                e.WorldDampingLocation *= Mathf.Lerp(1f, m.Scale.WorldDampingLocation, a);
                e.WorldDampingRotation *= Mathf.Lerp(1f, m.Scale.WorldDampingRotation, a);
                e.Radius *= Mathf.Lerp(1f, m.Scale.Radius, a);
                e.LimitAngle *= Mathf.Lerp(1f, m.Scale.LimitAngle, a);
            }
            return e;
        }

        /// <summary>原作 ConsumeAndAdvancePhysicsSettingsMultipliers 的時間推進、租約到期與結束移除。</summary>
        internal void AdvanceMultipliers(float dt)
        {
            foreach (var m in multipliers)
            {
                m.ElapsedTime += dt;
                if (m.ExternallyDriven && m.LeaseEvaluations > 0) m.LeaseRemaining--;
            }
            for (int i = multipliers.Count - 1; i >= 0; i--)
            {
                var m = multipliers[i];
                if (m.ExternallyDriven && m.LeaseEvaluations > 0 && m.LeaseRemaining <= 0) StopMultiplierAt(i, m.LeaseExpireBlendOut);
            }
            for (int i = multipliers.Count - 1; i >= 0; i--)
            {
                var m = multipliers[i];
                if (!m.ExternallyDriven && !m.InfiniteHold && m.ElapsedTime >= m.RiseTime + m.HoldTime + m.DecayTime) multipliers.RemoveAt(i);
            }
        }

        /// <summary>原作 ConsumeAndRemoveExpiredTransientExternalForces 的壽命遞減。</summary>
        internal void AdvanceTransients(float dt)
        {
            for (int i = transientForces.Count - 1; i >= 0; i--)
            {
                transientForces[i].RemainingLifetime -= dt;
                if (transientForces[i].RemainingLifetime <= 0f) transientForces.RemoveAt(i);
            }
        }
    }
}
