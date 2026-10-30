// 可重用的資料資產：碰撞組（LimitsDataAsset）、骨間約束組（BoneConstraintsDataAsset）、設定預設（PresetDataAsset）、
// 共用程序風（Shared Publisher 的風）。都用骨頭名稱指定，套到不同角色時用名稱在階層裡找。
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>
    /// 共用的程序風（對應 Shared Publisher 發布的風）：參照同一個頻道的角色共用參數、時鐘與突風，一幀只前進一次。
    /// </summary>
    [CreateAssetMenu(menuName = "KawaiiPhysics/Wind Channel")]
    public class KawaiiPhysicsWindChannel : ScriptableObject
    {
        [Tooltip("程序風的參數（參照這個頻道的角色共用）")]
        public KPProceduralWindParams parameters = new KPProceduralWindParams();
        [NonSerialized] KPProceduralWindState state;

        /// <summary>共用的時鐘與突風狀態。</summary>
        public KPProceduralWindState State => state ??= new KPProceduralWindState();

        /// <summary>觸發突風（參照這個頻道的所有角色）：strength 強度，rise／hold／decay 升起、維持、落下的秒數。</summary>
        public void RequestGust(float strength, float rise, float decay, float hold = 0f, bool realTime = false) =>
            State.StartGust(strength, rise, decay, hold, realTime);

        /// <summary>目前的突風在 blendOut 秒內落到 0。</summary>
        public void RequestGustStop(float blendOut) => State.StopGust(blendOut);

        void OnEnable() => state = null;
    }
}
