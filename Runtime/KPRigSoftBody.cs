// 陣列接口的軟組織設定：欄位名稱與預設值跟 KawaiiPhysicsSoftBody 一樣，只有骨頭參照改成骨架的索引（int，-1＝沒有）。
// KawaiiPhysicsSoftBody 每幀把設定值複製進自己持有的一份，交給同一套每幀的換算。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KawaiiPhysicsUnity
{
    /// <summary>
    /// 一組軟組織骨頭的設定（陣列接口用）。欄位與 KawaiiPhysicsSoftBody 相同（說明見那邊），骨頭參照是骨架的索引（-1＝沒有）。
    /// </summary>
    [Serializable]
    public class KPRigSoftBody
    {
        [Header("Bones")]
        [Tooltip("要模擬的骨頭，各自是一個彈簧。祖先也是軟組織骨頭時（不論是不是同一個元件），子骨的錨點跟著祖先的位移與伸縮，也感受祖先晃動的慣性")]
        public List<int> bones = new List<int>();
        [Tooltip("角色根（慣性拆成角色移動與動畫、重力參考用）；空的話用上層的 Animator，再沒有就用自己。站立時 +Y 要朝上")]
        public int referenceRoot = -1;

        [Header("Spring")]
        [Tooltip("振動頻率（Hz）：無阻尼時每秒來回幾次。越大越硬")]
        [Min(0.01f)] public float frequency = 3f;
        [Tooltip("阻尼比：0＝不衰減、1＝臨界阻尼（不回彈）、大於 1＝過阻尼")]
        [Min(0f)] public float dampingRatio = 0.25f;
        [Tooltip("重力加速度（世界空間，公尺/秒²）。平衡時的下垂量＝重力 ÷ (2π×頻率)²")]
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);
        [Tooltip("重力相對靜止姿勢：站立時的形狀已經包含重力，只有姿勢偏離站立（躺、趴、倒立）時才往重力方向偏")]
        public bool gravityRelativeToRestPose = true;
        [Tooltip("額外的加速度（世界空間，公尺/秒²），應用端可以每幀改")]
        public Vector3 externalAcceleration;

        [Header("Inertia")]
        [Tooltip("動畫造成的錨點加減速的慣性倍率（0＝跟著動畫走、不晃；1＝真實慣性）。祖先軟組織骨頭的晃動不受這個影響，一律是真實慣性")]
        [Range(0f, 2f)] public float animationInertia = 1f;
        [Tooltip("角色根移動、轉向造成的慣性倍率（0＝跟著角色走、不晃；1＝真實慣性）")]
        [Range(0f, 2f)] public float movementInertia = 1f;

        [Header("Limit（骨頭區域軸向，公尺）")]
        [Tooltip("各軸往負方向最多離開錨點多遠（≤0；0＝鎖住這個方向）")]
        public Vector3 limitMin = new Vector3(-0.05f, -0.05f, -0.05f);
        [Tooltip("各軸往正方向最多離開錨點多遠（≥0；0＝鎖住這個方向）")]
        public Vector3 limitMax = new Vector3(0.05f, 0.05f, 0.05f);

        [Header("Shape")]
        [Tooltip("依位移傾斜：骨頭朝『父骨 → 位移後的位置』轉動的比例（0＝只平移）")]
        [Range(0f, 1f)] public float tilt;
        [Tooltip("壓扁與拉伸的軸（骨頭區域）")]
        public KawaiiPhysicsSoftBody.Axis stretchAxis = KawaiiPhysicsSoftBody.Axis.Y;
        [Tooltip("沿軸位移到上限時伸長的比例（負值＝反向；0＝不縮放）。子骨頭跟著縮放（子軟組織骨頭的位置會跟著伸縮移動），建議只用在末端骨頭")]
        [Range(-1f, 1f)] public float stretchAmount;
        [Tooltip("維持體積：沿軸伸長時另外兩軸縮小的程度（1＝體積不變）")]
        [Range(0f, 1f)] public float volumePreservation = 1f;

        [Header("Collision")]
        [Tooltip("骨頭的碰撞半徑（公尺）")]
        [Min(0f)] public float radius = 0.03f;
        [Tooltip("碰撞體（球、膠囊、錐形膠囊、箱、平面；凸包不支援）。清單每幀讀取，增減不用重建")]
        public List<KPRigCollider> colliders = new List<KPRigCollider>();
        [Tooltip("碰撞優先：碰撞體壓過來時骨頭可以被推到位移上限之外（不穿透）。關掉時位移上限優先（碰撞體可能穿進骨頭）")]
        public bool collisionPriority = true;

        [Header("Simulation")]
        [Tooltip("物理結果的混合比例（0＝完全照動畫）。它是動畫與物理的混合比例、不是力：改了（一次跳或漸變）時子孫軟組織骨頭不把因此的移動當成加速；祖先真的晃動照常傳下去")]
        [Range(0f, 1f)] public float alpha = 1f;
        [Tooltip("子步進的步頻（每秒步數）。彈簧本身是解析解，子步進只影響碰撞與位移上限的精度")]
        [Min(1)] public int targetFramerate = 60;
        [Tooltip("每幀最多幾個子步進")]
        [Range(1, 16)] public int maxSubsteps = 4;
        [Tooltip("角色根一幀移動超過這個距離（公尺）就當成瞬移、重置（0＝不判定）")]
        [Min(0f)] public float teleportDistanceThreshold = 3f;
        [Tooltip("角色根一幀轉動超過這個角度（度）就當成瞬移、重置（0＝不判定）")]
        [Min(0f)] public float teleportRotationThreshold = 90f;

        [Tooltip("模擬中（停用時骨頭維持動畫姿勢；元件版對應 isActiveAndEnabled）")]
        public bool enabled = true;
    }
}
