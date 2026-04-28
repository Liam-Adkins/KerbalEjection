using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
 * Current Goal: Create new part at position of exploding Part. Load all kerbals into it, and then EVA them, before destroying the part.
 * */
namespace KerbalEjection
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class kerbalEjection : MonoBehaviour
    {
        HashSet<string> ejectedKerbals = new HashSet<string>(); //Track kerbals that need to be ejected when they EVA
        string versionNumber = "0.0.1"; 
        /*
         * Runs at the start of the flight scene. Subscribes necessary functions to their corresponding events
         */
        public void Start ()
        {
            GameEvents.onPartWillDie.Add(onExplosion);
            GameEvents.onCrewOnEva.Add(onEVA);
        }

        /*
         * Runs at the end of the flight scene. Unsubscribes functions from events to prevent memory leaks and unintended behavior in other scenes
         */
        public void OnDisable()
        {
            GameEvents.onPartWillDie.Remove(onExplosion);
            GameEvents.onCrewOnEva.Remove(onEVA);
        }

        /*
         * Should run when a part explodes.
         * Checks if the part contains crew members, if so, it will attempt to EVA them.
         */
        private void onExplosion(Part part)
        {
            if (part.protoModuleCrew.Count == 0)
            {
                return;
            }
            List<ProtoCrewMember> crewSnapshot = new List<ProtoCrewMember>(part.protoModuleCrew);
            foreach (ProtoCrewMember kerbal in crewSnapshot)
            {
                log("Pod Contains: " + kerbal.name);
            }
            KerbalEVA flyingKerbal = null;
            foreach (ProtoCrewMember kerbal in crewSnapshot)
            {
                if (kerbal == null)
                {
                    log("Error: " + kerbal.name + " does not exist");
                    continue;
                }
                ejectedKerbals.Add(kerbal.name);
                flyingKerbal = ejectKerbal(kerbal, part);
                if (flyingKerbal == null)
                {
                    log("Error: Failed to spawn EVA for " + kerbal.name);
                    continue;
                }
                log("Ejecting " + kerbal.name);
            }
            StartCoroutine(SwitchToEVAVesselWhenReady(flyingKerbal));
        }
        

        //Creates a new kerbal on EVA
        private KerbalEVA ejectKerbal(ProtoCrewMember kerbal, Part fromPart)
        {
            if (kerbal.KerbalRef != null)
            {
                kerbal.KerbalRef.state = Kerbal.States.BAILED_OUT;
            }
            KerbalEVA kerbalEVA = FlightEVA.Spawn(kerbal);
            CollisionEnhancer.bypass = true;
            kerbalEVA.gameObject.SetActive(value: true);
            kerbalEVA.part.vessel = kerbalEVA.gameObject.AddComponent<Vessel>();
            kerbalEVA.transform.position = fromPart.transform.position + Vector3.up * 2f + fromPart.transform.rotation * Vector3.right * 2f;
            kerbalEVA.vessel.Initialize();
            kerbalEVA.vessel.id = Guid.NewGuid();
            kerbalEVA.GetComponent<Rigidbody>().velocity = FlightGlobals.ActiveVessel.GetComponent<Rigidbody>().velocity;
            if (fromPart != null)
            {
                double num = fromPart.temperature;
                if (num > kerbalEVA.part.maxTemp - 50.0)
                {
                    num = kerbalEVA.part.maxTemp - 50.0;
                }
                if (num < PhysicsGlobals.SpaceTemperature)
                {
                    num = PhysicsGlobals.SpaceTemperature;
                }
                if (GameSettings.EVA_INHERIT_PART_TEMPERATURE)
                {
                    kerbalEVA.part.skinTemperature = (kerbalEVA.part.skinUnexposedTemperature = (kerbalEVA.part.temperature = num));
                }
                else
                {
                    kerbalEVA.part.skinTemperature = (kerbalEVA.part.skinUnexposedTemperature = (kerbalEVA.part.temperature = kerbalEVA.evaExitTemperature));
                }
                kerbalEVA.part.staticPressureAtm = fromPart.staticPressureAtm;
            }
            kerbal.flightLog.AddEntryUnique(FlightLog.EntryType.ExitVessel, fromPart.vessel.orbit.referenceBody.name);
            fromPart.RemoveCrewmember(kerbal);
            kerbalEVA.part.AddCrewmember(kerbal);
            if (KerbalInventoryScenario.Instance != null)
            {
                KerbalInventoryScenario.Instance.RemoveKerbalInventoryInstance(kerbal.name);
            }
            kerbalEVA.gameObject.name = kerbal.GetKerbalEVAPartName() + " (" + kerbal.name + ")";
            kerbalEVA.vessel.vesselName = kerbal.name;
            kerbalEVA.vessel.vesselType = VesselType.EVA;
            kerbalEVA.vessel.orbit.referenceBody = FlightGlobals.getMainBody(kerbalEVA.transform.position);
            kerbalEVA.vessel.lastVel = kerbalEVA.vessel.orbit.GetRelativeVel() - kerbalEVA.vessel.orbit.GetRotFrameVel(kerbalEVA.vessel.orbit.referenceBody);
            kerbalEVA.vessel.launchedFrom = fromPart.vessel.launchedFrom;
            kerbalEVA.vessel.IgnoreGForces(10);
            kerbalEVA.part.flightID = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
            kerbalEVA.part.missionID = fromPart.missionID;
            kerbalEVA.part.launchID = fromPart.launchID;
            kerbalEVA.part.flagURL = fromPart.flagURL;
            GameEvents.onCrewOnEva.Fire(new GameEvents.FromToAction<Part, Part>(fromPart, kerbalEVA.part));
            GameEvents.onCrewTransferred.Fire(new GameEvents.HostedFromToAction<ProtoCrewMember, Part>(kerbal, fromPart, kerbalEVA.part));
            Vessel.CrewWasModified(fromPart.vessel, kerbalEVA.vessel);
            return kerbalEVA;
        }

        //Switches focus to the EVA vessel
        private IEnumerator SwitchToEVAVesselWhenReady(KerbalEVA eva)
        {
            while (!eva.Ready)
            {
                yield return null;
            }
            if (FlightGlobals.ForceSetActiveVessel(eva.vessel))
            {
                FlightInputHandler.SetNeutralControls();
            }
        }

        /*
         * Creates a force in a random direction within the upper half of a sphere. Gives that force a random magnitude, and then applies it to the kerbal.
         * This creates an "ejection" effect, knocking them away from the explosion.
         */
        private Vector3 applyEjectionForce(Part kerbonaut)
        {
            float ejectionMagnitude = UnityEngine.Random.Range(20f, 100f);
            Vector3 ejectionForce = new Vector3(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(0f, 1f), UnityEngine.Random.Range(-1f, 1f)) * ejectionMagnitude;
            kerbonaut.Rigidbody.AddForce(ejectionForce, ForceMode.Impulse);
            return ejectionForce;
        }

        /*
         * Runs whenever a kerbal goes on EVA. Makes all kerbals on EVA invincible. If the kerbal is set to be ejected, it will apply an ejection force to the kerbal.
         */
        private void onEVA(GameEvents.FromToAction<Part, Part> data)
        {
            Part kerbonaut = data.to;
            if (kerbonaut == null)
            {
                log("Error: EVA part is null");
                return;
            }
            applyInvincibility(kerbonaut);

            ProtoCrewMember pcm = kerbonaut.protoModuleCrew.FirstOrDefault();
            if (pcm == null)
            {
                log("Error: Crew Member lacks PCM");
                return;
            }
            string kerbalName = pcm.name;
            log("Checking EVA for " + kerbalName);
            if (ejectedKerbals.Contains(kerbalName))
            {
                kerbonaut.transform.position += Vector3.up * 2f;
                if (kerbonaut == null || kerbonaut.Rigidbody == null)
                {
                    log("Error: Kerbal EVA part or rigidbody is null");
                    return;
                }
                Vector3 ejectionForce = applyEjectionForce(kerbonaut);
                log("Launching " + kerbalName + " with force " + ejectionForce);
                ejectedKerbals.Remove(kerbalName);
            }
        }

        /*
         * Makes the kerbal invincible
         */
        private void applyInvincibility(Part part)
        {
            part.crashTolerance = float.MaxValue;
            part.maxTemp = float.MaxValue;
            part.skinMaxTemp = float.MaxValue;
            part.maxDepth = float.MaxValue;
            part.maxPressure = float.MaxValue;
        }

        /*
         * Logs messages to the console with a consistent format and version number for easier debugging.
         */
        private void log(string message)
        {
            print("[KerbalEjection Version " + versionNumber + "] " + message);
        }
    }

}

