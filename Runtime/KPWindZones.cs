// 場景風（WindZone）的收集：所有主機共用一份清單（搜尋場景會配置記憶體，只在場景或 Chain 有變動時做），
// 每個主機用自己的模擬時間算脈動，填進自己的陣列。
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    internal static class KPWindZones
    {
        static readonly List<WindZone> zones = new List<WindZone>();
        static bool dirty = true;

        /// <summary>下一次 Fill 前重新收集場景的 WindZone。</summary>
        public static void MarkDirty() => dirty = true;

        /// <summary>收集中的 WindZone 數（含停用的）。</summary>
        public static int Count => zones.Count;

        /// <summary>需要時重新收集（穩態每幀 0 配置）。</summary>
        public static void Refresh()
        {
            if (!dirty) return;
            dirty = false;
            zones.Clear();
            // 停用中的也收：之後啟用時每幀的 activeInHierarchy 檢查會把它算進來
            zones.AddRange(Object.FindObjectsByType<WindZone>(FindObjectsInactive.Include));
        }

        /// <summary>啟用中的 WindZone 填進 dst（主風＋脈動當成風速，脈動用 simTime），回傳筆數。</summary>
        public static int Fill(NativeArray<KPWindZone> dst, float simTime)
        {
            Refresh();
            var n = 0;
            foreach (var z in zones)
            {
                if (z == null || !z.gameObject.activeInHierarchy || n >= dst.Length) continue;
                var pulse = 1f + z.windPulseMagnitude * Mathf.Sin(simTime * z.windPulseFrequency * Mathf.PI * 2f);
                dst[n++] = new KPWindZone
                {
                    Position = z.transform.position,
                    Direction = z.transform.forward,
                    Radius = z.radius,
                    Spherical = z.mode == WindZoneMode.Spherical ? 1 : 0,
                    Speed = z.windMain * pulse,
                };
            }
            return n;
        }

        public static void Clear()
        {
            zones.Clear();
            dirty = true;
        }
    }
}
