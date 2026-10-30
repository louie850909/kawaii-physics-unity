// 軟組織（KawaiiPhysicsSoftBody）的原生資料。Chain 的資料（KPTypes）不共用：兩邊的骨頭模型不同
//（Chain 是保持骨長、只轉動的鐘擺；軟組織是以動畫姿勢為錨點、可以平移的阻尼彈簧）。
using Unity.Mathematics;

namespace KawaiiPhysicsUnity
{
    /// <summary>一個 KawaiiPhysicsSoftBody 元件（設定每幀從元件複製；狀態跨幀保留）。</summary>
    internal struct KPSoftBodyData
    {
        public int BoneStart;
        public int BoneCount;
        /// <summary>角色根在 TransformAccessArray 的索引。</summary>
        public int RootIndex;
        public int ColliderStart;
        public int ColliderCount;

        // ---- 設定 ----
        public int Enabled;
        public float Alpha;
        /// <summary>彈簧的角頻率（2π × 頻率）。</summary>
        public float Omega;
        public float DampingRatio;
        public float3 Gravity;
        public int GravityRelativeToRest;
        public float3 ExternalAcceleration;
        public float AnimationInertia;
        public float MovementInertia;
        public float3 LimitMin;
        public float3 LimitMax;
        public int CollisionPriority;
        public float Tilt;
        public int StretchAxis;
        public float StretchAmount;
        public float VolumePreservation;
        public float Radius;
        public float TargetFramerate;
        public int MaxSubsteps;
        public float TeleportDistance;
        /// <summary>瞬移判定的旋轉角（弧度，0＝不判定）。</summary>
        public float TeleportAngle;
        public int ResetRequested;

        // ---- 狀態 ----
        public float3 PrevRootPosition;
        /// <summary>上一幀角色根的軸向（世界矩陣正規化的欄）。</summary>
        public float3x3 PrevRootFrame;
        public int RootInitialized;
        /// <summary>上一次解算的 dt（重建時換祖先，用來算祖先晃動的速度）。</summary>
        public float LastDeltaTime;
        /// <summary>上一幀的輸出設定（改了的話，子孫用上一幀的設定量祖先的速度）。</summary>
        public float PrevAlpha;
        public float PrevTilt;
        public int PrevStretchAxis;
        public float PrevStretchAmount;
        public float PrevVolumePreservation;
        public float3 PrevLimitMin;
        public float3 PrevLimitMax;

        // ---- 這一幀的暫存（解算群組時先算好，骨頭共用）----
        public int FrameSkip;
        public int FrameReset;
        /// <summary>alpha、傾斜、伸縮這一幀改了。</summary>
        public int FrameOutputChanged;
        /// <summary>位移上限這一幀改了。</summary>
        public int FrameLimitChanged;
        public float3 RootPosition;
        public float3x3 RootFrame;
        public quaternion MoveRotation;
        public int Steps;
        public float Pp, Pv, Vp, Vv;
        public float CarryAnim;
        public float CarryMove;
    }

    /// <summary>一根軟組織骨頭。位移、速度都是世界空間，相對於錨點（這一幀動畫後的姿勢＋祖先軟組織骨頭的位移）。</summary>
    internal struct KPSoftBone
    {
        public int TransformIndex;
        /// <summary>所屬元件（KPSoftWorld.Bodies 的索引）。</summary>
        public int Body;
        /// <summary>最近的祖先軟組織骨頭（世界的骨頭索引，可以在別的元件），沒有＝-1。同一個群組裡祖先一定先解。</summary>
        public int ParentSoft;
        /// <summary>靜止姿勢時父骨相對角色根的軸向（Fᵣᵀ·Fₚ，重力相對靜止姿勢時用；鏡像時行列式為負）。</summary>
        public float3x3 RestParentInRoot;
        /// <summary>重力參考還沒記（啟用時矩陣退化，例如縮放 0）：第一次能用的幀再記。</summary>
        public int RestPending;

        // ---- 狀態 ----
        /// <summary>0＝下一次解算時初始化；1＝剛初始化，下一個 dt＞0 的幀只記錄錨點速度、不當成加速；2＝正常。</summary>
        public int Initialized;
        /// <summary>重建時沒沿用狀態、但上一幀還在模擬（同一幀停用再啟用、換角色根、搬到別的元件）：第一次解算當成重置，不是新加入。</summary>
        public int WasRunning;
        /// <summary>祖先晃動那一份的基準：0＝這一幀重新取（祖先重置、略過、上限改了）、1＝基準有了（下一個 dt＞0 的幀只記錄速度）、2＝正常。</summary>
        public int JiggleStage;
        public float3 Offset;
        public float3 Velocity;
        /// <summary>上一幀動畫後的姿勢（不含祖先的位移）。</summary>
        public float3 PrevBase;
        public float3 PrevBaseInRoot;
        public float3x3 PrevBaseFrameInRoot;
        /// <summary>上一幀祖先造成的錨點位移。</summary>
        public float3 PrevJiggle;
        public float3 PrevMoveVelocity;
        public float3 PrevAnimVelocity;
        public float3 PrevJiggleVelocity;
        /// <summary>主執行緒累積的速度變化（衝量），下一次解算時加上去後清零。</summary>
        public float3 PendingImpulse;

        // ---- 這一幀的結果（給子孫骨頭用）----
        /// <summary>讀取時的世界 → 寫回後的世界（仿射，含這根骨頭與祖先的位移、傾斜、伸縮）。</summary>
        public float4x4 Delta;
        public float4x4 DeltaInverse;
        /// <summary>上一幀的 Delta／DeltaInverse（重建時祖先換了，用來在新的祖先鏈下重算祖先晃動的位置與速度）。</summary>
        public float4x4 PrevDelta;
        public float4x4 PrevDeltaInverse;
        /// <summary>用上一幀的輸出設定算的 Delta（ChainChanged 時才有效）。</summary>
        public float4x4 DeltaContinuous;
        /// <summary>這根骨頭或祖先的輸出設定這一幀改了：子孫用 DeltaContinuous 量速度。</summary>
        public int ChainChanged;
        /// <summary>這一幀的位移跳了、跳動量不知道（重置、初始化、略過、上限改了，或祖先有這種事件）：子孫要重新取祖先晃動的基準。</summary>
        public int Discontinuity;
    }

    /// <summary>讀到的 Transform（骨頭與角色根都讀）。</summary>
    internal struct KPSoftRead
    {
        /// <summary>localToWorldMatrix。軸向從這裡取，不用 Transform.rotation（負縮放的階層裡兩者不一致）。</summary>
        public float4x4 World;
        public float3 LocalPosition;
        public quaternion LocalRotation;
        public float3 LocalScale;
        /// <summary>父 Transform 的 localToWorldMatrix（由自己的矩陣與區域 TRS 反推）。</summary>
        public float4x4 ParentMatrix;
        /// <summary>父 Transform 的 worldToLocalMatrix。</summary>
        public float4x4 ParentInverse;
        /// <summary>0＝這一幀不能用（Transform 已刪除、縮放為 0、數值不是有限值）。</summary>
        public int Valid;
    }

    /// <summary>寫回或還原的區域姿勢。Mode 0＝不寫（角色根、這一幀不能用的骨頭）。</summary>
    internal struct KPSoftLocal
    {
        public float3 LocalPosition;
        public quaternion LocalRotation;
        public float3 LocalScale;
        public int Mode;
    }

    /// <summary>
    /// 一起解的元件：骨頭有祖先關係的元件放同一個群組（一個工作依序解），群組之間平行。
    /// GroupBodies／GroupBones 的一段；骨頭依階層深度排序（祖先在前）。
    /// </summary>
    internal struct KPSoftGroup
    {
        public int BodyStart;
        public int BodyCount;
        public int BoneStart;
        public int BoneCount;
    }

    /// <summary>這一幀的碰撞體（世界空間；主執行緒每幀從 KawaiiPhysicsCollider 算）。</summary>
    internal struct KPSoftCollider
    {
        public const int Sphere = 0, Capsule = 1, Box = 2, Plane = 3;

        public int Shape;
        /// <summary>球心、膠囊 +軸端、箱中心、平面上一點。</summary>
        public float3 A;
        /// <summary>膠囊 -軸端；平面的法線。</summary>
        public float3 B;
        /// <summary>球、膠囊 +軸端的半徑。</summary>
        public float RadiusA;
        /// <summary>膠囊 -軸端的半徑（錐形膠囊兩端不同）。</summary>
        public float RadiusB;
        public quaternion Rotation;
        public float3 Extent;
        public int Inner;
    }
}
