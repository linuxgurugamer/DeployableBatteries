using System;
using UnityEngine;
using Experience.Effects;
using KSP.Localization;
using KSP_Log;

namespace DeployableBatteries
{
    static class BaseFieldExtensions
    {
        static public UI_Control uiControlCurrent(this BaseField field)
        {
            if (HighLogic.LoadedSceneIsFlight)
            {
                return field.uiControlFlight;
            }
            else if (HighLogic.LoadedSceneIsEditor)
            {
                return field.uiControlEditor;
            }
            else
            {
                return null;
            }
        }
    }

    public class ModuleDeployableBattery : ModuleGroundSciencePart, IModuleInfo
    {

        [KSPField(isPersistant = false, guiActive = true, guiActiveUnfocused = true, unfocusedRange = 20, guiName = "Electric Charge", guiFormat = "F2"),
        UI_ProgressBar(minValue = 0f, maxValue = 400f, scene = UI_Scene.Flight)]
        public float currentEC = 0;

        [KSPField(isPersistant = false, guiActive = true, guiActiveUnfocused = true, unfocusedRange = 30, guiName = "Base Power-Unit-Hours", guiFormat = "F2")]
        public float basePowerUnitHours = 0;

        [KSPField(isPersistant = false, guiActive = true, guiActiveUnfocused = true, unfocusedRange = 30, guiName = "Actual Power-Unit-Hours", guiFormat = "F2")]
        public float powerUnitHours = 0;

        [KSPField(isPersistant = true, guiActive = true, guiActiveUnfocused = true, guiName = "Max Power Unit Flow"),
            UI_FloatRange(stepIncrement = 1f, maxValue = 5f, minValue = 0f)]
        public float maxPowerUnitsFlow = 1;

        [KSPField(guiActive = false, guiActiveEditor = false)]
        protected bool isBattery = true;

        public bool IsBattery
        {
            get { return isBattery; }
            set
            {
                if (value == isBattery)
                    return;

                isBattery = value;
                if (base.beingRetrieved)
                    return;

                GameEvents.onGroundSciencePartChanged.Fire(this);
            }
        }



#if false
        [KSPEvent(active = true, guiActive = false, guiActiveEditor = true, guiName = "Enable surface attach (currently disabled)")]
        public void ToggleSrfAttach()
        {
            this.part.attachRules.srfAttach ^= true;
            Events["ToggleSrfAttach"].guiName =
                String.Format("{0} srfAttach (currently {1})",
                this.part.attachRules.srfAttach ? "Disabled" : "enabled",
                this.part.attachRules.srfAttach ? "Enabled" : "disabled"
                );
        }
#endif

        [KSPField(isPersistant = true)]
        public float kerbalEffectAdjustments = 1;
        float invKerbalEffectAdjustments = 1;

        [KSPField]
        public float solarPanelChargeRate = 0.35f / 20f;

        [KSPField]
        public string power = "ElectricCharge";

        int ElectricityId;

        double availAmount;
        double maxPUFlowPerDeltaTime;
        float totalPowerNeeded = 0f;
        int totalPowerProduced = 0;
        int totalSolarPowerProduced = 0;
        int totalBatPowerProduced = 0;

        int numControllers = 0;
        int numBatteries = 0;

        const int delaySecs = 10;
        int delayTics = (int)(delaySecs / Time.fixedDeltaTime);
        int timeTics = 0;

        const float inv3600 = 1f / 3600f;

        double lastTimeUpdated;

        Log Log;

        /// <summary>
        /// Initialize all values
        /// Set maxValue for the ProgressBar and FloatRange from part config
        /// </summary>
        public void Start()
        {
            Log = new Log("ModuleDeployableBattery");

            ElectricityId = PartResourceLibrary.Instance.GetDefinition(power).id;

            var uiRange = this.Fields["maxPowerUnitsFlow"].uiControlCurrent() as UI_FloatRange;
            uiRange.maxValue = maxPowerUnitsFlow;

            if (HighLogic.LoadedSceneIsFlight)
            {
                if (part.Resources.Contains(power))
                {
                    var uiProgressBar = this.Fields["currentEC"].uiControlCurrent() as UI_ProgressBar;
                    uiProgressBar.maxValue = (float)part.Resources[power].maxAmount;
                }
                maxPUFlowPerDeltaTime = maxPowerUnitsFlow * Time.fixedDeltaTime;

                GetCargoPartsInRange(true);
            }
            lastTimeUpdated = Planetarium.fetch.time;
        }

        /// <summary>
        /// Find all Cargo parts within range, used to calculate power needs
        /// If firstTime is true, then also find the closest EVA'd kerbal and use that kerbal's stats for any adjustments
        /// The assumption is that the closest kerbal will be the one which dropped the part
        /// </summary>
        /// <param name="firstTime"></param>
        void GetCargoPartsInRange(bool firstTime = false)
        {
            numControllers = 0;
            numBatteries = 0;

            ModuleGroundExpControl closestModuleControl = null;
            totalPowerNeeded = 0f;
            totalPowerProduced = 0;
            totalSolarPowerProduced = 0;
            totalBatPowerProduced = 0;

            Vessel nearestVessel = null;
            float nearestVesselDistance = -1;

            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v != null && v.Parts.Count == 1)
                {
                    var moduleControl = v.FindPartModuleImplementing<ModuleGroundExpControl>();
                    if (moduleControl != null && moduleControl.part != null)
                    {
                        float num = Vector3.Distance(base.transform.position, moduleControl.part.transform.position);
                        if (num <= moduleControl.controlUnitRange)
                        {
                            if (closestModuleControl == null)
                                closestModuleControl = moduleControl;
                            else
                            {
                                float num2 = Vector3.Distance(base.transform.position, closestModuleControl.part.transform.position);
                                if (num2 < num)
                                    closestModuleControl = moduleControl;
                            }
                            numControllers++;

                            Log.Info("Controller found, power needed: " + closestModuleControl.powerNeeded + ", experimentsConnected: " + closestModuleControl.experimentsConnected);

                            int i = closestModuleControl.powerNeeded.IndexOf(' ');

                            if (i > 0)
                            {
                                totalPowerNeeded += float.Parse(closestModuleControl.powerNeeded.Substring(0, closestModuleControl.powerNeeded.IndexOf(' ')));
                            }
                            else
                                if (closestModuleControl.powerNeeded != null && closestModuleControl.powerNeeded != "")
                                totalPowerNeeded += float.Parse(closestModuleControl.powerNeeded);
                        }
                    }

                    if (v.isEVA)
                    {
                        if (nearestVessel == null)
                        {
                            nearestVessel = v;
                            nearestVesselDistance = Vector3.Distance(base.transform.position, v.Parts[0].transform.position);
                        }
                        else
                        {
                            float num2 = Vector3.Distance(base.transform.position, v.Parts[0].transform.position);
                            if (num2 < nearestVesselDistance)
                            {
                                nearestVessel = v;
                                nearestVesselDistance = num2;
                            }
                        }
                    }
                }
            }

            if (closestModuleControl != null)
            {
                foreach (Vessel v in FlightGlobals.Vessels)
                {
                    if (v != null && v.Parts.Count == 1)
                    {
                        ModuleGroundSciencePart moduleSolar = v.FindPartModuleImplementing<ModuleGroundSciencePart>();
                        if (moduleSolar != null && moduleSolar.IsSolarPanel)
                        {
                            float num = Vector3.Distance(base.transform.position, moduleSolar.part.transform.position);
                            if (num <= closestModuleControl.controlUnitRange)
                            {
                                Log.Info("vessel.directSunlight: " + moduleSolar.vessel.directSunlight + ", Solar found, PowerUnitsRequired: " + moduleSolar.PowerUnitsRequired + ", PowerUnitsProduced: " + moduleSolar.PowerUnitsProduced +
                                    ", ActualPowerUnitsProduced: " + moduleSolar.ActualPowerUnitsProduced);

                                if (moduleSolar.Enabled)
                                {
                                    //
                                    // The following is designed to work around a bug
                                    // https://bugs.kerbalspaceprogram.com/issues/24349
                                    //
                                    if (TimeWarp.CurrentRate != 1)
                                    {
                                        if (moduleSolar.vessel.directSunlight)
                                            moduleSolar.ActualPowerUnitsProduced = moduleSolar.PowerUnitsProduced;
                                        else
                                            moduleSolar.ActualPowerUnitsProduced = 0;
                                    }
                                    totalSolarPowerProduced += moduleSolar.ActualPowerUnitsProduced;
                                }
                            }
                        }
                        else
                        {
                            ModuleDeployableBattery moduleBattery = v.FindPartModuleImplementing<ModuleDeployableBattery>();

                            if (moduleBattery != null && moduleBattery.IsBattery)
                            {
                                float num = Vector3.Distance(base.transform.position, moduleBattery.part.transform.position);
                                if (num <= closestModuleControl.controlUnitRange)
                                {
                                    numBatteries++;
                                    int batPowerProduced = (int)moduleBattery.maxPowerUnitsFlow;

                                    if (moduleBattery.availAmount < maxPUFlowPerDeltaTime)
                                    {
                                        batPowerProduced = 0;
                                    }
                                    totalBatPowerProduced += batPowerProduced;
                                }
                            }
                        }
                    }
                }
            }

            // There is no easy way to know which EVA'd kerbal dropped/placed this, so when created,
            // as part of the look of all vessels, find the kerbal nearest this part, and use that 
            // kerbal's skill to get the effect

            if (firstTime)
            {
                if (nearestVessel != null && nearestVessel.isEVA && nearestVesselDistance < 2)
                {
                    if (nearestVessel.parts[0].protoModuleCrew[0].HasEffect<DeployedSciencePowerSkill>())
                    {
                        DeployedSciencePowerSkill effect = nearestVessel.parts[0].protoModuleCrew[0].GetEffect<DeployedSciencePowerSkill>();
                        if (effect != null)
                        {
                            kerbalEffectAdjustments = effect.GetValue();
                            invKerbalEffectAdjustments = 1f / kerbalEffectAdjustments;
                            string msg1 = "Kerbal Effect Adjustment: " + kerbalEffectAdjustments + ", Description: " + Localizer.Format("#autoLOC_8002229", effect.GetValue());
                            string msg2 = ("MaxValue: " + ((solarPanelChargeRate / kerbalEffectAdjustments) * part.Resources[power].amount).ToString("F2"));

                            Log.Info(msg1);
                            Log.Info(msg2);
                            ScreenMessages.PostScreenMessage(msg1, 10, ScreenMessageStyle.UPPER_CENTER);
                            ScreenMessages.PostScreenMessage(msg2, 10, ScreenMessageStyle.UPPER_CENTER);
                        }
                    }
                }
            }
            Log.Info("totalPowerNeeded: " + totalPowerNeeded + ", totalPowerProduced: " + totalPowerProduced);
        }

        /// <summary>
        /// Process
        /// </summary>
        public void FixedUpdate()
        {
#if false
            if (HighLogic.LoadedSceneIsEditor)
            {
                if (this.part != null)
                    this.enabled = part.attachRules.srfAttach;
                else
                    this.enabled = true;
                if (this.part != null && EditorLogic.SelectedPart != null)
                {
                    if (this.part != EditorLogic.SelectedPart)
                        this.enabled = false;
                    else
                        this.enabled = true;
                }
                else
                    this.enabled = false;
            }
#endif
            if (timeTics++ > delayTics)
                GetCargoPartsInRange();

            availAmount = part.Resources[power].amount;
            if (Enabled && numBatteries > 0)
            {
                double timeSinceLastUpdate = Planetarium.fetch.time - lastTimeUpdated;
                float batPowerNeeded = ((float)totalSolarPowerProduced - totalPowerNeeded) * (float)timeSinceLastUpdate / numBatteries;


                float powerFlow = (solarPanelChargeRate * invKerbalEffectAdjustments) * batPowerNeeded;

                Log.Info("totalPowerNeeded: " + totalPowerNeeded + ", totalPowerProduced: " + totalPowerProduced + ", timeSinceLastUpdate: " + timeSinceLastUpdate.ToString("F3") + ", powerFlow: " + powerFlow);


                if (powerFlow > 0) // charging
                {
                    ActualPowerUnitsProduced =
                            PowerUnitsProduced = (int)maxPowerUnitsFlow;
                    part.Resources[power].amount = Math.Min(part.Resources[power].amount + powerFlow, part.Resources[power].maxAmount);

                }
                else // discharging
                {
                    if (availAmount >= Math.Min(maxPUFlowPerDeltaTime, powerFlow))
                    {
                        ActualPowerUnitsProduced =
                            PowerUnitsProduced = (int)Math.Min(maxPowerUnitsFlow, totalPowerNeeded);
                        part.Resources[power].amount = Math.Max(0, Math.Min(part.Resources[power].amount + powerFlow, part.Resources[power].maxAmount));
                    }
                    else
                    {
                        ActualPowerUnitsProduced =
                           PowerUnitsProduced = 0;
                    }
                }
            }
            currentEC = (float)part.Resources[power].amount;
            basePowerUnitHours = currentEC / solarPanelChargeRate * inv3600;
            //powerUnitHours = currentEC / (solarPanelChargeRate / kerbalEffectAdjustments) * inv3600;
            powerUnitHours = basePowerUnitHours * kerbalEffectAdjustments;
            lastTimeUpdated = Planetarium.fetch.time;
            Log.Info("powerUnitsProduced: " + powerUnitsProduced + ", actualPowerUnitsProduced: " + actualPowerUnitsProduced);
        }

        public string GetModuleTitle()
        {
            return "ModuleDeployableBattery";
        }

        public override string GetInfo()
        {
            string str = power + ": " + part.Resources[power].amount + "/" + part.Resources[power].maxAmount;
            str += "\n\nPower-Unit-Hours (PUH)";
            str += "\n\nBase PUH: " + ((solarPanelChargeRate ) * part.Resources[power].amount).ToString("F2");
            str += "\nEngineer lvl 2-3 PUH: " + ((solarPanelChargeRate * 2) * part.Resources[power].amount).ToString("F2");
            str += "\nEngineer lvl 4-5 PUH: " + ((solarPanelChargeRate * 3) * part.Resources[power].amount).ToString("F2");
            return str;
        }

        public string GetPrimaryField()
        {
            return power;
        }

        public Callback<Rect> GetDrawModulePanelCallback()
        {
            return null;
        }

    }
}
