using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using RimWorld;
using HarmonyLib;
using Multiplayer.API;
using Verse;
using Verse.AI;

namespace KanbanStockpile
{
    static class KanbanExtensions
    {
        public static bool TryGetKanbanSettings(this IntVec3 cell, Map map, out KanbanSettings ks, out SlotGroup slotGroup)
        {
            ks = new KanbanSettings();
            slotGroup = cell.GetSlotGroup(map);
            if( slotGroup?.Settings == null ) return false;

            // grab latest configs for this stockpile from our state manager
            ks = State.Get(slotGroup.Settings.owner.ToString());

            // skip all this stuff now if stockpile is not configured to use at least one feature
            if (ks.srt == 100 && ks.ssl == 0) return false;

            return true;
        }
    }

    //********************
    //PickUpAndHaul Patches
    static class PickUpAndHaul_Patch
    {
        public static void ApplyPatch(Harmony harmony)
        {
            var unloadFirstThingToil = typeof(PickUpAndHaul.JobDriver_UnloadYourHauledInventory)
                .GetNestedTypes(BindingFlags.Instance | BindingFlags.NonPublic)
                .First(t => t.FullName.Contains("c__DisplayClass4_0"))?
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .First(x => x.Name.EndsWith("b__0") && x.ReturnType == typeof(void));

            harmony.Patch(unloadFirstThingToil, transpiler: new HarmonyMethod(typeof(PickUpAndHaul_Patch), nameof(PickUpAndHaul_Patch.UnloadYourHauledInventory_MakeNewToils_Transpiler)));
        }

        static bool CheckKanbanSettings(Map map, IntVec3 cell, ThingCount thingCount, ref int countToDrop)
        {
            if(!cell.TryGetKanbanSettings(map, out var ks, out var slotGroup)) return false;

            var thing = thingCount.Thing;
            int stackLimit = Math.Max(1, (int) (thing.def.stackLimit * ks.srt / 100f));
            KSLog.Message($"[UnloadHauledInventory] {thing.LabelCap} x{thing.stackCount} / limit = {stackLimit}");

            List<Thing> things = map.thingGrid.ThingsListAt(cell);
            for (int i = 0; i < things.Count; i++) {
                Thing t = things[i];
                if (!t.def.EverStorable(false)) continue; // skip non-storable things as they aren't actually *in* the stockpile
                if (!t.CanStackWith(thing)) continue; // skip it if it cannot stack with thing to haul
                if (t.stackCount >= stackLimit) continue; // no need to refill until count is below threshold

                int needMax = stackLimit - t.stackCount;
                countToDrop = Math.Min(thing.stackCount, needMax);
                KSLog.Message($"  drop to stack => stack: {t.stackCount}, countToDrop: {countToDrop}");
                return true;
            }

            countToDrop = thing.stackCount > stackLimit ? stackLimit : thing.stackCount;
            KSLog.Message($"  drop to empty cell => countToDrop: {countToDrop}");
            return true;
        }

        static IEnumerable<CodeInstruction> UnloadYourHauledInventory_MakeNewToils_Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilGen)
        {
            var setTarget = AccessTools.Method(typeof(Job), nameof(Job.SetTarget));

            var code = instructions.ToList();
            int idx = -1;
            int setTargetIdx = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode == OpCodes.Callvirt && code[i].operand == setTarget) setTargetIdx++;
                if (setTargetIdx == 2)
                {
                    idx = i + 1;
                    break;
                }
            }

            if (idx == -1)
            {
                Log.Error($"[KanbanStockpile] Can't find insertion place");
                return code;
            }

            var checkKanbanSettings = AccessTools.Method(typeof(PickUpAndHaul_Patch), nameof(CheckKanbanSettings));
            var thingMap = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Map));
            var jobDriverPawn = AccessTools.Field(typeof(JobDriver), nameof(JobDriver.pawn));
            var countToDrop = AccessTools.Field(typeof(PickUpAndHaul.JobDriver_UnloadYourHauledInventory), nameof(PickUpAndHaul.JobDriver_UnloadYourHauledInventory.countToDrop));
            var nestedThis = AccessTools.TypeByName("PickUpAndHaul.JobDriver_UnloadYourHauledInventory")?
                .GetNestedTypes(BindingFlags.Instance | BindingFlags.NonPublic)
                .First(t => t.FullName.Contains("c__DisplayClass4_0"))?
                .GetFields(BindingFlags.Instance | BindingFlags.Public)
                .First(x => x.Name.EndsWith("4__this"));

            var continueLabel = ilGen.DefineLabel();

            code[idx].labels.Add(continueLabel);
            /*
            this.job.SetTarget(TargetIndex.A, thingCount.Thing);
			this.job.SetTarget(TargetIndex.B, c);

            >>> if (CheckKanbanSettings(pawn.Map, c, thingCount, ref countToDrop)) return;

            this.countToDrop = thingCount.Thing.stackCount;
             */
            code.InsertRange(idx, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, nestedThis),
                new CodeInstruction(OpCodes.Ldfld, jobDriverPawn),
                new CodeInstruction(OpCodes.Callvirt, thingMap),
                new CodeInstruction(OpCodes.Ldloc_1),
                new CodeInstruction(OpCodes.Ldloc_0),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, nestedThis),
                new CodeInstruction(OpCodes.Ldflda, countToDrop),
                new CodeInstruction(OpCodes.Call, checkKanbanSettings),
                new CodeInstruction(OpCodes.Brfalse_S, continueLabel),
                new CodeInstruction(OpCodes.Ret)
            });
            return code;
        }
    }

    //********************
    //ITab_Storage Patches
    [HarmonyPatch(typeof(ITab_Storage), "TopAreaHeight", MethodType.Getter)]
    static class ITab_Storage_TopAreaHeight_Patch
    {
        //private float TopAreaHeight
        public const float extraHeight = 28f;
        public static void Postfix(ref float __result)
        {
            __result += extraHeight;
        }
    }

    [HarmonyPatch(typeof(ITab_Storage), "FillTab")]
    static class ITab_Storage_FillTab_Patch
    {
        //protected override void FillTab()
        static MethodInfo GetTopAreaHeight = AccessTools.Property(typeof(ITab_Storage), "TopAreaHeight").GetGetMethod(true);
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            //		public static void BeginGroup(Rect position);
            MethodInfo BeginGroupInfo = AccessTools.Method(typeof(GUI), nameof(GUI.BeginGroup), new Type[] { typeof(Rect) });

            //class Verse.ThingFilter RimWorld.StorageSettings::'filter'
            FieldInfo filterInfo = AccessTools.Field(typeof(StorageSettings), "filter");
            MethodInfo DoThingFilterConfigWindowInfo = AccessTools.Method(typeof(ThingFilterUI), "DoThingFilterConfigWindow");

            bool firstTopAreaHeight = true;
            List<CodeInstruction> instList = instructions.ToList();
            for(int i=0;i<instList.Count;i++)
            {
                CodeInstruction inst = instList[i];

                yield return inst;

                if (firstTopAreaHeight &&
                        inst.Calls(GetTopAreaHeight))
                {
                    firstTopAreaHeight = false;
                    yield return new CodeInstruction(OpCodes.Ldc_R4, ITab_Storage_TopAreaHeight_Patch.extraHeight);
                    yield return new CodeInstruction(OpCodes.Sub);
                }

                if(inst.Calls(BeginGroupInfo))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);//ITab_Storage this
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ITab_Storage_FillTab_Patch), nameof(DrawKanbanSettings)));
                }
            }
        }

        public static DateTime lastUpdateTime = DateTime.Now;
        public static PropertyInfo SelStoreInfo = AccessTools.Property(typeof(ITab_Storage), "SelStoreSettingsParent");
        public static void DrawKanbanSettings(ITab_Storage tab)
        {
            IHaulDestination haulDestination = SelStoreInfo.GetValue(tab, null) as IHaulDestination;
            if (haulDestination == null) return;
            StorageSettings settings = haulDestination.GetStoreSettings();
            if (settings == null) return;

            //ITab_Storage.WinSize = 300
            float buttonMargin = ITab_Storage_TopAreaHeight_Patch.extraHeight + 4;
            Rect rect = new Rect(0f, (float)GetTopAreaHeight.Invoke(tab, new object[] { }) - ITab_Storage_TopAreaHeight_Patch.extraHeight - 2, 280, ITab_Storage_TopAreaHeight_Patch.extraHeight);

            // if Stockpile Ranking is installed, scootch these widgets up so it doesn't overlap
            // https://github.com/alextd/RimWorld-StockpileRanking/blob/master/Source/RankSelection.cs#L18
            if (KanbanStockpileLoader.IsStockpileRankingLoaded) {
                rect.y -= 26f;
            }

            rect.x += buttonMargin;
            rect.width -= buttonMargin * 3;
            Text.Font = GameFont.Small;

            KanbanSettings ks, tmp;
            ks = State.Get(settings.owner.ToString());
            tmp.srt = ks.srt;
            tmp.ssl = ks.ssl;

            string stackRefillThresholdLabel = "KS.StackRefillThreshold".Translate(ks.srt);

            string similarStackLimitLabel;
            if (ks.ssl > 0) {
                similarStackLimitLabel  = "KS.SimilarStackLimit".Translate(ks.ssl);
            } else {
                similarStackLimitLabel  = "KS.SimilarStackLimitOff".Translate();
            }

            //Stack Refill Threshold Slider
            tmp.srt = (int)Widgets.HorizontalSlider(new Rect(0f, rect.yMin + 10f, 150f, 15f),
                    ks.srt, 0f, 100f, false, stackRefillThresholdLabel, null, null, 1f);

            //Similar Stack Limit Slider
            tmp.ssl = (int)Widgets.HorizontalSlider(new Rect(155, rect.yMin + 10f, 125f, 15f),
                    ks.ssl, 0f, 8f, false, similarStackLimitLabel, null, null, 1f);


            if( (ks.srt != tmp.srt) ||
                    (ks.ssl != tmp.ssl) ) {

                // Accept slider changes no faster than 4Hz (250ms) to prevent spamming multiplayer sync lag
                DateTime curTime = DateTime.Now;
                if( (curTime - lastUpdateTime).TotalMilliseconds < 250) {
                    return;
                }
                lastUpdateTime = curTime;

                KSLog.Message("[KanbanStockpile] Changed Stack Refill Threshold for settings with haulDestination named: " + settings.owner.ToString());
                ks.srt = tmp.srt;
                ks.ssl = tmp.ssl;
                State.Set(settings.owner.ToString(), ks);
            }
        }
    }

    //********************
    //HaulAIUtility Patches
    [HarmonyPatch(typeof(HaulAIUtility), "HaulToCellStorageJob")]
    public static class HaulToCellStorageJob_Patch
    {
        public static bool Prefix(Pawn p, Thing t, IntVec3 storeCell, bool fitInStoreCell, ref Job __result)
        {
            if(!storeCell.TryGetKanbanSettings(t.Map, out var ks, out var slotGroup)) return true;

            int stackLimit = Math.Max(1, (int) (t.def.stackLimit * ks.srt / 100f));
            KSLog.Message($"[UnloadHauledInventory] {t.LabelCap} x{t.stackCount} / limit = {stackLimit}");

            int countToDrop = -1;
            List<Thing> things = t.Map.thingGrid.ThingsListAt(storeCell);
            for (int i = 0; i < things.Count; i++) {
                Thing t2 = things[i];
                if (!t2.def.EverStorable(false)) continue; // skip non-storable things as they aren't actually *in* the stockpile
                if (!t2.CanStackWith(t)) continue; // skip it if it cannot stack with thing to haul
                if (t2.stackCount >= stackLimit) continue; // no need to refill until count is below threshold

                int needMax = stackLimit - t2.stackCount;
                countToDrop = Math.Min(t.stackCount, needMax);
                KSLog.Message($"  drop to stack => stack: {t2.stackCount}, countToDrop: {countToDrop}");
                break;
            }

            if (countToDrop > 0)
            {
                Job job = new Job(JobDefOf.HaulToCell, t, storeCell)
                {
                    count = countToDrop,
                    haulOpportunisticDuplicates = true,
                    haulMode = HaulMode.ToCellStorage
                };
                __result = job;
                KSLog.Message($"  dispatch job1, thing={t},cell={storeCell},countToDrop={countToDrop}");
                return false;
            }

            Job job2 = new Job(JobDefOf.HaulToCell, t, storeCell);
            job2.count = t.stackCount > stackLimit ? stackLimit : t.stackCount;
            job2.haulOpportunisticDuplicates = false;
            job2.haulMode = HaulMode.ToCellStorage;
            __result = job2;
            KSLog.Message($"  dispatch job2(empty cell), thing={t},cell={storeCell},countToDrop={job2.count}");
            return false;
        }
    }

    //********************
    //StoreUtility Patches
    [HarmonyPatch(typeof(StoreUtility), "NoStorageBlockersIn")]
    public class StoreUtility_NoStorageBlockersIn_Patch
    {
        public static void Postfix(ref bool __result, IntVec3 c, Map map, Thing thing)
        {
            // NOTE: Likely LWM Deep Storages Prefix() and Vanilla NoStorageBlockersIn() itself have already run
            // returning false means storage is "full" so do *not* try to haul the thing
            // returning true means storage still has space available for thing so try to haul it

            // storage already filled up so no need to try to limit it further
            if (__result == false) return;

            // make sure we have everything we need to continue
            if(!c.TryGetKanbanSettings(map, out var ks, out var slotGroup)) return;

            // Assuming JobDefOf.HaulToContainer for Building_Storage vs JobDefOf.HaulToCell otherwise
            bool isContainer = (slotGroup?.parent is Building_Storage);

            // StackRefillThreshold checks only here at cell c
            List<Thing> things = map.thingGrid.ThingsListAt(c);
            int numDuplicates = 0;

            int stackLimit = Math.Max(1, (int) (thing.def.stackLimit * ks.srt / 100f));
            // TODO #5 consider re-ordering to prevent refilling an accidental/leftover duplicate stack
            // Design Decision: use for loops instead of foreach as they may be faster and similar to this vanilla function
            for (int i = 0; i < things.Count; i++) {
                Thing t = things[i];
                if (!t.def.EverStorable(false)) continue; // skip non-storable things as they aren't actually *in* the stockpile
                if (!t.CanStackWith(thing)) continue; // skip it if it cannot stack with thing to haul
                if (t.stackCount >= stackLimit) continue; // no need to refill until count is below threshold

                if (!isContainer) {
                    // pawns are smart enough to grab a partial stack for vanilla cell stockpiles so no need to explicitly check here
                    // maybe this is a JobDefOf.HaulToCell job?
                    KSLog.Message("[KanbanStockpile] YES HAUL PARTIAL STACK OF THING TO TOPOFF STACK IN CELL STOCKPILE!");
                    __result = true;
                    return;
                } else if (t.stackCount < stackLimit) {
                    // pawns seem to try to haul a full stack no matter what for HaulToContainer unlike HaulToCell CurJobDef's
                    // so for here when trying to haul to deep storage explicitly ensure stack to haul is partial stack
                    // maybe this is a JobDefOf.HaulToContainer job?
                    KSLog.Message("[KanbanStockpile] YES HAUL EXISTING PARTIAL STACK OF THING TO BUILDING STORAGE CONTAINER!");
                    __result = true;
                    return;
                }
            }

            if (ks.ssl == 0) return;
            // SimilarStackLimit check all cells in the slotgroup (potentially CPU intensive for big zones/limits)
            // SlotGroup.HeldThings
            for (int j = 0; j < slotGroup.CellsList.Count; j++) {
                IntVec3 cell = slotGroup.CellsList[j];
                things = map.thingGrid.ThingsListAt(cell);
                for (int i = 0; i < things.Count; i++) {
                    Thing t = things[i];
                    if (!t.def.EverStorable(false)) continue; // skip non-storable things as they aren't actually *in* the stockpile
                    if (!t.CanStackWith(thing) && t.def != thing.def) continue; // skip it if it cannot stack with thing to haul

                    // even a partial stack is a dupe so count it regardless
                    numDuplicates += Math.Max(1, t.stackCount / stackLimit);
                    if (numDuplicates >= ks.ssl) {
                        KSLog.Message("[KanbanStockpile] NO DON'T HAUL AS THERE IS ALREADY TOO MANY OF THAT KIND OF STACK!");
                        __result = false;
                        return;
                    }
                }
            }

            // iterate over all outstanding reserved jobs to prevent hauling duplicate similar stacks
            if (KanbanStockpile.Settings.aggressiveSimilarStockpileLimiting == false) return;
            if (map.reservationManager == null) return;
            var reservations = map.reservationManager.reservations;
            if (reservations == null) return;
            ReservationManager.Reservation r;
            for (int i = 0; i < reservations.Count; i++) {
                r = reservations[i];
                if (r == null) continue;
                if (r.Job == null) continue;
                if (!(r.Job.def == JobDefOf.HaulToCell || r.Job.def == JobDefOf.HaulToContainer)) continue;

                Thing t = r.Job.targetA.Thing;
                if (t == null) continue;
                if (t == thing) continue;  // no need to check against itself
                if (!t.CanStackWith(thing)) continue; // skip it if it cannot stack with thing to haul

                IntVec3 dest;
                if (r.Job.def == JobDefOf.HaulToCell) {
                    dest = r.Job.targetB.Cell;
                } else {
                    // case of JobDefOf.HaulToContainer
                    Thing container = r.Job.targetB.Thing;
                    if (container == null) continue;
                    dest = container.Position;
                }

                if (dest == null) continue;
                SlotGroup sg = dest.GetSlotGroup(map);
                if (sg == null) continue;
                if (sg != slotGroup) continue; // skip it as the similar thing is being hauled to a different stockpile

                // there is a thing that can stack with this thing and is already reserved for hauling to the desired stockpile: DUPE!
                numDuplicates++;
                if (numDuplicates >= ks.ssl) {
                    KSLog.Message("[KanbanStockpile] NO DON'T HAUL AS THERE IS ALREADY SOMEONE RESERVING JOB TO DO IT!");
                    __result = false;
                    return;
                }
            }

            // if we get here, haul that thing!
            return;
        }
    }


    //********************
    //StorageSettings Patches
    [HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.ExposeData))]
    public class StorageSettings_ExposeData_Patch
    {
        public static void Postfix(StorageSettings __instance)
        {
            // The clipboard StorageSettings has no owner, so assume a null is the clipboard...
            string label = __instance?.owner?.ToString() ?? "___clipboard";
            KSLog.Message("[KanbanStockpile] ExposeData() with owner name: " + label);
            KanbanSettings ks = State.Get(label);

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // this mode implicitly takes the value currently in srt and saves it out
                Scribe_Values.Look(ref ks.srt, "stackRefillThreshold", 100, true);
                Scribe_Values.Look(ref ks.ssl, "similarStackLimit", 0, true);
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // this mode implicitly loads some other value into this instance of srt
                Scribe_Values.Look(ref ks.srt, "stackRefillThreshold", 100, false);
                Scribe_Values.Look(ref ks.ssl, "similarStackLimit", 0, false);
                State.Set(label, ks);
            }
        }
    }

    [HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.CopyFrom))]
	class StorageSettings_CopyFrom_Patch
	{
		//public void CopyFrom(StorageSettings other)
        public static void CopyFrom(StorageSettings __instance, StorageSettings other)
        {
            KSLog.Message("[KanbanStockpile] CopyFrom()");
            string label = other?.owner?.ToString() ?? "___clipboard";
            KanbanSettings ks = State.Get(label);
            label = __instance?.owner?.ToString() ?? "___clipboard";
            State.Set(label, ks);
        }

		public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
            //private void TryNotifyChanged()
			MethodInfo TryNotifyChangedInfo = AccessTools.Method(typeof(StorageSettings), "TryNotifyChanged");

			foreach (CodeInstruction i in instructions)
			{
				if(i.Calls(TryNotifyChangedInfo))
				{
					//RankComp.CopyFrom(__instance, other);
					yield return new CodeInstruction(OpCodes.Ldarg_0);//this
					yield return new CodeInstruction(OpCodes.Ldarg_1);//other
					yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(StorageSettings_CopyFrom_Patch), nameof(StorageSettings_CopyFrom_Patch.CopyFrom)));
				}
				yield return i;
			}
		}
	}

    //********************
    //ZoneStockpile Patches
	[HarmonyPatch(typeof(Zone_Stockpile), nameof(Zone_Stockpile.PostDeregister))]
    static class Zone_Stockpile_PostDeregister_Patch
    {
        public static void Postfix(Zone_Stockpile __instance)
        {
            KSLog.Message("[KanbanStockpile] Zone_Stockpile_PostDeregister_Patch.Postfix()");
            if(State.Exists(__instance.ToString())) {
                KSLog.Message("[KanbanStockpile] Removing " + __instance.ToString());
                State.Del(__instance.ToString());
            }
        }
    }

    //********************
    //Dialog_RenameZone Patches
    [HarmonyPatch(typeof(Dialog_RenameZone), "SetName")]
    static class Dialog_RenameZone_SetName_Patch
    {
        public static void Prefix(Zone ___zone, string name)
        {
            //private Zone zone;
            string oldName = ___zone?.label ?? "N/A";

            KSLog.Message("[KanbanStockpile] Dialog_RenameZone.SetName() oldName: " + oldName);
            KSLog.Message("[KanbanStockpile] Dialog_RenameZone.SetName() newName: " + name);
            if(oldName == "N/A") return;

            State.Set(name, State.Get(oldName));
            State.Del(oldName);
        }
    }
}
