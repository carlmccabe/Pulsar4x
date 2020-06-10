using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Pulsar4X.ECSLib.ComponentFeatureSets.Damage;
using Pulsar4X.ECSLib.Industry;

namespace Pulsar4X.ECSLib.ComponentFeatureSets.Missiles
{

    public enum GuidanceTypes
    {
        Dumbfire,
        Parent,
        Passive,
        Active
    }

    public enum TriggerTypes
    {
        Contact,
        Timer, 
        Prox,
        Depth,
    }

    public enum PayloadTypes
    {
        Explosive,
        Shaped,
        BombPumpedLaser,
        Submunitions
    }

    
    
    public class OrdnancePayloadAtb : IComponentDesignAttribute
    {
        public TriggerTypes Trigger;
        public double Mass;
        public OrdnancePayloadAtb(TriggerTypes trigger, double totalMass)
        {
            Trigger = trigger;
            Mass = totalMass;
        }

        public void OnComponentInstallation(Entity parentEntity, ComponentInstance componentInstance)
        {
            
        }
    }

    public class OrdnanceExplosivePayload : OrdnancePayloadAtb
    {
        public double ExposiveTnTEQMass; //tnt equvelent
        public double FragMass;
        public double FragCount;
        public double FragCone;
        
        public OrdnanceExplosivePayload(int trigger, double totalMass, double tntEqMass, double fragMass, double fragCount, double fragCone) : base((TriggerTypes)trigger, totalMass)
        {
            ExposiveTnTEQMass = tntEqMass;
            FragMass = fragMass;
            FragCount = fragCount;
            FragCone = fragCone;
        }
    }
    
    public class OrdnanceShapedPayload : OrdnancePayloadAtb
    {
        double ExposiveTnTEQMass; 
        double LinerRadius;
        double LinerDepth;
        double LinerAngle;
        double LinerThickness;
        public OrdnanceShapedPayload(int trigger, double totalMass, double tntEqMass, double linerRadius, double linerDepth, double linerThickness): base((TriggerTypes)trigger, totalMass)
        {
            ExposiveTnTEQMass = tntEqMass;
            LinerRadius = linerRadius;
            LinerDepth = linerDepth;
            LinerAngle = Math.Asin(LinerRadius / LinerDepth);
            LinerThickness = linerThickness;
        }
    }
    public class OrdnanceLaserPayload : OrdnancePayloadAtb
    {
        public OrdnanceLaserPayload(int trigger, double totalMass, Guid designID): base((TriggerTypes)trigger, totalMass)
        {
        }
    }
    public class OrdnanceSubmunitionsPayload : OrdnancePayloadAtb
    {
        public OrdnanceSubmunitionsPayload(int trigger, double totalMass, Guid designID, int count): base((TriggerTypes)trigger, totalMass)
        {
        }
    }

    
    
    
    public class OrdnanceDesign : ICargoable, IConstrucableDesign, ISerializable
    {
        public ConstructableGuiHints GuiHints { get; } = ConstructableGuiHints.IsOrdinance;
        public Guid ID { get; } = Guid.NewGuid();
        public string Name { get; set; }
        public Guid CargoTypeID { get; }
        public int DesignVersion = 0;
        public bool IsObsolete = false;
        public int Mass { get; }
        public double WetMass { get; }
        public double DryMass { get; }
        public double ExaustVelocity { get; }
        public double BurnRate { get; }
        
        public double Volume;
        public List<(ComponentDesign design, int count)> Components;
        public (ArmorSD type, float thickness) Armor;
        public Dictionary<Guid, int> ResourceCosts { get; internal set; } = new Dictionary<Guid, int>();
        public Dictionary<Guid, int> MineralCosts = new Dictionary<Guid, int>();
        public Dictionary<Guid, int> MaterialCosts = new Dictionary<Guid, int>();
        public Dictionary<Guid, int> ComponentCosts = new Dictionary<Guid, int>();
        public Dictionary<Guid, int> ShipInstanceCost = new Dictionary<Guid, int>();
        public int CrewReq;
        public int IndustryPointCosts { get; }
        
        //TODO: this is one of those places where moddata has bled into hardcode...
        //the guid here is from IndustryTypeData.json "Ordinance Construction"
        public Guid IndustryTypeID { get; } = new Guid("5ADBF620-3740-4FD7-98BE-E8670D58945F");
        public void OnConstructionComplete(Entity industryEntity, CargoStorageDB storage, Guid productionLine, IndustryJob batchJob, IConstrucableDesign designInfo)
        { 
            var industrydb = industryEntity.GetDataBlob<IndustryAbilityDB>();
        }

        public int CreditCost;
        public EntityDamageProfileDB DamageProfileDB;

        [JsonConstructor]
        internal OrdnanceDesign()
        {
        }

        public OrdnanceDesign(FactionInfoDB faction, string name, double fuelAmountKG,  List<(ComponentDesign design, int count)> components)
        {
            faction.MissileDesigns.Add(ID, this);
            faction.IndustryDesigns[ID] = this;
            Name = name;
            Components = components;
            
            //TODO! we're leaking softcode into hard code here! this is the "ordnance" cargo type, tells us to store this missile in "ordnance" type cargo. 
            CargoTypeID = new Guid("055E2026-20A4-4CFA-A8CA-A01915A48B5E"); 
            BurnRate = 0;
            Guid fuelType = Guid.Empty;
            double fuelMass = fuelAmountKG;
            foreach (var component in components)
            {
                //If the mounttype does not include missiles, it will just ignore the component and wont add it. 
                if((component.design.ComponentMountType & ComponentMountType.Missile) == ComponentMountType.Missile)
                {
                    Mass += component.design.Mass * component.count;
                    CreditCost += component.design.CreditCost;

                    if (ComponentCosts.ContainsKey(component.design.ID))
                    {
                        ComponentCosts[component.design.ID] = ComponentCosts[component.design.ID] + component.count;
                    }
                    else
                    {
                        ComponentCosts.Add(component.design.ID, component.count);
                    }

                    if (component.design.TryGetAttribute<NewtonionThrustAtb>(out NewtonionThrustAtb thrAtb))
                    {
                        //thrusters should all be of the same type.
                        ExaustVelocity = thrAtb.ExhaustVelocity;
                        BurnRate += thrAtb.FuelBurnRate;
                         fuelType = thrAtb.FuelType;
                    }
                }
            }


            WetMass = Mass + fuelMass;
            DryMass = Mass;
            
            MineralCosts.ToList().ForEach(x => ResourceCosts[x.Key] = x.Value);
            MaterialCosts.ToList().ForEach(x => ResourceCosts[x.Key] = x.Value);
            ComponentCosts.ToList().ForEach(x => ResourceCosts[x.Key] = x.Value);
            IndustryPointCosts = Mass;
        }
        

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            throw new NotImplementedException();
        }
    }
    public class ElectronicsSuite : IComponentDesignAttribute
    {


        public TriggerTypes TriggerType = TriggerTypes.Contact;

        public GuidanceTypes GuidenceType = GuidanceTypes.Dumbfire;



        public ElectronicsSuite(int guidenceType)
        {
            GuidenceType = (GuidanceTypes)guidenceType;
        }

        public void OnComponentInstallation(Entity parentEntity, ComponentInstance componentInstance)
        {
            
        }
    }

    interface IGuidenceType
    {
        GuidanceTypes GetGuidenceType { get; }
    }

    struct DumbfireGuidence : IGuidenceType
    {
        public GuidanceTypes GetGuidenceType => GuidanceTypes.Dumbfire;
        //public double TriggerAfterSeconds;
    }
    struct ParentGuidence : IGuidenceType
    {
        public GuidanceTypes GetGuidenceType => GuidanceTypes.Parent;
    }
    struct PassiveGuidence : IGuidenceType
    {
        public GuidanceTypes GetGuidenceType => GuidanceTypes.Passive;
    }
    struct ActiveGuidence : IGuidenceType
    {
        public GuidanceTypes GetGuidenceType => GuidanceTypes.Active;
    }
    
    interface ITriggerType
    {
        TriggerTypes GetTriggerType { get; }
    }

    struct ContactTrigger : ITriggerType
    {
        public TriggerTypes GetTriggerType => TriggerTypes.Contact;
        //public double TriggerAfterSeconds;
    }
    struct TimerTrigger : ITriggerType
    {
        public TriggerTypes GetTriggerType => TriggerTypes.Timer;
        public double TriggerAfterSeconds;
    }
    struct ProxTrigger : ITriggerType
    {
        public TriggerTypes GetTriggerType => TriggerTypes.Prox;
        public double TriggerWhenDistanceFromTarget;
    }
    struct DepthTrigger : ITriggerType
    {
        public TriggerTypes GetTriggerType => TriggerTypes.Depth;
        public double TriggerWhenDistanceAfterContact;
    }
}