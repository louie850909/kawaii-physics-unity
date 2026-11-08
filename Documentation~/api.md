# API 參考

套件 `com.louie.kawaii-physics` 的所有公開型別與成員（命名空間 `KawaiiPhysicsUnity`，Editor 的部分在 `KawaiiPhysicsUnity.Editor`）。
這份文件由產生器從反射的成員清單與原始碼的說明產生；單元測試會檢查每個公開成員都出現在這裡。

## 目錄

- 元件：[KawaiiPhysicsAnimationEvents](#kawaiiphysicsanimationevents)、[KawaiiPhysicsChain](#kawaiiphysicschain)、[KawaiiPhysicsChain.Axis](#kawaiiphysicschainaxis)、[KawaiiPhysicsChain.BoneConstraint](#kawaiiphysicschainboneconstraint)、[KawaiiPhysicsChain.RootBoneSetting](#kawaiiphysicschainrootbonesetting)、[KawaiiPhysicsChain.SyncBone](#kawaiiphysicschainsyncbone)、[KawaiiPhysicsChain.SyncTargetRoot](#kawaiiphysicschainsynctargetroot)、[KawaiiPhysicsCollider](#kawaiiphysicscollider)、[KawaiiPhysicsCollider.Axis](#kawaiiphysicscollideraxis)、[KawaiiPhysicsCollider.Shape](#kawaiiphysicscollidershape)、[KawaiiPhysicsSoftBody](#kawaiiphysicssoftbody)、[KawaiiPhysicsSoftBody.Axis](#kawaiiphysicssoftbodyaxis)
- 陣列接口：[KPRigChain](#kprigchain)、[KPRigChain.BoneConstraint](#kprigchainboneconstraint)、[KPRigChain.RootBoneSetting](#kprigchainrootbonesetting)、[KPRigChain.SyncBone](#kprigchainsyncbone)、[KPRigChain.SyncTargetRoot](#kprigchainsynctargetroot)、[KPRigCollider](#kprigcollider)、[KPRigSoftBody](#kprigsoftbody)
- 資產：[KawaiiPhysicsConstraintsAsset](#kawaiiphysicsconstraintsasset)、[KawaiiPhysicsConstraintsAsset.Entry](#kawaiiphysicsconstraintsassetentry)、[KawaiiPhysicsConstraintsAsset.RegexPair](#kawaiiphysicsconstraintsassetregexpair)、[KawaiiPhysicsEventPreset](#kawaiiphysicseventpreset)、[KawaiiPhysicsEventPreset.Kind](#kawaiiphysicseventpresetkind)、[KawaiiPhysicsLimitsAsset](#kawaiiphysicslimitsasset)、[KawaiiPhysicsLimitsAsset.Entry](#kawaiiphysicslimitsassetentry)、[KawaiiPhysicsPreset](#kawaiiphysicspreset)、[KawaiiPhysicsPreset.Data](#kawaiiphysicspresetdata)、[KawaiiPhysicsWindChannel](#kawaiiphysicswindchannel)
- 外力：[KPCurveEvaluateType](#kpcurveevaluatetype)、[KPExternalForce](#kpexternalforce)、[KPForceBasic](#kpforcebasic)、[KPForceCurve](#kpforcecurve)、[KPForceGravity](#kpforcegravity)、[KPForceProceduralWind](#kpforceproceduralwind)、[KPForceSpace](#kpforcespace)、[KPForceWind](#kpforcewind)、[KPProceduralWindParams](#kpproceduralwindparams)、[KPProceduralWindState](#kpproceduralwindstate)、[KPWindSource](#kpwindsource)
- 系統與設定：[KPBackend](#kpbackend)、[KPComplianceType](#kpcompliancetype)、[KPPlanarConstraint](#kpplanarconstraint)、[KPSettings](#kpsettings)、[KPSettingsMultiplier](#kpsettingsmultiplier)、[KPSimulationSpace](#kpsimulationspace)、[KPSyncDirection](#kpsyncdirection)、[KPSystem](#kpsystem)、[KPTeleportType](#kpteleporttype)
- Editor：[KawaiiPhysicsChainEditor](#kawaiiphysicschaineditor)、[KPExternalForceDrawer](#kpexternalforcedrawer)、[KPExternalForceMenu](#kpexternalforcemenu)

## 元件

### KawaiiPhysicsAnimationEvents

*class* — 原作 AnimNotify／AnimNotifyState（SetAlpha、AddExternalForce、TriggerGust、SettingsMultiplier）的 Unity 版： 放在有 Animator 的物件上，動畫片段的 AnimationEvent 呼叫這裡的方法，作用到底下所有的 KawaiiPhysicsChain 與 KawaiiPhysicsSoftBody。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `KawaiiPhysicsSetAlpha` | method | `Void KawaiiPhysicsSetAlpha(Single alpha)` | AnimationEvent：floatParameter＝新的 alpha（Chain 與 SoftBody）。 |
| `KawaiiPhysicsResetDynamics` | method | `Void KawaiiPhysicsResetDynamics()` | AnimationEvent：底下所有 Chain 與 SoftBody 重新開始物理（ResetDynamics）。 |
| `KawaiiPhysicsEvent` | method | `Void KawaiiPhysicsEvent(Object preset)` | AnimationEvent：objectReferenceParameter＝KawaiiPhysicsEventPreset。 SoftBodyImpulse 作用到名稱符合 softBodyNameFilter 的 SoftBody，其他種類作用到名稱符合 chainNameFilter 的 Chain。 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsChain

*class* — 對應原作的一個 KawaiiPhysics AnimNode：從 rootBone（與 additionalRootBones）往下的骨頭全部模擬。 實際運算由 KPSystem 把所有 Chain 打包成一批 Burst 工作處理。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `rootBone` | field | `Transform` | 模擬的起點骨頭；它底下的骨頭（排除清單以外）全部模擬。起點骨頭本身跟著動畫 |
| `excludeBones` | field | `List<Transform>` | 不模擬的骨頭（連同它底下的骨頭） |
| `additionalRootBones` | field | `List<RootBoneSetting>` | 其他的起點骨頭（例如左右兩束頭髮放在同一個元件）；可以各自指定排除清單 |
| `dummyBoneLength` | field | `Single` | 末端假骨的長度（公尺）；0 表示不加。末端骨頭沒有子骨頭時，要靠它才會轉 |
| `boneForwardAxis` | field | `Axis` | 骨頭的前方軸，只用在末端假骨的方向 |
| `component` | field | `Transform` | 元件（角色根）。不指定時用上層的 Animator，再沒有就用自己 |
| `boneSubdivisionCount` | field | `Int32` | 相鄰骨頭之間至少插入幾個假骨（改善碰撞，例如裙子穿腿） |
| `boneSubdivisionCollisionOnly` | field | `Boolean` | 插入的假骨只參加碰撞與約束，不做速度積分 |
| `boneSubdivisionDensifyByRadius` | field | `Boolean` | 依半徑加密：骨頭間距比半徑大的地方插更多（每段最多 50） |
| `boneConstraintSubdivisionCount` | field | `Int32` | 每條橫向骨間約束插入幾個碰撞代理（填補相鄰兩列之間的縫） |
| `boneConstraintSubdivisionFeedbackScale` | field | `Single` | 每條橫向骨間約束插入幾個碰撞代理（填補相鄰兩列之間的縫） |
| `damping` | field | `Single` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `stiffness` | field | `Single` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `worldDampingLocation` | field | `Single` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `worldDampingRotation` | field | `Single` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `radius` | field | `Single` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `limitAngle` | field | `Single` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `planarConstraint` | field | `KPPlanarConstraint` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `alpha` | field | `Single` | 物理結果與動畫姿勢的混合比例（0＝只有動畫、1＝完全物理） |
| `dampingCurve` | field | `AnimationCurve` | damping 依骨長比例（根 0 → 末端 1）乘上的倍率；沒有關鍵格時是 1 |
| `stiffnessCurve` | field | `AnimationCurve` | stiffness 依骨長比例（根 0 → 末端 1）乘上的倍率；沒有關鍵格時是 1 |
| `worldDampingLocationCurve` | field | `AnimationCurve` | worldDampingLocation 依骨長比例乘上的倍率；沒有關鍵格時是 1 |
| `worldDampingRotationCurve` | field | `AnimationCurve` | worldDampingRotation 依骨長比例乘上的倍率；沒有關鍵格時是 1 |
| `radiusCurve` | field | `AnimationCurve` | radius 依骨長比例乘上的倍率；沒有關鍵格時是 1 |
| `limitAngleCurve` | field | `AnimationCurve` | limitAngle 依骨長比例乘上的倍率；沒有關鍵格時是 1 |
| `simulationSpace` | field | `KPSimulationSpace` | 模擬空間：元件空間（跟著角色，預設）、世界空間、基準骨空間 |
| `simulationBaseBone` | field | `Transform` | simulationSpace＝BaseBoneSpace 時的基準骨頭 |
| `targetFramerate` | field | `Int32` | 模擬的步頻（每秒步數）；固定子步進時每步是 1/targetFramerate 秒 |
| `fixedSubstepping` | field | `Boolean` | 用固定時間的子步進（幀率變動時結果穩定）；關閉時每幀一步 |
| `maxSubsteps` | field | `Int32` | 一幀最多幾個子步進（掉幀時不會無限追趕） |
| `skelCompMoveScale` | field | `Vector3` | 一幀最多幾個子步進（掉幀時不會無限追趕） |
| `teleportDistanceThreshold` | field | `Single` | 角色一幀移動超過這個距離（公尺）就當成瞬移、物理重新開始；0＝不偵測 |
| `teleportRotationThreshold` | field | `Single` | 角色一幀移動超過這個距離（公尺）就當成瞬移、物理重新開始；0＝不偵測 |
| `warmUpFrames` | field | `Int32` | 開始時空跑幾幀，讓物理先穩定 |
| `needWarmUp` | field | `Boolean` | 開始時空跑幾幀，讓物理先穩定 |
| `useWarmUpWhenResetDynamics` | field | `Boolean` | ResetDynamics 之後先暖機 warmUpFrames 幀 |
| `colliders` | field | `List<KawaiiPhysicsCollider>` | 跟這條鏈碰撞的碰撞體 |
| `limitsAsset` | field | `KawaiiPhysicsLimitsAsset` | 碰撞資產（用骨頭名稱指定的一組碰撞形狀） |
| `sharedCollisionSource` | field | `Boolean` | 把自己的碰撞體提供給同一角色底下、同一標籤的其他 Chain |
| `useSharedCollision` | field | `Boolean` | 使用同一角色底下、同一標籤的其他 Chain 提供的碰撞體 |
| `sharedCollisionTag` | field | `String` | 共用碰撞的標籤：同一個根物件底下、標籤相同的來源與使用端才共用 |
| `boneConstraints` | field | `List<BoneConstraint>` | 骨間約束：兩根骨頭之間維持建立時的距離（例如裙子相鄰的兩條） |
| `boneConstraintsAsset` | field | `KawaiiPhysicsConstraintsAsset` | 骨間約束資產（用骨頭名稱指定） |
| `boneConstraintGlobalComplianceType` | field | `KPComplianceType` | 骨間約束預設的柔軟度（約束沒有個別指定時用） |
| `boneConstraintIterationCountBeforeCollision` | field | `Int32` | 碰撞之前解約束的次數 |
| `boneConstraintIterationCountAfterCollision` | field | `Int32` | 碰撞之前解約束的次數 |
| `autoAddChildDummyBoneConstraint` | field | `Boolean` | 碰撞之前解約束的次數 |
| `allowWorldCollision` | field | `Boolean` | 跟場景的 Physics 碰撞體碰撞（每幀用球掃掠） |
| `worldCollisionLayers` | field | `LayerMask` | 世界碰撞要碰的圖層 |
| `useSimpleWorldCollision` | field | `Boolean` | 簡易世界碰撞：定期收集附近的簡單碰撞體（球、膠囊、箱、網格的包圍盒）與地面，不做掃掠 |
| `simpleWorldCollisionGatherInterval` | field | `Single` | 多久重新收集一次附近的碰撞體（秒） |
| `simpleWorldCollisionGatherRadius` | field | `Single` | 多久重新收集一次附近的碰撞體（秒） |
| `simpleWorldCollisionLayers` | field | `LayerMask` | 多久重新收集一次附近的碰撞體（秒） |
| `simpleWorldCollisionGroundCollision` | field | `Boolean` | 往下打一條射線，把地面當成薄箱子 |
| `simpleWorldCollisionMaxShapes` | field | `Int32` | 每種形狀最多收集幾個 |
| `gravity` | field | `Vector3` | 重力（m/s²）。原作預設是 0 |
| `useWorldSpaceGravity` | field | `Boolean` | gravity 是世界空間的向量（關閉時是元件空間） |
| `useLegacyGravity` | field | `Boolean` | 原作 legacy：把 0.5·g·dt² 直接加到位置 |
| `simpleExternalForce` | field | `Vector3` | 固定的外力（加速度，公尺／秒²） |
| `useWorldSpaceSimpleExternalForce` | field | `Boolean` | simpleExternalForce 是世界空間的向量（關閉時是元件空間） |
| `enableWind` | field | `Boolean` | 場景的 WindZone 會吹動 |
| `windScale` | field | `Single` | 場景風（WindZone）的強度倍率 |
| `windDirectionNoiseAngle` | field | `Single` | 場景風的方向每幀隨機偏移的最大角度（度） |
| `externalForces` | field | `List<KPExternalForce>` | 外力（五種，可以多個） |
| `syncBones` | field | `List<SyncBone>` | SyncBone：來源骨頭的移動推動目標骨頭 |
| `preset` | field | `KawaiiPhysicsPreset` | 啟用時把這個預設的設定套到這條鏈 |
| `MaxTransientExternalForces` | const | `Int32` | 同時存在的暫時外力（RequestTransientExternalForce、RequestTransientGust）上限。 |
| `MaxPhysicsSettingsMultipliers` | const | `Int32` | 同時存在的設定倍率（StartSettingsMultiplier）上限。 |
| `TransientGustInheritAllWinds` | const | `Int32` | RequestTransientGust 的 inheritForceIndex：所有啟用中的程序風各產生一個突風。 |
| `MarkDirty` | method | `Void MarkDirty()` | 結構改了（骨頭、碰撞體、約束、SyncBone、模擬空間等），下一幀重建這一條（只有它的模擬狀態重置，其他 Chain 不受影響）。 damping、stiffness、worldDamping、radius、limitAngle 的值會自動偵測，不用呼叫這個。 |
| `MarkSettingsDirty` | method | `Void MarkSettingsDirty()` | 改了依骨長比例的曲線（dampingCurve 等）、外力的骨頭篩選或 forceRateByBoneLengthRate 之後呼叫： 下一幀就地更新，不重建、不重置。例外：依半徑加密細分或有橋接假骨時，改了 radiusCurve 會改變假骨數量，這時重建這一條。 |
| `ResolveComponent` | method | `Transform ResolveComponent()` | 元件（角色根）：有指定 component 就用它，否則用上層的 Animator，再沒有就用自己。 |
| `EvaluateSettings` | method | `KPSettings EvaluateSettings(Single lengthRate)` | 依骨長比例算出的基本設定（還沒夾範圍；倍率在每幀的工作裡乘）。 |
| `ResetDynamics` | method | `Void ResetDynamics(KPTeleportType type = Reset)` | 原作 ResetDynamics。 |
| `RequestTransientExternalForce` | method | `Int64 RequestTransientExternalForce(KPExternalForce force, Single lifetime)` | 加一個暫時外力，lifetime 秒後移除。回傳停止用的 handle。 |
| `RequestTransientGust` | method | `Int64 RequestTransientGust(Single strength, Single riseTime, Single decayTime, Vector3 direction = null, Int32 inheritForceIndex = -1, Single holdTime = 0, Boolean realTimeEnvelope = False)` | 原作 RequestTransientGust：以某個程序風為底（inheritForceIndex；-1 找第一個啟用中的）產生一陣突風。 direction 非零時用世界空間的這個方向。 |
| `RequestStopTransientExternalForce` | method | `Void RequestStopTransientExternalForce(Int64 handle, Single blendOutTime)` | 原作 RequestStopTransientExternalForce：blendOut 秒內淡出（程序風是讓突風落到 0）。 |
| `StartSettingsMultiplier` | method | `Int64 StartSettingsMultiplier(KPSettingsMultiplier scale, Single riseTime, Single holdTime, Single decayTime, Boolean infiniteHold = False)` | 原作 RequestStartPhysicsSettingsMultiplier：起、持、落的包絡；infiniteHold 時持續到停止。 |
| `PushSettingsMultiplier` | method | `Void PushSettingsMultiplier(Int64 handle, KPSettingsMultiplier scale, Single pushAlpha, Int32 leaseEvaluations = 0, Single leaseExpireBlendOut = 0.2)` | 原作 RequestPushPhysicsSettingsMultiplier：由外部每幀推送強度（alpha）。 leaseEvaluations＞0 時，這麼多幀沒推送就在 leaseExpireBlendOut 秒內淡出。 |
| `StopSettingsMultiplier` | method | `Void StopSettingsMultiplier(Int64 handle, Single blendOutTime)` | 原作 RequestStopPhysicsSettingsMultiplier。 |
| `.ctor` | constructor | `()` | 預設建構子。 |
| `TransientForceCount` | property | `Int32 { get; }` | 目前有效的暫時外力數。 |
| `SettingsMultiplierCount` | property | `Int32 { get; }` | 目前有效的設定倍率數。 |

### KawaiiPhysicsChain.Axis

*enum* — 骨頭的前方軸（末端假骨往這個方向延伸）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `XPositive` | value | `Axis` | 區域 +X |
| `XNegative` | value | `Axis` | 區域 −X |
| `YPositive` | value | `Axis` | 區域 +Y |
| `YNegative` | value | `Axis` | 區域 −Y |
| `ZPositive` | value | `Axis` | 區域 +Z |
| `ZNegative` | value | `Axis` | 區域 −Z |

### KawaiiPhysicsChain.BoneConstraint

*class* — 骨間約束：兩根骨頭之間維持建立時的距離（XPBD）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `bone1` | field | `Transform` | 約束的第一根骨頭 |
| `bone2` | field | `Transform` | 約束的第二根骨頭 |
| `overrideCompliance` | field | `Boolean` | 用這條約束自己的柔軟度（不用全域預設） |
| `complianceType` | field | `KPComplianceType` | 這條約束的柔軟度 |
| `excludeFromSubdivision` | field | `Boolean` | 這條約束不插入橋接假骨 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsChain.RootBoneSetting

*class* — 額外的起點骨頭。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `rootBone` | field | `Transform` | 起點骨頭 |
| `useOverrideExcludeBones` | field | `Boolean` | 用自己的排除清單（不用元件的 excludeBones） |
| `overrideExcludeBones` | field | `List<Transform>` | 這個起點用的排除清單 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsChain.SyncBone

*class* — 對應 FKawaiiPhysicsSyncBone：來源骨頭偏離靜止姿勢的量推動目標骨頭的姿勢（例：大腿推裙子）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `bone` | field | `Transform` | 來源骨頭（例：大腿） |
| `targetRoots` | field | `List<SyncTargetRoot>` | 被推動的骨頭 |
| `globalScale` | field | `Vector3` | 推動量各軸的倍率 |
| `scaleCurveByDeltaDistance` | field | `AnimationCurve` | 依移動距離（公尺）乘上的倍率 |
| `applyDirectionX` | field | `KPSyncDirection` | X 軸只套用正向、負向、兩者或不套用 |
| `applyDirectionY` | field | `KPSyncDirection` | Y 軸只套用正向、負向、兩者或不套用 |
| `applyDirectionZ` | field | `KPSyncDirection` | Z 軸只套用正向、負向、兩者或不套用 |
| `enableDistanceAttenuation` | field | `Boolean` | 依目標骨頭與來源骨頭的距離衰減 |
| `attenuationInnerRadius` | field | `Single` | 距離小於這個值不衰減（公尺） |
| `attenuationOuterRadius` | field | `Single` | 距離大於這個值衰減到 maxAttenuationRate（公尺） |
| `maxAttenuationRate` | field | `Single` | 最遠處的衰減比例 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsChain.SyncTargetRoot

*class* — SyncBone 推動的目標。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `bone` | field | `Transform` | 目標骨頭 |
| `includeChildBones` | field | `Boolean` | 連子骨頭一起推動 |
| `scaleCurveByBoneLengthRate` | field | `AnimationCurve` | 依骨長比例乘上的倍率 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsCollider

*class* — 放在身體骨頭上（或底下）的碰撞體，跟著這個 Transform 走。 膠囊、錐形膠囊的軸由 direction 決定（預設 Y，跟 Unity 的 CapsuleCollider 一樣）；平面法線是區域 +Y。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `shape` | field | `Shape` | 形狀 |
| `center` | field | `Vector3` | 中心（這個 Transform 區域空間的偏移） |
| `direction` | field | `Axis` | 膠囊、錐形膠囊的軸 |
| `radius` | field | `Single` | 半徑（公尺）；錐形膠囊是 +軸端的半徑 |
| `radius1` | field | `Single` | 錐形膠囊 -軸端的半徑（+軸端用 radius） |
| `length` | field | `Single` | 膠囊兩端球心之間的長度 |
| `extent` | field | `Vector3` | 箱的半邊長 |
| `inner` | field | `Boolean` | 球限定：勾選時把骨頭關在球裡，不勾是往外推 |
| `convexMesh` | field | `Mesh` | 凸包：用這個網格的三角形面當平面（網格要是凸的） |
| `.ctor` | constructor | `()` | 預設建構子。 |
| `AxisRotation` | property | `Quaternion { get; }` | 把區域 +Y 轉到 direction 軸的旋轉。 |

### KawaiiPhysicsCollider.Axis

*enum* — 骨頭的前方軸（末端假骨往這個方向延伸）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `X` | value | `Axis` | 區域 X 軸 |
| `Y` | value | `Axis` | 區域 Y 軸 |
| `Z` | value | `Axis` | 區域 Z 軸 |

### KawaiiPhysicsCollider.Shape

*enum* — 碰撞的形狀。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `Sphere` | value | `Shape` | 球（inner 時把骨頭關在球裡） |
| `Capsule` | value | `Shape` | 膠囊 |
| `TaperedCapsule` | value | `Shape` | 錐形膠囊（兩端半徑不同） |
| `Box` | value | `Shape` | 箱（extent 是半邊長） |
| `Plane` | value | `Shape` | 平面（區域 +Y 是法線，骨頭推到正面） |
| `Convex` | value | `Shape` | 凸包（convexMesh 的三角形面） |

### KawaiiPhysicsSoftBody

*class* — 平移式軟組織（胸、臀、腹部等）：每根骨頭以這一幀動畫後的姿勢為錨點，像掛在阻尼彈簧上的質點， 可以離開錨點一小段再彈回（位移有上限），並可依位移傾斜、沿軸壓扁與拉伸。 跟 KawaiiPhysicsChain（保持骨長、只轉動的鐘擺）互補；同一根骨頭不要同時給兩者模擬。 實際運算由 KPSystem 在 Chain 之前用 Burst 平行處理；設定每幀讀取，改了就地生效。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `bones` | field | `List<Transform>` | 要模擬的骨頭，各自是一個彈簧。祖先也是軟組織骨頭時（不論是不是同一個元件），子骨的錨點跟著祖先的位移與伸縮，也感受祖先晃動的慣性 |
| `referenceRoot` | field | `Transform` | 角色根（慣性拆成角色移動與動畫、重力參考用）；空的話用上層的 Animator，再沒有就用自己。站立時 +Y 要朝上 |
| `frequency` | field | `Single` | 振動頻率（Hz）：無阻尼時每秒來回幾次。越大越硬 |
| `dampingRatio` | field | `Single` | 阻尼比：0＝不衰減、1＝臨界阻尼（不回彈）、大於 1＝過阻尼 |
| `gravity` | field | `Vector3` | 重力加速度（世界空間，公尺/秒²）。平衡時的下垂量＝重力 ÷ (2π×頻率)² |
| `gravityRelativeToRestPose` | field | `Boolean` | 重力相對靜止姿勢：站立時的形狀已經包含重力，只有姿勢偏離站立（躺、趴、倒立）時才往重力方向偏 |
| `externalAcceleration` | field | `Vector3` | 額外的加速度（世界空間，公尺/秒²），應用端可以每幀改 |
| `animationInertia` | field | `Single` | 動畫造成的錨點加減速的慣性倍率（0＝跟著動畫走、不晃；1＝真實慣性）。祖先軟組織骨頭的晃動不受這個影響，一律是真實慣性 |
| `movementInertia` | field | `Single` | 角色根移動、轉向造成的慣性倍率（0＝跟著角色走、不晃；1＝真實慣性） |
| `limitMin` | field | `Vector3` | 各軸往負方向最多離開錨點多遠（≤0；0＝鎖住這個方向） |
| `limitMax` | field | `Vector3` | 各軸往正方向最多離開錨點多遠（≥0；0＝鎖住這個方向） |
| `tilt` | field | `Single` | 依位移傾斜：骨頭朝『父骨 → 位移後的位置』轉動的比例（0＝只平移） |
| `stretchAxis` | field | `Axis` | 壓扁與拉伸的軸（骨頭區域） |
| `stretchAmount` | field | `Single` | 沿軸位移到上限時伸長的比例（負值＝反向；0＝不縮放）。子骨頭跟著縮放（子軟組織骨頭的位置會跟著伸縮移動），建議只用在末端骨頭 |
| `volumePreservation` | field | `Single` | 維持體積：沿軸伸長時另外兩軸縮小的程度（1＝體積不變） |
| `radius` | field | `Single` | 骨頭的碰撞半徑（公尺） |
| `colliders` | field | `List<KawaiiPhysicsCollider>` | 碰撞體（球、膠囊、錐形膠囊、箱、平面；凸包不支援）。清單每幀讀取，增減不用重建 |
| `collisionPriority` | field | `Boolean` | 碰撞優先：碰撞體壓過來時骨頭可以被推到位移上限之外（不穿透）。關掉時位移上限優先（碰撞體可能穿進骨頭） |
| `alpha` | field | `Single` | 物理結果的混合比例（0＝完全照動畫）。它是動畫與物理的混合比例、不是力：改了（一次跳或漸變）時子孫軟組織骨頭不把因此的移動當成加速；祖先真的晃動照常傳下去 |
| `targetFramerate` | field | `Int32` | 子步進的步頻（每秒步數）。彈簧本身是解析解，子步進只影響碰撞與位移上限的精度 |
| `maxSubsteps` | field | `Int32` | 每幀最多幾個子步進 |
| `teleportDistanceThreshold` | field | `Single` | 角色根一幀移動超過這個距離（公尺）就當成瞬移、重置（0＝不判定） |
| `teleportRotationThreshold` | field | `Single` | 角色根一幀轉動超過這個角度（度）就當成瞬移、重置（0＝不判定） |
| `ResolveRoot` | method | `Transform ResolveRoot()` | 角色根：有指定 referenceRoot 就用它，否則用上層的 Animator，再沒有就用自己。 |
| `MarkDirty` | method | `Void MarkDirty()` | 用程式改了 bones 或 referenceRoot 之後呼叫，下一幀重建（同一根骨頭的模擬狀態沿用）。 其他設定（頻率、上限、碰撞體清單等）每幀讀取，不用呼叫。 |
| `ResetDynamics` | method | `Void ResetDynamics()` | 下一幀重新初始化：位移回到平衡位置、速度歸零。 |
| `CaptureRestPose` | method | `Void CaptureRestPose()` | 現在的區域姿勢記成靜止姿勢（每幀動畫前還原到這裡），父骨相對角色根的旋轉記成重力參考。 應用端改了骨頭的靜止姿勢（體型調整等）之後，在 Update 或 LateUpdate（物理寫回之前）呼叫； 重力參考要在角色站立的姿勢下記錄。 |
| `AddImpulse` | method | `Void AddImpulse(Vector3 velocityChange)` | 所有骨頭加上速度變化（世界空間，公尺/秒），下一次模擬時生效。停用中的元件忽略。 |
| `AddImpulse` | method | `Void AddImpulse(Int32 boneIndex, Vector3 velocityChange)` | bones[boneIndex] 加上速度變化（世界空間，公尺/秒），下一次模擬時生效。停用中的元件忽略。 例外 `ArgumentOutOfRangeException`：boneIndex 超出 bones 的範圍。 |
| `AddImpulseAtPosition` | method | `Void AddImpulseAtPosition(Vector3 worldPosition, Vector3 velocityChange, Single radius)` | 撞擊點附近的骨頭加上速度變化（世界空間，公尺/秒），依距離衰減：權重 (1 − 距離/radius)²，radius 以外為 0； radius ≤ 0 時不衰減。距離用模擬那一刻骨頭動畫後的位置計算。停用中的元件忽略。 |
| `IsSimulating` | method | `Boolean IsSimulating(Int32 boneIndex)` | bones[boneIndex] 有沒有在模擬（還沒建置、null 或已刪除、被其他元件先拿走的骨頭是 false）。 |
| `GetOffset` | method | `Vector3 GetOffset(Int32 boneIndex)` | bones[boneIndex] 目前離開錨點的位移（世界空間，公尺；alpha 混合之前）。沒在模擬時是零。 |
| `GetVelocity` | method | `Vector3 GetVelocity(Int32 boneIndex)` | bones[boneIndex] 目前相對錨點的速度（世界空間，公尺/秒）。沒在模擬時是零。 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsSoftBody.Axis

*enum* — 骨頭的前方軸（末端假骨往這個方向延伸）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `X` | value | `Axis` | 區域 X 軸 |
| `Y` | value | `Axis` | 區域 Y 軸 |
| `Z` | value | `Axis` | 區域 Z 軸 |

## 陣列接口

### KPRigChain

*class* — 一條骨鏈的設定（陣列接口用）。欄位與 KawaiiPhysicsChain 相同，骨頭參照是骨架的索引（-1＝沒有）。 執行時的操作（暫時外力、設定倍率、ResetDynamics）也跟元件一樣。KawaiiPhysicsChain 每幀把欄位複製進自己持有的一份，套件內部只有這一條流程。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `rootBone` | field | `Int32` | 模擬的起點骨頭；它底下的骨頭（排除清單以外）全部模擬。起點骨頭本身跟著動畫（骨架的索引；-1＝沒有） |
| `excludeBones` | field | `List<Int32>` | 不模擬的骨頭（連同它底下的骨頭）（骨架的索引） |
| `additionalRootBones` | field | `List<RootBoneSetting>` | 其他的起點骨頭（例如左右兩束頭髮放在同一個元件）；可以各自指定排除清單 |
| `dummyBoneLength` | field | `Single` | 末端假骨的長度（公尺）；0 表示不加。末端骨頭沒有子骨頭時，要靠它才會轉 |
| `boneForwardAxis` | field | `Axis` | 骨頭的前方軸，只用在末端假骨的方向 |
| `component` | field | `Int32` | 元件（角色根）。不指定時用上層的 Animator，再沒有就用自己（骨架的索引；-1＝沒有） |
| `boneSubdivisionCount` | field | `Int32` | 相鄰骨頭之間至少插入幾個假骨（改善碰撞，例如裙子穿腿） |
| `boneSubdivisionCollisionOnly` | field | `Boolean` | 插入的假骨只參加碰撞與約束，不做速度積分 |
| `boneSubdivisionDensifyByRadius` | field | `Boolean` | 依半徑加密：骨頭間距比半徑大的地方插更多（每段最多 50） |
| `boneConstraintSubdivisionCount` | field | `Int32` | 每條橫向骨間約束插入幾個碰撞代理（填補相鄰兩列之間的縫） |
| `boneConstraintSubdivisionFeedbackScale` | field | `Single` | 每條橫向骨間約束插入幾個碰撞代理（填補相鄰兩列之間的縫） |
| `damping` | field | `Single` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `stiffness` | field | `Single` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `worldDampingLocation` | field | `Single` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `worldDampingRotation` | field | `Single` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `radius` | field | `Single` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `limitAngle` | field | `Single` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `planarConstraint` | field | `KPPlanarConstraint` | 阻尼：每步速度保留 1−damping，越大越快停下 |
| `alpha` | field | `Single` | 物理結果與動畫姿勢的混合比例（0＝只有動畫、1＝完全物理） |
| `dampingCurve` | field | `AnimationCurve` | damping 依骨長比例（根 0 → 末端 1）乘上的倍率；沒有關鍵格時是 1 |
| `stiffnessCurve` | field | `AnimationCurve` | stiffness 依骨長比例（根 0 → 末端 1）乘上的倍率；沒有關鍵格時是 1 |
| `worldDampingLocationCurve` | field | `AnimationCurve` | worldDampingLocation 依骨長比例乘上的倍率；沒有關鍵格時是 1 |
| `worldDampingRotationCurve` | field | `AnimationCurve` | worldDampingRotation 依骨長比例乘上的倍率；沒有關鍵格時是 1 |
| `radiusCurve` | field | `AnimationCurve` | radius 依骨長比例乘上的倍率；沒有關鍵格時是 1 |
| `limitAngleCurve` | field | `AnimationCurve` | limitAngle 依骨長比例乘上的倍率；沒有關鍵格時是 1 |
| `simulationSpace` | field | `KPSimulationSpace` | 模擬空間：元件空間（跟著角色，預設）、世界空間、基準骨空間 |
| `simulationBaseBone` | field | `Int32` | simulationSpace＝BaseBoneSpace 時的基準骨頭（骨架的索引；-1＝沒有） |
| `targetFramerate` | field | `Int32` | 模擬的步頻（每秒步數）；固定子步進時每步是 1/targetFramerate 秒 |
| `fixedSubstepping` | field | `Boolean` | 用固定時間的子步進（幀率變動時結果穩定）；關閉時每幀一步 |
| `maxSubsteps` | field | `Int32` | 一幀最多幾個子步進（掉幀時不會無限追趕） |
| `skelCompMoveScale` | field | `Vector3` | 一幀最多幾個子步進（掉幀時不會無限追趕） |
| `teleportDistanceThreshold` | field | `Single` | 角色一幀移動超過這個距離（公尺）就當成瞬移、物理重新開始；0＝不偵測 |
| `teleportRotationThreshold` | field | `Single` | 角色一幀移動超過這個距離（公尺）就當成瞬移、物理重新開始；0＝不偵測 |
| `warmUpFrames` | field | `Int32` | 開始時空跑幾幀，讓物理先穩定 |
| `needWarmUp` | field | `Boolean` | 開始時空跑幾幀，讓物理先穩定 |
| `useWarmUpWhenResetDynamics` | field | `Boolean` | ResetDynamics 之後先暖機 warmUpFrames 幀 |
| `colliders` | field | `List<KPRigCollider>` | 跟這條鏈碰撞的碰撞體 |
| `limitsAsset` | field | `KawaiiPhysicsLimitsAsset` | 碰撞資產（用骨頭名稱指定的一組碰撞形狀） |
| `sharedCollisionSource` | field | `Boolean` | 把自己的碰撞體提供給同一角色底下、同一標籤的其他 Chain |
| `useSharedCollision` | field | `Boolean` | 使用同一角色底下、同一標籤的其他 Chain 提供的碰撞體 |
| `sharedCollisionTag` | field | `String` | 共用碰撞的標籤：同一個根物件底下、標籤相同的來源與使用端才共用 |
| `boneConstraints` | field | `List<BoneConstraint>` | 骨間約束：兩根骨頭之間維持建立時的距離（例如裙子相鄰的兩條） |
| `boneConstraintsAsset` | field | `KawaiiPhysicsConstraintsAsset` | 骨間約束資產（用骨頭名稱指定） |
| `boneConstraintGlobalComplianceType` | field | `KPComplianceType` | 骨間約束預設的柔軟度（約束沒有個別指定時用） |
| `boneConstraintIterationCountBeforeCollision` | field | `Int32` | 碰撞之前解約束的次數 |
| `boneConstraintIterationCountAfterCollision` | field | `Int32` | 碰撞之前解約束的次數 |
| `autoAddChildDummyBoneConstraint` | field | `Boolean` | 碰撞之前解約束的次數 |
| `allowWorldCollision` | field | `Boolean` | 跟場景的 Physics 碰撞體碰撞（每幀用球掃掠） |
| `worldCollisionLayers` | field | `LayerMask` | 世界碰撞要碰的圖層 |
| `useSimpleWorldCollision` | field | `Boolean` | 簡易世界碰撞：定期收集附近的簡單碰撞體（球、膠囊、箱、網格的包圍盒）與地面，不做掃掠 |
| `simpleWorldCollisionGatherInterval` | field | `Single` | 多久重新收集一次附近的碰撞體（秒） |
| `simpleWorldCollisionGatherRadius` | field | `Single` | 多久重新收集一次附近的碰撞體（秒） |
| `simpleWorldCollisionLayers` | field | `LayerMask` | 多久重新收集一次附近的碰撞體（秒） |
| `simpleWorldCollisionGroundCollision` | field | `Boolean` | 往下打一條射線，把地面當成薄箱子 |
| `simpleWorldCollisionMaxShapes` | field | `Int32` | 每種形狀最多收集幾個 |
| `gravity` | field | `Vector3` | 重力（m/s²）。原作預設是 0 |
| `useWorldSpaceGravity` | field | `Boolean` | gravity 是世界空間的向量（關閉時是元件空間） |
| `useLegacyGravity` | field | `Boolean` | 原作 legacy：把 0.5·g·dt² 直接加到位置 |
| `simpleExternalForce` | field | `Vector3` | 固定的外力（加速度，公尺／秒²） |
| `useWorldSpaceSimpleExternalForce` | field | `Boolean` | simpleExternalForce 是世界空間的向量（關閉時是元件空間） |
| `enableWind` | field | `Boolean` | 場景的 WindZone 會吹動 |
| `windScale` | field | `Single` | 場景風（WindZone）的強度倍率 |
| `windDirectionNoiseAngle` | field | `Single` | 場景風的方向每幀隨機偏移的最大角度（度） |
| `externalForces` | field | `List<KPExternalForce>` | 外力（五種，可以多個） |
| `syncBones` | field | `List<SyncBone>` | SyncBone：來源骨頭的移動推動目標骨頭 |
| `MarkDirty` | method | `Void MarkDirty()` | 結構改了（骨頭、碰撞體、約束、SyncBone、模擬空間等），下一幀重建這一條（只有它的模擬狀態重置，其他 Chain 不受影響）。 damping、stiffness、worldDamping、radius、limitAngle 的值會自動偵測，不用呼叫這個。 |
| `MarkSettingsDirty` | method | `Void MarkSettingsDirty()` | 改了依骨長比例的曲線（dampingCurve 等）、外力的骨頭篩選或 forceRateByBoneLengthRate 之後呼叫： 下一幀就地更新，不重建、不重置。例外：依半徑加密細分或有橋接假骨時，改了 radiusCurve 會改變假骨數量，這時重建這一條。 |
| `EvaluateSettings` | method | `KPSettings EvaluateSettings(Single lengthRate)` | 依骨長比例算出的基本設定（還沒夾範圍；倍率在每幀的工作裡乘）。 |
| `ResetDynamics` | method | `Void ResetDynamics(KPTeleportType type = Reset)` | 原作 ResetDynamics。 |
| `RequestTransientExternalForce` | method | `Int64 RequestTransientExternalForce(KPExternalForce force, Single lifetime)` | 加一個暫時外力，lifetime 秒後移除。回傳停止用的 handle。 |
| `RequestTransientGust` | method | `Int64 RequestTransientGust(Single strength, Single riseTime, Single decayTime, Vector3 direction = null, Int32 inheritForceIndex = -1, Single holdTime = 0, Boolean realTimeEnvelope = False)` | 原作 RequestTransientGust：以某個程序風為底（inheritForceIndex；-1 找第一個啟用中的）產生一陣突風。 direction 非零時用世界空間的這個方向。 |
| `RequestStopTransientExternalForce` | method | `Void RequestStopTransientExternalForce(Int64 handle, Single blendOutTime)` | 原作 RequestStopTransientExternalForce：blendOut 秒內淡出（程序風是讓突風落到 0）。 |
| `StartSettingsMultiplier` | method | `Int64 StartSettingsMultiplier(KPSettingsMultiplier scale, Single riseTime, Single holdTime, Single decayTime, Boolean infiniteHold = False)` | 原作 RequestStartPhysicsSettingsMultiplier：起、持、落的包絡；infiniteHold 時持續到停止。 |
| `PushSettingsMultiplier` | method | `Void PushSettingsMultiplier(Int64 handle, KPSettingsMultiplier scale, Single pushAlpha, Int32 leaseEvaluations = 0, Single leaseExpireBlendOut = 0.2)` | 原作 RequestPushPhysicsSettingsMultiplier：由外部每幀推送強度（alpha）。 leaseEvaluations＞0 時，這麼多幀沒推送就在 leaseExpireBlendOut 秒內淡出。 |
| `StopSettingsMultiplier` | method | `Void StopSettingsMultiplier(Int64 handle, Single blendOutTime)` | 原作 RequestStopPhysicsSettingsMultiplier。 |
| `enabled` | field | `Boolean` | 模擬中（停用時骨頭維持動畫姿勢；元件版對應 isActiveAndEnabled） |
| `.ctor` | constructor | `()` | 預設建構子。 |
| `TransientForceCount` | property | `Int32 { get; }` | 目前有效的暫時外力數。 |
| `SettingsMultiplierCount` | property | `Int32 { get; }` | 目前有效的設定倍率數。 |

### KPRigChain.RootBoneSetting

*class* — 額外的起點骨頭（陣列接口用）。欄位與 KawaiiPhysicsChain.RootBoneSetting 相同，骨頭是骨架的索引。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `rootBone` | field | `Int32` | 起點骨頭（骨架的索引；-1＝沒有） |
| `useOverrideExcludeBones` | field | `Boolean` | 用自己的排除清單（不用元件的 excludeBones） |
| `overrideExcludeBones` | field | `List<Int32>` | 這個起點用的排除清單（骨架的索引） |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KPRigChain.BoneConstraint

*class* — 骨間約束（陣列接口用）。欄位與 KawaiiPhysicsChain.BoneConstraint 相同，骨頭是骨架的索引。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `bone1` | field | `Int32` | 約束的第一根骨頭（骨架的索引；-1＝沒有） |
| `bone2` | field | `Int32` | 約束的第二根骨頭（骨架的索引；-1＝沒有） |
| `overrideCompliance` | field | `Boolean` | 用這條約束自己的柔軟度（不用全域預設） |
| `complianceType` | field | `KPComplianceType` | 這條約束的柔軟度 |
| `excludeFromSubdivision` | field | `Boolean` | 這條約束不插入橋接假骨 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KPRigChain.SyncBone

*class* — SyncBone（陣列接口用）。欄位與 KawaiiPhysicsChain.SyncBone 相同，骨頭是骨架的索引。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `bone` | field | `Int32` | 來源骨頭（例：大腿）（骨架的索引；-1＝沒有） |
| `targetRoots` | field | `List<SyncTargetRoot>` | 被推動的骨頭 |
| `globalScale` | field | `Vector3` | 推動量各軸的倍率 |
| `scaleCurveByDeltaDistance` | field | `AnimationCurve` | 依移動距離（公尺）乘上的倍率 |
| `applyDirectionX` | field | `KPSyncDirection` | X 軸只套用正向、負向、兩者或不套用 |
| `applyDirectionY` | field | `KPSyncDirection` | Y 軸只套用正向、負向、兩者或不套用 |
| `applyDirectionZ` | field | `KPSyncDirection` | Z 軸只套用正向、負向、兩者或不套用 |
| `enableDistanceAttenuation` | field | `Boolean` | 依目標骨頭與來源骨頭的距離衰減 |
| `attenuationInnerRadius` | field | `Single` | 距離小於這個值不衰減（公尺） |
| `attenuationOuterRadius` | field | `Single` | 距離大於這個值衰減到 maxAttenuationRate（公尺） |
| `maxAttenuationRate` | field | `Single` | 最遠處的衰減比例 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KPRigChain.SyncTargetRoot

*class* — SyncBone 推動的目標（陣列接口用）。欄位與 KawaiiPhysicsChain.SyncTargetRoot 相同，骨頭是骨架的索引。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `bone` | field | `Int32` | 目標骨頭（骨架的索引；-1＝沒有） |
| `includeChildBones` | field | `Boolean` | 連子骨頭一起推動 |
| `scaleCurveByBoneLengthRate` | field | `AnimationCurve` | 依骨長比例乘上的倍率 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KPRigCollider

*class* — 碰撞體的設定（陣列接口用）：欄位與 KawaiiPhysicsCollider 相同，跟著的骨頭改成骨架的索引 bone。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `bone` | field | `Int32` | 跟著的骨頭（骨架的索引）；-1＝沒有 |
| `shape` | field | `Shape` | 形狀 |
| `center` | field | `Vector3` | 中心（這個 Transform 區域空間的偏移） |
| `direction` | field | `Axis` | 膠囊、錐形膠囊的軸 |
| `radius` | field | `Single` | 半徑（公尺）；錐形膠囊是 +軸端的半徑 |
| `radius1` | field | `Single` | 錐形膠囊 -軸端的半徑（+軸端用 radius） |
| `length` | field | `Single` | 膠囊兩端球心之間的長度 |
| `extent` | field | `Vector3` | 箱的半邊長 |
| `inner` | field | `Boolean` | 球限定：勾選時把骨頭關在球裡，不勾是往外推 |
| `convexMesh` | field | `Mesh` | 凸包：用這個網格的三角形面當平面（網格要是凸的） |
| `.ctor` | constructor | `()` | 預設建構子。 |
| `AxisRotation` | property | `Quaternion { get; }` | 把區域 +Y 轉到 direction 軸的旋轉。 |

### KPRigSoftBody

*class* — 一組軟組織骨頭的設定（陣列接口用）。欄位與 KawaiiPhysicsSoftBody 相同，骨頭參照是骨架的索引（-1＝沒有）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `bones` | field | `List<Int32>` | 要模擬的骨頭，各自是一個彈簧。祖先也是軟組織骨頭時（不論是不是同一個元件），子骨的錨點跟著祖先的位移與伸縮，也感受祖先晃動的慣性（骨架的索引） |
| `referenceRoot` | field | `Int32` | 角色根（慣性拆成角色移動與動畫、重力參考用）；空的話用上層的 Animator，再沒有就用自己。站立時 +Y 要朝上（骨架的索引；-1＝沒有） |
| `frequency` | field | `Single` | 振動頻率（Hz）：無阻尼時每秒來回幾次。越大越硬 |
| `dampingRatio` | field | `Single` | 阻尼比：0＝不衰減、1＝臨界阻尼（不回彈）、大於 1＝過阻尼 |
| `gravity` | field | `Vector3` | 重力加速度（世界空間，公尺/秒²）。平衡時的下垂量＝重力 ÷ (2π×頻率)² |
| `gravityRelativeToRestPose` | field | `Boolean` | 重力相對靜止姿勢：站立時的形狀已經包含重力，只有姿勢偏離站立（躺、趴、倒立）時才往重力方向偏 |
| `externalAcceleration` | field | `Vector3` | 額外的加速度（世界空間，公尺/秒²），應用端可以每幀改 |
| `animationInertia` | field | `Single` | 動畫造成的錨點加減速的慣性倍率（0＝跟著動畫走、不晃；1＝真實慣性）。祖先軟組織骨頭的晃動不受這個影響，一律是真實慣性 |
| `movementInertia` | field | `Single` | 角色根移動、轉向造成的慣性倍率（0＝跟著角色走、不晃；1＝真實慣性） |
| `limitMin` | field | `Vector3` | 各軸往負方向最多離開錨點多遠（≤0；0＝鎖住這個方向） |
| `limitMax` | field | `Vector3` | 各軸往正方向最多離開錨點多遠（≥0；0＝鎖住這個方向） |
| `tilt` | field | `Single` | 依位移傾斜：骨頭朝『父骨 → 位移後的位置』轉動的比例（0＝只平移） |
| `stretchAxis` | field | `Axis` | 壓扁與拉伸的軸（骨頭區域） |
| `stretchAmount` | field | `Single` | 沿軸位移到上限時伸長的比例（負值＝反向；0＝不縮放）。子骨頭跟著縮放（子軟組織骨頭的位置會跟著伸縮移動），建議只用在末端骨頭 |
| `volumePreservation` | field | `Single` | 維持體積：沿軸伸長時另外兩軸縮小的程度（1＝體積不變） |
| `radius` | field | `Single` | 骨頭的碰撞半徑（公尺） |
| `colliders` | field | `List<KPRigCollider>` | 碰撞體（球、膠囊、錐形膠囊、箱、平面；凸包不支援）。清單每幀讀取，增減不用重建 |
| `collisionPriority` | field | `Boolean` | 碰撞優先：碰撞體壓過來時骨頭可以被推到位移上限之外（不穿透）。關掉時位移上限優先（碰撞體可能穿進骨頭） |
| `alpha` | field | `Single` | 物理結果的混合比例（0＝完全照動畫）。它是動畫與物理的混合比例、不是力：改了（一次跳或漸變）時子孫軟組織骨頭不把因此的移動當成加速；祖先真的晃動照常傳下去 |
| `targetFramerate` | field | `Int32` | 子步進的步頻（每秒步數）。彈簧本身是解析解，子步進只影響碰撞與位移上限的精度 |
| `maxSubsteps` | field | `Int32` | 每幀最多幾個子步進 |
| `teleportDistanceThreshold` | field | `Single` | 角色根一幀移動超過這個距離（公尺）就當成瞬移、重置（0＝不判定） |
| `teleportRotationThreshold` | field | `Single` | 角色根一幀轉動超過這個角度（度）就當成瞬移、重置（0＝不判定） |
| `enabled` | field | `Boolean` | 模擬中（停用時骨頭維持動畫姿勢；元件版對應 isActiveAndEnabled） |
| `.ctor` | constructor | `()` | 預設建構子。 |

## 資產

### KawaiiPhysicsConstraintsAsset

*class* — 對應 UKawaiiPhysicsBoneConstraintsDataAsset：用骨頭名稱指定的骨間約束組，可用正規表示式產生。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `entries` | field | `List<Entry>` | 約束（兩根骨頭的名稱） |
| `regexPatterns` | field | `List<RegexPair>` | ApplyRegex 用：兩個樣式在骨頭名稱清單裡依序配對（例：skirt_00_(\\d+) 與 skirt_01_(\\d+)） |
| `ApplyRegex` | method | `Int32 ApplyRegex(IEnumerable<String> boneNames)` | 原作 ApplyRegex：骨頭名稱清單每行一個，兩個樣式各自依序找下一個符合的，配成一對。 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsConstraintsAsset.Entry

*class* — 一條骨間約束。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `boneName1` | field | `String` | 第一根骨頭的名稱 |
| `boneName2` | field | `String` | 第二根骨頭的名稱 |
| `overrideCompliance` | field | `Boolean` | 用這條約束自己的柔軟度 |
| `complianceType` | field | `KPComplianceType` | 這條約束的柔軟度 |
| `excludeFromSubdivision` | field | `Boolean` | 這條約束不插入橋接假骨 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsConstraintsAsset.RegexPair

*class* — ApplyRegex 用的一對樣式。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `pattern1` | field | `String` | 第一根骨頭名稱的正規表示式 |
| `pattern2` | field | `String` | 第二根骨頭名稱的正規表示式 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsEventPreset

*class* — AnimationEvent 用的事件內容。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `kind` | field | `Kind` | 事件種類 |
| `chainNameFilter` | field | `String` | 空的話作用到全部 Chain；否則只作用到名稱含這個字串的 |
| `strength` | field | `Single` | 突風強度 |
| `riseTime` | field | `Single` | 突風升起的時間（秒） |
| `holdTime` | field | `Single` | 突風維持的時間（秒） |
| `decayTime` | field | `Single` | 突風落下的時間（秒） |
| `direction` | field | `Vector3` | 突風方向（零向量＝沿用程序風的方向） |
| `realTimeEnvelope` | field | `Boolean` | 突風的包絡用真實時間（不受程序風 timeScale 影響） |
| `force` | field | `KPExternalForce` | 要加的暫時外力 |
| `lifetime` | field | `Single` | 要加的暫時外力 |
| `multiplier` | field | `KPSettingsMultiplier` | 設定倍率 |
| `duration` | field | `Single` | 倍率的總時間（秒） |
| `blendIn` | field | `Single` | 倍率升起的時間（秒） |
| `blendOut` | field | `Single` | 倍率落下的時間（秒） |
| `softBodyImpulse` | field | `Vector3` | 速度變化（公尺/秒），角色根（SoftBody 的 referenceRoot）的區域空間；例：(0,-0.6,0)＝往下一頓 |
| `softBodyNameFilter` | field | `String` | 空的話作用到全部 SoftBody；否則只作用到名稱含這個字串的 |
| `Apply` | method | `Void Apply(KawaiiPhysicsChain chain)` | 把這個事件套到一條鏈。 |
| `Apply` | method | `Void Apply(KPRigChain chain)` | 把這個事件套到陣列接口的一條鏈。 |
| `Apply` | method | `Void Apply(KawaiiPhysicsSoftBody softBody)` | 把這個事件套到一個軟組織元件（只有 SoftBodyImpulse 有作用）。 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsEventPreset.Kind

*enum* — 事件種類。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `Gust` | value | `Kind` | 突風（RequestTransientGust） |
| `AddExternalForce` | value | `Kind` | 暫時外力（RequestTransientExternalForce） |
| `SettingsMultiplier` | value | `Kind` | 設定倍率（StartSettingsMultiplier） |
| `SoftBodyImpulse` | value | `Kind` | 軟組織的衝量（KawaiiPhysicsSoftBody.AddImpulse；作用到 SoftBody，不作用到 Chain） |

### KawaiiPhysicsLimitsAsset

*class* — 對應 UKawaiiPhysicsLimitsDataAsset：用骨頭名稱指定的一組碰撞形狀，可選擇左右鏡像。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `entries` | field | `List<Entry>` | 碰撞形狀（掛在哪根骨頭用名稱指定） |
| `mirror` | field | `Boolean` | 產生左右鏡像 |
| `mirrorFrom` | field | `String` | 名稱裡含這個字串的條目，換成 mirrorTo 後再產生一份 |
| `mirrorTo` | field | `String` | 鏡像後把 mirrorFrom 換成這個字串 |
| `mirrorAxis` | field | `Axis` | 在骨頭區域空間裡翻轉的軸 |
| `Resolve` | method | `List<Entry> Resolve()` | 含鏡像後的全部條目。已經有同名骨頭條目的反側不再產生（原作 bSkipMirroredBoneWithExistingCollision）。 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsLimitsAsset.Entry

*class* — 一條骨間約束。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `shape` | field | `Shape` | 形狀 |
| `boneName` | field | `String` | 掛在哪根骨頭（名稱） |
| `offset` | field | `Vector3` | 骨頭區域空間的位置偏移 |
| `rotation` | field | `Vector3` | 骨頭區域空間的旋轉（歐拉角，度） |
| `direction` | field | `Axis` | 膠囊、錐形膠囊的軸 |
| `radius` | field | `Single` | 半徑（公尺）；錐形膠囊是 +軸端的半徑 |
| `radius1` | field | `Single` | 半徑（公尺）；錐形膠囊是 +軸端的半徑 |
| `length` | field | `Single` | 半徑（公尺）；錐形膠囊是 +軸端的半徑 |
| `extent` | field | `Vector3` | 半徑（公尺）；錐形膠囊是 +軸端的半徑 |
| `inner` | field | `Boolean` | 球：把骨頭關在球裡 |
| `convexMesh` | field | `Mesh` | 凸包：用這個網格的三角形面當平面 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsPreset

*class* — 對應 UKawaiiPhysicsPresetDataAsset：存一組可在角色之間共用的設定。 欄位名跟 KawaiiPhysicsChain 一樣，套用時用 JSON 覆寫同名欄位。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `data` | field | `Data` | 預設的設定值 |
| `ApplyTo` | method | `Void ApplyTo(KawaiiPhysicsChain chain)` | 把設定套到 chain。只改了設定就在下一幀就地更新（不重置），改到結構（骨頭細分等）才重建這一條。 |
| `CaptureFrom` | method | `Void CaptureFrom(KawaiiPhysicsChain chain)` | 把 chain 目前的設定記到這個預設。 |
| `ApplyTo` | method | `Void ApplyTo(KPRigChain chain)` | 把設定套到陣列接口的 chain（欄位名稱相同）。只改了設定就就地更新，改到結構才重建這一條。 |
| `CaptureFrom` | method | `Void CaptureFrom(KPRigChain chain)` | 把陣列接口的 chain 目前的設定記到這個預設。 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsPreset.Data

*class* — 預設的設定值（欄位名稱與 KawaiiPhysicsChain 相同）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `damping` | field | `Single` | 同 KawaiiPhysicsChain.damping |
| `stiffness` | field | `Single` | 同 KawaiiPhysicsChain.stiffness |
| `worldDampingLocation` | field | `Single` | 同 KawaiiPhysicsChain.worldDampingLocation |
| `worldDampingRotation` | field | `Single` | 同 KawaiiPhysicsChain.worldDampingRotation |
| `radius` | field | `Single` | 同 KawaiiPhysicsChain.radius |
| `limitAngle` | field | `Single` | 同 KawaiiPhysicsChain.limitAngle |
| `dampingCurve` | field | `AnimationCurve` | 同 KawaiiPhysicsChain.dampingCurve |
| `stiffnessCurve` | field | `AnimationCurve` | 同 KawaiiPhysicsChain.stiffnessCurve |
| `worldDampingLocationCurve` | field | `AnimationCurve` | 同 KawaiiPhysicsChain.worldDampingLocationCurve |
| `worldDampingRotationCurve` | field | `AnimationCurve` | 同 KawaiiPhysicsChain.worldDampingRotationCurve |
| `radiusCurve` | field | `AnimationCurve` | 同 KawaiiPhysicsChain.radiusCurve |
| `limitAngleCurve` | field | `AnimationCurve` | 同 KawaiiPhysicsChain.limitAngleCurve |
| `dummyBoneLength` | field | `Single` | 同 KawaiiPhysicsChain.dummyBoneLength |
| `planarConstraint` | field | `KPPlanarConstraint` | 同 KawaiiPhysicsChain.planarConstraint |
| `targetFramerate` | field | `Int32` | 同 KawaiiPhysicsChain.targetFramerate |
| `gravity` | field | `Vector3` | 同 KawaiiPhysicsChain.gravity |
| `enableWind` | field | `Boolean` | 同 KawaiiPhysicsChain.enableWind |
| `windScale` | field | `Single` | 同 KawaiiPhysicsChain.windScale |
| `windDirectionNoiseAngle` | field | `Single` | 同 KawaiiPhysicsChain.windDirectionNoiseAngle |
| `boneSubdivisionCount` | field | `Int32` | 同 KawaiiPhysicsChain.boneSubdivisionCount |
| `boneSubdivisionCollisionOnly` | field | `Boolean` | 同 KawaiiPhysicsChain.boneSubdivisionCollisionOnly |
| `boneConstraintSubdivisionCount` | field | `Int32` | 同 KawaiiPhysicsChain.boneConstraintSubdivisionCount |
| `boneConstraintGlobalComplianceType` | field | `KPComplianceType` | 同 KawaiiPhysicsChain.boneConstraintGlobalComplianceType |
| `boneConstraintIterationCountBeforeCollision` | field | `Int32` | 同 KawaiiPhysicsChain.boneConstraintIterationCountBeforeCollision |
| `boneConstraintIterationCountAfterCollision` | field | `Int32` | 同 KawaiiPhysicsChain.boneConstraintIterationCountAfterCollision |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KawaiiPhysicsWindChannel

*class* — 共用的程序風（對應 Shared Publisher 發布的風）：參照同一個頻道的角色共用參數、時鐘與突風，一幀只前進一次。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `parameters` | field | `KPProceduralWindParams` | 程序風的參數（參照這個頻道的角色共用） |
| `RequestGust` | method | `Void RequestGust(Single strength, Single rise, Single decay, Single hold = 0, Boolean realTime = False)` | 觸發突風（參照這個頻道的所有角色）：strength 強度，rise／hold／decay 升起、維持、落下的秒數。 |
| `RequestGustStop` | method | `Void RequestGustStop(Single blendOut)` | 目前的突風在 blendOut 秒內落到 0。 |
| `.ctor` | constructor | `()` | 預設建構子。 |
| `State` | property | `KPProceduralWindState { get; }` | 共用的時鐘與突風狀態。 |

## 外力

### KPCurveEvaluateType

*enum* — 曲線外力在一幀裡怎麼取值。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `Single` | value | `KPCurveEvaluateType` | 幀結束時的值 |
| `Average` | value | `KPCurveEvaluateType` | 一幀內取樣的平均 |
| `Max` | value | `KPCurveEvaluateType` | 一幀內取樣的最大值 |
| `Min` | value | `KPCurveEvaluateType` | 一幀內取樣的最小值 |

### KPExternalForce

*abstract class* — 對應 FKawaiiPhysics_ExternalForce。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `enabled` | field | `Boolean` | 啟用 |
| `applyBoneFilter` | field | `List<Transform>` | 空的話全部骨頭都套用 |
| `ignoreBoneFilter` | field | `List<Transform>` | 不受這個力的骨頭 |
| `applyBoneNameFilter` | field | `List<String>` | 依骨頭名稱：空的話全部骨頭都套用（陣列接口 KPRig 只看名稱的篩選） |
| `ignoreBoneNameFilter` | field | `List<String>` | 依骨頭名稱：不受這個力的骨頭 |
| `space` | field | `KPForceSpace` | 力的向量的空間 |
| `randomForceScaleRange` | field | `Vector2` | 每幀在這個範圍內隨機取一個倍率 |
| `forceRateByBoneLengthRate` | field | `AnimationCurve` | 依骨長比例（根 0、末端 1）乘上的倍率；沒有 key 視為 1 |
| `oneShot` | field | `Boolean` | 只作用一幀就移除 |
| `CanApplyTo` | method | `Boolean CanApplyTo(Transform bone)` | 這根骨頭會不會受這個力（applyBoneFilter 有指定時只限其中、ignoreBoneFilter 裡的除外）。 |
| `CanApplyToName` | method | `Boolean CanApplyToName(String boneName)` | 這個名稱的骨頭會不會受這個力（applyBoneNameFilter 有指定時只限其中、ignoreBoneNameFilter 裡的除外）。 |
| `CloneForTransient` | method | `KPExternalForce CloneForTransient()` | 複製一份（暫時外力用，不跟原本的共用狀態）。 |

### KPForceBasic

*class* — 對應 FKawaiiPhysics_ExternalForce_Basic：固定方向的力，可以每隔一段時間才作用一次。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `forceDir` | field | `Vector3` | 力（加速度，公尺／秒²） |
| `interval` | field | `Single` | 大於 0 時每隔這麼多秒作用一幀 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KPForceCurve

*class* — 對應 FKawaiiPhysics_ExternalForce_Curve：隨時間變化的力（XYZ 三條曲線）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `forceX` | field | `AnimationCurve` | X 方向的力隨時間的曲線 |
| `forceY` | field | `AnimationCurve` | Y 方向的力隨時間的曲線 |
| `forceZ` | field | `AnimationCurve` | Z 方向的力隨時間的曲線 |
| `curveEvaluateType` | field | `KPCurveEvaluateType` | 一幀裡怎麼取曲線的值 |
| `substepCount` | field | `Int32` | Average／Max／Min 時一幀取樣幾次 |
| `timeScale` | field | `Single` | Average／Max／Min 時一幀取樣幾次 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KPForceGravity

*class* — 對應 FKawaiiPhysics_ExternalForce_Gravity：加在速度上的重力（世界空間）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `usePhysicsGravityScale` | field | `Boolean` | 勾選時乘上 Physics.gravity 的大小（原作是 CharacterMovement 的 GravityZ） |
| `useOverrideGravityDirection` | field | `Boolean` | 用自訂的重力方向 |
| `overrideGravityDirection` | field | `Vector3` | 自訂的重力方向 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KPForceProceduralWind

*class* — 對應 FKawaiiPhysics_ExternalForce_ProceduralWind：常數＋搖擺（正弦）＋漣漪（沿骨長有相位差）＋強度週期＋平滑雜訊＋突風。 windSource＝Shared 時，參數與時鐘來自共用的 KawaiiPhysicsWindChannel（原作的 Shared Publisher）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `windSource` | field | `KPWindSource` | 參數與時鐘的來源：這個外力自己（Local）或共用頻道（Shared） |
| `sharedChannel` | field | `KawaiiPhysicsWindChannel` | 共用頻道（windSource＝Shared 時） |
| `parameters` | field | `KPProceduralWindParams` | 這個外力自己的參數（windSource＝Local 時） |
| `RequestGust` | method | `Void RequestGust(Single strength, Single rise, Single decay, Single hold = 0, Boolean realTime = False)` | 觸發突風：strength 強度，rise／hold／decay 升起、維持、落下的秒數。 |
| `RequestGustStop` | method | `Void RequestGustStop(Single blendOut)` | 目前的突風在 blendOut 秒內落到 0。 |
| `Sample` | static method | `Void Sample(KPProceduralWindParams p, KPProceduralWindState s, Single& sinesWithoutRipple, Single& strengthCycle, Single& random, Single& gust)` | 原作 ComputeWindSample（lengthRate＝0 的部分；漣漪的骨頭相位在工作裡算）。 |
| `CloneForTransient` | method | `KPExternalForce CloneForTransient()` | 複製一份（時鐘與突風狀態不共用）。 |
| `.ctor` | constructor | `()` | 預設建構子。 |
| `State` | property | `KPProceduralWindState { get; }` | 目前的時鐘與突風狀態（共用時是頻道的）。 |

### KPForceSpace

*enum* — 外力向量的空間。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `ComponentSpace` | value | `KPForceSpace` | 元件空間 |
| `WorldSpace` | value | `KPForceSpace` | 世界空間 |
| `BoneSpace` | value | `KPForceSpace` | 骨頭空間（各骨頭的區域空間） |

### KPForceWind

*class* — 對應 FKawaiiPhysics_ExternalForce_Wind：場景的 WindZone 當成力（每根骨頭各自取風、加錐角雜訊）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `windDirectionNoiseAngle` | field | `Single` | 風向隨機偏移的最大角度（度） |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KPProceduralWindParams

*class* — 程序風的參數（ProceduralWind 與共用風頻道共用）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `windDirection` | field | `Vector3` | 風向（世界空間） |
| `windDirectionNoiseAngle` | field | `Single` | 風向雜訊的最大角度（度） |
| `windDirectionNoisePeriod` | field | `Single` | 風向雜訊的最大角度（度） |
| `timeScale` | field | `Single` | 風向雜訊的最大角度（度） |
| `constantForce` | field | `Single` | 固定的風力 |
| `swayForce` | field | `Single` | 搖擺（正弦）的強度 |
| `swayPeriod` | field | `Single` | 搖擺的週期（秒） |
| `swayPhaseOffset` | field | `Single` | 搖擺的週期（秒） |
| `rippleForce` | field | `Single` | 漣漪（沿骨長傳遞的波）的強度 |
| `ripplePeriod` | field | `Single` | 漣漪的週期（秒） |
| `ripplePhaseOffset` | field | `Single` | 漣漪的週期（秒） |
| `rippleTipPhaseDelay` | field | `Single` | 末端相對根的相位延遲（度） |
| `strengthCycleRange` | field | `Vector2` | 強度週期的範圍（最小、最大倍率） |
| `strengthCyclePeriod` | field | `Single` | 強度週期（秒） |
| `strengthCyclePhaseOffset` | field | `Single` | 強度週期（秒） |
| `randomForce` | field | `Single` | 平滑雜訊的強度 |
| `randomForcePeriod` | field | `Single` | 雜訊的週期（秒） |
| `seed` | field | `Int32` | 雜訊的週期（秒） |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KPProceduralWindState

*class* — 程序風的時鐘與突風（對應 FKawaiiProceduralWindRuntimeState）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `Time` | field | `Single` | 時鐘（秒，乘過 timeScale）。 |
| `UnscaledTime` | field | `Single` | 沒乘 timeScale 的時鐘（秒）。 |
| `GustActive` | field | `Boolean` | 突風進行中。 |
| `GustRealTime` | field | `Boolean` | 突風用沒乘 timeScale 的時鐘。 |
| `GustStart` | field | `Single` | 突風開始的時間（秒）。 |
| `GustStrength` | field | `Single` | 突風開始的時間（秒）。 |
| `GustRise` | field | `Single` | 突風開始的時間（秒）。 |
| `GustHold` | field | `Single` | 突風開始的時間（秒）。 |
| `GustDecay` | field | `Single` | 突風開始的時間（秒）。 |
| `EvaluateGust` | method | `Single EvaluateGust(Single t)` | 時間 t 的突風強度。 |
| `Advance` | method | `Void Advance(Single dt, Single timeScale)` | 時鐘前進 dt 秒（乘上 timeScale）。 |
| `AdvanceOncePerFrame` | method | `Void AdvanceOncePerFrame(Int32 frame, Single dt, Single timeScale)` | 共用頻道一幀只前進一次。 |
| `StartGust` | method | `Void StartGust(Single strength, Single rise, Single decay, Single hold, Boolean realTime)` | 開始突風：strength 強度，rise／hold／decay 升起、維持、落下的秒數，realTime＝用沒乘 timeScale 的時鐘。 |
| `StopGust` | method | `Void StopGust(Single blendOut)` | 原作 ConsumePendingRequests 的停止：目前強度在 blendOut 秒內落到 0。 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KPWindSource

*enum* — 程序風的參數與時鐘的來源。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `Local` | value | `KPWindSource` | 這個外力自己的參數與時鐘 |
| `Shared` | value | `KPWindSource` | 共用頻道（KawaiiPhysicsWindChannel） |

## 系統與設定

### KPBackend

*enum* — 解算的執行方式。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `BurstParallel` | value | `KPBackend` | Burst，一個角色一個工作，丟給工作執行緒平行跑（預設）。 |
| `BurstSingleThread` | value | `KPBackend` | Burst，在主執行緒依序跑。 |
| `Managed` | value | `KPBackend` | 不經 Burst、直接呼叫 C# 解算（可以下中斷點除錯）。 |

### KPComplianceType

*enum* — 骨間約束的柔軟度（XPBD compliance；原作的七種，由硬到軟）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `Concrete` | value | `KPComplianceType` | 混凝土（最硬） |
| `Wood` | value | `KPComplianceType` | 木頭 |
| `Leather` | value | `KPComplianceType` | 皮革 |
| `Tendon` | value | `KPComplianceType` | 肌腱 |
| `Rubber` | value | `KPComplianceType` | 橡膠 |
| `Muscle` | value | `KPComplianceType` | 肌肉 |
| `Fat` | value | `KPComplianceType` | 脂肪（最軟） |

### KPPlanarConstraint

*enum* — 鎖住模擬空間的一軸。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `None` | value | `KPPlanarConstraint` | 不鎖 |
| `X` | value | `KPPlanarConstraint` | 鎖 X（只在 YZ 平面動） |
| `Y` | value | `KPPlanarConstraint` | 鎖 Y（只在 XZ 平面動） |
| `Z` | value | `KPPlanarConstraint` | 鎖 Z（只在 XY 平面動） |

### KPSettings

*struct* — 每根骨頭的物理設定。對應 FKawaiiPhysicsSettings。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `Damping` | field | `Single` | 阻尼。 |
| `Stiffness` | field | `Single` | 剛性。 |
| `WorldDampingLocation` | field | `Single` | 抵銷角色移動慣性的比例。 |
| `WorldDampingRotation` | field | `Single` | 抵銷角色旋轉慣性的比例。 |
| `Radius` | field | `Single` | 碰撞半徑（公尺）。 |
| `LimitAngle` | field | `Single` | 最大偏離角度（度）。 |

### KPSettingsMultiplier

*struct* — 暫時的設定倍率（全部 1＝不變）。對應 FKawaiiPhysicsSettingsMultiplier。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `Damping` | field | `Single` | damping 的倍率。 |
| `Stiffness` | field | `Single` | stiffness 的倍率。 |
| `WorldDampingLocation` | field | `Single` | worldDampingLocation 的倍率。 |
| `WorldDampingRotation` | field | `Single` | worldDampingRotation 的倍率。 |
| `Radius` | field | `Single` | radius 的倍率。 |
| `LimitAngle` | field | `Single` | limitAngle 的倍率。 |
| `One` | static property | `KPSettingsMultiplier { get; }` | 全部是 1（不改變）的倍率。 |

### KPSimulationSpace

*enum* — 模擬空間。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `ComponentSpace` | value | `KPSimulationSpace` | 元件空間（跟著角色，預設） |
| `WorldSpace` | value | `KPSimulationSpace` | 世界空間 |
| `BaseBoneSpace` | value | `KPSimulationSpace` | 基準骨空間（simulationBaseBone） |

### KPSyncDirection

*enum* — SyncBone 的一個方向要不要套用。對應 ESyncBoneDirection。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `Both` | value | `KPSyncDirection` | 正負都套用 |
| `Positive` | value | `KPSyncDirection` | 只套用正向 |
| `Negative` | value | `KPSyncDirection` | 只套用負向 |
| `None` | value | `KPSyncDirection` | 不套用 |

### KPSystem

*static class* — 把所有 KawaiiPhysicsChain 打包成一組 NativeArray，每幀： Update 開頭（動畫前）把骨頭放回靜止姿勢；所有 LateUpdate 之後 準備外力等 → 讀 → 解 →（世界碰撞）→ 寫。 兩個時間點都用 PlayerLoop 插入，不用 MonoBehaviour。 Chain 加入、移除或結構變更時只重建那一條（其他 Chain 的模擬狀態保留）；只改設定時就地更新，不重建。 軟組織（KawaiiPhysicsSoftBody）也在同樣兩個時間點處理，模擬在 Chain 之前。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `Backend` | static field | `KPBackend` | 解算的執行方式（預設 Burst 平行）。 |
| `Register` | static method | `Void Register(KawaiiPhysicsChain chain)` | 加入模擬（KawaiiPhysicsChain.OnEnable 會呼叫）。下一幀只建這一條，其他 Chain 不受影響。 |
| `Unregister` | static method | `Void Unregister(KawaiiPhysicsChain chain)` | 移出模擬（KawaiiPhysicsChain.OnDisable 會呼叫）。當場移除這一條：骨頭立刻放回靜止姿勢，這一幀之後的解算也不再寫回； 並忘掉這些骨頭的靜止姿勢，之後再有 Chain 用到同一根骨頭（重新啟用、換裝後的新 Chain）時，從那時候的姿勢重新記。 |
| `Register` | static method | `Void Register(KawaiiPhysicsSoftBody softBody)` | 軟組織加入模擬（KawaiiPhysicsSoftBody.OnEnable 會呼叫）。骨頭的靜止姿勢在這時記下來（動畫還沒動）。 下一幀重建軟組織，其他軟組織元件的模擬狀態沿用。 |
| `Unregister` | static method | `Void Unregister(KawaiiPhysicsSoftBody softBody)` | 軟組織移出模擬（KawaiiPhysicsSoftBody.OnDisable 會呼叫）。下一幀動畫前骨頭放回靜止姿勢。 |
| `RefreshWindZones` | static method | `Void RefreshWindZones()` | 場景的 WindZone 重新收集。場景載入／卸載、Chain 加入或移除時會自動做； 執行中用程式新增 WindZone 時呼叫這個（刪除的 WindZone 每幀會自動略過）。 |
| `RestorePhase` | static method | `Void RestorePhase()` | 動畫更新前呼叫。 |
| `SimulatePhase` | static method | `Void SimulatePhase(Single deltaTime)` | 所有 LateUpdate 之後呼叫。軟組織先解（寫回之後 Chain 才讀，掛在軟組織骨頭底下的 Chain 會跟著動）。 |
| `Shutdown` | static method | `Void Shutdown()` | 全部清掉並釋放原生記憶體（離開 Play、程式結束、重新編譯腳本時自動呼叫）。 |
| `AfterSimulate` | event | `Action` | 物理寫回 Transform 之後（同一幀、所有 LateUpdate 之後）。要拿物理後骨頭的處理（鏡頭跟隨、截圖）掛在這裡。 |
| `FrameIndex` | static property | `Int32 { get; }` | SimulatePhase 的呼叫次數與累計時間（編輯模式下 Time.frameCount 不會前進）。 |
| `SimTime` | static property | `Single { get; }` | 累計的模擬時間（秒）。 |
| `ChainCount` | static property | `Int32 { get; }` | 模擬中的 Chain 數。 |
| `BoneCount` | static property | `Int32 { get; }` | 模擬中的骨頭數（含末端、細分、橋接的假骨）。 |
| `LastSyncMs` | static property | `Double { get; }` | 上一次 Chain 加入、移除或重建結構時，同步花的時間（毫秒）。 |
| `LastSimulateMs` | static property | `Double { get; }` | 上一幀 SimulatePhase 在主執行緒花的時間（毫秒，含等待工作完成；含軟組織）。 |
| `SoftBodyCount` | static property | `Int32 { get; }` | 模擬中的軟組織元件數。 |
| `SoftBoneCount` | static property | `Int32 { get; }` | 模擬中的軟組織骨頭數。 |
| `RestCacheCount` | static property | `Int32 { get; }` | 靜止姿勢快取的筆數（測試用）。 |

### KPTeleportType

*enum* — ResetDynamics 的種類。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `Teleport` | value | `KPTeleportType` | 這一幀不反映元件的移動（原作 TeleportPhysics）。 |
| `Reset` | value | `KPTeleportType` | 重新初始化（位置回到姿勢），可搭配暖機（原作 ResetPhysics）。 |

## Editor

### KawaiiPhysicsChainEditor

*class* — KawaiiPhysicsChain 的 Inspector：預設欄位＋「新增外力」按鈕。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `OnInspectorGUI` | method | `Void OnInspectorGUI()` | 畫 Inspector。 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KPExternalForceDrawer

*class* — 外力欄位的 Inspector：標題列的型別選單（換型別、清空）＋欄位。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `GetPropertyHeight` | method | `Single GetPropertyHeight(SerializedProperty property, GUIContent label)` | 欄位的高度。 |
| `OnGUI` | method | `Void OnGUI(Rect position, SerializedProperty property, GUIContent label)` | 畫欄位。 |
| `.ctor` | constructor | `()` | 預設建構子。 |

### KPExternalForceMenu

*static class* — 外力型別的清單與設定（Inspector 的選單、測試、編輯器腳本共用）。

| 成員 | 種類 | 型別／簽章 | 說明 |
|---|---|---|---|
| `DisplayName` | static method | `String DisplayName(Type t)` | 選單上顯示的型別名稱（去掉 KPForce 前綴）。 |
| `SetType` | static method | `Void SetType(Object[] targets, String path, Type type)` | 把 path 的外力換成 type 的新實例（null＝清空）；可以復原。 |
| `Add` | static method | `Void Add(Object[] chains, Type type)` | 在 Chain 的外力清單最後加一個 type 的新實例；可以復原。 |
| `ForceTypes` | static property | `Type[] { get; }` | 可以加的外力型別（KPExternalForce 的非抽象子類別、有無參數建構子；使用者自訂的也會列出）。 |

