# Multiplayer targets: old vs new Assembly-CSharp

Old: 1.7.9790.38077, new: 1.7.9795.32073, compared 2026-10-26.
Targets from scripts/mp_targets.py. "changed" compares opcodes and resolved operands, ignoring branch offsets.

**0 items need attention** (missing in the new build or changed IL). "Not found in either" rows are Multiplayer's own helpers or lookups the extractor couldn't parse.

## Lambda lookups

| Lookup | Old member | New member | Status |
|---|---|---|---|
| `Ability.GetGizmos` #0 | <GetGizmos>b__102_0 | <GetGizmos>b__102_0 | unchanged |
| `Ability.QueueCastingJob` #0 | <>c__DisplayClass83_0.<QueueCastingJob>b__0 | <>c__DisplayClass83_0.<QueueCastingJob>b__0 | unchanged |
| `Archive.Add` #0 | <>c.<Add>b__6_0 | <>c.<Add>b__6_0 | unchanged |
| `BookOutcomeDoer_GiveQuest.GetGizmos` #1 | <GetGizmos>b__17_1 | <GetGizmos>b__17_1 | unchanged |
| `Building_ArchonexusCore.GetMultiSelectFloatMenuOptions` #0 | <GetMultiSelectFloatMenuOptions>b__7_0 | <GetMultiSelectFloatMenuOptions>b__7_0 | unchanged |
| `Building_Bed.GetBedRestFloatMenuOption` #0 | <>c__DisplayClass58_0.<GetBedRestFloatMenuOption>b__0 | <>c__DisplayClass58_0.<GetBedRestFloatMenuOption>b__0 | unchanged |
| `Building_Bed.SetBedOwnerTypeByInterface` #0 | <>c__DisplayClass55_0.<SetBedOwnerTypeByInterface>b__0 | <>c__DisplayClass55_0.<SetBedOwnerTypeByInterface>b__0 | unchanged |
| `Building_BioferriteHarvester.GetGizmos` #1 | <GetGizmos>b__31_1 | <GetGizmos>b__31_1 | unchanged |
| `Building_BioferriteHarvester.GetGizmos` #3 | <GetGizmos>b__31_3 | <GetGizmos>b__31_3 | unchanged |
| `Building_Bookcase.GetGizmos` #0 | <GetGizmos>b__72_0 | <GetGizmos>b__72_0 | unchanged |
| `Building_Crate.GetMultiSelectFloatMenuOptions` #0 | <GetMultiSelectFloatMenuOptions>b__13_0 | <GetMultiSelectFloatMenuOptions>b__13_0 | unchanged |
| `Building_Door.GetGizmos` #1 | <GetGizmos>b__81_1 | <GetGizmos>b__81_1 | unchanged |
| `Building_FermentingBarrel.GetGizmos` #0 | <GetGizmos>b__36_0 | <GetGizmos>b__36_0 | unchanged |
| `Building_FermentingBarrel.GetGizmos` #1 | <GetGizmos>b__36_1 | <GetGizmos>b__36_1 | unchanged |
| `Building_FirefoamPopper.GetGizmos` #1 | <GetGizmos>b__6_1 | <GetGizmos>b__6_1 | unchanged |
| `Building_GeneExtractor.GetGizmos` #2 | <GetGizmos>b__38_2 | <GetGizmos>b__38_2 | unchanged |
| `Building_GeneExtractor.SelectPawn` #0 | <>c__DisplayClass36_0.<SelectPawn>b__0 | <>c__DisplayClass36_0.<SelectPawn>b__0 | unchanged |
| `Building_GrowthVat.GetGizmos` #1 | <GetGizmos>b__86_1 | <GetGizmos>b__86_1 | unchanged |
| `Building_GrowthVat.GetGizmos` #11 | <GetGizmos>b__86_11 | <GetGizmos>b__86_11 | unchanged |
| `Building_GrowthVat.GetGizmos` #12 | <GetGizmos>b__86_12 | <GetGizmos>b__86_12 | unchanged |
| `Building_GrowthVat.GetGizmos` #3 | <GetGizmos>b__86_3 | <GetGizmos>b__86_3 | unchanged |
| `Building_GrowthVat.GetGizmos` #4 | <GetGizmos>b__86_4 | <GetGizmos>b__86_4 | unchanged |
| `Building_GrowthVat.GetGizmos` #5 | <GetGizmos>b__86_5 | <GetGizmos>b__86_5 | unchanged |
| `Building_GrowthVat.GetGizmos` #6 | <GetGizmos>b__86_6 | <GetGizmos>b__86_6 | unchanged |
| `Building_HoldingPlatform.GetGizmos` #1 | <>c__DisplayClass71_0.<GetGizmos>b__1 | <>c__DisplayClass71_0.<GetGizmos>b__1 | unchanged |
| `Building_MechCharger.GetGizmos` #0 | <GetGizmos>b__46_0 | <GetGizmos>b__46_0 | unchanged |
| `Building_MechCharger.GetGizmos` #1 | <GetGizmos>b__46_1 | <GetGizmos>b__46_1 | unchanged |
| `Building_MechCharger.GetGizmos` #2 | <GetGizmos>b__46_2 | <GetGizmos>b__46_2 | unchanged |
| `Building_MechCharger.GetGizmos` #3 | <GetGizmos>b__46_3 | <GetGizmos>b__46_3 | unchanged |
| `Building_MechGestator.GetGizmos` #0 | <GetGizmos>b__32_0 | <GetGizmos>b__32_0 | unchanged |
| `Building_OutfitStand.GetGizmos` #1 | <GetGizmos>b__97_1 | <GetGizmos>b__97_1 | unchanged |
| `Building_PassengerShuttle.GetGizmos` #2 | <>c__DisplayClass28_0.<GetGizmos>b__2 | <>c__DisplayClass28_0.<GetGizmos>b__2 | unchanged |
| `Building_PodLauncher.GetGizmos` #0 | <GetGizmos>b__2_0 | <GetGizmos>b__2_0 | unchanged |
| `Building_SubcoreScanner.GetGizmos` #1 | <GetGizmos>b__45_1 | <GetGizmos>b__45_1 | unchanged |
| `Building_SubcoreScanner.GetGizmos` #6 | <GetGizmos>b__45_6 | <GetGizmos>b__45_6 | unchanged |
| `Building_SubcoreScanner.GetGizmos` #7 | <GetGizmos>b__45_7 | <GetGizmos>b__45_7 | unchanged |
| `Building_Trap.GetGizmos` #1 | <GetGizmos>b__31_1 | <GetGizmos>b__31_1 | unchanged |
| `Building_TurretGun.GetGizmos` #1 | <GetGizmos>b__71_1 | <GetGizmos>b__71_1 | unchanged |
| `Building_TurretGun.GetGizmos` #2 | <GetGizmos>b__71_2 | <GetGizmos>b__71_2 | unchanged |
| `Building_VoidMonolith.GetFloatMenuOptions` #0 | <>c__DisplayClass80_0.<GetFloatMenuOptions>b__0 | <>c__DisplayClass80_0.<GetFloatMenuOptions>b__0 | unchanged |
| `Building_VoidMonolith.GetGizmos` #1 | <GetGizmos>b__79_1 | <GetGizmos>b__79_1 | unchanged |
| `Building_VoidMonolith.GetGizmos` #2 | <GetGizmos>b__79_2 | <GetGizmos>b__79_2 | unchanged |
| `Building_WorkTableAutonomous.GetGizmos` #0 | <GetGizmos>b__21_0 | <GetGizmos>b__21_0 | unchanged |
| `Building_WorkTableAutonomous.GetGizmos` #1 | <GetGizmos>b__21_1 | <GetGizmos>b__21_1 | unchanged |
| `Caravan.GetGizmos` #10 | <GetGizmos>b__123_10 | <GetGizmos>b__123_10 | unchanged |
| `Caravan.GetGizmos` #11 | <GetGizmos>b__123_11 | <GetGizmos>b__123_11 | unchanged |
| `Caravan.GetGizmos` #12 | <GetGizmos>b__123_12 | <GetGizmos>b__123_12 | unchanged |
| `Caravan.GetGizmos` #14 | <GetGizmos>b__123_14 | <GetGizmos>b__123_14 | unchanged |
| `Caravan.GetGizmos` #16 | <GetGizmos>b__123_16 | <GetGizmos>b__123_16 | unchanged |
| `Caravan.GetGizmos` #17 | <GetGizmos>b__123_17 | <GetGizmos>b__123_17 | unchanged |
| `Caravan.GetGizmos` #18 | <>c__DisplayClass123_0.<GetGizmos>b__18 | <>c__DisplayClass123_0.<GetGizmos>b__18 | unchanged |
| `Caravan.GetGizmos` #4 | <GetGizmos>b__123_4 | <GetGizmos>b__123_4 | unchanged |
| `Caravan.GetGizmos` #5 | <GetGizmos>b__123_5 | <GetGizmos>b__123_5 | unchanged |
| `Caravan.GetGizmos` #7 | <GetGizmos>b__123_7 | <GetGizmos>b__123_7 | unchanged |
| `Caravan.GetGizmos` #9 | <GetGizmos>b__123_9 | <GetGizmos>b__123_9 | unchanged |
| `Caravan.GetRefuelGizmo` #2 | <>c__DisplayClass125_0.<GetRefuelGizmo>b__2 | <>c__DisplayClass125_0.<GetRefuelGizmo>b__2 | unchanged |
| `CaravanAbandonOrBanishUtility.TryAbandonOrBanishViaInterface` #0 | <>c__DisplayClass0_0.<TryAbandonOrBanishViaInterface>b__0 | <>c__DisplayClass0_0.<TryAbandonOrBanishViaInterface>b__0 | unchanged |
| `CaravanAbandonOrBanishUtility.TryAbandonOrBanishViaInterface` #1 | <>c__DisplayClass0_0.<TryAbandonOrBanishViaInterface>b__1 | <>c__DisplayClass0_0.<TryAbandonOrBanishViaInterface>b__1 | unchanged |
| `CaravanAbandonOrBanishUtility.TryAbandonSpecificCountViaInterface` #0 | <>c__DisplayClass2_0.<TryAbandonSpecificCountViaInterface>b__0 | <>c__DisplayClass2_0.<TryAbandonSpecificCountViaInterface>b__0 | unchanged |
| `CaravanVisitUtility.TradeCommand` #0 | <>c__DisplayClass2_0.<TradeCommand>b__0 | <>c__DisplayClass2_0.<TradeCommand>b__0 | unchanged |
| `ChoiceLetter_AcceptCreepJoiner.Choices` #0 | <get_Choices>b__10_0 | <get_Choices>b__10_0 | unchanged |
| `ChoiceLetter_AcceptCreepJoiner.Choices` #1 | <get_Choices>b__10_1 | <get_Choices>b__10_1 | unchanged |
| `ChoiceLetter_AcceptCreepJoiner.Choices` #2 | <get_Choices>b__10_2 | <get_Choices>b__10_2 | unchanged |
| `ChoiceLetter_AcceptJoiner.Choices` #0 | <get_Choices>b__10_0 | <get_Choices>b__10_0 | unchanged |
| `ChoiceLetter_AcceptJoiner.Choices` #1 | <get_Choices>b__10_1 | <get_Choices>b__10_1 | unchanged |
| `ChoiceLetter_AcceptVisitors.Option_Accept` #0 | <get_Option_Accept>b__9_0 | <get_Option_Accept>b__9_0 | unchanged |
| `ChoiceLetter_AcceptVisitors.Option_RejectWithCharityConfirmation` #1 | <get_Option_RejectWithCharityConfirmation>b__11_1 | <get_Option_RejectWithCharityConfirmation>b__11_1 | unchanged |
| `ChoiceLetter_ChoosePawn.Option_ChoosePawn` #0 | <>c__DisplayClass8_0.<Option_ChoosePawn>b__0 | <>c__DisplayClass8_0.<Option_ChoosePawn>b__0 | unchanged |
| `ChoiceLetter_RansomDemand.Choices` #0 | <get_Choices>b__5_0 | <get_Choices>b__5_0 | unchanged |
| `ChoiceLetter.Option_Reject` #0 | <get_Option_Reject>b__18_0 | <get_Option_Reject>b__18_0 | unchanged |
| `Command_SetPlantToGrow.ProcessInput` #2 | <>c__DisplayClass5_0.<ProcessInput>b__2 | <>c__DisplayClass5_0.<ProcessInput>b__2 | unchanged |
| `Command_SetTargetFuelLevel.ProcessInput` #2 | <ProcessInput>b__2_2 | <ProcessInput>b__2_2 | unchanged |
| `Comp_AtmosphericHeater.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__20_0 | <CompGetGizmosExtra>b__20_0 | unchanged |
| `CompAbilityEffect_ResurrectMech.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__15_0 | <CompGetGizmosExtra>b__15_0 | unchanged |
| `CompAbilityEffect_ResurrectMech.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__15_1 | <CompGetGizmosExtra>b__15_1 | unchanged |
| `CompActivity.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__28_0 | <CompGetGizmosExtra>b__28_0 | unchanged |
| `CompActivity.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__28_1 | <CompGetGizmosExtra>b__28_1 | unchanged |
| `CompActivity.CompGetGizmosExtra` #2 | <CompGetGizmosExtra>b__28_2 | <CompGetGizmosExtra>b__28_2 | unchanged |
| `CompAnalyzable.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__15_0 | <CompGetGizmosExtra>b__15_0 | unchanged |
| `CompApparelVerbOwner_Charged.CompGetWornGizmosExtra` #0 | <CompGetWornGizmosExtra>b__15_0 | <CompGetWornGizmosExtra>b__15_0 | unchanged |
| `CompAtomizer.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__22_1 | <CompGetGizmosExtra>b__22_1 | unchanged |
| `CompAtomizer.CompGetGizmosExtra` #3 | <>c__DisplayClass22_0.<CompGetGizmosExtra>b__3 | <>c__DisplayClass22_0.<CompGetGizmosExtra>b__3 | unchanged |
| `CompBandNode.CompGetGizmosExtra` #7 | <>c__DisplayClass21_1.<CompGetGizmosExtra>b__7 | <>c__DisplayClass21_1.<CompGetGizmosExtra>b__7 | unchanged |
| `CompBandNode.CompGetGizmosExtra` #8 | <CompGetGizmosExtra>b__21_8 | <CompGetGizmosExtra>b__21_8 | unchanged |
| `CompBiosculpterPod.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__91_1 | <CompGetGizmosExtra>b__91_1 | unchanged |
| `CompBiosculpterPod.CompGetGizmosExtra` #3 | <CompGetGizmosExtra>b__91_3 | <CompGetGizmosExtra>b__91_3 | unchanged |
| `CompBiosculpterPod.CompGetGizmosExtra` #5 | <CompGetGizmosExtra>b__91_5 | <CompGetGizmosExtra>b__91_5 | unchanged |
| `CompBiosculpterPod.CompGetGizmosExtra` #6 | <CompGetGizmosExtra>b__91_6 | <CompGetGizmosExtra>b__91_6 | unchanged |
| `CompBiosculpterPod.CompGetGizmosExtra` #7 | <CompGetGizmosExtra>b__91_7 | <CompGetGizmosExtra>b__91_7 | unchanged |
| `CompBiosculpterPod.CompGetGizmosExtra` #8 | <CompGetGizmosExtra>b__91_8 | <CompGetGizmosExtra>b__91_8 | unchanged |
| `CompBiosculpterPod.CompGetGizmosExtra` #9 | <CompGetGizmosExtra>b__91_9 | <CompGetGizmosExtra>b__91_9 | unchanged |
| `CompCanBeDormant.CompGetGizmosExtra` #1 | <>c__DisplayClass20_0.<CompGetGizmosExtra>b__1 | <>c__DisplayClass20_0.<CompGetGizmosExtra>b__1 | unchanged |
| `CompCauseGameCondition_ForceWeather.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__3_0 | <CompGetGizmosExtra>b__3_0 | unchanged |
| `CompCauseGameCondition_PsychicEmanation.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__15_0 | <CompGetGizmosExtra>b__15_0 | unchanged |
| `CompCauseGameCondition_PsychicEmanation.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__15_1 | <CompGetGizmosExtra>b__15_1 | unchanged |
| `CompCauseGameCondition_PsychicSuppression.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__3_0 | <CompGetGizmosExtra>b__3_0 | unchanged |
| `CompChimera.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__9_0 | <CompGetGizmosExtra>b__9_0 | unchanged |
| `CompDeepDrill.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__18_0 | <CompGetGizmosExtra>b__18_0 | unchanged |
| `CompDissolution.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__28_0 | <CompGetGizmosExtra>b__28_0 | unchanged |
| `CompDissolution.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__28_1 | <CompGetGizmosExtra>b__28_1 | unchanged |
| `CompDissolution.CompGetGizmosExtra` #2 | <CompGetGizmosExtra>b__28_2 | <CompGetGizmosExtra>b__28_2 | unchanged |
| `CompDissolution.CompGetGizmosExtra` #4 | <>c__DisplayClass28_0.<CompGetGizmosExtra>b__4 | <>c__DisplayClass28_0.<CompGetGizmosExtra>b__4 | unchanged |
| `CompEggContainer.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__6_0 | <CompGetGizmosExtra>b__6_0 | unchanged |
| `CompEggLayer.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__20_0 | <CompGetGizmosExtra>b__20_0 | unchanged |
| `CompEquippableAbilityReloadable.CompGetEquippedGizmosExtra` #0 | <CompGetEquippedGizmosExtra>b__24_0 | <CompGetEquippedGizmosExtra>b__24_0 | unchanged |
| `CompExplosive.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__32_0 | <CompGetGizmosExtra>b__32_0 | unchanged |
| `CompFleshmassHeart.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__32_0 | <CompGetGizmosExtra>b__32_0 | unchanged |
| `CompFleshmassHeart.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__32_1 | <CompGetGizmosExtra>b__32_1 | unchanged |
| `CompFleshmassHeart.CompGetGizmosExtra` #2 | <CompGetGizmosExtra>b__32_2 | <CompGetGizmosExtra>b__32_2 | unchanged |
| `CompFleshmassNucleus.CompGetGizmosExtra` #1 | <>c__DisplayClass21_0.<CompGetGizmosExtra>b__1 | <>c__DisplayClass21_0.<CompGetGizmosExtra>b__1 | unchanged |
| `CompFleshmassNucleus.CompGetGizmosExtra` #3 | <>c__DisplayClass21_1.<CompGetGizmosExtra>b__3 | <>c__DisplayClass21_1.<CompGetGizmosExtra>b__3 | unchanged |
| `CompFleshmassSpitter.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__39_0 | <CompGetGizmosExtra>b__39_0 | unchanged |
| `CompFlickable.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__20_1 | <CompGetGizmosExtra>b__20_1 | unchanged |
| `CompGenepackContainer.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__27_1 | <CompGetGizmosExtra>b__27_1 | unchanged |
| `CompGlower.CompGetGizmosExtra` #2 | <>c__DisplayClass34_1.<CompGetGizmosExtra>b__2 | <>c__DisplayClass34_1.<CompGetGizmosExtra>b__2 | unchanged |
| `CompGlower.CompGetGizmosExtra` #3 | <>c__DisplayClass34_2.<CompGetGizmosExtra>b__3 | <>c__DisplayClass34_2.<CompGetGizmosExtra>b__3 | unchanged |
| `CompGrayStatueGas.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__7_0 | <CompGetGizmosExtra>b__7_0 | unchanged |
| `CompHackable.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__51_1 | <CompGetGizmosExtra>b__51_1 | unchanged |
| `CompHackable.CompGetGizmosExtra` #7 | <CompGetGizmosExtra>b__51_7 | <CompGetGizmosExtra>b__51_7 | unchanged |
| `CompHackable.CompGetGizmosExtra` #8 | <CompGetGizmosExtra>b__51_8 | <CompGetGizmosExtra>b__51_8 | unchanged |
| `CompHoldingPlatformTarget.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__38_0 | <CompGetGizmosExtra>b__38_0 | unchanged |
| `CompHoldingPlatformTarget.CompGetGizmosExtra` #2 | <CompGetGizmosExtra>b__38_2 | <CompGetGizmosExtra>b__38_2 | unchanged |
| `CompHoldingPlatformTarget.CompGetGizmosExtra` #4 | <CompGetGizmosExtra>b__38_4 | <CompGetGizmosExtra>b__38_4 | unchanged |
| `CompHoldingPlatformTarget.CompGetGizmosExtra` #5 | <CompGetGizmosExtra>b__38_5 | <CompGetGizmosExtra>b__38_5 | unchanged |
| `CompInteractable.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__62_1 | <CompGetGizmosExtra>b__62_1 | unchanged |
| `CompInteractableRocketswarmLauncher.TargetLocation` #0 | <>c__DisplayClass4_0.<TargetLocation>b__0 | <>c__DisplayClass4_0.<TargetLocation>b__0 | unchanged |
| `CompLaunchable.CompGetGizmosExtra` #3 | <CompGetGizmosExtra>b__36_3 | <CompGetGizmosExtra>b__36_3 | unchanged |
| `CompLongRangeMineralScanner.CompGetGizmosExtra` #1 | <>c__DisplayClass7_0.<CompGetGizmosExtra>b__1 | <>c__DisplayClass7_0.<CompGetGizmosExtra>b__1 | unchanged |
| `CompMechCarrier.CompGetGizmosExtra` #2 | <CompGetGizmosExtra>b__25_2 | <CompGetGizmosExtra>b__25_2 | unchanged |
| `CompMechCarrier.CompGetGizmosExtra` #3 | <CompGetGizmosExtra>b__25_3 | <CompGetGizmosExtra>b__25_3 | unchanged |
| `CompMechCarrier.CompGetGizmosExtra` #4 | <CompGetGizmosExtra>b__25_4 | <CompGetGizmosExtra>b__25_4 | unchanged |
| `CompMechPowerCell.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__11_0 | <CompGetGizmosExtra>b__11_0 | unchanged |
| `CompMechPowerCell.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__11_1 | <CompGetGizmosExtra>b__11_1 | unchanged |
| `CompMechRelay.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__12_0 | <CompGetGizmosExtra>b__12_0 | unchanged |
| `CompMechRepairable.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__3_1 | <CompGetGizmosExtra>b__3_1 | unchanged |
| `CompNeuralSupercharger.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__16_1 | <CompGetGizmosExtra>b__16_1 | unchanged |
| `CompNociosphere.TargetLocation` #1 | <>c__DisplayClass60_0.<TargetLocation>b__1 | <>c__DisplayClass60_0.<TargetLocation>b__1 | unchanged |
| `CompObelisk.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__40_0 | <CompGetGizmosExtra>b__40_0 | unchanged |
| `CompOverseerSubject.CompGetGizmosExtra` #2 | <>c__DisplayClass23_0.<CompGetGizmosExtra>b__2 | <>c__DisplayClass23_0.<CompGetGizmosExtra>b__2 | unchanged |
| `CompPilotConsole.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__15_1 | <CompGetGizmosExtra>b__15_1 | unchanged |
| `CompPilotConsole.CompGetGizmosExtra` #2 | <CompGetGizmosExtra>b__15_2 | <CompGetGizmosExtra>b__15_2 | unchanged |
| `CompPilotConsole.StartChoosingDestination_NewTemp` #4 | <>c__DisplayClass14_0.<StartChoosingDestination_NewTemp>b__4 | <>c__DisplayClass14_0.<StartChoosingDestination_NewTemp>b__4 | unchanged |
| `CompPilotConsole.StartChoosingDestination_NewTemp` #5 | <>c__DisplayClass14_1.<StartChoosingDestination_NewTemp>b__5 | <>c__DisplayClass14_1.<StartChoosingDestination_NewTemp>b__5 | unchanged |
| `CompPlantable.BeginTargeting` #3 | <>c__DisplayClass9_0.<BeginTargeting>b__3 | <>c__DisplayClass9_0.<BeginTargeting>b__3 | unchanged |
| `CompPlantable.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__8_0 | <CompGetGizmosExtra>b__8_0 | unchanged |
| `CompPollutionPump.CompGetGizmosExtra` #1 | <>c__DisplayClass12_0.<CompGetGizmosExtra>b__1 | <>c__DisplayClass12_0.<CompGetGizmosExtra>b__1 | unchanged |
| `CompPowerTrader.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__39_0 | <CompGetGizmosExtra>b__39_0 | unchanged |
| `CompProjectileInterceptor.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__63_0 | <CompGetGizmosExtra>b__63_0 | unchanged |
| `CompProjectileInterceptor.CompGetGizmosExtra` #2 | <CompGetGizmosExtra>b__63_2 | <CompGetGizmosExtra>b__63_2 | unchanged |
| `CompProximityFuse.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__3_0 | <CompGetGizmosExtra>b__3_0 | unchanged |
| `CompPsylinkable.CompFloatMenuOptions` #0 | <>c__DisplayClass13_0.<CompFloatMenuOptions>b__0 | <>c__DisplayClass13_0.<CompFloatMenuOptions>b__0 | unchanged |
| `CompRechargeable.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__11_0 | <CompGetGizmosExtra>b__11_0 | unchanged |
| `CompRefuelable.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__51_1 | <CompGetGizmosExtra>b__51_1 | unchanged |
| `CompRefuelable.CompGetGizmosExtra` #2 | <CompGetGizmosExtra>b__51_2 | <CompGetGizmosExtra>b__51_2 | unchanged |
| `CompRefuelable.CompGetGizmosExtra` #3 | <CompGetGizmosExtra>b__51_3 | <CompGetGizmosExtra>b__51_3 | unchanged |
| `CompRefuelable.CompGetGizmosExtra` #4 | <CompGetGizmosExtra>b__51_4 | <CompGetGizmosExtra>b__51_4 | unchanged |
| `CompRefuelable.CompGetGizmosExtra` #5 | <CompGetGizmosExtra>b__51_5 | <CompGetGizmosExtra>b__51_5 | unchanged |
| `CompRevenant.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__45_0 | <CompGetGizmosExtra>b__45_0 | unchanged |
| `CompRevenant.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__45_1 | <CompGetGizmosExtra>b__45_1 | unchanged |
| `CompRevenant.CompGetGizmosExtra` #2 | <CompGetGizmosExtra>b__45_2 | <CompGetGizmosExtra>b__45_2 | unchanged |
| `CompRevenant.CompGetGizmosExtra` #3 | <CompGetGizmosExtra>b__45_3 | <CompGetGizmosExtra>b__45_3 | unchanged |
| `CompScanner.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__15_0 | <CompGetGizmosExtra>b__15_0 | unchanged |
| `CompSendSignalOnCountdown.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__7_0 | <CompGetGizmosExtra>b__7_0 | unchanged |
| `CompShield.CompGetWornGizmosExtra` #0 | <CompGetWornGizmosExtra>b__29_0 | <CompGetWornGizmosExtra>b__29_0 | unchanged |
| `CompShuttle.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__54_1 | <CompGetGizmosExtra>b__54_1 | unchanged |
| `CompSpawner.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__14_0 | <CompGetGizmosExtra>b__14_0 | unchanged |
| `CompSpawnerHives.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__16_0 | <CompGetGizmosExtra>b__16_0 | unchanged |
| `CompSpawnerItems.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__5_0 | <CompGetGizmosExtra>b__5_0 | unchanged |
| `CompSpawnerPawn.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__33_0 | <CompGetGizmosExtra>b__33_0 | unchanged |
| `CompSpawnSubplant.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__17_0 | <CompGetGizmosExtra>b__17_0 | unchanged |
| `CompStudiable.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__48_1 | <CompGetGizmosExtra>b__48_1 | unchanged |
| `CompStudiable.CompGetGizmosExtra` #2 | <CompGetGizmosExtra>b__48_2 | <CompGetGizmosExtra>b__48_2 | unchanged |
| `CompStudiable.CompGetGizmosExtra` #3 | <CompGetGizmosExtra>b__48_3 | <CompGetGizmosExtra>b__48_3 | unchanged |
| `CompStudyUnlocks.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__21_0 | <CompGetGizmosExtra>b__21_0 | unchanged |
| `CompTempControl.CompGetGizmosExtra` #2 | <CompGetGizmosExtra>b__14_2 | <CompGetGizmosExtra>b__14_2 | unchanged |
| `CompTerrainPump.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__19_0 | <CompGetGizmosExtra>b__19_0 | unchanged |
| `CompToxifier.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__11_1 | <CompGetGizmosExtra>b__11_1 | unchanged |
| `CompToxifier.CompGetGizmosExtra` #3 | <>c__DisplayClass11_0.<CompGetGizmosExtra>b__3 | <>c__DisplayClass11_0.<CompGetGizmosExtra>b__3 | unchanged |
| `CompTreeConnection.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__74_1 | <CompGetGizmosExtra>b__74_1 | unchanged |
| `CompTreeConnection.CompGetGizmosExtra` #2 | <CompGetGizmosExtra>b__74_2 | <CompGetGizmosExtra>b__74_2 | unchanged |
| `CompTreeConnection.CompGetGizmosExtra` #3 | <CompGetGizmosExtra>b__74_3 | <CompGetGizmosExtra>b__74_3 | unchanged |
| `CompTurretGun.CompGetGizmosExtra` #1 | <CompGetGizmosExtra>b__37_1 | <CompGetGizmosExtra>b__37_1 | unchanged |
| `CompVoidStructure.CompGetGizmosExtra` #0 | <CompGetGizmosExtra>b__16_0 | <CompGetGizmosExtra>b__16_0 | unchanged |
| `Corpse.GetGizmos` #0 | <GetGizmos>b__57_0 | <GetGizmos>b__57_0 | unchanged |
| `Corpse.GetGizmos` #1 | <GetGizmos>b__57_1 | <GetGizmos>b__57_1 | unchanged |
| `DebugTools_Health.Options_Hediff_BodyParts` #0 | <>c__DisplayClass6_0.<Options_Hediff_BodyParts>b__0 | <>c__DisplayClass6_0.<Options_Hediff_BodyParts>b__0 | unchanged |
| `DebugTools_Health.Options_Hediff_BodyParts` #2 | <>c__DisplayClass6_1.<Options_Hediff_BodyParts>b__2 | <>c__DisplayClass6_1.<Options_Hediff_BodyParts>b__2 | unchanged |
| `DebugToolsPawns.Options_GiveToInventory` #0 | <>c__DisplayClass56_0.<Options_GiveToInventory>b__0 | <>c__DisplayClass56_0.<Options_GiveToInventory>b__0 | unchanged |
| `DebugToolsPawns.Options_GiveToInventory` #3 | <>c__DisplayClass56_1.<Options_GiveToInventory>b__3 | <>c__DisplayClass56_1.<Options_GiveToInventory>b__3 | unchanged |
| `DebugToolsPawns.Options_SetPrimary` #0 | <>c__DisplayClass55_0.<Options_SetPrimary>b__0 | <>c__DisplayClass55_0.<Options_SetPrimary>b__0 | unchanged |
| `DebugToolsPawns.Options_SetPrimary` #3 | <>c__DisplayClass55_1.<Options_SetPrimary>b__3 | <>c__DisplayClass55_1.<Options_SetPrimary>b__3 | unchanged |
| `DebugToolsPawns.Options_Wear` #0 | <>c__DisplayClass54_0.<Options_Wear>b__0 | <>c__DisplayClass54_0.<Options_Wear>b__0 | unchanged |
| `DebugToolsPawns.Options_Wear` #3 | <>c__DisplayClass54_1.<Options_Wear>b__3 | <>c__DisplayClass54_1.<Options_Wear>b__3 | unchanged |
| `DebugToolsPawns.PawnGearDevOptions` #3 | <>c__DisplayClass53_0.<PawnGearDevOptions>b__3 | <>c__DisplayClass53_0.<PawnGearDevOptions>b__3 | unchanged |
| `Designator_MechControlGroup.ProcessInput` #1 | <>c__DisplayClass9_0.<ProcessInput>b__1 | <>c__DisplayClass9_0.<ProcessInput>b__1 | unchanged |
| `Designator.RightClickFloatMenuOptions` #0 | <>c__DisplayClass37_0.<get_RightClickFloatMenuOptions>b__0 | <>c__DisplayClass37_0.<get_RightClickFloatMenuOptions>b__0 | unchanged |
| `Designator.RightClickFloatMenuOptions` #1 | <get_RightClickFloatMenuOptions>b__37_1 | <get_RightClickFloatMenuOptions>b__37_1 | unchanged |
| `Dialog_BeginRitual.DrawRoleSelection` #0 | <>c__DisplayClass54_0.<DrawRoleSelection>b__0 | <>c__DisplayClass54_0.<DrawRoleSelection>b__0 | unchanged |
| `Dialog_BeginRitual.DrawRoleSelection` #3 | <>c__DisplayClass54_2.<DrawRoleSelection>b__3 | <>c__DisplayClass54_2.<DrawRoleSelection>b__3 | unchanged |
| `Dialog_BeginRitual.DrawRoleSelection` #4 | <>c__DisplayClass54_1.<DrawRoleSelection>b__4 | <>c__DisplayClass54_1.<DrawRoleSelection>b__4 | unchanged |
| `Dialog_CreateXenogerm.DrawGenepack` #8 | <>c__DisplayClass21_0.<DrawGenepack>b__8 | <>c__DisplayClass21_0.<DrawGenepack>b__8 | unchanged |
| `Dialog_StyleSelection.DoWindowContents` #0 | <>c__DisplayClass10_0.<DoWindowContents>b__0 | <>c__DisplayClass10_0.<DoWindowContents>b__0 | unchanged |
| `Dialog_StyleSelection.DoWindowContents` #1 | <>c__DisplayClass10_1.<DoWindowContents>b__1 | <>c__DisplayClass10_1.<DoWindowContents>b__1 | unchanged |
| `Dialog_StyleSelection.DoWindowContents` #2 | <>c__DisplayClass10_2.<DoWindowContents>b__2 | <>c__DisplayClass10_2.<DoWindowContents>b__2 | unchanged |
| `EnterCooldownComp.GetGizmos` #0 | <GetGizmos>b__16_0 | <GetGizmos>b__16_0 | unchanged |
| `EnterCooldownComp.GetGizmos` #1 | <GetGizmos>b__16_1 | <GetGizmos>b__16_1 | unchanged |
| `FactionGiftUtility.OfferGiftsCommand` #0 | <>c__DisplayClass1_0.<OfferGiftsCommand>b__0 | <>c__DisplayClass1_0.<OfferGiftsCommand>b__0 | unchanged |
| `FloatMenuOptionProvider_CaptureEntity.GetOptionsFor` #2 | <>c__DisplayClass9_0.<GetOptionsFor>b__2 | <>c__DisplayClass9_0.<GetOptionsFor>b__2 | unchanged |
| `FloatMenuOptionProvider_CarryToBiosculpterPod.GetOptionsFor` #0 | <>c__DisplayClass9_1.<GetOptionsFor>b__0 | <>c__DisplayClass9_1.<GetOptionsFor>b__0 | unchanged |
| `Gene_Deathrest.GetGizmos` #2 | <GetGizmos>b__58_2 | <GetGizmos>b__58_2 | unchanged |
| `Gene_Deathrest.GetGizmos` #3 | <GetGizmos>b__58_3 | <GetGizmos>b__58_3 | unchanged |
| `Gene_Deathrest.GetGizmos` #5 | <>c__DisplayClass58_0.<GetGizmos>b__5 | <>c__DisplayClass58_0.<GetGizmos>b__5 | unchanged |
| `Gene_Healing.GetGizmos` #0 | <GetGizmos>b__5_0 | <GetGizmos>b__5_0 | unchanged |
| `Gene_PsychicBonding.GetGizmos` #0 | <GetGizmos>b__8_0 | <GetGizmos>b__8_0 | unchanged |
| `Genepack.GetGizmos` #1 | <GetGizmos>b__18_1 | <GetGizmos>b__18_1 | unchanged |
| `GeneResourceDrainUtility.GetResourceDrainGizmos` #0 | <>c__DisplayClass3_0.<GetResourceDrainGizmos>b__0 | <>c__DisplayClass3_0.<GetResourceDrainGizmos>b__0 | unchanged |
| `GeneResourceDrainUtility.GetResourceDrainGizmos` #1 | <>c__DisplayClass3_0.<GetResourceDrainGizmos>b__1 | <>c__DisplayClass3_0.<GetResourceDrainGizmos>b__1 | unchanged |
| `GeneUIUtility.DoDebugButton` #11 | <>c__DisplayClass25_1.<DoDebugButton>b__11 | <>c__DisplayClass25_1.<DoDebugButton>b__11 | unchanged |
| `GeneUIUtility.DoDebugButton` #12 | <>c__DisplayClass25_2.<DoDebugButton>b__12 | <>c__DisplayClass25_2.<DoDebugButton>b__12 | unchanged |
| `GeneUIUtility.DoDebugButton` #13 | <>c__DisplayClass25_3.<DoDebugButton>b__13 | <>c__DisplayClass25_3.<DoDebugButton>b__13 | unchanged |
| `GeneUIUtility.DoDebugButton` #4 | <>c__DisplayClass25_0.<DoDebugButton>b__4 | <>c__DisplayClass25_0.<DoDebugButton>b__4 | unchanged |
| `GeneUIUtility.DoDebugButton` #5 | <>c__DisplayClass25_0.<DoDebugButton>b__5 | <>c__DisplayClass25_0.<DoDebugButton>b__5 | unchanged |
| `GeneUIUtility.DoDebugButton` #7 | <>c__DisplayClass25_0.<DoDebugButton>b__7 | <>c__DisplayClass25_0.<DoDebugButton>b__7 | unchanged |
| `GravshipUtility.PreLaunchConfirmation` #4 | <>c.<PreLaunchConfirmation>b__17_4 | <>c.<PreLaunchConfirmation>b__17_4 | unchanged |
| `GroundSpawner.GetGizmos` #1 | <>c__DisplayClass23_0.<GetGizmos>b__1 | <>c__DisplayClass23_0.<GetGizmos>b__1 | unchanged |
| `Hediff_DeathRefusal.GetGizmos` #2 | <>c__DisplayClass27_0.<GetGizmos>b__2 | <>c__DisplayClass27_0.<GetGizmos>b__2 | unchanged |
| `Hediff_Labor.GetGizmos` #0 | <GetGizmos>b__12_0 | <GetGizmos>b__12_0 | unchanged |
| `Hediff_LaborPushing.GetGizmos` #0 | <GetGizmos>b__6_0 | <GetGizmos>b__6_0 | unchanged |
| `Hediff_LaborPushing.GetGizmos` #1 | <GetGizmos>b__6_1 | <GetGizmos>b__6_1 | unchanged |
| `Hediff_LaborPushing.GetGizmos` #2 | <GetGizmos>b__6_2 | <GetGizmos>b__6_2 | unchanged |
| `Hediff_LaborPushing.GetGizmos` #3 | <GetGizmos>b__6_3 | <GetGizmos>b__6_3 | unchanged |
| `Hediff_MetalhorrorImplant.GetGizmos` #0 | <>c__DisplayClass42_0.<GetGizmos>b__0 | <>c__DisplayClass42_0.<GetGizmos>b__0 | unchanged |
| `Hediff_MetalhorrorImplant.GetGizmos` #1 | <>c__DisplayClass42_0.<GetGizmos>b__1 | <>c__DisplayClass42_0.<GetGizmos>b__1 | unchanged |
| `Hediff_MetalhorrorImplant.GetGizmos` #2 | <>c__DisplayClass42_0.<GetGizmos>b__2 | <>c__DisplayClass42_0.<GetGizmos>b__2 | unchanged |
| `Hediff_MetalhorrorImplant.GetGizmos` #3 | <>c__DisplayClass42_0.<GetGizmos>b__3 | <>c__DisplayClass42_0.<GetGizmos>b__3 | unchanged |
| `Hediff_MetalhorrorImplant.GetGizmos` #6 | <>c__DisplayClass42_1.<GetGizmos>b__6 | <>c__DisplayClass42_1.<GetGizmos>b__6 | unchanged |
| `Hediff_Pregnant.GetGizmos` #0 | <GetGizmos>b__21_0 | <GetGizmos>b__21_0 | unchanged |
| `Hediff_Pregnant.GetGizmos` #1 | <GetGizmos>b__21_1 | <GetGizmos>b__21_1 | unchanged |
| `Hediff_Shambler.GetGizmos` #0 | <GetGizmos>b__36_0 | <GetGizmos>b__36_0 | unchanged |
| `HumanEmbryo.CanImplantFloatOption` #1 | <>c__DisplayClass17_0.<CanImplantFloatOption>b__1 | <>c__DisplayClass17_0.<CanImplantFloatOption>b__1 | unchanged |
| `HumanEmbryo.GetGizmos` #2 | <GetGizmos>b__11_2 | <GetGizmos>b__11_2 | unchanged |
| `HumanOvum.GetGizmos` #1 | <GetGizmos>b__3_1 | <GetGizmos>b__3_1 | unchanged |
| `ITab_Bills.FillTab` #2 | <>c__DisplayClass10_3.<FillTab>b__2 | <>c__DisplayClass10_3.<FillTab>b__2 | unchanged |
| `ITab_Fishing.FillTab` #0 | <>c__DisplayClass9_1.<FillTab>b__0 | <>c__DisplayClass9_1.<FillTab>b__0 | unchanged |
| `ITab_Pawn_Visitor.DoPrisonerTab` #5 | <>c__DisplayClass8_1.<DoPrisonerTab>b__5 | <>c__DisplayClass8_1.<DoPrisonerTab>b__5 | unchanged |
| `ITab_Pawn_Visitor.DoSlaveTab` #6 | <>c__DisplayClass7_0.<DoSlaveTab>b__6 | <>c__DisplayClass7_0.<DoSlaveTab>b__6 | unchanged |
| `LordJob_BestowingCeremony.GetPawnGizmos` #0 | <>c__DisplayClass45_0.<GetPawnGizmos>b__0 | <>c__DisplayClass45_0.<GetPawnGizmos>b__0 | unchanged |
| `LordJob_BestowingCeremony.GetPawnGizmos` #2 | <>c__DisplayClass45_0.<GetPawnGizmos>b__2 | <>c__DisplayClass45_0.<GetPawnGizmos>b__2 | unchanged |
| `LordJob_Ritual.GetPawnGizmos` #0 | <>c__DisplayClass105_0.<GetPawnGizmos>b__0 | <>c__DisplayClass105_0.<GetPawnGizmos>b__0 | unchanged |
| `LordToil_BestowingCeremony_Wait.ExtraFloatMenuOptions` #0 | <>c__DisplayClass7_0.<ExtraFloatMenuOptions>b__0 | <>c__DisplayClass7_0.<ExtraFloatMenuOptions>b__0 | unchanged |
| `MainTabWindow_Mechs.DoWindowContents` #0 | <>c.<DoWindowContents>b__8_0 | <>c.<DoWindowContents>b__8_0 | unchanged |
| `MechanitorControlGroupGizmo.GetWorkModeOptions` #1 | <>c__DisplayClass16_1.<GetWorkModeOptions>b__1 | <>c__DisplayClass16_1.<GetWorkModeOptions>b__1 | unchanged |
| `MechanitorUtility.GetMechGizmos` #2 | <>c__DisplayClass21_0.<GetMechGizmos>b__2 | <>c__DisplayClass21_0.<GetMechGizmos>b__2 | unchanged |
| `MechanitorUtility.GetMechGizmos` #3 | <>c__DisplayClass21_0.<GetMechGizmos>b__3 | <>c__DisplayClass21_0.<GetMechGizmos>b__3 | unchanged |
| `MinifiedTree.GetGizmos` #0 | <GetGizmos>b__16_0 | <GetGizmos>b__16_0 | unchanged |
| `MinifiedTree.GetGizmos` #1 | <GetGizmos>b__16_1 | <GetGizmos>b__16_1 | unchanged |
| `MinifiedTree.GetGizmos` #2 | <GetGizmos>b__16_2 | <GetGizmos>b__16_2 | unchanged |
| `MonumentMarker.GetGizmos` #1 | <GetGizmos>b__29_1 | <GetGizmos>b__29_1 | unchanged |
| `MonumentMarker.GetGizmos` #4 | <GetGizmos>b__29_4 | <GetGizmos>b__29_4 | unchanged |
| `MonumentMarker.GetGizmos` #5 | <GetGizmos>b__29_5 | <GetGizmos>b__29_5 | unchanged |
| `Pawn_ApparelTracker.SortWornApparelIntoDrawOrder` #0 | <>c.<SortWornApparelIntoDrawOrder>b__74_0 | <>c.<SortWornApparelIntoDrawOrder>b__74_0 | unchanged |
| `Pawn_CarryTracker.GetGizmos` #0 | <GetGizmos>b__22_0 | <GetGizmos>b__22_0 | unchanged |
| `Pawn_CarryTracker.GetGizmos` #1 | <>c__DisplayClass22_0.<GetGizmos>b__1 | <>c__DisplayClass22_0.<GetGizmos>b__1 | unchanged |
| `Pawn_ConnectionsTracker.GetGizmos` #3 | <GetGizmos>b__10_3 | <GetGizmos>b__10_3 | unchanged |
| `Pawn_CreepJoinerTracker.GetGizmos` #0 | <GetGizmos>b__50_0 | <GetGizmos>b__50_0 | unchanged |
| `Pawn_NeedsTracker.GetGizmos` #0 | <GetGizmos>b__39_0 | <GetGizmos>b__39_0 | unchanged |
| `Pawn_NeedsTracker.GetGizmos` #1 | <GetGizmos>b__39_1 | <GetGizmos>b__39_1 | unchanged |
| `Pawn_PlayerSettings.GetGizmos` #1 | <GetGizmos>b__36_1 | <GetGizmos>b__36_1 | unchanged |
| `Pawn_PlayerSettings.GetGizmos` #2 | <GetGizmos>b__36_2 | <GetGizmos>b__36_2 | unchanged |
| `Pawn_TrainingTracker.GetGizmos` #0 | <>c__DisplayClass25_0.<GetGizmos>b__0 | <>c__DisplayClass25_0.<GetGizmos>b__0 | unchanged |
| `Pawn_TrainingTracker.GetGizmos` #3 | <>c.<GetGizmos>b__25_3 | <>c.<GetGizmos>b__25_3 | unchanged |
| `Pawn.GetGizmos` #0 | <GetGizmos>b__346_0 | <GetGizmos>b__346_0 | unchanged |
| `Pawn.GetGizmos` #1 | <GetGizmos>b__346_1 | <GetGizmos>b__346_1 | unchanged |
| `Pawn.GetGizmos` #2 | <GetGizmos>b__346_2 | <GetGizmos>b__346_2 | unchanged |
| `Pawn.GetGizmos` #3 | <GetGizmos>b__346_3 | <GetGizmos>b__346_3 | unchanged |
| `Pawn.GetGizmos` #5 | <>c__DisplayClass346_0.<GetGizmos>b__5 | <>c__DisplayClass346_0.<GetGizmos>b__5 | unchanged |
| `Pawn.GetGizmos` #6 | <GetGizmos>b__346_6 | <GetGizmos>b__346_6 | unchanged |
| `Pawn.GetGizmos` #7 | <GetGizmos>b__346_7 | <GetGizmos>b__346_7 | unchanged |
| `PawnColumnWorker_ControlGroup.Button_GenerateMenu` #0 | <>c__DisplayClass3_1.<Button_GenerateMenu>b__0 | <>c__DisplayClass3_1.<Button_GenerateMenu>b__0 | unchanged |
| `PitBurrow.GetGizmos` #0 | <GetGizmos>b__14_0 | <GetGizmos>b__14_0 | unchanged |
| `PitGate.GetGizmos` #3 | <GetGizmos>b__45_3 | <GetGizmos>b__45_3 | unchanged |
| `Plant.GetGizmos` #0 | <GetGizmos>b__119_0 | <GetGizmos>b__119_0 | unchanged |
| `Plant.GetGizmos` #1 | <GetGizmos>b__119_1 | <GetGizmos>b__119_1 | unchanged |
| `PriorityWork.GetGizmos` #0 | <GetGizmos>b__17_0 | <GetGizmos>b__17_0 | unchanged |
| `PsychicRitualToil_InvokeHorax.DebugFinishGizmo` #0 | <DebugFinishGizmo>b__41_0 | <DebugFinishGizmo>b__41_0 | unchanged |
| `RitualOutcomeEffectWorker_GravshipLaunch.Apply` #0 | <>c__DisplayClass2_0.<Apply>b__0 | <>c__DisplayClass2_0.<Apply>b__0 | unchanged |
| `SettleInEmptyTileUtility.SetupCamp` #0 | <>c__DisplayClass4_0.<SetupCamp>b__0 | <>c__DisplayClass4_0.<SetupCamp>b__0 | unchanged |
| `ShipJob_Wait.GetJobGizmos` #0 | <GetJobGizmos>b__12_0 | <GetJobGizmos>b__12_0 | unchanged |
| `ShipJob_Wait.GetJobGizmos` #1 | <GetJobGizmos>b__12_1 | <GetJobGizmos>b__12_1 | unchanged |
| `SocialCardUtility.DrawPawnRoleSelection` #0 | <>c__DisplayClass42_0.<DrawPawnRoleSelection>b__0 | <>c__DisplayClass42_0.<DrawPawnRoleSelection>b__0 | unchanged |
| `SocialCardUtility.DrawPawnRoleSelection` #3 | <>c__DisplayClass42_1.<DrawPawnRoleSelection>b__3 | <>c__DisplayClass42_1.<DrawPawnRoleSelection>b__3 | unchanged |
| `StorageGroupUtility.StorageGroupMemberGizmos` #0 | <>c__DisplayClass5_0.<StorageGroupMemberGizmos>b__0 | <>c__DisplayClass5_0.<StorageGroupMemberGizmos>b__0 | unchanged |
| `StorageGroupUtility.StorageGroupMemberGizmos` #2 | <>c__DisplayClass5_0.<StorageGroupMemberGizmos>b__2 | <>c__DisplayClass5_0.<StorageGroupMemberGizmos>b__2 | unchanged |
| `StudyUtility.TargetHoldingPlatformForEntity` #2 | <>c__DisplayClass0_0.<TargetHoldingPlatformForEntity>b__2 | <>c__DisplayClass0_0.<TargetHoldingPlatformForEntity>b__2 | unchanged |
| `SubSustainer..ctor` #0 | <.ctor>b__12_0 | <.ctor>b__12_0 | unchanged |
| `Sustainer..ctor` #0 | <.ctor>b__15_0 | <.ctor>b__15_0 | unchanged |
| `Thing.GetGizmos` #0 | <GetGizmos>b__203_0 | <GetGizmos>b__203_0 | unchanged |
| `Tile.GetGizmos` #0 | <>c.<GetGizmos>b__57_0 | <>c.<GetGizmos>b__57_0 | unchanged |
| `Tile.GetGizmos` #4 | <>c__DisplayClass57_0.<GetGizmos>b__4 | <>c__DisplayClass57_0.<GetGizmos>b__4 | unchanged |
| `TimedDetectionRaids.GetGizmos` #0 | <GetGizmos>b__27_0 | <GetGizmos>b__27_0 | unchanged |
| `TimedDetectionRaids.GetGizmos` #1 | <GetGizmos>b__27_1 | <GetGizmos>b__27_1 | unchanged |
| `TimedDetectionRaids.GetGizmos` #2 | <GetGizmos>b__27_2 | <GetGizmos>b__27_2 | unchanged |
| `UnfinishedThing.GetGizmos` #0 | <GetGizmos>b__28_0 | <GetGizmos>b__28_0 | unchanged |
| `UnfinishedThing.GetGizmos` #1 | <GetGizmos>b__28_1 | <GetGizmos>b__28_1 | unchanged |
| `UnnaturalCorpse.GetGizmos` #0 | <GetGizmos>b__20_0 | <GetGizmos>b__20_0 | unchanged |
| `UnnaturalCorpse.GetGizmos` #1 | <GetGizmos>b__20_1 | <GetGizmos>b__20_1 | unchanged |
| `UnnaturalCorpse.GetGizmos` #2 | <GetGizmos>b__20_2 | <GetGizmos>b__20_2 | unchanged |
| `UnnaturalCorpse.GetGizmos` #3 | <GetGizmos>b__20_3 | <GetGizmos>b__20_3 | unchanged |
| `UnnaturalCorpse.GetGizmos` #4 | <GetGizmos>b__20_4 | <GetGizmos>b__20_4 | unchanged |
| `WorldObject.GetGizmos` #1 | <GetGizmos>b__111_1 | <GetGizmos>b__111_1 | unchanged |
| `Xenogerm.GetGizmos` #2 | <GetGizmos>b__16_2 | <GetGizmos>b__16_2 | unchanged |
| `Xenogerm.SetTargetPawn` #1 | <>c__DisplayClass11_0.<SetTargetPawn>b__1 | <>c__DisplayClass11_0.<SetTargetPawn>b__1 | unchanged |
| `Zone_Fishing.GetGizmos` #1 | <GetGizmos>b__52_1 | <GetGizmos>b__52_1 | unchanged |
| `Zone_Growing.GetGizmos` #1 | <GetGizmos>b__19_1 | <GetGizmos>b__19_1 | unchanged |
| `Zone_Growing.GetGizmos` #3 | <GetGizmos>b__19_3 | <GetGizmos>b__19_3 | unchanged |

## Transpiled methods

| Method | Status |
|---|---|
| `CompShuttle_ContainedColonistCount_Patch.IsFreeColonistAnyPlayerFaction` | not found in either |
| `FishShadowComponent_MapComponentTick_Patch.WaterBodyHash` | not found in either |
| `Math.Abs` | not found in either |
| `object.GetHashCode` | not found in either |
| `Pawn_TraderTracker_ColonyThingsWillingToBuy_Patch.AllBuildingsColonistOfDefOfPlayer` | not found in either |
| `Pawn_TraderTracker_ColonyThingsWillingToBuy_Patch.AllColonistBuildingsOfTypeOfPlayer` | not found in either |
| `RecacheColonistBelieverCountPatch.IsColonistAnyFaction` | not found in either |
| `RecacheColonistBelieverCountPatch.IsColonyMechAnyFaction` | not found in either |
| `RecacheColonistBelieverCountPatch.IsColonySubhumanAnyFaction` | not found in either |
| `RenderTexture.IsCreated` | not found in either |
| `TradeUtility_AllLaunchableThingsForTrade_Patch.AllPoweredOfPlayer` | not found in either |
| `TradingWindow.AllTradeables` | not found in either |
| `AnomalyUtility.TryGetNearbyUnseenCell` | unchanged |
| `Apparel.WornGraphicPath` | unchanged |
| `Building_Bookcase.GetDirectlyHeldThings` | unchanged |
| `Building_OrbitalTradeBeacon.AllPowered` | unchanged |
| `Building_Trap.KnowsOfTrap` | unchanged |
| `CameraDriver.CurrentViewRect` | unchanged |
| `CellRect.Contains` | unchanged |
| `CharacterCardUtility.DoTopStack` | unchanged |
| `ColonistBar.CheckRecacheEntries` | unchanged |
| `Command.GizmoOnGUIShrunk` | unchanged |
| `CompShuttle.ContainedColonistCount` | unchanged |
| `Dialog_Trade.CacheTradeables` | unchanged |
| `Dialog_Trade.DoWindowContents` | unchanged |
| `Faction.IsPlayer` | unchanged |
| `Faction.OfPlayer` | unchanged |
| `FastTileFinder.Closest` | unchanged |
| `FastTileFinder.Query` | unchanged |
| `Find.Maps` | unchanged |
| `Find.World` | unchanged |
| `FishShadowComponent.MapComponentTick` | unchanged |
| `FlyShipLeaving.LeaveMap` | unchanged |
| `Game.LoadGame` | unchanged |
| `Gen.HashCombine` | unchanged |
| `Gen.HashCombineInt` | unchanged |
| `GenConstruct.CanPlaceBlueprintAt_NewTemp` | unchanged |
| `GenConstruct.CanPlaceBlueprintOver` | unchanged |
| `GenConstruct.InteractionCellStandable` | unchanged |
| `GenConstruct.NotBlockingAnyInteractionCells` | unchanged |
| `GenPlace.HaulPlaceBlockerIn` | unchanged |
| `GenSpawn.Spawn` | unchanged |
| `GenSpawn.SpawnBuildingAsPossible` | unchanged |
| `GenSpawn.SpawningWipes` | unchanged |
| `GenSpawn.WipeAndRefundExistingThings` | unchanged |
| `GenSpawn.WipeExistingThings` | unchanged |
| `Gizmo.GizmoOnGUI` | unchanged |
| `GizmoGridDrawer.DrawGizmoGrid` | unchanged |
| `GlobalControlsUtility.DoTimespeedControls` | unchanged |
| `GlobalTargetInfo.GetHashCode` | unchanged |
| `Gravship.ConstantRandSeed` | unchanged |
| `HistoryAutoRecorder.Tick` | unchanged |
| `Ideo.RecacheColonistBelieverCount` | unchanged |
| `ITab_ContentsBooks.DoRow` | unchanged |
| `LayoutWorker.FillAllRooms` | unchanged |
| `LetterStack.ReceiveLetter` | unchanged |
| `ListerBuildings.AllBuildingsColonistOfDef` | unchanged |
| `ListerBuildings.AllColonistBuildingsOfType` | unchanged |
| `ListerThings.Add` | unchanged |
| `LongEventHandler.ExecuteToExecuteWhenFinished` | unchanged |
| `LongEventHandler.UpdateCurrentAsynchronousEvent` | unchanged |
| `Map.NextGenSeed` | unchanged |
| `MapPawns.AnyPawnBlockingMapRemoval` | unchanged |
| `MapPawns.IsValidColonyPawn` | unchanged |
| `ModLister.BiotechInstalled` | unchanged |
| `ModsConfig.BiotechActive` | unchanged |
| `MoteMaker.MakeStaticMote` | unchanged |
| `Page_ModsConfig.DoBottomButtons` | unchanged |
| `Page_ModsConfig.DoModRow` | unchanged |
| `Page_ModsConfig.DoWindowContents` | unchanged |
| `PathGridDoorsBlockedJob.CanBlockEver` | unchanged |
| `PathGridDoorsBlockedJob.Execute` | unchanged |
| `Pawn_AgeTracker.AgeBiologicalYears` | unchanged |
| `Pawn_AgeTracker.AgeBiologicalYearsFloat` | unchanged |
| `Pawn_GeneTracker.Notify_GenesChanged` | unchanged |
| `Pawn_JobTracker.StartJob` | unchanged |
| `Pawn_RecordsTracker.ExposeData` | unchanged |
| `Pawn_TraderTracker.ColonyThingsWillingToBuy` | unchanged |
| `Pawn.IsColonist` | unchanged |
| `Pawn.IsColonyMech` | unchanged |
| `Pawn.IsColonySubhuman` | unchanged |
| `Pawn.IsFreeColonist` | unchanged |
| `Pawn.IsFreeNonSlaveColonist` | unchanged |
| `Pawn.ProcessPostTickVisuals` | unchanged |
| `PawnBioAndNameGenerator.TryGetRandomUnusedSolidName` | unchanged |
| `PawnCapacitiesHandler.GetLevel` | unchanged |
| `PawnsArrivalModeWorker_EmergeFromWater.Arrive` | unchanged |
| `PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoCryptosleep` | unchanged |
| `PawnUtility.ShouldCollideWithPawns` | unchanged |
| `PlaceWorker_NeverAdjacentTrap.AllowsPlacing` | unchanged |
| `Plan.Cells` | unchanged |
| `PortraitsCache.Get` | unchanged |
| `Precept_Role.ValidatePawn` | unchanged |
| `Prefs.AutomaticPauseMode` | unchanged |
| `QuestPart_LendColonistsToFaction.Enable` | unchanged |
| `Rand.PopState` | unchanged |
| `Rand.PushState` | unchanged |
| `ScenPart_StartingAnimal.PetWeight` | unchanged |
| `Selector.IsSelected` | unchanged |
| `SituationalThoughtHandler.AppendSocialThoughts` | unchanged |
| `SituationalThoughtHandler.Notify_SituationalThoughtsDirty` | unchanged |
| `StatWorker.GetValue` | unchanged |
| `SubcameraDriver.UpdatePositions` | unchanged |
| `TargetInfo.GetHashCode` | unchanged |
| `Thing.DeSpawn` | unchanged |
| `Thing.IDNumberFromThingID` | unchanged |
| `Thing.Position` | unchanged |
| `ThingOwner.TryDrop` | unchanged |
| `TickManager.Pause` | unchanged |
| `TimeControls.DoTimeControlsGUI` | unchanged |
| `TradeDeal.AllTradeables` | unchanged |
| `TradeDeal.TryExecute` | unchanged |
| `TradeUtility.AllLaunchableThingsForTrade` | unchanged |
| `TransferableUtility.TransferAsOne` | unchanged |
| `UndercaveMapComponent.MapComponentTick` | unchanged |
| `UnityData.GetIdealBatchCount` | unchanged |
| `Widgets.ButtonImageWithBG` | unchanged |
| `Widgets.EndGroup` | unchanged |
| `Widgets.Label` | unchanged |
| `WindManager.BaseWindSpeedAt` | unchanged |
| `Window.InnerWindowOnGUI` | unchanged |
| `WindowStack.ImmediateWindow` | unchanged |
| `WorkGiver_ConstructDeliverResourcesToBlueprints.NoCostFrameMakeJobFor` | unchanged |
| `World.ConstantRandSeed` | unchanged |
| `Zone.Cells` | unchanged |

## Review

Reviewed Oct 26 2026 for 1.7.4985 rev1269 -> 1.7.4990 rev1069 (Steam update Oct 26, 12:03). Every lambda lookup
resolves to the same member with the same IL and all 125 transpiled methods are unchanged. The 12 "not found in
either" rows are the same as in docs/rwdiff-1.7.4982-1.7.4984.md: Multiplayer's own helpers and non-game methods
that the extractor picks up from transpiler bodies. scripts/mp_targets.py reads only Source/Client and
Source/MultiplayerLoader, so the transpilers of the built-in IsoRim (IsoRim/Source, Source/IsoRim/MpFacing.cs) are
not in the tables; the whole-assembly compare below covers them too.

The whole assembly was also compared: every type (attributes, base type, interfaces), field (name, type,
attributes) and method (name and signature, attributes, IL normalized with RwDiff's rules plus the local variable
signature and exception clauses). Both builds have 16222 types and 91616 methods. What changed:

- Waylith moved its texture types from `Waylith.Rendering` to `Waylith.Graphics.Textures` (`TexturePixelData`,
  `BcEncodeTarget`, `TextureCompressionSettings`, `ITextureCacheSource`; `Mipmapper` to
  `Waylith.Graphics.Textures.Mips`). The callers only got the new type references: `GenTexture.GenerateMipmaps`,
  `GlobalTextureAtlasManager.CompressAtlas`, `get_AtlasCompressionEnabled` and `ReleaseCpuLevels`,
  `StaticTextureAtlas.TryAdoptCachedPages` and `TryGetCacheKey`. Three changed signature for the same reason (removed
  and added again with the new parameter type): `GlobalTextureAtlasManager.CompressAtlasPage` and `TryEncodeOnGpu`,
  `StaticTextureAtlas.TryOpenCachedPage`. Multiplayer's only atlas patch, `NoAtlases` (prefix on
  `GlobalTextureAtlasManager.BakeStaticAtlases`, Debug/DebugPatches.cs), targets an unchanged method. IsoRim reads
  `GlobalTextureAtlasManager.staticTextureAtlases` and `StaticTextureAtlas.tiles` by name (SpritePixels.cs), and both
  fields are unchanged. The same move broke the IsoRim.dll built against 4985 (`TexturePixelData`, fixed in
  IsoRim/Source/LiveTexture.cs and SpritePixels.cs).
- `GraphicData` has a new public field `uncompressed`. `CopyFrom` copies it and `Init` calls
  `UnityStubs.ImportDefaults.Uncompressed(texPath)` when it's set, which makes Waylith keep that texture uncompressed
  (`TextureCompressionSettings.KeepRaw`). The `<ConfigErrors>d__47` state machine is now `d__48`. This only affects
  def loading and rendering. Multiplayer doesn't patch it or look it up.
- `ModsConfig`: `-mod=` now accepts only mods from the local Mods folder. `ApplyCommandLineModList` skips any entry
  whose mod isn't `ContentSource.ModsFolder` and logs "-mod: '<id>' is not in the local Mods folder, ignoring". Workshop
  mods can't be picked with `-mod=` any more, so `-mod=brrainz.harmony,Multiplayer-1.7` (scripts/run_two.sh, the
  CLAUDE.md test launch) now starts Multiplayer without Harmony on this PC, where Harmony comes from the Workshop
  (2009463077) and the local Mods folder holds only Multiplayer-1.7. The method also sets a new static
  `commandLineModList`, and `Save` and `SaveFromList` then return early with "ModsConfig.xml not saved, the mod list
  came from the command line". Multiplayer's `JoinDataWindow.DoRestart` (mod mismatch, Restart) sets
  `ModsConfig.data.activeMods` and calls `ModsConfig.Save()` before `GenCommandLine.Restart()`; with `-mod=` that save
  is now skipped. The restart keeps `-mod=`, so the command-line list won after the restart on 4985 as well; only the
  log line is new. The new field renumbered the compiler-generated members: `<>c` lambdas 28->29, 44->45, 49->50
  and display classes 29->30, 50->51, 51->52, 53->54 (`TryGetCommandLineModList`, `SetActiveToList`,
  `RestartFromChangedMods`, `ApplyCommandLineModList`, `GetModWarnings`, `ModHasAnyOrderingIssues`, `TrySortMods`).
  Multiplayer looks up no `ModsConfig` lambda or display class.
- `GenText.ToStringReadable(KeyCode)` first asks `UnityStubs.Internal.KeyboardLayout.Label(k)` for a label from
  the keyboard layout and falls back to the old table. This is UI text only, and Multiplayer never sends or hashes it.
- `CharacterCardUtility` static constructor: `ValidNameRegex` also accepts U+30FB and U+FF65 (katakana middle
  dots) in names typed in the rename dialog. Multiplayer transpiles `DoTopStack` and prefixes `DrawCharacterCard`,
  and both are unchanged. The check runs before the synced rename is sent. Players on different builds get the
  join mismatch window, because `VersionControl.CurrentVersionString` includes the build number.

None of these is a Harmony target (prefix, postfix, transpiler, sync method or sync field) of Multiplayer or
IsoRim, and none of them touches hashing, randomness or the tick. No Multiplayer patch needs changing.

1.7.4984 -> 1.7.4985, never compared before: the same whole-assembly compare finds no difference in any type, field,
method signature or normalized IL. The determinism and realtime scans for 4990 list the same call sites as the
4984 scans (79 in 47 types, 354 in 160 types). Only the header line changed.

The update also replaced UnityStubs.dll, Waylith.dll, Waylith.UnityCompat.dll, Waylith.AssetBake.dll,
Waylith.VideoDecode.dll, RimWorld.dll/.exe, com.rlabrecque.steamworks.net.dll and Assets.zip. It also replaced the
runtime itself: coreclr.dll, clrjit.dll, hostfxr.dll, System.Private.CoreLib.dll and about 40 BCL assemblies, with
the version string still 10.0.13-waylithclr.dev. RimWorld.runtimeconfig.json is unchanged since Oct 19. No old
copies were kept, so none of these was diffed. These checks were run against the new files:

- Every type and member reference from Multiplayer.dll, MultiplayerCommon.dll and MultiplayerLoader.dll into
  Assembly-CSharp, UnityStubs, Waylith, Waylith.UnityCompat and Steamworks resolves (1312 type and 2492 member
  references, 0 missing). This holds both for the release zip built at 12:02 against 4985 and for the 12:13
  rebuild. Multiplayer references no Waylith type directly. The same check flags the 18 `TexturePixelData`
  references of the 4985-built IsoRim.dll and none in the rebuilt one.
- System.Private.CoreLib: `HashCode` still seeds per process from `Interop.GetRandomBytes`
  (`GenerateGlobalSeed`). `ValueTuple<T1,T2>.GetHashCode` and `RuntimeMethodInfo.GetHashCode` still go through
  `HashCode.Combine`. `String.GetHashCode` still switches to the Mono algorithm on
  `WaylithClr.MonoStringHashCode`. `Enum.GetHashCode` is still the hash of the underlying value. The hashing facts
  in CLAUDE.md still hold.
- UnityStubs still lacks `Font.fontNames`, `TextEditor.UpdateScrollOffsetIfNeeded`, `TextEditor.m_RevealCursor`,
  `SystemInfo.graphicsDeviceVendor` and `SystemInfo.deviceUniqueIdentifier`. Still present:
  `Application.consoleLogPath` (LogGenerator), `Camera.WorldToScreenPoint` returning the view depth as z
  (MapScreen), the prefix targets `GUI.skin` and `GUIStyle.CalcSize`, and `RenderTexture.IsCreated` and
  `Time.deltaTime`, the calls that two transpilers look for.
- The new clrjit.dll can inline differently, so whether patches fire needs an in-game check. Run "Patch hit counts"
  in a session.
