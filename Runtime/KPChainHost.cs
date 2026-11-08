// 場景上的 KawaiiPhysicsChain 的主機（骨頭是 Transform）：元件清單、欄位複製、動畫前還原、TransformAccessArray 的讀寫。共用的部分在 KPChainCore。
// KPSystem 持有一個全域的主機（場景上的 KawaiiPhysicsChain，骨頭是 Transform），PlayerLoop 每幀呼叫它。
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace KawaiiPhysicsUnity
{
    internal sealed class KPChainHost : KPChainCore
    {
        /// <summary>骨頭的來源（場景上的 Transform）。</summary>
        internal readonly KPTransformHierarchy Hierarchy;

        public KPChainHost() : this(new KPTransformHierarchy()) { }
        KPChainHost(KPTransformHierarchy h) : base(h) => Hierarchy = h;

        protected override Vector3 ComponentPosition(int component) => Hierarchy.Get(component).position;
        protected override bool IsOwnCollider(Transform collider, int component) => collider.IsChildOf(Hierarchy.Get(component).root);

        readonly List<KawaiiPhysicsChain> chains = new List<KawaiiPhysicsChain>();
        bool membershipChanged;
        /// <summary>上次回收節點 id 之後的登記數（超過兩倍才再回收）。</summary>
        int hierarchyCountAfterFree;

        /// <summary>加入模擬。下一幀只建這一條，其他 Chain 不受影響。</summary>
        public void Register(KawaiiPhysicsChain chain)
        {
            if (!chains.Contains(chain)) chains.Add(chain);
            // SyncBone 的靜止位置在啟用時（動畫還沒動）記下來，對應原作的參考姿勢
            var comp = Hierarchy.Id(chain.ResolveComponent());
            foreach (var s in chain.syncBones)
            {
                if (s == null || s.bone == null) continue;
                var node = Hierarchy.Id(s.bone);
                if (!syncRestCache.ContainsKey(node)) syncRestCache.Add(node, Hierarchy.ComponentSpacePosition(comp, node));
            }
            membershipChanged = true;
        }

        /// <summary>移出模擬：當場移除這一條（骨頭放回靜止姿勢），並忘掉這些骨頭的靜止姿勢。</summary>
        public void Unregister(KawaiiPhysicsChain chain)
        {
            if (!chains.Remove(chain)) return;
            membershipChanged = true;
            var seg = chain.runtime?.Segment;
            if (world == null || seg == null || !world.Segments.Contains(seg)) return;
            // 等到下一幀才移除的話，呼叫端在那之前改的區域姿勢（換裝、體型）會被舊的靜止姿勢蓋掉
            Sync();
            if (seg.Rest == null) return;
            foreach (var (index, _, _) in seg.Rest)
            {
                var node = seg.Nodes[index];
                if (Hierarchy.IsAlive(node)) restCache.Remove(node);
            }
        }

        /// <summary>碰撞體在 Inspector 改了：所有 Chain 下一幀比對結構簽章，有用到它的才重建。</summary>
        public void RequestValidateAll()
        {
            foreach (var c in chains)
                if (c != null) c.validateRequested = true;
        }

        /// <summary>動畫更新前：需要時同步，骨頭放回靜止姿勢。</summary>
        public void Restore()
        {
            var needsSync = membershipChanged;
            foreach (var c in chains)
            {
                if (c == null)
                {
                    needsSync = true;
                    continue;
                }
                // Inspector 改了東西：結構簽章沒變就只更新設定，變了才重建這一條
                if (c.validateRequested)
                {
                    c.validateRequested = false;
                    c.CopyToSettings(Hierarchy, true);
                    var seg = c.runtime?.Segment;
                    if (seg == null || seg.Signature != KPWorld.ComputeSignature(c.settings)) c.dirty = true;
                    else c.settingsDirty = true;
                }
                needsSync |= c.dirty;
            }
            if (needsSync) Sync();
            if (world == null || world.InstanceCount == 0) return;
            new KPRestoreJob { Rest = world.Rest }.Schedule(world.Transforms).Complete();
        }

        /// <summary>準備 → 讀 → 解 →（世界碰撞）→ 寫，做完才回來。</summary>
        public void Simulate(float deltaTime)
        {
            // 元件的欄位複製進設定類別（每幀的值；結構在同步時換算）
            foreach (var c in chains)
            {
                if (c != null) c.CopyToSettings(Hierarchy, false);
                else if (!ReferenceEquals(c, null)) c.settings.skipFrame = true; // 刪掉了、下次動畫前才同步：這一幀照舊
            }
            PrepareFrame(deltaTime);
            // 流程消耗掉的旗標寫回元件
            foreach (var c in chains)
                if (c != null) c.needWarmUp = c.settings.needWarmUp;
            // 指標要在排程之前取：讀取工作排下去之後再對 Reads 呼叫 GetUnsafePtr 會被安全檢查擋下
            var ptrs = world.Pointers();
            var read = new KPReadJob { Reads = world.Reads }.ScheduleReadOnly(world.Transforms, 32);
            var handle = ScheduleSolve(deltaTime, read, ptrs);
            handle = ScheduleWorldCollision(handle, ptrs);
            new KPWriteJob { Writes = world.Writes }.Schedule(world.Transforms, handle).Complete();
        }

        // ===================================================================
        // 建立與清理
        // ===================================================================

        void Sync()
        {
            membershipChanged = false;
            chains.RemoveAll(c => c == null);
            settingsList.Clear();
            foreach (var c in chains)
            {
                c.CopyToSettings(Hierarchy, true);
                settingsList.Add(c.settings);
            }
            SyncWorld(settingsList);
            settingsList.Clear();
            foreach (var c in chains)
            {
                c.dirty = false;
                c.needWarmUp = c.settings.needWarmUp; // 建立時消耗的暖機旗標寫回元件
            }
            // 靜止姿勢快取：停用後才刪除的角色，骨頭不會在移除時被清掉 → 快取超過使用中的兩倍時清一次（攤提成本）
            var limit = 2 * Mathf.Max(256, world.UsedTransforms);
            if (restCache.Count > limit) PruneDestroyed(restCache);
            if (syncRestCache.Count > limit) PruneDestroyed(syncRestCache);
            if (Hierarchy.Count > 2 * Mathf.Max(256, hierarchyCountAfterFree)) FreeDeadNodes();
        }

        readonly List<int> pruneKeys = new List<int>();
        readonly List<KPRigChain> settingsList = new List<KPRigChain>();

        void PruneDestroyed<T>(Dictionary<int, T> cache)
        {
            pruneKeys.Clear();
            foreach (var node in cache.Keys)
                if (!Hierarchy.IsAlive(node)) pruneKeys.Add(node);
            foreach (var node in pruneKeys) cache.Remove(node);
            pruneKeys.Clear();
        }

        readonly HashSet<int> referencedNodes = new HashSet<int>();

        /// <summary>被刪掉的 Transform 的 id 回收（角色反覆生成、刪除時登記表不會一直長）。段還在用的不回收。</summary>
        void FreeDeadNodes()
        {
            PruneDestroyed(restCache);
            PruneDestroyed(syncRestCache);
            referencedNodes.Clear();
            foreach (var s in world.Segments)
            {
                foreach (var node in s.Nodes) referencedNodes.Add(node);
                referencedNodes.Add(s.Family.root);
            }
            Hierarchy.FreeDead(referencedNodes);
            referencedNodes.Clear();
            hierarchyCountAfterFree = Hierarchy.Count;
        }

        /// <summary>全部清掉並釋放原生記憶體（亂數種子不重設）。</summary>
        public void Shutdown()
        {
            DisposeCore();
            chains.Clear();
            Hierarchy.Clear();
            hierarchyCountAfterFree = 0;
            membershipChanged = false;
        }
    }
}
