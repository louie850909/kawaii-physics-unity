# KawaiiPhysics for Unity

[KawaiiPhysics](https://github.com/pafuhana1213/KawaiiPhysics)（Unreal Engine 的骨頭偽物理，pafuhana1213，MIT）移植到 Unity 6.6 的套件。
頭髮、裙子、飾品這類「沿著骨頭鏈晃動」的東西用它：不用 Rigidbody，演算法與原作相同（Verlet＋剛性＋碰撞＋角度限制＋XPBD 骨間約束），
用 Burst＋Job System 平行處理所有角色。
胸、臀這類「整塊肉跟著身體走，被甩或被撞時晃一下再回來」的部位用平移式的軟組織元件 `KawaiiPhysicsSoftBody`（v0.4 起）。

- [安裝](#安裝)
- [快速開始](#快速開始)
- [元件與資產](#元件與資產)
- [執行時 API](#執行時-api)
- [軟組織（胸、臀）](#軟組織胸臀)
- [執行順序](#執行順序)
- [效能](#效能)
- [平台](#平台)
- [限制](#限制)
- [測試](#測試)
- [API 參考](api.md)

## 安裝

需求：Unity 6000.6 以上。相依的套件與模組（package.json 已宣告，會自動加入）：
Burst 2.0、Mathematics 1.4、Collections 6.6、Physics、Wind、Animation、JSONSerialize 模組。

Package Manager →「+」→「Add package from git URL…」貼上：

```
https://github.com/louie850909/kawaii-physics-unity.git
```

指定版本時在網址後面加 `#v0.5.0`。也可以把這個 repo 下載下來，在 `Packages/manifest.json` 用本機路徑：

```json
"com.louie.kawaii-physics": "file:<套件資料夾的路徑>"
```

範例在 Package Manager 的 Samples 分頁匯入（Basic）。

## 快速開始

1. 角色上有一串骨頭（例如 `hair_00 → hair_01 → …`）。在任何物件上加 `KawaiiPhysicsChain`，`rootBone` 指到鏈的第一根骨頭。
   `component` 不指定時用上層的 Animator（角色根）。
2. 末端骨頭沒有子骨頭時，設 `dummyBoneLength`（末端假骨）末端才會轉。
3. 要碰撞的地方（頭、肩、大腿）加 `KawaiiPhysicsCollider`，拖進 `colliders`。
4. 按 Play。調 `damping`、`stiffness`、`radius`、`limitAngle`；Play 中改這些值會在下一幀就地生效，不會重置。

## 元件與資產

| 名稱 | 用途 |
|---|---|
| `KawaiiPhysicsChain` | 一條（或多條）骨頭鏈的物理。對應原作一個 AnimNode：骨頭、細分假骨、物理設定與依骨長比例的曲線、模擬空間、子步進、暖機、瞬移、平面限制、alpha、碰撞、骨間約束、重力、場景風、外力、SyncBone、預設 |
| `KawaiiPhysicsSoftBody` | 平移式軟組織（胸、臀、腹部）：以動畫姿勢為錨點的阻尼彈簧，位移上限、慣性拆分、依位移傾斜、壓扁與拉伸、衝量 API（見[軟組織](#軟組織胸臀)） |
| `KawaiiPhysicsCollider` | 球（內／外）、膠囊、錐形膠囊、箱、平面、凸包（凸包只有 Chain 支援） |
| `KawaiiPhysicsLimitsAsset` | 用骨頭名稱指定的一組碰撞形狀，可左右鏡像（套到不同角色用） |
| `KawaiiPhysicsConstraintsAsset` | 用骨頭名稱指定的骨間約束，可用正規表示式產生 |
| `KawaiiPhysicsPreset` | 設定的預設（`ApplyTo`、`CaptureFrom`） |
| `KawaiiPhysicsWindChannel` | 多個角色共用的程序風（同一個時鐘與突風） |
| `KawaiiPhysicsEventPreset`＋`KawaiiPhysicsAnimationEvents` | 動畫片段的 AnimationEvent 觸發突風、暫時外力、設定倍率、軟組織的衝量、ResetDynamics |

外力（`externalForces`，Inspector 用「新增外力 ▾」或元素標題的型別選單加入）：
`KPForceBasic`（固定方向，可間歇）、`KPForceGravity`、`KPForceCurve`（隨時間的曲線）、`KPForceWind`（場景的 WindZone）、
`KPForceProceduralWind`（常數＋搖擺＋漣漪＋強度週期＋雜訊＋突風，可共用頻道）。

## 執行時 API

```csharp
chain.ResetDynamics();                                   // 物理重新開始（瞬移後、換裝後）
chain.ResetDynamics(KPTeleportType.Teleport);            // 只把這一幀的移動當成瞬移
long h = chain.RequestTransientExternalForce(new KPForceBasic { forceDir = Vector3.up * 5f }, 0.5f);
chain.RequestStopTransientExternalForce(h, 0.2f);
chain.RequestTransientGust(strength: 2f, riseTime: 0.2f, decayTime: 1f);
var m = KPSettingsMultiplier.One; m.Stiffness = 0.3f;
long s = chain.StartSettingsMultiplier(m, 0.2f, 1f, 0.5f); // 起、持、落（秒）
chain.PushSettingsMultiplier(s, m, 1f);                  // 由程式逐幀驅動
chain.MarkDirty();                                       // 結構改了（骨頭、碰撞體、約束、外力的骨頭篩選或曲線…）：只重建這一條
chain.MarkSettingsDirty();                               // 改了依骨長比例的曲線、外力的骨頭篩選或曲線：就地更新（不重置）
KPSystem.RefreshWindZones();                             // 執行中用程式新增 WindZone 之後
KPSystem.AfterSimulate += OnPhysicsDone;                 // 物理寫回 Transform 之後（鏡頭跟隨等）
```

- `damping`、`stiffness`、`worldDampingLocation`、`worldDampingRotation`、`radius`、`limitAngle` 的值每幀自動偵測，改了就地生效。
- 加入、移除、停用一條 Chain，或改了它的結構，只重建那一條；共用碰撞的來源變了時，同家族的使用端也會重建。其他角色的模擬狀態保留（空洞過半整理時資料會搬到新位置，狀態一樣保留）。停用或移除時骨頭**當場**放回靜止姿勢（同一幀之後的解算也不再寫回），並忘掉那些骨頭的靜止姿勢：之後重新啟用、或換裝後新的 Chain 用到同一根骨頭時，從那時候的姿勢重新記。所以停用之後可以直接改骨頭的區域姿勢（換裝、體型），不會被舊的靜止姿勢蓋掉。
- 在 Inspector 改欄位：結構沒變只就地更新設定，結構變了才重建那一條。外力的骨頭篩選（applyBoneFilter／ignoreBoneFilter）與依骨長比例的曲線（forceRateByBoneLengthRate）不算結構，就地更新；用程式改這些之後呼叫 `MarkSettingsDirty()`。
- 執行中打開或關掉 `useSimpleWorldCollision` 會自動重建那一條（簡易世界碰撞的槽位在建立時保留）。

## 軟組織（胸、臀）

`KawaiiPhysicsSoftBody` 是平移式的軟組織。Chain 是保持骨長、只轉動的鐘擺（頭髮、裙子）；軟組織的骨頭可以離開動畫姿勢一小段再彈回，
適合胸、臀、腹部、大腿內側這類「整塊肉」的晃動與撞擊後的回彈。

### 模型

- 每根骨頭是掛在阻尼彈簧上的質點，錨點是這一幀動畫後的姿勢。三軸同一個 `frequency`（Hz，越大越硬）與 `dampingRatio`（0＝不衰減、1＝臨界阻尼不回彈）。
- 彈簧用解析解推進：任何幀率、任何參數都穩定，振動頻率與衰減跟參數一致（測試比對解析解，誤差 < 1e-5 m）。子步進（`targetFramerate`、`maxSubsteps`）只影響碰撞與位移上限的精度。
- **慣性拆成兩部分**：角色根的移動與轉向（`movementInertia`）、動畫（`animationInertia`）。0＝跟著走、不晃；1＝真實慣性；最大 2。
  例：走路、轉身不想晃就把 `movementInertia` 調低，動作裡的上下擺動照樣晃。
- **重力**：`gravityRelativeToRestPose`（預設開）時，站立的形狀維持動畫的樣子，只有躺、趴、倒立時才往重力方向偏，平衡位移 (g − R·g)/ω²（R＝父骨相對站立時的旋轉）；
  關掉時一直下垂 g/ω²（ω＝2π×頻率）。`externalAcceleration` 是應用端每幀可改的額外加速度。
- **位移上限**：骨頭區域軸向的橢球，正負方向可以不同（`limitMin`／`limitMax`，公尺）；某個方向設 0 就鎖住（例如不准往身體裡面陷）。
- **外形**：`tilt` 依位移傾斜（骨頭轉向「父骨 → 位移後的位置」）；`stretchAxis`＋`stretchAmount` 沿軸壓扁與拉伸（位移到上限時伸長 stretchAmount 倍），`volumePreservation` 維持體積。
- **碰撞**：`colliders` 的 `KawaiiPhysicsCollider`（球內外、膠囊、錐形膠囊、箱、平面；凸包不支援），骨頭半徑 `radius`。清單每幀讀取，增減不用重建。
  碰撞體壓過來、跟位移上限衝突時，`collisionPriority`（預設開）讓碰撞優先：骨頭被推到上限之外、不穿透；關掉時上限優先（碰撞體可能穿進骨頭）。
- **階層**：祖先也是軟組織骨頭時（不論是不是同一個元件），子骨的錨點跟著祖先的位移、傾斜與伸縮，子骨也感受到祖先晃動的慣性（例：基部＋尖端兩根）。
  祖先晃動的慣性一律是真實慣性，不受 `animationInertia` 影響。有祖先關係的元件會一起解（同一個工作，祖先先解），沒有關係的元件平行。
  祖先的輸出設定（`alpha`、`tilt`、伸縮）改變、祖先加入或離開模擬時（不論哪一層），子孫把因此的那一段移動精確扣掉，祖先真的晃動照常傳下去。
  `alpha` 是動畫與物理的混合比例、不是力：一次跳或漸變都一樣不給子孫慣性，也不依幀率。
  祖先重置（含同一幀停用再啟用、換 `referenceRoot`、搬到別的元件這類重建時的重置）、這一幀略過或位移上限改了（跳動量不知道）時，
  子孫重新取基準：不被甩，但那兩幀不感受祖先的晃動。
- **軸向**：位移上限、壓扁拉伸的軸、傾斜、重力參考都用 Transform 世界矩陣的軸（正規化的欄），負縮放（鏡像）的階層也對；不用 `Transform.rotation`（鏡像時兩者不一致）。
  骨頭自己是負縮放時，`limitMin`／`limitMax` 與伸縮的正負方向跟著實際的區域軸鏡像。
  不等比縮放的祖先底下有轉動的骨頭（剪切）時，軸向取的是正交化後的近似，跟 `Transform.rotation` 不完全相同。
- **角色根左右翻轉**：執行中把角色根的縮放正負翻轉（例如 scale.x 1 ↔ −1）當成瞬移，重新開始（不受瞬移門檻影響，門檻設 0 也一樣）；骨頭自己的縮放正負翻轉時那根骨頭重新開始。
- **退化的矩陣**：骨頭的世界矩陣退化（某一軸縮放剛好是 0、三軸接近共面）的幀不模擬也不寫回，恢復後重新開始。
- 同一個元件的骨頭共用設定；設定不同的部位（胸、臀）分開放元件。

### 快速開始

1. 在角色上加 `KawaiiPhysicsSoftBody`，`bones` 拖進要晃的骨頭（例：左右胸各一根，或基部＋尖端）。`referenceRoot` 不指定時用上層的 Animator（站立時 +Y 要朝上）。
2. 調 `frequency`、`dampingRatio`、`limitMin`／`limitMax`。Scene 檢視選取元件時會畫出每根骨頭的上限（箱）與半徑（球）。
3. 臀部另加一個元件。Play 中改設定下一幀就地生效、不重置。

### 執行時 API

```csharp
softBody.AddImpulse(new Vector3(0f, -0.8f, 0f));                    // 所有骨頭：速度變化（世界空間，公尺/秒）
softBody.AddImpulse(0, Vector3.forward * 0.5f);                     // 只有 bones[0]
softBody.AddImpulseAtPosition(hitPoint, hitVelocity * 0.3f, 0.15f); // 撞擊點附近，依距離衰減 (1 − 距離/半徑)²
Vector3 offset = softBody.GetOffset(0);                             // 目前的位移（世界空間，公尺；給音效、特效用）
Vector3 velocity = softBody.GetVelocity(0);                         // 目前相對錨點的速度
softBody.ResetDynamics();                                           // 回到平衡位置、速度歸零
softBody.CaptureRestPose();                                         // 應用端改了骨頭的靜止姿勢（體型）之後
softBody.MarkDirty();                                               // 用程式改了 bones 或 referenceRoot 之後
```

- 衝量是「速度變化」，在下一次模擬（同一幀的 LateUpdate 之後）生效；停用中的元件忽略。
  `bones` 裡重複的骨頭只有第一次算（建置時重複的略過並警告），`AddImpulse(int, …)`、`GetOffset` 等指到重複的那一格時沒有作用、回零。
- `CaptureRestPose()` 記錄的是自己清單裡的骨頭；別的元件模擬中、而且清單裡還有的骨頭略過。在元件之間搬骨頭時，
  從原來的清單拿掉之後就可以在新的元件呼叫（同一幀也可以），下次重建採用。
- 動畫事件：`KawaiiPhysicsEventPreset` 的種類 `SoftBodyImpulse`（`softBodyImpulse` 是角色根區域空間的速度變化，`softBodyNameFilter` 依名稱篩選元件），
  由 `KawaiiPhysicsAnimationEvents.KawaiiPhysicsEvent` 觸發；`KawaiiPhysicsSetAlpha`、`KawaiiPhysicsResetDynamics` 也作用到軟組織。
- 加入、移除、停用元件時其他元件的模擬狀態不變；停用或移除時骨頭在下一幀動畫前回到靜止姿勢。
  重置（`ResetDynamics`、瞬移判定、啟用、重新啟用）時質點跟著錨點一起動、停在平衡位置：角色在移動中重置也不會被甩一下。重新啟用一律重新開始（同一幀內停用再啟用也一樣）。
- `referenceRoot` 被刪掉時自動重建，退回上層的 Animator 或自己。`KPSystem.SoftBodyCount`、`KPSystem.SoftBoneCount` 是目前的數量。

### 套件不做的事

套件只提供機制與介面，不帶任何特定遊戲、模型格式或角色的骨頭名稱與參數對應。
哪些骨頭要模擬、每個部位的頻率與上限、什麼時候加衝量（依碰撞、動畫、互動），由應用端讀自己的資料後設定元件的欄位、呼叫 API。

## 陣列接口（沒有 Transform 的角色）

骨頭姿勢放在 `NativeArray`、由自己的工作鏈計算的角色（例如用 Burst 算姿勢、直接寫蒙皮矩陣的系統），用 `KPRig`。
骨鏈與軟組織的建立、每幀的準備（外力、倍率、風、碰撞體、衝量）、解算都跟 `KawaiiPhysicsChain`、`KawaiiPhysicsSoftBody` 是同一套程式，
結果一致（測試在同一個角色上比對兩條路，120 幀內差 0.01 mm 以下）。

```csharp
// 骨架：父的索引（父要在子之前）、名稱、靜止姿勢（區域 TRS）。colliderBones＝碰撞體物件（建骨鏈時不當成子骨頭）
var rig = new KPRig(parents, names, restPositions, restRotations, restScales, colliderBones);
rig.Owner = characterRoot;   // 簡易世界碰撞不收它底下的碰撞體（可省略）
var hair = new KPRigChain { rootBone = rig.FindBone("hair_root"), dummyBoneLength = 0.05f, gravity = new Vector3(0, -9.8f, 0) };
hair.colliders.Add(new KPRigCollider { bone = rig.FindBone("head"), radius = 0.09f });
rig.AddChain(hair);
var bust = new KPRigSoftBody { bones = { rig.FindBone("bust_L"), rig.FindBone("bust_R") }, frequency = 3f };
rig.AddSoftBody(bust);

// 每幀（LateUpdate 等）：模擬空間（骨架的根）的姿勢算好之後
var frame = new KPRigFrame
{
    DeltaTime = dt,
    RootToWorld = root.localToWorldMatrix,
    RootRotation = root.rotation,
    Model = model,                  // NativeArray<float4x4>：每根骨頭在根空間的矩陣
    ModelRotation = modelRotation,  // NativeArray<quaternion>：父的旋轉連乘自己的區域旋轉
};
var soft = rig.ScheduleSoftBodies(frame, poseHandle);
// soft 完成後：rig.SoftBodyBones 的骨頭換成 Output 的區域值（Mode 3：位置、旋轉、縮放），重算模擬空間姿勢（只套軟組織的）
var chains = rig.ScheduleChains(frame, recomputeHandle);
// chains 完成後：rig.AffectedBones 裡、Output[i].Mode 不是 0 的骨頭換成 Output 的區域值，再重算模擬空間姿勢
rig.Dispose(); // 用完（會先等工作完成）
```

- `KPRigChain`、`KPRigCollider`、`KPRigSoftBody` 的欄位跟元件一樣（說明見 `KawaiiPhysicsChain`、`KawaiiPhysicsCollider`、`KawaiiPhysicsSoftBody`），
  骨頭是骨架的索引。`component`、`referenceRoot`＝-1 是骨架的根（元件空間的原點）。限制資產、約束資產用名稱在整個骨架裡找。
- 順序：軟組織 → 重算模擬空間 → 骨鏈 → 重算。骨鏈的輸入只套這一幀的軟組織結果：`Output` 裡骨鏈的值（Mode 1、2）是上一幀的，
  不能當成骨鏈的輸入（Transform 版在動畫前把骨頭放回靜止姿勢，效果相同）。沒有骨鏈讀到軟組織骨頭時，兩次重算可以合成一次。
- 軟組織的衝量（`AddImpulse`、`AddImpulseAtPosition`）、`GetOffset`／`GetVelocity`、`ResetDynamics` 也在 `KPRigSoftBody` 上；
  撞擊點衝量的衰減用工作裡讀到的骨頭位置。
- 每個角色各自一個 `KPRig`（各自的 `KPWorld`）。排程前會先等上一次的工作完成，所以同一幀排第二次時給 `DeltaTime = 0`。
- 動畫前不用還原：呼叫端每幀從動畫或靜止姿勢重新算模擬空間姿勢。物理結果不要寫回動畫的本地值，當成一層覆蓋只給重算模擬空間時用。
- 讀取的區域姿勢從 `Model` 反推（父的矩陣的反矩陣），所以 `Model` 要包含所有會影響骨頭的調整（體型的縮放等）。
- 外力的骨頭篩選只看名稱（`applyBoneNameFilter`／`ignoreBoneNameFilter`）；Transform 的篩選清單在陣列接口沒有作用。
- 簡易世界碰撞的收集中心是骨架的根這一幀的位置；`component` 指定成某根骨頭時用那根骨頭的靜止姿勢推算。
- 世界碰撞（`allowWorldCollision`）在排程的工作裡掃掠，用的是排程那一刻的物理場景。
- 共用的程序風頻道（`KawaiiPhysicsWindChannel`）以各主機的幀數推進：同一個頻道同時給 `KawaiiPhysicsChain` 與 `KPRig` 用時，一幀可能推進兩次。

## 執行順序

兩個時間點都用 PlayerLoop 插入，不需要 MonoBehaviour：

1. `Update` 的 `ScriptRunBehaviourUpdate` 之前：Chain 與軟組織的骨頭放回靜止姿勢（動畫沒寫到的骨頭不會把上一幀的物理結果當成姿勢）；有變動時在這裡同步。
2. `PreLateUpdate` 的 `ScriptRunBehaviourLateUpdate` 之後：軟組織（讀 → 解 → 寫）→ Chain（準備外力、風、碰撞 → 讀 Transform → 解算（Burst 平行）→ 世界碰撞 → 寫回 Transform）→ `KPSystem.AfterSimulate`。
   軟組織先寫回，掛在軟組織骨頭底下的 Chain 會跟著動。

所以 LateUpdate 裡讀到的骨頭還是動畫姿勢；要物理之後的位置請掛 `AfterSimulate`。

## 效能

Windows、i5-12400（12 執行緒）、Unity 6000.6.7f1 實測：

| 項目 | 數值 |
|---|---|
| 16 角色 × 144 骨，每幀（還原＋模擬，主執行緒含等待工作） | 編輯器中位 0.24 ms；IL2CPP Player 平均 0.32 ms（Player 另含 1 個開滿功能的角色） |
| 加入一個角色（已有 17 個） | IL2CPP Player 平均 0.34 ms、最大 0.73 ms；Mono Player 平均 0.42 ms（10 次中第 1 次 1.20 ms）；編輯器平均約 0.9–1.2 ms |
| 移除一個角色（不觸發整理時） | IL2CPP Player 0.07 ms；編輯器約 0.3 ms |
| 整理（空洞過半時那一次；例：一次移除 16 個中的 9 個） | 編輯器 4.0 ms（Player 未量） |
| 穩態每幀 GC 配置 | 0 B（編輯器的精確量測；Player 量不到 4 KB 以下的配置） |
| 軟組織：16 角色 × 胸＋臀（32 元件、64 骨），每幀 | 編輯器中位 0.14 ms |
| 軟組織：128 角色（256 元件、512 骨），每幀 | 編輯器中位 0.85 ms |
| Chain＋軟組織：17 角色（另有 34 個軟組織元件、102 骨），每幀 | IL2CPP Player 平均 0.32 ms、Mono Player 平均 0.35 ms；GC 0 B |
| 軟組織：加入或移除一個角色（整批重建，跟元件總數成正比） | 16 角色時編輯器平均約 0.5–1 ms；128 角色時約 4–5 ms；IL2CPP Player 17 角色（含 Chain）加入平均 0.30 ms、最大 0.57 ms |

- 第一次加入角色那一幀會走到新程式路徑（Mono 的 JIT），之後就是上面的數字。
- 解算後端 `KPSystem.Backend`：Burst 平行（預設）、Burst 單執行緒、Managed（可以下中斷點）。

## 平台

| 平台 | 狀態 |
|---|---|
| Windows Standalone（Mono、IL2CPP） | 已驗證：建置 0 錯誤 0 警告、Burst AOT、Player 實跑 0 例外 |
| macOS、Android（IL2CPP／ARM64）、iOS、WebGL | 未驗證 |

WebGL 上 Burst 與工作執行緒的行為還沒確認（建置與實跑都未驗證）。

## 限制

- **縮放**：角色根（元件）與骨頭假設等比縮放（取 lossyScale.x）。非等比縮放不會產生 NaN，但結果與原作不同。縮放為 0 時那一幀不模擬、也不寫回，縮放恢復後重新開始。
- **單位**：`KawaiiPhysicsCollider` 的 `radius`、`radius1`、`length`、`extent`、凸包的網格，與簡易世界碰撞收集到的形狀，都是世界單位（公尺），元件（模擬空間）有縮放也一樣大（0.4.7 起；之前骨鏈把它們當成模擬單位，元件縮放不是 1 時碰撞體跟著縮放）。Chain 的骨頭 `radius`、`dummyBoneLength` 是元件空間的單位（跟著元件縮放）。
- **世界碰撞**：Unity 的球掃掠不回報起點重疊，骨頭靜止貼著表面時最多會陷入一個半徑。
- **自訂外力**：外力的型別只限套件提供的五種；`KPExternalForce` 不能從套件外繼承（每幀的 `PreApply` 用到內部的資料結構）。
- **WindZone**：場景載入／卸載、Chain 加入或移除時會重新收集（停用中的也會收，之後啟用就生效）；執行中用程式新增 WindZone 之後呼叫 `KPSystem.RefreshWindZones()`。刪除的 WindZone 會自動略過。
- **同一根骨頭只能屬於一條 Chain**：兩條鏈模擬到同一根骨頭時，寫回的結果不保證。
- **結構變更會重置那一條**：骨頭、碰撞體、約束、SyncBone、模擬空間、外力的數量改變時，那一條 Chain 從動畫姿勢重新開始（其他 Chain 不受影響）。依半徑加密細分或有橋接假骨時，`radius` 與 `radiusCurve` 也算結構。
- **碰撞體在 Inspector 改尺寸**：用到它的 Chain 會重建。
- **動畫與靜止姿勢**：沒有動畫寫入的骨頭每幀放回第一次看到時的靜止姿勢；執行中用程式直接改這些骨頭的區域姿勢會被蓋掉（請改 Chain 的設定或用外力）。要改靜止姿勢時，先停用那條 Chain、改完再啟用（停用時會忘掉舊的靜止姿勢）。
- **停用的成本**：停用一條 Chain 當場做一次同步（跟以前下一幀做的是同一件事）；同一幀停用很多條時是很多次同步。
- **移除段落留下的空間**：角色反覆生成與刪除時，空出來的位置給下一個放得下的角色重用；空出來的部分超過一半才整理（那一次會比較慢，實測見「效能」）。
- **軟組織的靜止姿勢**：啟用時記錄，每幀動畫前還原到這裡。執行中應用端改了軟組織骨頭的區域姿勢（體型調整等）之後要呼叫 `CaptureRestPose()`，否則會被還原。
- **軟組織的重力參考**：`gravityRelativeToRestPose` 假設 `referenceRoot` 的 +Y 在站立時朝上，而且啟用（或 `CaptureRestPose()`）時父骨相對角色根是站立的姿勢。
  啟用時角色根或父骨的縮放是 0（從縮放 0 長出來的演出）：重力參考等第一次能用的幀再記。
- **軟組織的單位與縮放**：位移上限與半徑是世界單位（公尺），沿骨頭的軸向量測，不隨角色縮放。壓扁與拉伸會連帶縮放子骨頭（子軟組織骨頭的位置跟著伸縮移動，模擬的位移照樣加在上面），建議只用在末端骨頭。
- **軟組織的碰撞**：只有 `KawaiiPhysicsCollider`（沒有世界碰撞），凸包不支援；軟組織骨頭之間不互撞（左右互相擠壓請放碰撞體）。
  `collisionPriority` 開著時，碰撞體把骨頭推出位移上限是正常的（「不超過上限」只在沒有碰撞的時候成立）。
  錨點本身就在碰撞體裡時（例如把軀幹的碰撞體也放進清單），骨頭會被推到碰撞體表面，可以遠超過上限，錨點正好在球心時往 +Y 推；
  這種碰撞體不要放進軟組織的清單，或把 `collisionPriority` 關掉（上限優先）。
- **同一根骨頭給兩個軟組織元件**：後面的略過並警告；同時給 Chain 與軟組織時寫回的結果不保證。
- **祖先晃動傳給子孫的近似**（審查實測，跟真實慣性的差）：
  - 重建時祖先鏈換了：用上一幀的基準位置代替上上一幀的。三層以上、留下的祖先有傾斜或伸縮、角色又在移動時，子骨偏約 7–31 mm（角色速度 1.2–5 m/s）。
  - 換鏈的前一幀 dt＝0（暫停）時，子骨丟掉一幀祖先的慣性（約 20 mm）。
  - 祖先正在晃時 `alpha` 一次跳，祖先當下速度的 Δα 倍仍會傳給子孫（約 1–12 mm）；漸變時沒有這個問題。
  - 祖先的 `collisionPriority`、`radius` 改了，碰撞把祖先一幀內推開時子骨會被甩（約 9 mm）。
- **延後記錄的重力參考**：縮放 0 時啟用的元件，重力參考記的是第一個能用的幀（動畫之後）的姿勢；那一幀不是站立姿勢時會偏（例：前傾 30° 約 14 mm）。
  這種角色請在站立姿勢時呼叫一次 `CaptureRestPose()`。
- **軟組織的加入與移除**：整批重建（元件與骨頭都少，成本見「效能」），同一根骨頭的模擬狀態沿用（停用過的元件除外）。
  v0.4.1 起重建多了分組（跨元件的階層），v0.4.4 又多了祖先鏈的比對，重建的成本比 v0.4 高（128 角色時約 3 → 5 ms）。
- **軟組織離原點很遠時**：子孫骨頭用 4×4 矩陣連乘傳遞祖先的變化，浮點誤差隨座標變大：離原點 1 km 約 0.3–0.4 mm、10 km 約 3–7 mm 的抖動。
  開放世界請用浮動原點（floating origin）。
- **動畫事件**：`KawaiiPhysicsAnimationEvents` 每次事件會找一次底下的元件（清單重用，不配置陣列）；有名稱篩選時讀元件名稱會配置字串。
- **曲線表**：外力與 SyncBone 的曲線取樣後依內容共用（形狀一樣的曲線只放一份），角色反覆生成、停用再啟用、重建、事件重複觸發都不會讓表變大；只有出現新的曲線形狀才增加（例如 Play 中在 Inspector 拖曲線，每個新形狀多 33 個 float）。`KPSystem.Shutdown()` 時清空。

## 測試

套件的測試在 `Tests/`（Editor 與 PlayMode 兩個組件）。在專案的 `Packages/manifest.json` 加上：

```json
"testables": ["com.louie.kawaii-physics"]
```

再用 Test Runner，或命令列：

```
Unity.exe -batchmode -projectPath <專案> -runTests -testPlatform EditMode -testResults <結果.xml>
Unity.exe -batchmode -projectPath <專案> -runTests -testPlatform PlayMode -testResults <結果.xml>
```

`Performance` 類別是效能測試（門檻放寬給編輯器；正式的數字在 Player 量）。

## 授權

MIT，見 `LICENSE.md`。移植的演算法與從原作取得的測試資料沿用原作 KawaiiPhysics（© pafuhana1213，MIT）的授權，原作的授權全文在 `Third Party Notices.md`，散布時要一起附上。
