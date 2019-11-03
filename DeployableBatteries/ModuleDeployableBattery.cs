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

        [KSPField(isPersistant = false, guiActive = true, guiActiveUnfocused = true, guiName = "Electric Charge", guiFormat = "F2"),
        UI_ProgressBar(minValue = 0f, maxValue = 400f, scene = UI_Scene.Flight)]
        public float currentEC = 0;

        [KSPField(isPersistant = false, guiActive = true, guiActiveUnfocused = true, guiName = "Power-Unit-Hours", guiFormat = "F2")]
        public float powerUnitHours = 0;

        [KSPField(isPersistant = true, guiActive = true, guiActiveUnfocused = true, guiName = "Max Power Unit Flow"),
            UI_FloatRange(stepIncrement = 1f, maxValue = 5f, minValue = 0f)]
        public float maxPowerUnitsFlow = 1;

        [KSPField(isPersistant = true)]
        public float kerbalEffectAdjustments = 1;

        [KSPField]
        public float solarPanelChargeRate = 0.35f;

        [KSPField]
        public string power = "ElectricCharge";

        int ElectricityId;

        double availAmount;
        double maxPUFlowPerDeltaTime;
        float totalPowerNeeded = 0f;
        int totalPowerProduced = 0;

        int numControllers = 0;
        int numBatteries = 0;

        const int delaySecs = 10;
        int delayTics = (int)(delaySecs / Time.fixedDeltaTime);
        int timeTics = 0;

        const float inv3600 = 1 / 3600;

        Log Log;

        /// <summary>
        /// Initialize all values
        /// Set maxValue for the ProgressBar and FloatRange from part config
        /// </summary>
        public void Start()
        {
            Log = new Log("ModuleDeployableBattery");
            Log.Info("Start, kerbalEffectAdjustments: " + kerbalEffectAdjustments);

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

                GetCargoInRange(true);
            }
        }

        /// <summary>
        /// Find all Cargo parts within range, used to calculate power needs
        /// If firstTime is true, then also find the closest EVA'd kerbal and use that kerbal's stats for any adjustments
        /// The assumption is that the closest kerbal will be the one which dropped the part
        /// </summary>
        /// <param name="firstTime"></param>
        void GetCargoInRange(bool firstTime = false)
        {
            numControllers = 0;
            numBatteries = 0;

            ModuleGroundExpControl closestModuleControl = null;
            totalPowerNeeded = 0f;
            totalPowerProduced = 0;

            Vessel nearestVessel = null;
            float nearestVesselDistance = -1;

            foreach (var v in FlightGlobals.Vessels)
            {
                var moduleControl = v.FindPartModuleImplementing<ModuleGroundExpControl>();
                if (moduleControl != null)
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
  
                        Log.Info("Controller found, power needed: " + moduleControl.powerNeeded + ", experimentsConnected: " + moduleControl.experimentsConnected);

                        int i = moduleControl.powerNeeded.IndexOf(' ');

                        if (i > 0)
                        {
                            totalPowerNeeded += float.Parse(moduleControl.powerNeeded.Substring(0, moduleControl.powerNeeded.IndexOf(' ')));
                        }
                        else
                            if (moduleControl.powerNeeded != null && moduleControl.powerNeeded != "")
                            totalPowerNeeded += float.Parse(moduleControl.powerNeeded);
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
            if (closestModuleControl != null)
            {
                foreach (var v in FlightGlobals.Vessels)
                {
                    var moduleSolar = v.FindPartModuleImplementing<ModuleGroundSciencePart>();
                    if (moduleSolar != null && moduleSolar.IsSolarPanel)
                    {
                        float num = Vector3.Distance(base.transform.position, moduleSolar.part.transform.position);
                        if (num <= closestModuleControl.controlUnitRange)
                        {
                            Log.Info("Solar found, PowerUnitsRequired: " + moduleSolar.PowerUnitsRequired + ", PowerUnitsProduced: " + moduleSolar.PowerUnitsProduced);

                            if (moduleSolar.Enabled)
                                totalPowerProduced += moduleSolar.PowerUnitsProduced;
                        }
                    }
                    var moduleBattery = v.FindPartModuleImplementing<ModuleDeployableBattery>();
                    if (moduleBattery != null)
                    {
                        float num = Vector3.Distance(base.transform.position, moduleBattery.part.transform.position);
                        if (num <= closestModuleControl.controlUnitRange)
                        {
                            numBatteries++;
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

                    Log.Info("FirstTime, nearestVessel: " + nearestVessel.vesselName);
                    if (nearestVessel.parts[0].protoModuleCrew[0].HasEffect<DeployedSciencePowerSkill>())
                    {
                        DeployedSciencePowerSkill effect = nearestVessel.parts[0].protoModuleCrew[0].GetEffect<DeployedSciencePowerSkill>();
                        if (effect != null)
                        {
                            kerbalEffectAdjustments = effect.GetValue();
                            Log.Info("After effect, kerbalEffectAdjustments: " + kerbalEffectAdjustments + ", Description: " + Localizer.Format("#autoLOC_8002229", effect.GetValue()));
                            Log.Info("MaxValue: " + ((solarPanelChargeRate / kerbalEffectAdjustments) * part.Resources[power].amount).ToString("F2"));
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
            if (timeTics++ > delayTics)
                GetCargoInRange();

            availAmount = part.Resources[power].amount;
            if (Enabled && numBatteries > 0)
            {
                float powerFlow = (solarPanelChargeRate / kerbalEffectAdjustments) * (totalPowerNeeded - totalPowerProduced) * Time.fixedDeltaTime / numBatteries;

                //Log.Info("totalPowerNeeded: " + totalPowerNeeded + ", totalPowerProduced: " + totalPowerProduced + ", powerFlow: " + powerFlow);

                if (powerFlow > 0)
                {
                    if (availAmount >= Math.Min(maxPUFlowPerDeltaTime, powerFlow))
                    {
                        actualPowerUnitsProduced =
                            powerUnitsProduced = (int)maxPowerUnitsFlow;
                        part.RequestResource(ElectricityId, maxPUFlowPerDeltaTime);
                    }
                    else
                        actualPowerUnitsProduced =
                            powerUnitsProduced = 0;
                }
                else
                {
                    actualPowerUnitsProduced =
                            powerUnitsProduced = 0;
                    part.Resources[power].amount = Math.Min(part.Resources[power].amount - powerFlow, part.Resources[power].maxAmount);
                }
            }
            currentEC = (float)part.Resources[power].amount;
            powerUnitHours = currentEC / (solarPanelChargeRate / kerbalEffectAdjustments) * inv3600;
        }

        public string GetModuleTitle()
        {
            return "ModuleDeployableBattery";
        }

        public override string GetInfo()
        {
            string str = power + ": " + part.Resources[power].amount + "/" + part.Resources[power].maxAmount;
            str += "\nPower-Unit-Hours: " + ((solarPanelChargeRate / kerbalEffectAdjustments) * part.Resources[power].amount).ToString("F2");
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
