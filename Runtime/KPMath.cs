// 解算用的數學輔助：照 UE 的定義寫，讓邊界行為（零向量、反向、近似平行）跟原作一致。
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

namespace KawaiiPhysicsUnity
{
    internal static unsafe class KPMath
    {
        public const float KindaSmallNumber = 1e-4f;
        public const float SmallNumber = 1e-8f;

        /// <summary>FVector::GetSafeNormal。長度平方小於 1e-8 回傳零向量。</summary>
        public static float3 SafeNormal(float3 v)
        {
            var lsq = lengthsq(v);
            if (lsq == 1f) return v;
            if (lsq < SmallNumber) return new float3(0f);
            return v * rsqrt(lsq);
        }

        public static bool IsNearlyZero(float3 v) => all(abs(v) <= KindaSmallNumber);

        /// <summary>FQuat::FindBetweenVectors（輸入不必正規化）。</summary>
        public static quaternion FindBetweenVectors(float3 a, float3 b)
        {
            var normAB = sqrt(lengthsq(a) * lengthsq(b));
            var w = normAB + dot(a, b);
            float4 q;
            if (w >= 1e-6f * normAB)
                q = new float4(cross(a, b), w);
            else
                q = abs(a.x) > abs(a.y) ? new float4(-a.z, 0f, a.x, 0f) : new float4(0f, -a.z, a.y, 0f);
            return new quaternion(normalize(q));
        }

        /// <summary>FVector::RotateAngleAxis（度；軸要先正規化）。</summary>
        public static float3 RotateAngleAxisDeg(float3 v, float angleDeg, float3 axis)
        {
            sincos(radians(angleDeg), out var s, out var c);
            return v * c + cross(axis, v) * s + axis * (dot(axis, v) * (1f - c));
        }

        /// <summary>FMath::ClosestPointOnSegment。</summary>
        public static float3 ClosestPointOnSegment(float3 p, float3 start, float3 end)
        {
            var seg = end - start;
            var dot1 = dot(p - start, seg);
            if (dot1 <= 0f) return start;
            var dot2 = dot(seg, seg);
            if (dot2 <= dot1) return end;
            return start + seg * (dot1 / dot2);
        }

        /// <summary>FQuat::Slerp（Slerp_NotNormalized 之後正規化）。</summary>
        public static quaternion SlerpUE(quaternion q1, quaternion q2, float t)
        {
            var rawCosom = dot(q1.value, q2.value);
            var cosom = abs(rawCosom);
            float scale0, scale1;
            if (cosom < 0.9999f)
            {
                var omega = acos(cosom);
                var invSin = 1f / sin(omega);
                scale0 = sin((1f - t) * omega) * invSin;
                scale1 = sin(t * omega) * invSin;
            }
            else
            {
                scale0 = 1f - t;
                scale1 = t;
            }
            scale1 = rawCosom >= 0f ? scale1 : -scale1;
            return new quaternion(normalize(scale0 * q1.value + scale1 * q2.value));
        }

        /// <summary>FVector::FindBestAxisVectors。</summary>
        public static void FindBestAxisVectors(float3 v, out float3 axis1, out float3 axis2)
        {
            var n = abs(v);
            axis1 = n.z > n.x && n.z > n.y ? new float3(1f, 0f, 0f) : new float3(0f, 0f, 1f);
            axis1 = SafeNormal(axis1 - v * dot(axis1, v));
            axis2 = cross(axis1, v);
        }

        public static float3 ForwardAxis(int axis)
        {
            switch (axis)
            {
                case 0: return new float3(1f, 0f, 0f);
                case 1: return new float3(-1f, 0f, 0f);
                case 2: return new float3(0f, 1f, 0f);
                case 3: return new float3(0f, -1f, 0f);
                case 4: return new float3(0f, 0f, 1f);
                default: return new float3(0f, 0f, -1f);
            }
        }

        /// <summary>KawaiiPhysics::EvaluateEnvelopeAlpha01（起、持、落的梯形包絡，0..1）。</summary>
        public static float EnvelopeAlpha01(float rise, float hold, float decay, float elapsed)
        {
            if (elapsed < 0f) return 0f;
            rise = max(rise, 0f);
            hold = max(hold, 0f);
            decay = max(decay, 0f);
            if (rise > KindaSmallNumber && elapsed < rise) return elapsed / rise;
            if (elapsed < rise + hold) return 1f;
            if (decay <= KindaSmallNumber) return 0f;
            var decayElapsed = elapsed - rise - hold;
            return decayElapsed < decay ? 1f - decayElapsed / decay : 0f;
        }

        /// <summary>KawaiiPhysics::ResolveWindGustEnvelope：總長固定時，把起與落壓進總長，剩下的是持續。</summary>
        public static void ResolveEnvelope(float duration, float rise, float decay, out float outRise, out float outHold, out float outDecay)
        {
            outRise = outHold = outDecay = 0f;
            if (duration <= 0f) return;
            outRise = max(0f, rise);
            outDecay = max(0f, decay);
            var edge = outRise + outDecay;
            if (edge > duration && edge > 0f)
            {
                var s = duration / edge;
                outRise *= s;
                outDecay *= s;
                return;
            }
            outHold = max(0f, duration - edge);
        }

        // ===== ProceduralWind 的雜訊（照原作的雜湊與 FRandomStream，讓同一個種子得到同樣的風） =====

        /// <summary>FKawaiiPhysics_ExternalForce_ProceduralWind::ComputeStableHash。</summary>
        public static uint StableHash(int seed, int gridIndex, int channel)
        {
            uint h = 0x811C9DC5u;
            h ^= (uint)seed; h *= 0x01000193u;
            h ^= (uint)gridIndex; h *= 0x01000193u;
            h ^= (uint)channel; h *= 0x01000193u;
            h ^= h >> 16; h *= 0x7FEB352Du;
            h ^= h >> 15; h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }

        /// <summary>FRandomStream(seed).FRandRange(-1, 1)：先 MutateSeed，再取 23 位元的小數。</summary>
        public static float SampleNoiseAt(int gridIndex, int seed, int channel)
        {
            var s = StableHash(seed, gridIndex, channel);
            s = s * 196314165u + 907633515u;
            var bits = 0x3F800000u | (s >> 9);
            var frac = asfloat(bits) - 1f;
            return -1f + 2f * frac;
        }

        /// <summary>FKawaiiPhysics_ExternalForce_ProceduralWind::SampleSmoothNoise（格點之間 smoothstep 內插）。</summary>
        public static float SampleSmoothNoise(float u, int seed, int channel)
        {
            var gridFloat = floor(u);
            var gridIndex = (int)gridFloat;
            var a = u - gridFloat;
            var smooth = a * a * (3f - 2f * a);
            return lerp(SampleNoiseAt(gridIndex, seed, channel), SampleNoiseAt(gridIndex + 1, seed, channel), smooth);
        }

        /// <summary>程序風的漣漪：沿骨長（lengthRate 0＝根、1＝末端）延遲相位的正弦。</summary>
        public static float ProceduralRipple(float rippleForce, float period, float phaseOffsetDeg, float tipDelayDeg, float time, float lengthRate) =>
            rippleForce * sin(2f * PI * time / max(period, 0.01f) - radians(lengthRate * tipDelayDeg) + radians(phaseOffsetDeg));

        /// <summary>ApplyConeNoiseToDirection：用兩個 [-1,1] 雜訊把方向偏到錐角內。</summary>
        public static float3 ApplyConeNoise(float3 dir, float noiseX, float noiseY, float halfAngleDeg)
        {
            var rad = radians(max(halfAngleDeg, 0f));
            var baseDir = SafeNormal(dir);
            if (IsNearlyZero(baseDir)) baseDir = new float3(1f, 0f, 0f);
            if (rad <= KindaSmallNumber) return baseDir;
            var disk = new float2(noiseX, noiseY);
            var len = length(disk);
            if (len <= KindaSmallNumber) return baseDir;
            if (len > 1f) disk /= len;
            FindBestAxisVectors(baseDir, out var ax, out var ay);
            var offset = SafeNormal(ax * disk.x + ay * disk.y);
            var angle = rad * min(len, 1f);
            return SafeNormal(baseDir * cos(angle) + offset * sin(angle));
        }

        /// <summary>FMath::VRandCone 的同等做法：錐角內的隨機方向。</summary>
        public static float3 RandomInCone(ref Unity.Mathematics.Random rng, float3 dir, float halfAngleRad)
        {
            if (halfAngleRad <= 0f) return dir;
            var theta = 2f * PI * rng.NextFloat();
            var phi = acos(2f * rng.NextFloat() - 1f) % halfAngleRad;
            FindBestAxisVectors(SafeNormal(dir), out var ax, out _);
            var r = RotateAngleAxisDeg(dir, degrees(phi), ax);
            return SafeNormal(RotateAngleAxisDeg(r, degrees(theta), SafeNormal(dir)));
        }

        // ===== 曲線查表（AnimationCurve 不能進 Burst，先取樣成等距表） =====

        public const int LutSamples = 33;

        /// <summary>查 Luts[lut .. lut+LutSamples)，t 在 [0, domain]。lut &lt; 0 回傳 fallback。</summary>
        public static float EvalLut(float* luts, int lut, float t, float domain, float fallback)
        {
            if (lut < 0) return fallback;
            var x = saturate(domain > 0f ? t / domain : 0f) * (LutSamples - 1);
            var i = min((int)x, LutSamples - 2);
            return lerp(luts[lut + i], luts[lut + i + 1], x - i);
        }
    }
}
