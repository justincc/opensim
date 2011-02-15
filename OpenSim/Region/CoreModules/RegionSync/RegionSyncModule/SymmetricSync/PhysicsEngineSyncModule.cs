﻿/*
 * Copyright (c) Contributors: TO BE FILLED
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Client;
using OpenSim.Region.CoreModules.Framework.InterfaceCommander;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using log4net;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Mono.Addins;

namespace OpenSim.Region.CoreModules.RegionSync.RegionSyncModule
{

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "AttachmentsModule")]
    public class PhysicsEngineSyncModule : INonSharedRegionModule, IDSGActorSyncModule
    {
        #region INonSharedRegionModule

        public void Initialise(IConfigSource config)
        {
            m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

            IConfig syncConfig = config.Configs["RegionSyncModule"];
            m_active = false;
            if (syncConfig == null)
            {
                m_log.Warn(LogHeader + " No RegionSyncModule config section found. Shutting down.");
                return;
            }
            else if (!syncConfig.GetBoolean("Enabled", false))
            {
                m_log.Warn(LogHeader + " RegionSyncModule is not enabled. Shutting down.");
                return;
            }

            string actorType = syncConfig.GetString("DSGActorType", "").ToLower();

            //Read in configuration, if the local actor is configured to be a client manager, load this module.
            if (!actorType.Equals("physics_engine"))
            {
                m_log.Warn(LogHeader + ": not configured as Scene Persistence Actor. Shut down.");
                return;
            }

            m_actorID = syncConfig.GetString("ActorID", "");
            if (m_actorID.Equals(""))
            {
                m_log.Warn(LogHeader + ": ActorID not specified in config file. Shutting down.");
                return;
            }

            m_active = true;

            m_log.Warn(LogHeader + " Initialised");

        }

        //Called after Initialise()
        public void AddRegion(Scene scene)
        {
            if (!m_active)
                return;

            //connect with scene
            m_scene = scene;

            //register the module with SceneGraph. If needed, SceneGraph checks the module's ActorType to know what type of module it is.
            m_scene.RegisterModuleInterface<IDSGActorSyncModule>(this);

            // register actor
            if (!scene.GridService.RegisterActor(scene.RegionInfo.RegionID.ToString(),
                            "physics_engine", scene.RegionInfo.RegionID.ToString()))
            {
                m_log.ErrorFormat("{0}: Failure registering actor", LogHeader);
            }

            // Setup the command line interface
            //m_scene.EventManager.OnPluginConsole += EventManager_OnPluginConsole;
            //InstallInterfaces();

            //Register for the OnPostSceneCreation event
            //m_scene.EventManager.OnPostSceneCreation += OnPostSceneCreation;

            //Register for Scene/SceneGraph events
            //m_scene.SceneGraph.OnObjectCreate += new ObjectCreateDelegate(PhysicsEngine_OnObjectCreate);
            m_scene.SceneGraph.OnObjectCreateBySync += new ObjectCreateBySyncDelegate(PhysicsEngine_OnObjectCreateBySync);
            m_scene.EventManager.OnSymmetricSyncStop += PhysicsEngine_OnSymmetricSyncStop;
        }

        //Called after AddRegion() has been called for all region modules of the scene.
        //NOTE::However, at this point, Scene may not have requested all the needed region module interfaces yet.
        public void RegionLoaded(Scene scene)
        {
            if (!m_active)
                return;

        }

        public void RemoveRegion(Scene scene)
        {
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Close()
        {
            m_scene = null;
        }

        public string Name
        {
            get { return "PhysicsEngineSyncModule"; }
        }

        #endregion //INonSharedRegionModule


        #region IDSGActorSyncModule members and functions

        private DSGActorTypes m_actorType = DSGActorTypes.PhysicsEngine;
        public DSGActorTypes ActorType
        {
            get { return m_actorType; }
        }

        private string m_actorID;
        public string ActorID
        {
            get { return m_actorID; }
        }

        #endregion //IDSGActorSyncModule

        #region PhysicsEngineSyncModule memebers and functions
        private ILog m_log;
        private bool m_active = false;
        public bool Active
        {
            get { return m_active; }
        }

        private Scene m_scene;

        private string LogHeader = "[PhysicsEngineSyncModule]";

        /// <summary>
        /// Script Engine's action upon an object is added to the local scene
        /// </summary>
        private void PhysicsEngine_OnObjectCreateBySync(EntityBase entity)
        {
            if (entity is SceneObjectGroup)
            {
            }
        }

        public void PhysicsEngine_OnSymmetricSyncStop()
        {
            //remove all objects
            m_scene.DeleteAllSceneObjects();
        }

        #endregion //ScriptEngineSyncModule
    }
}