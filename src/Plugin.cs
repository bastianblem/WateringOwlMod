// WateringOwlMod.cs
// BepInEx Mod for Ale & Tale Tavern
// A helper owl that fills water buckets at the Well and delivers them to Dishwashers.
//
// WORKFLOW:
//   1. Player places owl house (shop item, cloned from CleanerHouse).
//   2. Player puts water buckets (empty, charge == 0) into the owl's inventory.
//   3. Owl wakes up (NestSitting), checks for a dishwasher that needs water.
//   4. If a bucket in inventory has charge == 0  → MoveTo Well → fill bucket.
//   5. MoveTo Dishwasher → charge its SupplySource (water) → back to nest.
//
// Build requirements:
//   - BepInEx 5.x
//   - HarmonyX / BepInEx.HarmonyX
//   - Unity.Netcode, Assembly-CSharp references from the game

using BepInEx;
using BepInEx.Logging;
using DG.Tweening;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using static SavedDevice;

namespace blubdev.WateringOwl
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Constants & shared helpers
    // ─────────────────────────────────────────────────────────────────────────
    public static class Utils
    {
        public static readonly ushort CLEANER_HOUSE_ID = 1156;
        public static readonly ushort WATERING_OWL_HOUSE_ID = 8766;
        public static readonly ushort WATER_BUCKET_ID = 108;

        public static Item FindEmptyBucket(ContainerNet container)
        {
            foreach (Item item in container.items)
                if (item.dataId == WATER_BUCKET_ID && item.charge == 0)
                    return item;
            return default;
        }

        public static Item FindFullBucket(ContainerNet container)
        {
            ItemData data = null;
            ItemManager.Instance.GetItemData(WATER_BUCKET_ID, out data);
            if (data == null) return default;
            foreach (Item item in container.items)
                if (item.dataId == WATER_BUCKET_ID && item.charge >= data.maxCharge)
                    return item;
            return default;
        }

        public static WellView FindWell()
            => UnityEngine.Object.FindObjectOfType<WellView>();

        public static (GameObject dishwasherGO, SupplySource waterSS) FindDishwasherNeedingWater(HelperCleaner owner)
        {
            foreach (Dishwasher dw in UnityEngine.Object.FindObjectsOfType<Dishwasher>())
            {
                SupplySource ss = GetWaterSupplySource(dw);
                if (ss != null && ss.value.Value < ss.maxValue)
                {
                    Vector3 point;
                    if (HelpersManager.Instance.GetReachablePointOnNavMesh(
                            (HelperBase)(object)owner,
                            ((Component)dw).transform.position,
                            out point, 20f))
                        return (((Component)dw).gameObject, ss);
                }
            }
            foreach (AutoDishwasher adw in UnityEngine.Object.FindObjectsOfType<AutoDishwasher>())
            {
                SupplySource ss = GetWaterSupplySourceAuto(adw);
                if (ss != null && ss.value.Value < ss.maxValue)
                {
                    Vector3 point;
                    if (HelpersManager.Instance.GetReachablePointOnNavMesh(
                            (HelperBase)(object)owner,
                            ((Component)adw).transform.position,
                            out point, 20f))
                        return (((Component)adw).gameObject, ss);
                }
            }
            return (null, null);
        }

        private static readonly FieldInfo _dwWaterField =
            typeof(Dishwasher).GetField("_ssWater", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo _adwWaterField =
            typeof(AutoDishwasher).GetField("_ssWater", BindingFlags.Instance | BindingFlags.NonPublic);

        public static SupplySource GetWaterSupplySource(Dishwasher dw)
            => (SupplySource)_dwWaterField?.GetValue(dw);
        public static SupplySource GetWaterSupplySourceAuto(AutoDishwasher adw)
            => (SupplySource)_adwWaterField?.GetValue(adw);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Task definition
    // ─────────────────────────────────────────────────────────────────────────
    public class HelperTaskWater : HelperTaskBase
    {
        public enum Step { FillBucket, DeliverWater }

        public Step step;
        public Vector3 movePos;
        public WellView well;
        public SupplySource dishwasherWaterSS;
        public GameObject dishwasherGO;

        public HelperTaskWater(Step newStep, Vector3 newMovePos)
        {
            step = newStep;
            movePos = newMovePos;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  State: NestSitting
    // ─────────────────────────────────────────────────────────────────────────
    public class OwlNestSittingState : StateBase<HelperCleanerStateMachine>
    {
        private bool _invokeJumpOnEnter;
        private bool _jumpInvoked;
        private float _jumpDuration = 1.2f;
        private HelperTaskWater _pendingTask;

        private HelperCleaner Owner => stateMachine.Owner;

        public OwlNestSittingState(HelperCleanerStateMachine sm, bool invokeJump = true)
            : base(sm)
        {
            _invokeJumpOnEnter = invokeJump;
        }

        public override void OnEnter()
        {
            ((HelperBase)(object)Owner).agent.enabled = false;
            ((HelperBase)(object)Owner).animator.SetBool("NestSit", true);
            if (_invokeJumpOnEnter)
                DoJump(((HelperBase)(object)Owner).defaultPos);
            if (PlayerManager.Instance.isLocalPlayerSpawned)
                SoundManager.Instance.PlayServerRpc(SoundEvent.HelperDeactivate,
                    ((Component)(object)Owner).transform.position, false, default(ServerRpcParams));
        }

        public override void OnExit() { }

        public override void ExecutePerFrame() { Owner.SetAnimatorMoveSpeed(); }

        public override void ExecutePerSecond()
        {
            if (_jumpInvoked || !((HelperBase)(object)Owner).isActivated) return;
            _pendingTask = BuildTask();
            if (_pendingTask != null)
                ((MonoBehaviour)(object)Owner).StartCoroutine(JumpOff());
        }

        private IEnumerator JumpOff()
        {
            _jumpInvoked = true;
            DoJump(((HelperBase)(object)Owner).landingPos.position);
            ((HelperBase)(object)Owner).animator.SetBool("NestSit", false);
            if (PlayerManager.Instance.isLocalPlayerSpawned)
                SoundManager.Instance.PlayServerRpc(SoundEvent.HelperActivate,
                    ((Component)(object)Owner).transform.position, false, default(ServerRpcParams));
            yield return new WaitForSeconds(_jumpDuration);
            ((HelperBase)(object)Owner).agent.enabled = true;
            stateMachine.ChangeState(new OwlIdleState(stateMachine, _pendingTask));
        }

        private void DoJump(Vector3 endPos)
        {
            ((HelperBase)(object)Owner).networkAnimator.SetTrigger("Jump");
            ShortcutExtensions.DOJump(((Component)(object)Owner).transform, endPos, 1f, 1, _jumpDuration, false);
            ShortcutExtensions.DORotate(((Component)(object)Owner).transform,
                ((HelperBase)(object)Owner).defaultRot, _jumpDuration, RotateMode.Fast);
        }

        private HelperTaskWater BuildTask() => OwlTaskBuilder.Build(Owner);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  State: Idle
    // ─────────────────────────────────────────────────────────────────────────
    public class OwlIdleState : StateBase<HelperCleanerStateMachine>
    {
        private HelperTaskWater _externalTask;
        private HelperCleaner Owner => stateMachine.Owner;

        public OwlIdleState(HelperCleanerStateMachine sm) : base(sm) { }
        public OwlIdleState(HelperCleanerStateMachine sm, HelperTaskWater task) : base(sm)
        { _externalTask = task; }

        public override void OnEnter()
        {
            HelperTaskWater task = _externalTask ?? OwlTaskBuilder.Build(Owner);
            if (task != null)
                stateMachine.ChangeState(new OwlMoveToState(stateMachine, task));
            else
                stateMachine.ChangeState(new OwlMoveToDefaultPosState(stateMachine));
        }

        public override void OnExit() { }
        public override void ExecutePerFrame() { Owner.SetAnimatorMoveSpeed(); }
        public override void ExecutePerSecond() { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  State: MoveTo
    // ─────────────────────────────────────────────────────────────────────────
    public class OwlMoveToState : StateBase<HelperCleanerStateMachine>
    {
        private HelperTaskWater _task;
        private HelperCleaner Owner => stateMachine.Owner;

        public OwlMoveToState(HelperCleanerStateMachine sm, HelperTaskWater task) : base(sm)
        { _task = task; }

        public override void OnEnter()
        {
            HelperCleaner owner = Owner;
            owner.OnDespawn = (Action)Delegate.Combine(owner.OnDespawn, new Action(OnDespawn));
            FurnitureManager.Instance.OnFurniturePlaced =
                (Action)Delegate.Combine(FurnitureManager.Instance.OnFurniturePlaced, new Action(OnFurniturePlaced));
        }

        public override void OnExit()
        {
            HelperCleaner owner = Owner;
            owner.OnDespawn = (Action)Delegate.Remove(owner.OnDespawn, new Action(OnDespawn));
            FurnitureManager.Instance.OnFurniturePlaced =
                (Action)Delegate.Remove(FurnitureManager.Instance.OnFurniturePlaced, new Action(OnFurniturePlaced));
        }

        public override void ExecutePerFrame()
        {
            Owner.SetAnimatorMoveSpeed();
            if (Vector2.Distance(
                    FlatDistanceExtension.xz(((Component)(object)Owner).transform.position),
                    FlatDistanceExtension.xz(_task.movePos))
                <= ((HelperBase)(object)Owner).targetDetectDistance)
            {
                stateMachine.ChangeState(new OwlDoTaskState(stateMachine, _task));
            }
        }

        public override void ExecutePerSecond()
        {
            if (Owner.CanMove())
                ((HelperBase)(object)Owner).agent.SetDestination(_task.movePos);
        }

        private void OnFurniturePlaced()
            => ((MonoBehaviour)(object)Owner).StartCoroutine(RecalculateDelay());

        private IEnumerator RecalculateDelay()
        {
            yield return new WaitForSeconds(1f);
            Vector3 pt;
            if (HelpersManager.Instance.GetReachablePointOnNavMesh(
                    (HelperBase)(object)Owner, _task.movePos, out pt, 20f))
                _task.movePos = pt;
        }

        private void OnDespawn()
        {
            FurnitureManager.Instance.OnFurniturePlaced =
                (Action)Delegate.Remove(FurnitureManager.Instance.OnFurniturePlaced, new Action(OnFurniturePlaced));
            HelperCleaner owner = Owner;
            owner.OnDespawn = (Action)Delegate.Remove(owner.OnDespawn, new Action(OnDespawn));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  State: DoTask
    // ─────────────────────────────────────────────────────────────────────────
    public class OwlDoTaskState : StateBase<HelperCleanerStateMachine>
    {
        private HelperTaskWater _task;
        private HelperCleaner Owner => stateMachine.Owner;

        public OwlDoTaskState(HelperCleanerStateMachine sm, HelperTaskWater task) : base(sm)
        { _task = task; }

        public override void OnEnter()
        {
            ((HelperBase)(object)Owner).animator.SetBool("Cleanup", true);
            ((HelperBase)(object)Owner).agent.isStopped = true;

            if (_task.step == HelperTaskWater.Step.FillBucket)
            {
                if (_task.well != null)
                    ShortcutExtensions.DOLookAt(((Component)(object)Owner).transform,
                        ((Component)_task.well).transform.position, 0.5f, AxisConstraint.Y, null);
                ((MonoBehaviour)(object)Owner).StartCoroutine(FillBucketAtWell());
            }
            else
            {
                if (_task.dishwasherGO != null)
                    ShortcutExtensions.DOLookAt(((Component)(object)Owner).transform,
                        _task.dishwasherGO.transform.position, 0.5f, AxisConstraint.Y, null);
                ((MonoBehaviour)(object)Owner).StartCoroutine(DeliverWaterToDishwasher());
            }
        }

        public override void OnExit()
        {
            ((HelperBase)(object)Owner).agent.enabled = true;
            ((HelperBase)(object)Owner).agent.isStopped = false;
            ((HelperBase)(object)Owner).animator.SetBool("Cleanup", false);
        }

        public override void ExecutePerFrame() { Owner.SetAnimatorMoveSpeed(); }
        public override void ExecutePerSecond() { }

        private IEnumerator FillBucketAtWell()
        {
            yield return new WaitForSeconds(1f);

            ContainerNet inv = ((HelperBase)(object)Owner).containerRef.containerNet;
            ItemData bucketData = null;
            ItemManager.Instance.GetItemData(Utils.WATER_BUCKET_ID, out bucketData);

            if (bucketData != null)
            {
                List<Item> toFill = new List<Item>();
                foreach (Item it in inv.items)
                    if (it.dataId == Utils.WATER_BUCKET_ID && it.charge == 0)
                        toFill.Add(it);

                foreach (Item bucket in toFill)
                {
                    Item filled = bucket;
                    filled.charge = bucketData.maxCharge;
                    inv.SetItemById(filled.id, filled);
                    yield return new WaitForSeconds(0.5f);
                }
            }

            HelperTaskWater next = OwlTaskBuilder.Build(Owner);
            if (next != null)
                stateMachine.ChangeState(new OwlMoveToState(stateMachine, next));
            else
                stateMachine.ChangeState(new OwlMoveToDefaultPosState(stateMachine));
        }

        private IEnumerator DeliverWaterToDishwasher()
        {
            yield return new WaitForSeconds(1f);

            ContainerNet inv = ((HelperBase)(object)Owner).containerRef.containerNet;
            if (_task.dishwasherWaterSS != null)
            {
                _task.dishwasherWaterSS.Charge(0u, inv);
                yield return new WaitForSeconds(1f);
            }

            HelperTaskWater next = OwlTaskBuilder.Build(Owner);
            if (next != null)
                stateMachine.ChangeState(new OwlMoveToState(stateMachine, next));
            else
                stateMachine.ChangeState(new OwlMoveToDefaultPosState(stateMachine));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  State: MoveToDefaultPos
    // ─────────────────────────────────────────────────────────────────────────
    public class OwlMoveToDefaultPosState : StateBase<HelperCleanerStateMachine>
    {
        private HelperCleaner Owner => stateMachine.Owner;

        public OwlMoveToDefaultPosState(HelperCleanerStateMachine sm) : base(sm) { }

        public override void OnEnter() { ExecutePerSecond(); }
        public override void OnExit() { }

        public override void ExecutePerFrame() { Owner.SetAnimatorMoveSpeed(); }

        public override void ExecutePerSecond()
        {
            if (Owner.CanMove() && ((Behaviour)((HelperBase)(object)Owner).agent).isActiveAndEnabled)
                ((HelperBase)(object)Owner).agent.SetDestination(((HelperBase)(object)Owner).defaultPos);

            if (Vector3.Distance(((Component)(object)Owner).transform.position,
                                 ((HelperBase)(object)Owner).defaultPos)
                <= ((HelperBase)(object)Owner).targetDetectDistance)
            {
                stateMachine.ChangeState(new OwlNestSittingState(stateMachine));
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Shared task builder (avoids duplicate logic across states)
    // ─────────────────────────────────────────────────────────────────────────
    internal static class OwlTaskBuilder
    {
        public static HelperTaskWater Build(HelperCleaner owner)
        {
            ContainerNet inv = ((HelperBase)(object)owner).containerRef.containerNet;

            // Priority 1: empty bucket → go fill at well
            Item emptyBucket = Utils.FindEmptyBucket(inv);
            if (emptyBucket.id != 0)
            {
                WellView well = Utils.FindWell();
                if (well != null)
                {
                    Vector3 navPoint;
                    HelpersManager.Instance.GetReachablePointOnNavMesh(
                        (HelperBase)(object)owner,
                        ((Component)well).transform.position,
                        out navPoint, 20f);
                    return new HelperTaskWater(HelperTaskWater.Step.FillBucket, navPoint)
                    { well = well };
                }
            }

            // Priority 2: full bucket + thirsty dishwasher → deliver
            Item fullBucket = Utils.FindFullBucket(inv);
            if (fullBucket.id != 0)
            {
                var (dwGO, ss) = Utils.FindDishwasherNeedingWater(owner);
                if (dwGO != null)
                {
                    Vector3 navPt;
                    HelpersManager.Instance.GetReachablePointOnNavMesh(
                        (HelperBase)(object)owner,
                        dwGO.transform.position,
                        out navPt, 20f);
                    return new HelperTaskWater(HelperTaskWater.Step.DeliverWater, navPt)
                    { dishwasherWaterSS = ss, dishwasherGO = dwGO };
                }
            }

            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  All state types that belong to this owl (used by Harmony patches)
    // ─────────────────────────────────────────────────────────────────────────
    internal static class OwlStateTypes
    {
        public static readonly List<Type> All = new List<Type>
        {
            typeof(OwlNestSittingState),
            typeof(OwlIdleState),
            typeof(OwlMoveToState),
            typeof(OwlDoTaskState),
            typeof(OwlMoveToDefaultPosState)
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Harmony patches
    // ─────────────────────────────────────────────────────────────────────────

    internal class Patch_HelperCleaner_OnNetworkSpawn
    {
        [HarmonyPatch(typeof(HelperCleaner), "OnNetworkSpawn")]
        [HarmonyPostfix]
        public static void Postfix(HelperCleaner __instance)
        {
            if (((UnityEngine.Object)(object)((Component)(object)__instance).gameObject).name
                != "Helper Watering Owl") return;

            HelperCleanerStateMachine sm = GetSM(__instance);
            sm.SetInitState(new OwlNestSittingState(sm, invokeJump: false));
        }

        private static HelperCleanerStateMachine GetSM(HelperCleaner h)
            => (HelperCleanerStateMachine)typeof(HelperCleaner)
               .GetField("stateMachine", BindingFlags.Instance | BindingFlags.NonPublic)
               .GetValue(h);
    }

    internal class Patch_HelperCleaner_Activate
    {
        [HarmonyPatch(typeof(HelperCleaner), "Activate")]
        [HarmonyPrefix]
        public static bool Prefix(HelperCleaner __instance)
        {
            if (((UnityEngine.Object)(object)((Component)(object)__instance).gameObject).name
                != "Helper Watering Owl") return true;

            ((HelperBase)(object)__instance).isActivated = true;
            HelperCleanerStateMachine sm = GetSM(__instance);
            if (sm.CurrentState.GetType().Name == nameof(OwlMoveToDefaultPosState))
                sm.ChangeState(new OwlIdleState(sm));
            return false;
        }

        private static HelperCleanerStateMachine GetSM(HelperCleaner h)
            => (HelperCleanerStateMachine)typeof(HelperCleaner)
               .GetField("stateMachine", BindingFlags.Instance | BindingFlags.NonPublic)
               .GetValue(h);
    }

    internal class Patch_HelperCleaner_Deactivate
    {
        [HarmonyPatch(typeof(HelperCleaner), "Deactivate")]
        [HarmonyPrefix]
        public static bool Prefix(HelperCleaner __instance)
        {
            if (((UnityEngine.Object)(object)((Component)(object)__instance).gameObject).name
                != "Helper Watering Owl") return true;

            HelperCleanerStateMachine sm = GetSM(__instance);
            if (!OwlStateTypes.All.Contains(sm.CurrentState.GetType())) return true;

            sm.ChangeState(new OwlMoveToDefaultPosState(sm));
            HelpersManager.Instance.RemoveFromTaskList((HelperBase)(object)__instance);
            ((HelperBase)(object)__instance).isActivated = false;
            return false;
        }

        private static HelperCleanerStateMachine GetSM(HelperCleaner h)
            => (HelperCleanerStateMachine)typeof(HelperCleaner)
               .GetField("stateMachine", BindingFlags.Instance | BindingFlags.NonPublic)
               .GetValue(h);
    }

    internal class Patch_HelperCleaner_CheckContainerChanged
    {
        [HarmonyPatch(typeof(HelperCleaner), "CheckContainerChanged")]
        [HarmonyPrefix]
        public static bool Prefix(HelperCleaner __instance)
        {
            if (((UnityEngine.Object)(object)((Component)(object)__instance).gameObject).name
                != "Helper Watering Owl") return true;

            HelperCleanerStateMachine sm = GetSM(__instance);
            if (!OwlStateTypes.All.Contains(sm.CurrentState.GetType())) return true;

            if (sm.CurrentState is HelperCleanerFullInventoryState &&
                !((HelperBase)(object)__instance).containerRef.containerNet.IsFull())
                sm.ChangeState(new OwlIdleState(sm));

            if (sm.CurrentState is OwlMoveToState)
                sm.ChangeState(new OwlIdleState(sm));

            return false;
        }

        private static HelperCleanerStateMachine GetSM(HelperCleaner h)
            => (HelperCleanerStateMachine)typeof(HelperCleaner)
               .GetField("stateMachine", BindingFlags.Instance | BindingFlags.NonPublic)
               .GetValue(h);
    }

    internal class Patch_StateMachine_ChangeState
    {
        [HarmonyPatch(typeof(HelperCleanerStateMachine), "ChangeState")]
        [HarmonyPrefix]
        public static bool Prefix(HelperCleanerStateMachine __instance,
                                   StateBase<HelperCleanerStateMachine> newState)
        {
            bool currentIsOwl = OwlStateTypes.All.Contains(__instance.CurrentState?.GetType());
            bool newIsOwl = OwlStateTypes.All.Contains(newState.GetType());
            if (!currentIsOwl && !newIsOwl) return true;

            bool ownerActive = ((HelperBase)(object)__instance.Owner).isActivated;
            bool targetIsNest = newState.GetType() == typeof(OwlNestSittingState);
            if (!ownerActive && !targetIsNest) return true;

            __instance.Owner.currentStateName = newState.GetType().Name;

            FieldInfo isTransitionF = typeof(HelperCleanerStateMachine)
                .GetField("isTransition", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo previousStateF = typeof(HelperCleanerStateMachine)
                .GetField("<PreviousState>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo currentStateF = typeof(HelperCleanerStateMachine)
                .GetField("<CurrentState>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

            isTransitionF.SetValue(__instance, true);
            __instance.CurrentState.OnExit();
            previousStateF.SetValue(__instance, __instance.CurrentState);
            currentStateF.SetValue(__instance, newState);
            __instance.CurrentState.OnEnter();
            isTransitionF.SetValue(__instance, false);
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Shop item injection + Furniture/Helper instantiation patches
    // ─────────────────────────────────────────────────────────────────────────
    internal class PatchWateringOwlSetup
    {
        [HarmonyPatch(typeof(ItemManager), "Awake")]
        [HarmonyPrefix]
        public static void AddCustomItems(ItemManager __instance)
        {
            ItemData cleanerHouseData = __instance.itemDataHub.itemData
                .FirstOrDefault(d => d.id == Utils.CLEANER_HOUSE_ID);

            if (cleanerHouseData == null)
            {
                Debug.LogWarning("[WateringOwl] Could not find Cleaner House ItemData!");
                return;
            }

            ItemData owlHouseData = new ItemData
            {
                id = Utils.WATERING_OWL_HOUSE_ID,
                name = "ItemDataNameWateringOwlHouse",
                icon = cleanerHouseData.icon,
                type = ItemData.Type.Furniture,
                maxStack = 1,
                maxCharge = 0,
                durability = 0,
                durabilityPerHit = 0,
                durabilityDecreasePerRepair = 0,
                price = 2200,
                playerCantSell = false,
                playerCantDrop = false,
                buyByOne = false,
                quest = false,
                shopItem = true,
                alchemicalIngredient = false,
                commonLootItem = false,
                levelDependant = 4,
                questDependant = 0,
                doNotSave = false,
                collectiblePrefab = cleanerHouseData.collectiblePrefab,
                fpPrefab = cleanerHouseData.fpPrefab,
                tpPrefab = cleanerHouseData.tpPrefab,
                netPrefab = cleanerHouseData.netPrefab,
                containerClean = null,
                containerDirty = null,
                itemDescription = "ItemDataDescrWateringOwlHouse"
            };

            __instance.itemDataHub.itemData =
                __instance.itemDataHub.itemData.Concat(new[] { owlHouseData }).ToArray();

            Debug.Log("[WateringOwl] Added WateringOwlHouse ItemData.");
        }

        [HarmonyPatch(typeof(FurnitureManager), "OnNetworkSpawn")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Furniture_OnNetworkSpawn_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return new CodeMatcher(instructions)
                .MatchForward(false, new CodeMatch(i =>
                    i.opcode == OpCodes.Call &&
                    (i.operand as MethodInfo)?.Name == "Instantiate" &&
                    (i.operand as MethodInfo)?.ReturnType == typeof(GameObject)))
                .InsertAndAdvance(
                    new CodeInstruction(OpCodes.Ldloc_2),
                    new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(SavedDevice), "itemDataId")),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PatchWateringOwlSetup), "InstantiateFurniture_OnLoad")))
                .RemoveInstruction()
                .Instructions();
        }

        [HarmonyPatch(typeof(FurnitureManager), "PlaceFurnitureServerRpc",
            new[] { typeof(uint), typeof(Vector3), typeof(Quaternion), typeof(ServerRpcParams) })]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> PlaceFurnitureServerRpc_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return new CodeMatcher(instructions)
                .MatchForward(false, new CodeMatch(i =>
                    i.opcode == OpCodes.Call &&
                    (i.operand as MethodInfo)?.Name == "Instantiate" &&
                    (i.operand as MethodInfo)?.ReturnType == typeof(GameObject)))
                .InsertAndAdvance(
                    new CodeInstruction(OpCodes.Ldloc_2),
                    new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Item), "dataId")),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PatchWateringOwlSetup), "InstantiateFurniture")))
                .RemoveInstruction()
                .Instructions();
        }

        [HarmonyPatch(typeof(HelperHouse), "OnNetworkSpawn")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> HelperHouse_OnNetworkSpawn_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return new CodeMatcher(instructions)
                .MatchForward(false, new CodeMatch(i =>
                    i.opcode == OpCodes.Call &&
                    (i.operand as MethodInfo)?.Name == "Instantiate" &&
                    (i.operand as MethodInfo)?.ReturnType == typeof(HelperBase)))
                .InsertAndAdvance(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(HelperHouse), "furniture")),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PatchWateringOwlSetup), "InstantiateHelper")))
                .RemoveInstruction()
                .Instructions();
        }

        public static GameObject InstantiateFurniture_OnLoad(
            GameObject original, Vector3 position, Quaternion rotation, Transform parent, ushort itemDataId)
        {
            GameObject go = UnityEngine.Object.Instantiate(original, position, rotation, parent);
            if (itemDataId == Utils.WATERING_OWL_HOUSE_ID) TintOwlHouse(go);
            return go;
        }

        public static GameObject InstantiateFurniture(
            GameObject original, Vector3 position, Quaternion rotation, ushort itemDataId)
        {
            GameObject go = UnityEngine.Object.Instantiate(original, position, rotation);
            if (itemDataId == Utils.WATERING_OWL_HOUSE_ID) TintOwlHouse(go);
            return go;
        }

        public static UnityEngine.Object InstantiateHelper(
            UnityEngine.Object original, Vector3 position, Quaternion rotation, Furniture furnitureInstance)
        {
            UnityEngine.Object obj = UnityEngine.Object.Instantiate(original, position, rotation);
            HelperBase helper = obj as HelperBase;

            if (furnitureInstance?.itemData?.id == Utils.WATERING_OWL_HOUSE_ID && helper != null)
            {
                TintOwl(helper);
                ((UnityEngine.Object)helper).name = "Helper Watering Owl";
                ((Component)(object)helper).GetComponent<Interactive>().ObjectTitle =
                    "InteractiveNameWateringOwl";
            }

            return obj;
        }

        private static void TintOwlHouse(GameObject house)
        {
            try
            {
                MeshRenderer r = house.transform
                    .Find("Container/Owls_Nest/Owls_Nest/Owls_Nest")
                    ?.GetComponent<MeshRenderer>();
                if (r != null) r.material.color = new Color(0.1f, 0.5f, 1f);
            }
            catch { }

            ItemData d = null;
            ItemManager.Instance.GetItemData(Utils.WATERING_OWL_HOUSE_ID, out d);
            if (d != null) house.GetComponent<Furniture>().itemData = d;
        }

        private static void TintOwl(HelperBase helper)
        {
            try
            {
                Color blue = new Color(0.1f, 0.5f, 1f);
                SkinnedMeshRenderer coat = ((Component)(object)helper).transform
                    .Find("HalloweenOwl_Gr/coat_low")?.GetComponent<SkinnedMeshRenderer>();
                SkinnedMeshRenderer hood = ((Component)(object)helper).transform
                    .Find("HalloweenOwl_Gr/hood_low")?.GetComponent<SkinnedMeshRenderer>();
                if (coat != null) coat.material.color = blue;
                if (hood != null) hood.material.color = blue;
            }
            catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Plugin entry point
    // ─────────────────────────────────────────────────────────────────────────
    [BepInPlugin("blubdev.WateringOwl", "Watering Owl", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("Plugin blubdev.WateringOwl is loaded!");

            Harmony harmony = new Harmony("blubdev.WateringOwl");
            harmony.PatchAll(typeof(Patch_HelperCleaner_OnNetworkSpawn));
            harmony.PatchAll(typeof(Patch_HelperCleaner_Activate));
            harmony.PatchAll(typeof(Patch_HelperCleaner_Deactivate));
            harmony.PatchAll(typeof(Patch_HelperCleaner_CheckContainerChanged));
            harmony.PatchAll(typeof(Patch_StateMachine_ChangeState));
            harmony.PatchAll(typeof(PatchWateringOwlSetup));

            InjectString("ItemData", "ItemDataNameWateringOwlHouse", "Watering Owl House");
            InjectString("ItemData", "ItemDataDescrWateringOwlHouse",
                "An owl that fills water buckets at the well and keeps your dishwashers topped up.");
            InjectString("Interactive", "InteractiveNameWateringOwl", "Watering Owl");
        }

        private static void InjectString(string table, string key, string value)
        {
            // Cast string to TableReference implicitly via the operator (not calling op_Implicit explicitly)
            StringTable t = ((LocalizedDatabase<StringTable, StringTableEntry>)(object)
                LocalizationSettings.StringDatabase)
                .GetTableAsync((TableReference)table, null).Result;

            if (t != null &&
                ((DetailedLocalizationTable<StringTableEntry>)(object)t).GetEntry(key) == null)
                ((DetailedLocalizationTable<StringTableEntry>)(object)t).AddEntry(key, value);
        }
    }
}