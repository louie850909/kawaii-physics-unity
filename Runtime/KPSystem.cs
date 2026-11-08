using System;
using System.Collections.Generic;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.SceneManagement;

namespace KawaiiPhysicsUnity
{
    /// <summary>解算的執行方式。</summary>
    public enum KPBackend
    {
        /// <summary>Burst，一個角色一個工作，丟給工作執行緒平行跑（預設）。</summary>
        BurstParallel,
        /// <summary>Burst，在主執行緒依序跑。</summary>
        BurstSingleThread,
        /// <summary>不經 Burst、直接呼叫 C# 解算（可以下中斷點除錯）。</summary>
        Managed,
    }

    /// <summary>
    /// 把所有 KawaiiPhysicsChain 打包成一組 NativeArray，每幀：
    /// Update 開頭（動畫前）把骨頭放回靜止姿勢；所有 LateUpdate 之後 準備外力等 → 讀 → 解 →（世界碰撞）→ 寫。
    /// 兩個時間點都用 PlayerLoop 插入，不用 MonoBehaviour。
    /// Chain 加入、移除或結構變更時只重建那一條（其他 Chain 的模擬狀態保留）；只改設定時就地更新，不重建。
    /// 軟組織（KawaiiPhysicsSoftBody）也在同樣兩個時間點處理，模擬在 Chain 之前。
    /// </summary>
    public static class KPSystem
    {
        /// <summary>解算的執行方式（預設 Burst 平行）。</summary>
        public static KPBackend Backend = KPBackend.BurstParallel;

        /// <summary>場景上的 KawaiiPhysicsChain 的主機（骨頭是 Transform）。</summary>
        static readonly KPChainHost host = new KPChainHost();

        internal static KPChainHost ChainHost => host;

        /// <summary>SimulatePhase 的呼叫次數與累計時間（編輯模式下 Time.frameCount 不會前進）。</summary>
        public static int FrameIndex => host.FrameIndex;
        /// <summary>累計的模擬時間（秒）。</summary>
        public static float SimTime => host.SimTime;

        internal static KPWorld World => host.World;

        /// <summary>模擬中的 Chain 數。</summary>
        public static int ChainCount => World?.InstanceCount ?? 0;

        /// <summary>模擬中的骨頭數（含末端、細分、橋接的假骨）。</summary>
        public static int BoneCount
        {
            get
            {
                var world = World;
                if (world == null) return 0;
                var n = 0;
                foreach (var s in world.Segments) n += s.Size.Bone;
                return n;
            }
        }

        /// <summary>上一次 Chain 加入、移除或重建結構時，同步花的時間（毫秒）。</summary>
        public static double LastSyncMs => World?.LastSyncMs ?? 0;

        /// <summary>物理寫回 Transform 之後（同一幀、所有 LateUpdate 之後）。要拿物理後骨頭的處理（鏡頭跟隨、截圖）掛在這裡。</summary>
        public static event Action AfterSimulate;

        /// <summary>上一幀 SimulatePhase 在主執行緒花的時間（毫秒，含等待工作完成；含軟組織）。</summary>
        public static double LastSimulateMs { get; private set; }

        static readonly System.Diagnostics.Stopwatch simulateWatch = new System.Diagnostics.Stopwatch();

        /// <summary>加入模擬（KawaiiPhysicsChain.OnEnable 會呼叫）。下一幀只建這一條，其他 Chain 不受影響。</summary>
        public static void Register(KawaiiPhysicsChain chain) => host.Register(chain);

        /// <summary>
        /// 移出模擬（KawaiiPhysicsChain.OnDisable 會呼叫）。當場移除這一條：骨頭立刻放回靜止姿勢，這一幀之後的解算也不再寫回；
        /// 並忘掉這些骨頭的靜止姿勢，之後再有 Chain 用到同一根骨頭（重新啟用、換裝後的新 Chain）時，從那時候的姿勢重新記。
        /// </summary>
        public static void Unregister(KawaiiPhysicsChain chain) => host.Unregister(chain);

        /// <summary>
        /// 軟組織加入模擬（KawaiiPhysicsSoftBody.OnEnable 會呼叫）。骨頭的靜止姿勢在這時記下來（動畫還沒動）。
        /// 下一幀重建軟組織，其他軟組織元件的模擬狀態沿用。
        /// </summary>
        public static void Register(KawaiiPhysicsSoftBody softBody) => KPSoftBodySystem.Register(softBody);

        /// <summary>軟組織移出模擬（KawaiiPhysicsSoftBody.OnDisable 會呼叫）。下一幀動畫前骨頭放回靜止姿勢。</summary>
        public static void Unregister(KawaiiPhysicsSoftBody softBody) => KPSoftBodySystem.Unregister(softBody);

        /// <summary>模擬中的軟組織元件數。</summary>
        public static int SoftBodyCount => KPSoftBodySystem.BodyCount;

        /// <summary>模擬中的軟組織骨頭數。</summary>
        public static int SoftBoneCount => KPSoftBodySystem.BoneCount;

        /// <summary>
        /// 場景的 WindZone 重新收集。場景載入／卸載、Chain 加入或移除時會自動做；
        /// 執行中用程式新增 WindZone 時呼叫這個（刪除的 WindZone 每幀會自動略過）。
        /// </summary>
        public static void RefreshWindZones() => KPWindZones.MarkDirty();

        /// <summary>碰撞體在 Inspector 改了：所有 Chain 下一幀比對結構簽章，有用到它的才重建。</summary>
        internal static void RequestValidateAll() => host.RequestValidateAll();

        /// <summary>動畫更新前呼叫。</summary>
        public static void RestorePhase()
        {
            KPSoftBodySystem.RestorePhase();
            host.Restore();
        }

        /// <summary>所有 LateUpdate 之後呼叫。軟組織先解（寫回之後 Chain 才讀，掛在軟組織骨頭底下的 Chain 會跟著動）。</summary>
        public static void SimulatePhase(float deltaTime)
        {
            var hasChains = host.HasChains;
            if (!hasChains && KPSoftBodySystem.World == null) return;
            simulateWatch.Restart();
            KPSoftBodySystem.SimulatePhase(deltaTime);
            if (hasChains) host.Simulate(deltaTime);
            LastSimulateMs = simulateWatch.Elapsed.TotalMilliseconds;
            AfterSimulate?.Invoke();
        }

        /// <summary>沒有其他工作在跑時用（會自己取指標）。</summary>
        internal static JobHandle ScheduleSolve(float deltaTime, JobHandle dependsOn) => host.ScheduleSolve(deltaTime, dependsOn, World.Pointers());

        /// <summary>靜止姿勢快取的筆數（測試用）。</summary>
        public static int RestCacheCount => host.RestCacheCount;

        /// <summary>全部清掉並釋放原生記憶體（離開 Play、程式結束、重新編譯腳本時自動呼叫）。</summary>
        public static void Shutdown()
        {
            KPSoftBodySystem.Shutdown();
            host.Shutdown();
            KPWindZones.Clear();
        }

        // ===================================================================
        // PlayerLoop
        // ===================================================================

        struct KPRestore { }
        struct KPSimulate { }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void InstallPlayerLoop()
        {
            // Domain Reload 關閉時靜態值會留到下一次 Play，這裡先清掉
            Shutdown();
            AfterSimulate = null;
            Application.quitting -= Shutdown;
            Application.quitting += Shutdown;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
#if UNITY_EDITOR
            // Play 中重新編譯腳本：Domain 卸載前先釋放原生陣列
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
#endif

            var loop = PlayerLoop.GetCurrentPlayerLoop();
            RemoveSystems(ref loop);
            Insert(ref loop, typeof(Update), typeof(Update.ScriptRunBehaviourUpdate), false,
                new PlayerLoopSystem { type = typeof(KPRestore), updateDelegate = RestorePhase });
            Insert(ref loop, typeof(PreLateUpdate), typeof(PreLateUpdate.ScriptRunBehaviourLateUpdate), true,
                new PlayerLoopSystem { type = typeof(KPSimulate), updateDelegate = () => SimulatePhase(Time.deltaTime) });
            PlayerLoop.SetPlayerLoop(loop);
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => KPWindZones.MarkDirty();
        static void OnSceneUnloaded(Scene scene) => KPWindZones.MarkDirty();

        static void Insert(ref PlayerLoopSystem loop, Type phase, Type anchor, bool after, PlayerLoopSystem system)
        {
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != phase) continue;
                var subs = new List<PlayerLoopSystem>(loop.subSystemList[i].subSystemList);
                var at = subs.FindIndex(s => s.type == anchor);
                subs.Insert(at < 0 ? (after ? subs.Count : 0) : (after ? at + 1 : at), system);
                loop.subSystemList[i].subSystemList = subs.ToArray();
                return;
            }
        }

        static void RemoveSystems(ref PlayerLoopSystem loop)
        {
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                var subs = loop.subSystemList[i].subSystemList;
                if (subs == null) continue;
                var list = new List<PlayerLoopSystem>(subs);
                list.RemoveAll(s => s.type == typeof(KPRestore) || s.type == typeof(KPSimulate));
                loop.subSystemList[i].subSystemList = list.ToArray();
            }
        }
    }
}
