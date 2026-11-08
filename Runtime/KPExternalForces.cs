// 外力（對應原作 ExternalForces/ 底下的五種）。每幀在主執行緒呼叫 PreApply 算出這一幀的值，
// 每根骨頭的套用在 Burst 工作裡（KPSolver.ApplyForcesTo*）。
using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    internal struct KPForceContext
    {
        /// <summary>這一幀的時間（原作 PreApply 時的 GetStepDeltaTime＝幀時間）。</summary>
        public float DeltaTime;
        public uint FrameSeed;
    }

    /// <summary>對應 FKawaiiPhysics_ExternalForce。</summary>
    [Serializable]
    public abstract class KPExternalForce
    {
        [Tooltip("啟用")]
        public bool enabled = true;
        [Tooltip("空的話全部骨頭都套用")]
        public List<Transform> applyBoneFilter = new List<Transform>();
        [Tooltip("不受這個力的骨頭")]
        public List<Transform> ignoreBoneFilter = new List<Transform>();
        [Tooltip("依骨頭名稱：空的話全部骨頭都套用（陣列接口 KPRig 只看名稱的篩選）")]
        public List<string> applyBoneNameFilter = new List<string>();
        [Tooltip("依骨頭名稱：不受這個力的骨頭")]
        public List<string> ignoreBoneNameFilter = new List<string>();
        [Tooltip("力的向量的空間")]
        public KPForceSpace space = KPForceSpace.WorldSpace;
        [Tooltip("每幀在這個範圍內隨機取一個倍率")]
        public Vector2 randomForceScaleRange = Vector2.one;
        [Tooltip("依骨長比例（根 0、末端 1）乘上的倍率；沒有 key 視為 1")]
        public AnimationCurve forceRateByBoneLengthRate = new AnimationCurve();
        [Tooltip("只作用一幀就移除")]
        public bool oneShot;

        /// <summary>套件外不能繼承（每幀的 PreApply 用到內部的資料結構）。</summary>
        internal KPExternalForce() { }

        [NonSerialized] internal int lut = -1;
        [NonSerialized] internal bool lutBaked;
        [NonSerialized] internal int lutGeneration;
        [NonSerialized] protected float randomizedScale = 1f;

        /// <summary>這根骨頭會不會受這個力（applyBoneFilter 有指定時只限其中、ignoreBoneFilter 裡的除外）。</summary>
        public bool CanApplyTo(Transform bone)
        {
            if (applyBoneFilter.Count > 0 && (bone == null || !applyBoneFilter.Contains(bone))) return false;
            if (ignoreBoneFilter.Count > 0 && bone != null && ignoreBoneFilter.Contains(bone)) return false;
            return true;
        }

        /// <summary>這個名稱的骨頭會不會受這個力（applyBoneNameFilter 有指定時只限其中、ignoreBoneNameFilter 裡的除外）。</summary>
        public bool CanApplyToName(string boneName)
        {
            if (applyBoneNameFilter.Count > 0 && (boneName == null || !applyBoneNameFilter.Contains(boneName))) return false;
            if (ignoreBoneNameFilter.Count > 0 && boneName != null && ignoreBoneNameFilter.Contains(boneName)) return false;
            return true;
        }

        /// <summary>
        /// 骨架上的一根骨頭（node＜0＝假骨）會不會受這個力：Transform 的篩選（只有 Transform 版的骨架）與名稱的篩選都要過。
        /// 名稱只在有名稱篩選時才取（Transform.name 會配置字串）。
        /// </summary>
        internal bool CanApplyTo(KPHierarchy hierarchy, int node)
        {
            if (hierarchy is KPTransformHierarchy th && !CanApplyTo(node >= 0 ? th.Get(node) : null)) return false;
            if (applyBoneNameFilter.Count == 0 && ignoreBoneNameFilter.Count == 0) return true;
            return CanApplyToName(node >= 0 ? hierarchy.Name(node) : null);
        }

        /// <summary>算出這一幀的值。回傳 false 表示這一幀不作用。</summary>
        internal virtual bool PreApply(in KPForceContext ctx, ref KPForce f)
        {
            randomizedScale = UnityEngine.Random.Range(randomForceScaleRange.x, randomForceScaleRange.y);
            f.Space = (int)space;
            f.Scale = 1f;
            return true;
        }

        /// <summary>原作 PostApply：一次性的外力用完就移除。</summary>
        internal virtual bool ShouldRemoveAfterApply() => oneShot;

        /// <summary>複製一份（暫時外力用，不跟原本的共用狀態）。</summary>
        public virtual KPExternalForce CloneForTransient() => (KPExternalForce)MemberwiseClone();
    }

    /// <summary>對應 FKawaiiPhysics_ExternalForce_Basic：固定方向的力，可以每隔一段時間才作用一次。</summary>
    [Serializable]
    public class KPForceBasic : KPExternalForce
    {
        [Tooltip("力（加速度，公尺／秒²）")]
        public Vector3 forceDir;
        [Tooltip("大於 0 時每隔這麼多秒作用一幀")]
        public float interval;
        [NonSerialized] float time;

        internal override bool PreApply(in KPForceContext ctx, ref KPForce f)
        {
            base.PreApply(ctx, ref f);
            time += ctx.DeltaTime;
            Vector3 force;
            if (interval > 0f)
            {
                if (time > interval)
                {
                    force = forceDir * randomizedScale;
                    time %= interval;
                }
                else force = Vector3.zero;
            }
            else force = forceDir * randomizedScale;
            f.Type = KPForceType.Position;
            f.Vector = force;
            return true;
        }
    }

    /// <summary>對應 FKawaiiPhysics_ExternalForce_Gravity：加在速度上的重力（世界空間）。</summary>
    [Serializable]
    public class KPForceGravity : KPExternalForce
    {
        [Tooltip("勾選時乘上 Physics.gravity 的大小（原作是 CharacterMovement 的 GravityZ）")]
        public bool usePhysicsGravityScale = true;
        [Tooltip("用自訂的重力方向")]
        public bool useOverrideGravityDirection;
        [Tooltip("自訂的重力方向")]
        public Vector3 overrideGravityDirection = Vector3.down;

        public KPForceGravity() { space = KPForceSpace.WorldSpace; }

        internal override bool PreApply(in KPForceContext ctx, ref KPForce f)
        {
            base.PreApply(ctx, ref f);
            Vector3 dir = useOverrideGravityDirection ? overrideGravityDirection.normalized : Vector3.down;
            if (usePhysicsGravityScale) dir *= Physics.gravity.magnitude;
            f.Type = KPForceType.Velocity;
            f.Space = (int)KPForceSpace.WorldSpace;
            f.Vector = dir * randomizedScale;
            return true;
        }
    }

    /// <summary>曲線外力在一幀裡怎麼取值。</summary>
    public enum KPCurveEvaluateType
    {
        /// <summary>幀結束時的值</summary>
        Single,
        /// <summary>一幀內取樣的平均</summary>
        Average,
        /// <summary>一幀內取樣的最大值</summary>
        Max,
        /// <summary>一幀內取樣的最小值</summary>
        Min,
    }

    /// <summary>對應 FKawaiiPhysics_ExternalForce_Curve：隨時間變化的力（XYZ 三條曲線）。</summary>
    [Serializable]
    public class KPForceCurve : KPExternalForce
    {
        [Tooltip("X 方向的力隨時間的曲線")]
        public AnimationCurve forceX = new AnimationCurve();
        [Tooltip("Y 方向的力隨時間的曲線")]
        public AnimationCurve forceY = new AnimationCurve();
        [Tooltip("Z 方向的力隨時間的曲線")]
        public AnimationCurve forceZ = new AnimationCurve();
        [Tooltip("一幀裡怎麼取曲線的值")]
        public KPCurveEvaluateType curveEvaluateType = KPCurveEvaluateType.Single;
        [Tooltip("Average／Max／Min 時一幀取樣幾次")]
        [Min(1)] public int substepCount = 10;
        public float timeScale = 1f;
        [NonSerialized] float time;

        float MaxCurveTime() => Mathf.Max(LastKeyTime(forceX), Mathf.Max(LastKeyTime(forceY), LastKeyTime(forceZ)));

        static float LastKeyTime(AnimationCurve c) => c != null && c.length > 0 ? c[c.length - 1].time : 0f;

        Vector3 Sample(float t) => new Vector3(
            forceX != null && forceX.length > 0 ? forceX.Evaluate(t) : 0f,
            forceY != null && forceY.length > 0 ? forceY.Evaluate(t) : 0f,
            forceZ != null && forceZ.length > 0 ? forceZ.Evaluate(t) : 0f);

        internal override bool PreApply(in KPForceContext ctx, ref KPForce f)
        {
            base.PreApply(ctx, ref f);
            var maxT = MaxCurveTime();
            Vector3 force;
            if (curveEvaluateType == KPCurveEvaluateType.Single)
            {
                time += ctx.DeltaTime * timeScale;
                if (maxT > 0f && time > maxT) time %= maxT;
                force = Sample(time) * randomizedScale;
            }
            else
            {
                var steps = Mathf.Max(substepCount, 1);
                var sub = ctx.DeltaTime * timeScale / steps;
                force = curveEvaluateType == KPCurveEvaluateType.Average ? Vector3.zero
                    : curveEvaluateType == KPCurveEvaluateType.Max ? Vector3.one * float.MinValue : Vector3.one * float.MaxValue;
                for (int i = 0; i < steps; i++)
                {
                    time += sub;
                    if (maxT > 0f && time > maxT) time %= maxT;
                    var v = Sample(time);
                    if (curveEvaluateType == KPCurveEvaluateType.Average) force += v;
                    else if (curveEvaluateType == KPCurveEvaluateType.Max) force = Vector3.Max(force, v);
                    else force = Vector3.Min(force, v);
                }
                if (curveEvaluateType == KPCurveEvaluateType.Average) force /= steps;
                force *= randomizedScale;
            }
            f.Type = KPForceType.Position;
            f.Vector = force;
            return true;
        }
    }

    /// <summary>對應 FKawaiiPhysics_ExternalForce_Wind：場景的 WindZone 當成力（每根骨頭各自取風、加錐角雜訊）。</summary>
    [Serializable]
    public class KPForceWind : KPExternalForce
    {
        [Tooltip("風向隨機偏移的最大角度（度）")]
        [Min(0f)] public float windDirectionNoiseAngle;

        public KPForceWind() { space = KPForceSpace.WorldSpace; }

        internal override bool PreApply(in KPForceContext ctx, ref KPForce f)
        {
            base.PreApply(ctx, ref f);
            f.Type = KPForceType.SceneWind;
            f.Space = (int)KPForceSpace.WorldSpace;
            f.Scale = randomizedScale;
            f.NoiseAngle = windDirectionNoiseAngle;
            f.Seed = (int)(ctx.FrameSeed * 2654435761u);
            return true;
        }
    }

    /// <summary>程序風的參數與時鐘的來源。</summary>
    public enum KPWindSource
    {
        /// <summary>這個外力自己的參數與時鐘</summary>
        Local,
        /// <summary>共用頻道（KawaiiPhysicsWindChannel）</summary>
        Shared,
    }

    /// <summary>程序風的參數（ProceduralWind 與共用風頻道共用）。</summary>
    [Serializable]
    public class KPProceduralWindParams
    {
        [Tooltip("風向（世界空間）")]
        public Vector3 windDirection = Vector3.forward;
        [Tooltip("風向雜訊的最大角度（度）")]
        [Min(0f)] public float windDirectionNoiseAngle;
        [Min(0.01f)] public float windDirectionNoisePeriod = 1f;
        public float timeScale = 1f;
        [Tooltip("固定的風力")]
        public float constantForce;
        [Tooltip("搖擺（正弦）的強度")]
        public float swayForce;
        [Tooltip("搖擺的週期（秒）")]
        [Min(0.01f)] public float swayPeriod = 1f;
        public float swayPhaseOffset;
        [Tooltip("漣漪（沿骨長傳遞的波）的強度")]
        public float rippleForce;
        [Tooltip("漣漪的週期（秒）")]
        [Min(0.01f)] public float ripplePeriod = 1f;
        public float ripplePhaseOffset;
        [Tooltip("末端相對根的相位延遲（度）")]
        public float rippleTipPhaseDelay = 180f;
        [Tooltip("強度週期的範圍（最小、最大倍率）")]
        public Vector2 strengthCycleRange = Vector2.one;
        [Tooltip("強度週期（秒）")]
        [Min(0.01f)] public float strengthCyclePeriod = 10f;
        public float strengthCyclePhaseOffset;
        [Tooltip("平滑雜訊的強度")]
        public float randomForce;
        [Tooltip("雜訊的週期（秒）")]
        [Min(0.01f)] public float randomForcePeriod = 0.5f;
        public int seed;
    }

    /// <summary>程序風的時鐘與突風（對應 FKawaiiProceduralWindRuntimeState）。</summary>
    public class KPProceduralWindState
    {
        /// <summary>時鐘（秒，乘過 timeScale）。</summary>
        public float Time;
        /// <summary>沒乘 timeScale 的時鐘（秒）。</summary>
        public float UnscaledTime;
        /// <summary>突風進行中。</summary>
        public bool GustActive;
        /// <summary>突風用沒乘 timeScale 的時鐘。</summary>
        public bool GustRealTime;
        /// <summary>突風開始的時間（秒）。</summary>
        public float GustStart, GustStrength, GustRise, GustHold, GustDecay;
        int lastTickFrame = -1;

        /// <summary>時間 t 的突風強度。</summary>
        public float EvaluateGust(float t)
        {
            if (!GustActive) return 0f;
            var elapsed = (GustRealTime ? UnscaledTime : t) - GustStart;
            if (elapsed < 0f) return 0f;
            return GustStrength * KPMath.EnvelopeAlpha01(GustRise, GustHold, GustDecay, elapsed);
        }

        /// <summary>時鐘前進 dt 秒（乘上 timeScale）。</summary>
        public void Advance(float dt, float timeScale)
        {
            dt = Mathf.Max(dt, 0f);
            Time += dt * timeScale;
            UnscaledTime += dt;
        }

        /// <summary>共用頻道一幀只前進一次。</summary>
        public void AdvanceOncePerFrame(int frame, float dt, float timeScale)
        {
            if (lastTickFrame == frame) return;
            lastTickFrame = frame;
            Advance(dt, timeScale);
        }

        /// <summary>開始突風：strength 強度，rise／hold／decay 升起、維持、落下的秒數，realTime＝用沒乘 timeScale 的時鐘。</summary>
        public void StartGust(float strength, float rise, float decay, float hold, bool realTime)
        {
            GustRealTime = realTime;
            GustStart = realTime ? UnscaledTime : Time;
            GustStrength = strength;
            GustRise = rise;
            GustDecay = decay;
            GustHold = hold;
            GustActive = true;
        }

        /// <summary>原作 ConsumePendingRequests 的停止：目前強度在 blendOut 秒內落到 0。</summary>
        public void StopGust(float blendOut)
        {
            if (!GustActive) return;
            if (blendOut <= KPMath.KindaSmallNumber) { GustActive = false; return; }
            var current = EvaluateGust(Time);
            GustRealTime = false;
            GustStart = Time;
            GustStrength = current;
            GustRise = 0f;
            GustHold = 0f;
            GustDecay = blendOut;
            GustActive = true;
        }
    }

    /// <summary>
    /// 對應 FKawaiiPhysics_ExternalForce_ProceduralWind：常數＋搖擺（正弦）＋漣漪（沿骨長有相位差）＋強度週期＋平滑雜訊＋突風。
    /// windSource＝Shared 時，參數與時鐘來自共用的 KawaiiPhysicsWindChannel（原作的 Shared Publisher）。
    /// </summary>
    [Serializable]
    public class KPForceProceduralWind : KPExternalForce
    {
        [Tooltip("參數與時鐘的來源：這個外力自己（Local）或共用頻道（Shared）")]
        public KPWindSource windSource = KPWindSource.Local;
        [Tooltip("共用頻道（windSource＝Shared 時）")]
        public KawaiiPhysicsWindChannel sharedChannel;
        [Tooltip("這個外力自己的參數（windSource＝Local 時）")]
        public KPProceduralWindParams parameters = new KPProceduralWindParams();

        [NonSerialized] KPProceduralWindState localState;

        /// <summary>目前的時鐘與突風狀態（共用時是頻道的）。</summary>
        public KPProceduralWindState State => windSource == KPWindSource.Shared && sharedChannel != null
            ? sharedChannel.State
            : localState ??= new KPProceduralWindState();

        KPProceduralWindParams Params => windSource == KPWindSource.Shared && sharedChannel != null ? sharedChannel.parameters : parameters;

        /// <summary>觸發突風：strength 強度，rise／hold／decay 升起、維持、落下的秒數。</summary>
        public void RequestGust(float strength, float rise, float decay, float hold = 0f, bool realTime = false) =>
            State.StartGust(strength, rise, decay, hold, realTime);

        /// <summary>目前的突風在 blendOut 秒內落到 0。</summary>
        public void RequestGustStop(float blendOut) => State.StopGust(blendOut);

        /// <summary>原作 ComputeWindSample（lengthRate＝0 的部分；漣漪的骨頭相位在工作裡算）。</summary>
        public static void Sample(KPProceduralWindParams p, KPProceduralWindState s, out float sinesWithoutRipple,
            out float strengthCycle, out float random, out float gust)
        {
            const float twoPi = Mathf.PI * 2f;
            var t = s.Time;
            sinesWithoutRipple = p.constantForce +
                p.swayForce * Mathf.Sin(twoPi * t / Mathf.Max(p.swayPeriod, 0.01f) + p.swayPhaseOffset * Mathf.Deg2Rad);
            strengthCycle = Mathf.Lerp(p.strengthCycleRange.x, p.strengthCycleRange.y,
                0.5f * (1f + Mathf.Sin(twoPi * t / Mathf.Max(p.strengthCyclePeriod, 0.01f) + p.strengthCyclePhaseOffset * Mathf.Deg2Rad)));
            random = p.randomForce * KPMath.SampleSmoothNoise(t / Mathf.Max(p.randomForcePeriod, 0.01f), p.seed, 0);
            gust = s.EvaluateGust(t);
        }

        internal override bool PreApply(in KPForceContext ctx, ref KPForce f)
        {
            base.PreApply(ctx, ref f);
            var p = Params;
            var s = State;
            if (windSource == KPWindSource.Shared && sharedChannel != null)
                s.AdvanceOncePerFrame(KPSystem.FrameIndex, ctx.DeltaTime, p.timeScale);
            else
                s.Advance(ctx.DeltaTime, p.timeScale);

            Sample(p, s, out var sines, out var cycle, out var random, out var gust);
            var dir = p.windDirection.sqrMagnitude > 1e-8f ? p.windDirection.normalized : Vector3.forward;
            if (p.windDirectionNoiseAngle > 0f)
            {
                var u = s.Time / Mathf.Max(p.windDirectionNoisePeriod, 0.01f);
                dir = KPMath.ApplyConeNoise(dir, KPMath.SampleSmoothNoise(u, p.seed, 1), KPMath.SampleSmoothNoise(u, p.seed, 2),
                    p.windDirectionNoiseAngle);
            }
            f.Type = KPForceType.ProceduralWind;
            f.Vector = dir;
            f.SinesWithoutRipple = sines;
            f.StrengthCycle = cycle;
            f.RandomTerm = random;
            f.Gust = gust;
            f.Time = s.Time;
            f.RippleForce = p.rippleForce;
            f.RipplePeriod = p.ripplePeriod;
            f.RipplePhaseOffset = p.ripplePhaseOffset;
            f.RippleTipPhaseDelay = p.rippleTipPhaseDelay;
            return true;
        }

        /// <summary>複製一份（時鐘與突風狀態不共用）。</summary>
        public override KPExternalForce CloneForTransient()
        {
            var c = (KPForceProceduralWind)MemberwiseClone();
            c.localState = null;
            c.parameters = JsonUtility.FromJson<KPProceduralWindParams>(JsonUtility.ToJson(Params));
            c.windSource = KPWindSource.Local;
            c.sharedChannel = null;
            return c;
        }
    }
}
