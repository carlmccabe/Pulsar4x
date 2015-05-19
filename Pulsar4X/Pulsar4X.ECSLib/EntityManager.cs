﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using Newtonsoft.Json;

namespace Pulsar4X.ECSLib
{
    public class GuidNotFoundException : Exception
    {
        public GuidNotFoundException()
        {
        }

        public GuidNotFoundException(string message) : base(message)
        {
        }

        public GuidNotFoundException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
    public class EntityManager : ISerializable
    {
        private static Dictionary<Type, int> _dataBlobTypes;

        private readonly List<Entity> _entities;
        private int _nextEntityID;
        private readonly List<List<BaseDataBlob>> _dataBlobMap;
        private readonly List<ComparableBitArray> _entityMasks;

        private static Dictionary<Guid, EntityManager> _globalGuidDictionary;
        private readonly Dictionary<Guid, Entity> _localEntityDictionary;

        private static ReaderWriterLockSlim _guidLock;
        private readonly object _entityLock;

        public EntityManager()
        {
            // Initialize our static variables.
            if (_dataBlobTypes == null)
            {
                _dataBlobTypes = new Dictionary<Type, int>();

                int i = 0;
                // Use reflection to setup all our dataBlobMap.
                // Find all types that implement BaseDataBlob
                foreach (Type type in Assembly.GetExecutingAssembly().GetTypes().Where(type => type.IsSubclassOf(typeof(BaseDataBlob)) && !type.IsAbstract))
                {
                    _dataBlobTypes.Add(type, i);
                    i++;
                }

                _globalGuidDictionary = new Dictionary<Guid, EntityManager>(65536);
                _guidLock = new ReaderWriterLockSlim();
            }

            // Initialize our instance variables.
            _dataBlobMap = new List<List<BaseDataBlob>>(_dataBlobTypes.Count);
            _entities = new List<Entity>(256);
            _entityMasks = new List<ComparableBitArray>(256);
            _localEntityDictionary = new Dictionary<Guid, Entity>(256);
            _entityLock = new object();

            // Fill out the first level of the datablob map.
            for (int i = 0; i < _dataBlobTypes.Count; i++)
            {
                _dataBlobMap.Add(new List<BaseDataBlob>(256));
            }

            _nextEntityID = 0;
            Clear();
        }

        /// <summary>
        /// Verifies that the supplied entityID is valid in this manager.
        /// </summary>
        /// <returns>True is the entityID is considered valid.</returns>
        private bool IsValidEntity(int entityID)
        {
            lock (_entityLock)
            {
                if (entityID < 0 || entityID >= _entities.Count)
                {
                    return false;
                }
                return _entities[entityID] != null;
            }
        }

        /// <summary>
        /// Verifies that the supplied entity is valid in this manager.
        /// </summary>
        /// <returns>True is the entity is considered valid.</returns>
        public bool IsValidEntity(Entity entity)
        {
            lock (_entityLock)
            {
                if (entity.Manager != this)
                {
                    return false;
                }
                if (!IsValidEntity(entity.ID))
                {
                    return false;
                }
                return _entities[entity.ID] == entity;
            }
        }

        /// <summary>
        /// Direct lookup of an entity's DataBlob.
        /// Slower than GetDataBlob(entityID, typeIndex)
        /// 
        /// NOT FOR USE OUTSIDE ENTITY.CS
        /// 
        /// Use entity.GetDataBlob<T>();
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when an invalid entityID is passed.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when T is not derived from BaseDataBlob.</exception>
        internal T GetDataBlob<T>(int entityID) where T : BaseDataBlob
        {
            int typeIndex = GetTypeIndex<T>();
            return GetDataBlob<T>(entityID, typeIndex);
        }

        /// <summary>
        /// Direct lookup of an entity's DataBlob.
        /// Fastest direct lookup available.
        /// 
        /// NOT FOR USE OUTSIDE ENTITY.CS
        /// 
        /// Use entity.GetDataBlob<T>(typeIndex);
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when an invalid typeIndex or entityID is passed.</exception>
        /// <exception cref="InvalidCastException">Thrown when typeIndex does not match m_dataBlobTypes entry for Type T</exception>
        internal T GetDataBlob<T>(int entityID, int typeIndex) where T : BaseDataBlob
        {
            lock (_entityLock)
            {
                return (T)_dataBlobMap[typeIndex][entityID];
            }
        }

        /// <summary>
        /// Direct lookup of an entity's DataBlob.
        /// Fastest direct lookup available.
        /// 
        /// NOT FOR USE OUTSIDE ENTITY.CS
        /// 
        /// Use entity.GetDataBlob<T>(typeIndex);
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when an invalid typeIndex or entityID is passed.</exception>
        internal BaseDataBlob GetDataBlob(int entityID, int typeIndex)
        {
            lock (_entityLock)
            {
                return _dataBlobMap[typeIndex][entityID];
            }
        }

        /// <summary>
        /// Sets the DataBlob for the specified entity.
        /// 
        /// NOT FOR USE OUTSIDE ENTITY.CS
        /// 
        /// Use entity.SetDataBlob(dataBlob);
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when dataBlob is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when an invalid entityID is passed.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when T is not derived from BaseDataBlob.</exception>
        internal void SetDataBlob<T>(int entityID, T dataBlob) where T : BaseDataBlob
        {
            int typeIndex = GetTypeIndex<T>();
            SetDataBlob(entityID, dataBlob, typeIndex);
        }

        /// <summary>
        /// Sets the DataBlob for the specified entity.
        /// 
        /// NOT FOR USE OUTSIDE ENTITY.CS
        /// 
        /// Use entity.SetDataBlob(dataBlob typeIndex);
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when dataBlob is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when an invalid typeIndex or entityID is passed.</exception>
        internal void SetDataBlob(int entityID, BaseDataBlob dataBlob, int typeIndex)
        {
            lock (_entityLock)
            {
                if (dataBlob == null)
                {
                    throw new ArgumentNullException("dataBlob", "Do not use SetDataBlob to remove a datablob. Use RemoveDataBlob.");
                }
                _dataBlobMap[typeIndex][entityID] = dataBlob;
                _entityMasks[entityID][typeIndex] = true;

                dataBlob.OwningEntity = _entities[entityID];
            }
        }

        /// <summary>
        /// Removes the DataBlob from the specified entity.
        /// Slower than RemoveDataBlob(entityID, typeIndex).
        /// 
        /// NOT FOR USE OUTSIDE ENTITY.CS
        /// 
        /// Use entity.RemoveDataBlob<T>();
        /// </summary>
        /// <exception cref="KeyNotFoundException">Thrown when T is not derived from BaseDataBlob.</exception>
        /// <exception cref="ArgumentException">Thrown when an invalid entityID is passed.</exception>
        internal void RemoveDataBlob<T>(int entityID) where T : BaseDataBlob
        {
            int typeIndex = GetTypeIndex<T>();
            lock (_entityLock)
            {
                RemoveDataBlob(entityID, typeIndex);
            }
        }

        /// <summary>
        /// Removes the DataBlob from the specified entity.
        /// Fastest DataBlob removal available.
        /// 
        /// NOT FOR USE OUTSIDE ENTITY.CS
        /// 
        /// Use entity.RemoveDataBlob(typeIndex);
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when an invalid typeIndex or entityID is passed.</exception>
        internal void RemoveDataBlob(int entityID, int typeIndex)
        {
            lock (_entityLock)
            {
                if (!IsValidEntity(entityID))
                {
                    throw new ArgumentException("Invalid Entity.");
                }

                _dataBlobMap[typeIndex][entityID] = null;
                _entityMasks[entityID][typeIndex] = false;
            }
        }

        /// <summary>
        /// Returns a list of all DataBlobs with type T.
        /// <para></para>
        /// Returns a blank list if no DataBlobs of type T found.
        /// </summary>
        /// <exception cref="KeyNotFoundException">Thrown when T is not derived from BaseDataBlob.</exception>
        public List<T> GetAllDataBlobsOfType<T>() where T : BaseDataBlob
        {
            var dataBlobs = new List<T>();
            lock (_entityLock)
            {
                dataBlobs.AddRange(_dataBlobMap[GetTypeIndex<T>()].Where(dataBlob => dataBlob != null).Cast<T>());

                return dataBlobs;
            }
        }

        /// <summary>
        /// Returns a list of all DataBlobs for a given entity.
        /// <para></para>
        /// Returns a blank list if entity has no DataBlobs.
        /// 
        /// NOT FOR USE OUTSIDE ENTITY.CS
        /// 
        /// Use entity.GetAllDataBlobs();
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when passed an invalid entity.</exception>
        internal List<BaseDataBlob> GetAllDataBlobsOfEntity(Entity entity)
        {
            if (!IsValidEntity(entity))
            {
                throw new ArgumentException("Invalid Entity.");
            }

            var entityDBs = new List<BaseDataBlob>();

            lock (_entityLock)
            {
                ComparableBitArray entityMask = _entityMasks[entity.ID];

                foreach (int setBit in entityMask.SetBits)
                {
                    entityDBs.Add(GetDataBlob(entity.ID, setBit));
                }

                return entityDBs;
            }
        }

        /// <summary>
        /// Returns a list of entityID id's for entities that have datablob type T.
        /// <para></para>
        /// Returns a blank list if no DataBlobs of type T exist.
        /// <para></para>
        /// DO NOT ASSUME THE ORDER OF THE RETURNED LIST!
        /// List returned is in order by entityID, which is determined internally by the EntityManager
        /// </summary>
        /// <exception cref="KeyNotFoundException">Thrown when T is not derived from BaseDataBlob.</exception>
        public List<Entity> GetAllEntitiesWithDataBlob<T>() where T : BaseDataBlob
        {
            int typeIndex = GetTypeIndex<T>();

            ComparableBitArray dataBlobMask = BlankDataBlobMask();
            dataBlobMask[typeIndex] = true;

            return GetAllEntitiesWithDataBlobs(dataBlobMask);
        }

        /// <summary>
        /// Returns a list of entityID id's for entities that contain all dataBlobs defined by
        /// the dataBlobMask.
        /// <para></para>
        /// Returns a blank list if no entities have all needed DataBlobs
        /// <para></para>
        /// DO NOT ASSUME THE ORDER OF THE RETURNED LIST!
        /// List returned is in order by entityID, which is determined internally by the EntityManager
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when passed a malformed (incorrect length) dataBlobMask.</exception>
        /// <exception cref="NullReferenceException">Thrown when dataBlobMask is null.</exception>
        public List<Entity> GetAllEntitiesWithDataBlobs(ComparableBitArray dataBlobMask)
        {
            var entities = new List<Entity>();

            lock (_entityLock)
            {
                if (dataBlobMask.Length != _dataBlobTypes.Count)
                {
                    throw new ArgumentException("dataBlobMask must contain a bit value for each dataBlobType.");
                }

                entities.AddRange(_entities.Where(entity => entity != null)
                                           .Where(entity => (_entityMasks[entity.ID] & dataBlobMask) == dataBlobMask));

                return entities;
            }
        }

        /// <summary>
        /// Returns the first entityID found with the specified DataBlobType.
        /// <para></para>
        /// Returns -1 if no entities have the specified DataBlobType.
        /// </summary>
        /// <exception cref="KeyNotFoundException">Thrown when T is not derived from BaseDataBlob.</exception>
        public Entity GetFirstEntityWithDataBlob<T>() where T : BaseDataBlob
        {
            return GetFirstEntityWithDataBlob(GetTypeIndex<T>());
        }

        /// <summary>
        /// Returns the first entityID found with the specified DataBlobType.
        /// <para></para>
        /// Returns -1 if no entities have the specified DataBlobType.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when passed an invalid typeIndex</exception>
        public Entity GetFirstEntityWithDataBlob(int typeIndex)
        {
            List<BaseDataBlob> dataBlobType = _dataBlobMap[typeIndex];
            lock (_entityLock)
            {
                return _entities.Where((entity, i) => dataBlobType[i] != null)
                                .DefaultIfEmpty(Entity.InvalidEntity)
                                .FirstOrDefault();
            }
        }

        /// <summary>
        /// Returns a blank DataBlob mask with the correct number of entries.
        /// </summary>
        public static ComparableBitArray BlankDataBlobMask()
        {
            return new ComparableBitArray(_dataBlobTypes.Count);
        }       
        
        /// <summary>
        /// Adds an entity with the pre-existing datablobs to this EntityManager.
        /// NOT FOR USE OUTSIDE ENTITY.CS
        /// 
        /// Use Entity.Create(manager, dataBlobs);
        /// </summary>
        internal Entity CreateEntity(List<BaseDataBlob> dataBlobs)
        {
            lock (_entityLock)
            {
                Guid entityGuid = CreateEntityGuid();
                return CreateEntity(new Entity(entityGuid, this), dataBlobs);
            }
        }

        private Entity CreateEntity(Entity entity, List<BaseDataBlob> dataBlobs)
        {
            int newID = CreateEntityID();
            entity.SetID(newID);
            SetupEntitySlot(entity);

            _localEntityDictionary.Add(entity.Guid, entity);

            if (dataBlobs != null)
            {
                foreach (BaseDataBlob dataBlob in dataBlobs)
                {
                    int typeIndex;
                    TryGetTypeIndex(dataBlob.GetType(), out typeIndex);
                    SetDataBlob(newID, dataBlob, typeIndex);
                }
            }

            return entity;
        }

        private void SetupEntitySlot(Entity entity)
        {
            if (entity.ID == _entities.Count)
            {
                _entities.Add(entity);
                _entityMasks.Add(new ComparableBitArray(_dataBlobTypes.Count));

                // Make sure the entityDBMaps have enough space for this entity.
                foreach (List<BaseDataBlob> entityDBMap in _dataBlobMap)
                {
                    entityDBMap.Add(null);
                }
            }
            else
            {
                _entities[entity.ID] = entity;
                _entityMasks[entity.ID] = new ComparableBitArray(_dataBlobTypes.Count);

                // Make sure the EntityDBMaps are null for this entity.
                // This should be done by RemoveentityID, but let's just be safe.
                for (int typeIndex = 0; typeIndex < _dataBlobTypes.Count; typeIndex++)
                {
                    _dataBlobMap[typeIndex][entity.ID] = null;
                }
            }
        }

        private int CreateEntityID()
        {
            int entityID;
            for (entityID = _nextEntityID; entityID < _entities.Count; entityID++)
            {
                if (_entities[entityID] == null)
                {
                    break;
                }
            }
            _nextEntityID = entityID + 1;
            return entityID;
        }

        private Guid CreateEntityGuid()
        {
            Guid entityGuid = Guid.NewGuid();
            _guidLock.EnterWriteLock();
            try
            {
                do
                {
                    entityGuid = Guid.NewGuid();
                } while (_globalGuidDictionary.ContainsKey(entityGuid));

                _globalGuidDictionary.Add(entityGuid, this);
            }
            finally
            {
                _guidLock.ExitWriteLock();
            }

            return entityGuid;
        }

        /// <summary>
        /// Removes this entityID from this entityID manager.
        /// 
        /// NOT FOR USE OUTSIDE ENTITY.CS
        /// 
        /// Use entity.Delete();
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when passed an invalid entity.</exception>
        internal void RemoveEntity(Entity entity)
        {
            if (!IsValidEntity(entity))
            {
                throw new ArgumentException("Entity does not valid in this manager.");
            }

            lock (_entityLock)
            {
                _guidLock.EnterWriteLock();
                try
                {
                    _globalGuidDictionary.Remove(entity.Guid);
                    _localEntityDictionary.Remove(entity.Guid);
                }
                finally
                {
                    _guidLock.ExitWriteLock();
                }

                // Remove the GUID from all lists.
                _entities[entity.ID] = null;

                if (_nextEntityID > entity.ID)
                {
                    _nextEntityID = entity.ID;
                }

                // Destroy references to datablobs.
                foreach (List<BaseDataBlob> dataBlobType in entity.Manager._dataBlobMap)
                {
                    dataBlobType[entity.ID] = null;
                }

                // Remove the entity finally.
                _entities[entity.ID] = null;
            }
        }

        /// <summary>
        /// Transfers an entity to the specified manager.
        /// 
        /// NOT FOR USE OUTSIDE ENTITY.CS
        /// 
        /// Use entity.Transfer(newManager);
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when passed an invalid entity.</exception>
        internal void TransferEntity(Entity entity, EntityManager manager)
        {
            if (!IsValidEntity(entity))
            {
                throw new ArgumentException("Entity is not valid in this manager.");
            }

            List<BaseDataBlob> dataBlobs = entity.GetAllDataBlobs();

            lock (_entityLock)
            {
                RemoveEntity(entity);
                _guidLock.EnterWriteLock();
                try
                {
                    _globalGuidDictionary[entity.Guid] = this;
                    manager.CreateEntity(entity, dataBlobs);
                }
                finally
                {
                    _guidLock.ExitWriteLock();
                }
            }
        }

        /// <summary>
        /// Gets the associated EntityManager and entityID of the specified Guid.
        /// </summary>
        /// <returns>True if entityID is found.</returns>
        public static bool FindEntityByGuid(Guid entityGuid, out Entity entity)
        {
            _guidLock.EnterReadLock();
            try
            {
                EntityManager manager;
                if (!_globalGuidDictionary.TryGetValue(entityGuid, out manager))
                {
                    entity = Entity.InvalidEntity;
                    return false;
                }

                if (!manager._localEntityDictionary.TryGetValue(entityGuid, out entity))
                {
                    // Can only be reached if memory corruption or somehow the _guidLock thread synchronization fails.
                    // Entity must be removed from the local manager, but not the global list. Should not be possible.
                    throw new GuidNotFoundException();
                }
                return true;
            }
            finally
            {
                _guidLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Gets the associated entityID of the specified Guid.
        /// <para></para>
        /// Does not throw exceptions.
        /// </summary>
        /// <returns>True if entityID exists in this manager.</returns>
        public bool TryGetEntityByGuid(Guid entityGuid, out Entity entity)
        {
            lock (_entityLock)
            {
                _guidLock.EnterReadLock();
                try
                {
                    if (_localEntityDictionary.TryGetValue(entityGuid, out entity))
                    {
                        return true;
                    }
                    entity = Entity.InvalidEntity;
                    return false;
                }
                finally
                {
                    _guidLock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Gets the associated Guid of the specified entityID.
        /// </summary>
        /// <returns>True if entityID exists in this manager.</returns>
        public bool TryGetEntityByID(int entityID, out Entity entity)
        {
            entity = Entity.InvalidEntity;

            lock (_entityLock)
            {
                if (!IsValidEntity(entityID))
                {
                    return false;
                }

                _guidLock.EnterReadLock();
                try
                {
                    entity = _entities[entityID];
                    return true;
                }
                finally
                {
                    _guidLock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Completely clears all entities.
        /// <para></para>
        /// Does not throw exceptions.
        /// </summary>
        public void Clear(bool clearAll = false)
        {
            for (int i = 0; i < _entities.Count; i++)
            {
                if (_entities[i] != null)
                {
                    RemoveEntity(_entities[i]);
                }
            }

            if (clearAll)
            {
                _globalGuidDictionary = new Dictionary<Guid, EntityManager>();
            }
        }

        /// <summary>
        /// Returns the true if the specified type is a valid DataBlobType.
        /// <para></para>
        /// typeIndex parameter is set to the typeIndex of the dataBlobType if found.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when dataBlobType is null.</exception>
        public static bool TryGetTypeIndex(Type dataBlobType, out int typeIndex)
        {
            return _dataBlobTypes.TryGetValue(dataBlobType, out typeIndex);
        }

        /// <summary>
        /// Faster than TryGetDataBlobTypeIndex and uses generics for type safety.
        /// </summary>
        /// <exception cref="KeyNotFoundException">Thrown when T is not derived from BaseDataBlob, or is Abstract</exception>
        public static int GetTypeIndex<T>() where T : BaseDataBlob
        {
            return _dataBlobTypes[typeof(T)];
        }

        #region ISerializable Methods

        /// <summary>
        /// Deserialization constructor. Responsible for putting the EnitityManager together.
        /// </summary>
        // ReSharper disable once UnusedMember.Global
        // ReSharper disable once UnusedParameter.Local
        public EntityManager(SerializationInfo info, StreamingContext context) : this()
        {
            // Retrieve the Guid list.
            var entityGuids = (List<Guid>)info.GetValue("Entities", typeof(List<Guid>));

            // Create an entity for each Guid.
            // Also ensures all Guid dictionaries are set.
            foreach (Guid guid in entityGuids)
            {
                Entity entity;
                if (FindEntityByGuid(guid, out entity))
                {
                    // Entity already created. Likey because it was referenced by a datablob in another manager.
                    // Entity should be residing in the global manager.
                    if (entity.Manager != Game.Instance.GlobalManager)
                    {
                        throw new JsonSerializationException("Entity already exists in non-global manager.");
                    }
                    entity.Transfer(this);
                }
                else
                {
                    // First time this Guid has been encountered.
                    lock (_guidLock)
                    {
                        _guidLock.EnterWriteLock();
                        try
                        {
                            _globalGuidDictionary.Add(guid, this);
                        }
                        finally
                        {
                            _guidLock.ExitWriteLock();
                        }
                        CreateEntity(new Entity(guid, this), null);
                    }
                }
            }

            // Deserialize the datablobs by type.
            foreach (KeyValuePair<Type, int> dataBlobTypeInfo in _dataBlobTypes)
            {
                Type dataBlobType = dataBlobTypeInfo.Key;

                // Little bit of magic here.
                // We're creating a List<DerivedDataBlobType> list.
                // We need to use the fully qualified DerivedDataBlobType (such as OrbitDB)
                // to ensure JSON constructs the right object with the right values.
                Type listType = typeof(List<>).MakeGenericType(dataBlobType);
                // Actually creating the list.
                dynamic dataBlobs = Activator.CreateInstance(listType);

                try
                {
                    // Try to populate the list.
                    dataBlobs = info.GetValue(dataBlobType.Name, listType);
                }
                catch (SerializationException e)
                {
                    if (e.Message == "Member '" + dataBlobType.Name + "' was not found.")
                    {
                        // Harmless. If an EntityManager is storing 0 of a type of datablob, it wont serialize anything for it.
                        // When we go to deserialize "nothing", we get this exception.
                    }
                    else
                    {
                        // Not harmless. This could be any number of normal deserialization problems.
                        // Most likely malformed input.
                        throw;
                    }
                }

                // Set the dataBlobs to their proper entity using a local Guid lookup.
                foreach (dynamic dataBlob in dataBlobs)
                {

                    dataBlob.OwningEntity.SetDataBlob(dataBlob);
                }
            }
        }

        /// <summary>
        /// Serialization function.
        /// This function defines exactly how to output the EntityManager.
        /// </summary>
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            // Serialize DataBlobs by type.
            foreach (KeyValuePair<Type, int> typeKVP in _dataBlobTypes)
            {
                Type dataBlobType = typeKVP.Key;
                int typeIndex = typeKVP.Value;

                // Find non-null datablobs in our memory structure.
                List<object> dataBlobs = (from dataBlob in _dataBlobMap[typeIndex]
                                            where dataBlob != null
                                            select (object)dataBlob).ToList();

                // Serialize the list if not empty.
                if (dataBlobs.Count > 0)
                {
                    info.AddValue(dataBlobType.Name, dataBlobs);
                }
            }
            // Serialize Entities
            List<Guid> defraggedEntities = (from entity in _entities
                                    where entity != null
                                    select entity.Guid).ToList();


            info.AddValue("Entities", defraggedEntities);
        }

        #endregion

        /// <summary>
        /// Returns the DataBlobMask for this entity.
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        public ComparableBitArray GetMask(Entity entity)
        {
            if (!IsValidEntity(entity))
            {
                throw new ArgumentException("Entity is not valid for this manager.");
            }

            return _entityMasks[entity.ID];
        }
    }
}