// KawaiiPhysics（https://github.com/pafuhana1213/KawaiiPhysics）的 Unity 移植：Burst＋Job System 用的資料結構。
// 原作是 MIT License（Copyright (c) 2019-2026 pafuhana1213），這份移植也用 MIT，原文附在 LICENSE.md。
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace KawaiiPhysicsUnity
{
    /// <summary>每根骨頭的物理設定。對應 FKawaiiPhysicsSettings。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KPSettings
    {
        /// <summary>阻尼。</summary>
        public float Damping;
        /// <summary>剛性。</summary>
        public float Stiffness;
        /// <summary>抵銷角色移動慣性的比例。</summary>
        public float WorldDampingLocation;
        /// <summary>抵銷角色旋轉慣性的比例。</summary>
        public float WorldDampingRotation;
        /// <summary>碰撞半徑（公尺）。</summary>
        public float Radius;
        /// <summary>最大偏離角度（度）。</summary>
        public float LimitAngle;
    }

    /// <summary>暫時的設定倍率（全部 1＝不變）。對應 FKawaiiPhysicsSettingsMultiplier。</summary>
    [System.Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct KPSettingsMultiplier
    {
        /// <summary>damping 的倍率。</summary>
        public float Damping;
        /// <summary>stiffness 的倍率。</summary>
        public float Stiffness;
        /// <summary>worldDampingLocation 的倍率。</summary>
        public float WorldDampingLocation;
        /// <summary>worldDampingRotation 的倍率。</summary>
        public float WorldDampingRotation;
        /// <summary>radius 的倍率。</summary>
        public float Radius;
        /// <summary>limitAngle 的倍率。</summary>
        public float LimitAngle;

        /// <summary>全部是 1（不改變）的倍率。</summary>
        public static KPSettingsMultiplier One => new KPSettingsMultiplier
        {
            Damping = 1f, Stiffness = 1f, WorldDampingLocation = 1f, WorldDampingRotation = 1f, Radius = 1f, LimitAngle = 1f,
        };
    }

    internal static class KPBoneFlags
    {
        public const int SkipSimulate = 1;
        /// <summary>末端假骨（沒有 Transform）。</summary>
        public const int Dummy = 2;
        /// <summary>骨頭之間細分出來的假骨（BoneSubdivision）。</summary>
        public const int InterBone = 4;
        /// <summary>橫向約束之間的碰撞代理（BoneConstraintSubdivision）。</summary>
        public const int Bridge = 8;
    }

    /// <summary>
    /// 要模擬的一根骨頭（實骨、末端假骨、細分假骨、橋接假骨都是）。對應 FKawaiiPhysicsModifyBone。
    /// 位置與旋轉都在模擬空間。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KPBone
    {
        public float3 Location;
        public float3 PrevLocation;
        public float3 PoseLocation;
        public float3 PrevPoseLocation;
        public float3 CurrentPoseLocation;
        /// <summary>這一幀開始時的位置（世界碰撞的掃掠起點）。</summary>
        public float3 FrameStartLocation;
        /// <summary>橋接假骨把碰撞推移轉給端點時的累加暫存。</summary>
        public float3 FeedbackPush;
        public float FeedbackWeight;
        public quaternion PoseRotation;
        public quaternion PrevPoseRotation;
        public quaternion CurrentPoseRotation;
        /// <summary>這一幀實際用的設定（BaseSettings × 倍率）。</summary>
        public KPSettings Settings;
        /// <summary>基本設定×依骨長比例曲線（還沒夾範圍）。</summary>
        public KPSettings BaseSettings;
        public int ParentIndex;
        public int ChildCount;
        public int TransformIndex;
        public int Flags;
        /// <summary>細分／橋接假骨的兩端實骨（整個陣列的索引）；末端假骨的 RealParent 是它延伸出來的那根實骨。</summary>
        public int RealParentIndex;
        public int RealChildIndex;
        public float InterBoneAlpha;
        public float LengthRate;
        public float BoneLength;
        /// <summary>第 k 個外力是否作用在這根骨頭（ApplyBoneFilter／IgnoreBoneFilter 先算好）。</summary>
        public uint ForceMask;
    }

    /// <summary>碰撞體共用的驅動資訊：DrivingIndex ≥ 0 跟著讀到的 Transform；-1＝模擬空間固定值；-2＝世界空間值（每幀換算）。</summary>
    internal static class KPDriving
    {
        public const int SimSpace = -1;
        public const int World = -2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KPSphere
    {
        public float3 Location;
        public float Radius;
        /// <summary>世界單位（公尺）的尺寸：每幀依這條鏈的模擬空間縮放換成上面的模擬單位（SimSpace 驅動的形狀不換）。</summary>
        public float WorldRadius;
        public int Inner;
        public int DrivingIndex;
        public float3 Offset;
        public float3 WorldLocation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KPCapsule
    {
        public float3 Location;
        public quaternion Rotation;
        public float Radius;
        public float Length;
        /// <summary>世界單位（公尺）的尺寸：每幀依這條鏈的模擬空間縮放換成上面的模擬單位（SimSpace 驅動的形狀不換）。</summary>
        public float WorldRadius, WorldLength;
        public int DrivingIndex;
        public float3 Offset;
        public quaternion OffsetRotation;
        public float3 WorldLocation;
        public quaternion WorldRotation;
        public float3 Start;
        public float3 End;
        public float3 FallbackPushDir;
    }

    /// <summary>錐形膠囊：區域 +Y 端半徑 Radius0、-Y 端 Radius1。對應 FTaperedCapsuleLimit。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KPTaperedCapsule
    {
        public float3 Location;
        public quaternion Rotation;
        public float Radius0;
        public float Radius1;
        public float Length;
        /// <summary>世界單位（公尺）的尺寸：每幀依這條鏈的模擬空間縮放換成上面的模擬單位（SimSpace 驅動的形狀不換）。</summary>
        public float WorldRadius0, WorldRadius1, WorldLength;
        public int DrivingIndex;
        public float3 Offset;
        public quaternion OffsetRotation;
        public float3 WorldLocation;
        public quaternion WorldRotation;
        public float3 Start;
        public float3 Segment;
        public float SegmentSizeSq;
        public float3 FallbackPushDir;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KPBox
    {
        public float3 Location;
        public quaternion Rotation;
        public float3 Extent;
        /// <summary>世界單位（公尺）的尺寸：每幀依這條鏈的模擬空間縮放換成上面的模擬單位（SimSpace 驅動的形狀不換）。</summary>
        public float3 WorldExtent;
        public int DrivingIndex;
        public float3 Offset;
        public quaternion OffsetRotation;
        public float3 WorldLocation;
        public quaternion WorldRotation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KPPlane
    {
        public float3 Location;
        public quaternion Rotation;
        public int DrivingIndex;
        public float3 Offset;
        public quaternion OffsetRotation;
        public float3 WorldLocation;
        public quaternion WorldRotation;
        public float3 Normal;
    }

    /// <summary>凸包：PlaneStart／PlaneCount 指到 ConvexPlanes（區域空間，xyz＝法線、w＝距離）。對應 FKawaiiPhysicsConvexLimit。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KPConvex
    {
        public float3 Location;
        public quaternion Rotation;
        public int DrivingIndex;
        public float3 Offset;
        public quaternion OffsetRotation;
        public float3 WorldLocation;
        public quaternion WorldRotation;
        public float3 LocalBoundsMin;
        public float3 LocalBoundsMax;
        /// <summary>模擬空間的一個單位是幾公尺（凸包的面與包圍盒是公尺）。0 當成 1（直接呼叫碰撞函式的測試）。</summary>
        public float MetersPerUnit;
        public int PlaneStart;
        public int PlaneCount;
    }

    /// <summary>XPBD 距離約束（對應 FModifyBoneConstraint）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KPConstraint
    {
        public int Bone1;
        public int Bone2;
        public float Length;
        public float Compliance;
        public float Lambda;
    }

    /// <summary>跟原作 XPBDComplianceValues 相同的值（EXPBDComplianceType 的順序）。</summary>
    internal static class KPCompliance
    {
        public const float Concrete = 0.00000000004f;
        public const float Wood = 0.00000000016f;
        public const float Leather = 0.000000001f;
        public const float Tendon = 0.000000002f;
        public const float Rubber = 0.0000001f;
        public const float Muscle = 0.00002f;
        public const float Fat = 0.0001f;

        public static float Of(KPComplianceType type)
        {
            switch (type)
            {
                case KPComplianceType.Concrete: return Concrete;
                case KPComplianceType.Wood: return Wood;
                case KPComplianceType.Leather: return Leather;
                case KPComplianceType.Tendon: return Tendon;
                case KPComplianceType.Rubber: return Rubber;
                case KPComplianceType.Muscle: return Muscle;
                default: return Fat;
            }
        }
    }

    /// <summary>骨間約束的柔軟度（XPBD compliance；原作的七種，由硬到軟）。</summary>
    public enum KPComplianceType
    {
        /// <summary>混凝土（最硬）</summary>
        Concrete,
        /// <summary>木頭</summary>
        Wood,
        /// <summary>皮革</summary>
        Leather,
        /// <summary>肌腱</summary>
        Tendon,
        /// <summary>橡膠</summary>
        Rubber,
        /// <summary>肌肉</summary>
        Muscle,
        /// <summary>脂肪（最軟）</summary>
        Fat,
    }

    /// <summary>模擬空間。</summary>
    public enum KPSimulationSpace
    {
        /// <summary>元件空間（跟著角色，預設）</summary>
        ComponentSpace,
        /// <summary>世界空間</summary>
        WorldSpace,
        /// <summary>基準骨空間（simulationBaseBone）</summary>
        BaseBoneSpace,
    }

    /// <summary>鎖住模擬空間的一軸。</summary>
    public enum KPPlanarConstraint
    {
        /// <summary>不鎖</summary>
        None,
        /// <summary>鎖 X（只在 YZ 平面動）</summary>
        X,
        /// <summary>鎖 Y（只在 XZ 平面動）</summary>
        Y,
        /// <summary>鎖 Z（只在 XY 平面動）</summary>
        Z,
    }

    /// <summary>外力向量的空間。</summary>
    public enum KPForceSpace
    {
        /// <summary>元件空間</summary>
        ComponentSpace,
        /// <summary>世界空間</summary>
        WorldSpace,
        /// <summary>骨頭空間（各骨頭的區域空間）</summary>
        BoneSpace,
    }

    /// <summary>SyncBone 的一個方向要不要套用。對應 ESyncBoneDirection。</summary>
    public enum KPSyncDirection
    {
        /// <summary>正負都套用</summary>
        Both,
        /// <summary>只套用正向</summary>
        Positive,
        /// <summary>只套用負向</summary>
        Negative,
        /// <summary>不套用</summary>
        None,
    }

    internal static class KPForceType
    {
        /// <summary>每步把 Vector×比例×dt 加到位置（Basic、Curve）。</summary>
        public const int Position = 0;
        /// <summary>每步把 Vector×比例×dt 加到速度（Gravity）。</summary>
        public const int Velocity = 1;
        /// <summary>場景風（WindZone）：每骨頭取風向、風速，加到位置（Wind 外力）。</summary>
        public const int SceneWind = 2;
        /// <summary>程序風：每骨頭算漣漪相位，加到位置（ProceduralWind）。</summary>
        public const int ProceduralWind = 3;
    }

    /// <summary>
    /// 一個外力這一幀的值（主執行緒的 PreApply 算好）。對應 FKawaiiPhysics_ExternalForce 的 Force 等快取。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KPForce
    {
        public int Type;
        public int Space;
        /// <summary>ForceRateByBoneLengthRate 的查表起點（-1＝固定 1）。</summary>
        public int RateLut;
        public int Seed;
        /// <summary>力的向量（Space 指定的空間；BoneSpace 時是骨頭區域空間）。</summary>
        public float3 Vector;
        public float Scale;
        // 程序風（ProceduralWind::Apply 用的快取）
        public float SinesWithoutRipple;
        public float StrengthCycle;
        public float RandomTerm;
        public float Gust;
        public float Time;
        public float RippleForce;
        public float RipplePeriod;
        public float RipplePhaseOffset;
        public float RippleTipPhaseDelay;
        // 場景風
        public float NoiseAngle;
    }

    /// <summary>場景裡的 WindZone（主執行緒每幀收集）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KPWindZone
    {
        public float3 Position;
        public float3 Direction;
        public float Radius;
        /// <summary>0 = 方向風，1 = 球形風。</summary>
        public int Spherical;
        public float Speed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KPSyncBone
    {
        public int TransformIndex;
        public int TargetStart;
        public int TargetCount;
        public int DeltaCurveLut;
        public float DeltaCurveMaxDistance;
        /// <summary>靜止姿勢時，這根骨頭在元件空間的位置。</summary>
        public float3 InitialPoseLocation;
        public float3 GlobalScale;
        public int3 Directions;
        public int EnableAttenuation;
        public float AttenuationInner;
        public float AttenuationOuter;
        public float MaxAttenuation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KPSyncTarget
    {
        public int BoneIndex;
        public float Scale;
    }

    /// <summary>
    /// 一個角色（原作的一個 AnimNode）的狀態。骨頭、碰撞體、約束、外力都是共用陣列裡的一段範圍。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KPInstance
    {
        public int BoneStart, BoneCount;
        public int SphereStart, SphereCount;
        public int CapsuleStart, CapsuleCount;
        public int TaperedStart, TaperedCount;
        public int BoxStart, BoxCount;
        public int PlaneStart, PlaneCount;
        public int ConvexStart, ConvexCount;
        public int ConstraintStart, ConstraintCount;
        public int SyncStart, SyncCount;
        public int ForceStart, ForceCount;
        public int ConstraintItersBefore, ConstraintItersAfter;
        public int TargetFramerate, MaxSubsteps;
        public int UseFixedSubstep;
        public int PoseInitialized;
        public int ComponentIndex;
        public int BaseBoneIndex;
        public int SimulationSpace;
        public int ForwardAxis;
        public int PlanarConstraint;
        public int Teleport;
        public int HasPrevComponent;
        public int UseLegacyGravity;
        public int UseWorldSpaceGravity;
        public int CollisionOnlySubdivision;
        public int WarmUpPending;
        public int EnableWind;
        public int WorldCollision;
        public int Enabled;
        public uint RandomSeed;
        public float SubstepAccumulator;
        public float DeltaTimeOld;
        public float StepDeltaTime;
        public float ConsumeFraction;
        public float DummyBoneLength;
        public float TeleportDistanceThreshold;
        public float TeleportRotationThreshold;
        public float BridgeFeedbackScale;
        public float Alpha;
        public float WindScale;
        public float WindNoiseAngle;
        public float WindGustFactor;
        public float3 Gravity;
        public float3 SimSpaceGravity;
        public float3 SimpleExternalForce;
        public int SimpleExternalForceWorldSpace;
        public float3 SimSpaceSimpleExternalForce;
        public float3 SkelCompMoveScale;
        public float3 SkelCompMoveVector;
        public quaternion SkelCompMoveRotation;
        public quaternion WindNoiseRotation;
        public KPSettingsMultiplier Multiplier;
        // 這一幀與上一幀的元件、模擬空間
        public float3 ComponentPosition;
        public quaternion ComponentRotation;
        public float ComponentScale;
        public float3 PrevComponentPosition;
        public quaternion PrevComponentRotation;
        public float3 SimPosition;
        public quaternion SimRotation;
        public float SimScale;
        /// <summary>上一幀的模擬空間在上一幀元件空間裡的位置／旋轉／縮放（原作 PrevBaseBoneSpace2ComponentSpace）。</summary>
        public float3 PrevSimPosition;
        public quaternion PrevSimRotation;
        public float PrevSimScale;

        public static KPInstance Create(int targetFramerate = 60)
        {
            return new KPInstance
            {
                TargetFramerate = targetFramerate,
                MaxSubsteps = 4,
                UseFixedSubstep = 1,
                ComponentIndex = -1,
                BaseBoneIndex = -1,
                UseWorldSpaceGravity = 1,
                CollisionOnlySubdivision = 1,
                Enabled = 1,
                RandomSeed = 1,
                DeltaTimeOld = 1f / targetFramerate,
                ConsumeFraction = 1f,
                TeleportDistanceThreshold = 3f,
                TeleportRotationThreshold = 10f,
                BridgeFeedbackScale = 1f,
                Alpha = 1f,
                WindScale = 1f,
                WindGustFactor = 1f,
                SkelCompMoveScale = new float3(1f),
                SkelCompMoveRotation = quaternion.identity,
                WindNoiseRotation = quaternion.identity,
                Multiplier = KPSettingsMultiplier.One,
                ComponentRotation = quaternion.identity,
                ComponentScale = 1f,
                PrevComponentRotation = quaternion.identity,
                SimRotation = quaternion.identity,
                SimScale = 1f,
                PrevSimRotation = quaternion.identity,
                PrevSimScale = 1f,
            };
        }
    }

    /// <summary>讀取工作從 Transform 拿到的值（世界空間＋區域）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KPReadPose
    {
        public float3 Position;
        public quaternion Rotation;
        public quaternion LocalRotation;
        public float3 LocalPosition;
        public float3 LossyScale;
    }

    /// <summary>寫入工作要寫回 Transform 的值（區域空間）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KPWriteLocal
    {
        public quaternion LocalRotation;
        public float3 LocalPosition;
        /// <summary>0 = 不寫，1 = 只寫旋轉，2 = 旋轉與位置都寫。</summary>
        public int Mode;
    }
}
