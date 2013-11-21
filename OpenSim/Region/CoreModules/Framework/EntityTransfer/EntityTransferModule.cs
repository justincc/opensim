/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Client;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Physics.Manager;
using OpenSim.Services.Interfaces;

using GridRegion = OpenSim.Services.Interfaces.GridRegion;

using OpenMetaverse;
using log4net;
using Nini.Config;
using Mono.Addins;

namespace OpenSim.Region.CoreModules.Framework.EntityTransfer
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "EntityTransferModule")]
    public class EntityTransferModule : INonSharedRegionModule, IEntityTransferModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const int DefaultMaxTransferDistance = 4095;
        public const bool WaitForAgentArrivedAtDestinationDefault = true;

        public string OutgoingTransferVersionName { get; set; }

        /// <summary>
        /// Determine the maximum entity transfer version we will use for teleports.
        /// </summary>
        public float MaxOutgoingTransferVersion { get; set; }

        /// <summary>
        /// The maximum distance, in standard region units (256m) that an agent is allowed to transfer.
        /// </summary>
        public int MaxTransferDistance { get; set; }

        /// <summary>
        /// If true then on a teleport, the source region waits for a callback from the destination region.  If
        /// a callback fails to arrive within a set time then the user is pulled back into the source region.
        /// </summary>
        public bool WaitForAgentArrivedAtDestination { get; set; }

        /// <summary>
        /// If true then we ask the viewer to disable teleport cancellation and ignore teleport requests.
        /// </summary>
        /// <remarks>
        /// This is useful in situations where teleport is very likely to always succeed and we want to avoid a 
        /// situation where avatars can be come 'stuck' due to a failed teleport cancellation.  Unfortunately, the
        /// nature of the teleport protocol makes it extremely difficult (maybe impossible) to make teleport 
        /// cancellation consistently suceed.
        /// </remarks>
        public bool DisableInterRegionTeleportCancellation { get; set; }

        /// <summary>
        /// Number of times inter-region teleport was attempted.
        /// </summary>
        private Stat m_interRegionTeleportAttempts;

        /// <summary>
        /// Number of times inter-region teleport was aborted (due to simultaneous client logout).
        /// </summary>
        private Stat m_interRegionTeleportAborts;

        /// <summary>
        /// Number of times inter-region teleport was successfully cancelled by the client.
        /// </summary>
        private Stat m_interRegionTeleportCancels;

        /// <summary>
        /// Number of times inter-region teleport failed due to server/client/network problems (e.g. viewer failed to
        /// connect with destination region).
        /// </summary>
        /// <remarks>
        /// This is not necessarily a problem for this simulator - in open-grid/hg conditions, viewer connectivity to
        /// destination simulator is unknown.
        /// </remarks>
        private Stat m_interRegionTeleportFailures;

        protected bool m_Enabled = false;

        public Scene Scene { get; private set; }

        /// <summary>
        /// Handles recording and manipulation of state for entities that are in transfer within or between regions
        /// (cross or teleport).
        /// </summary>
        private EntityTransferStateMachine m_entityTransferStateMachine;

        private ExpiringCache<UUID, ExpiringCache<ulong, DateTime>> m_bannedRegions =
                new ExpiringCache<UUID, ExpiringCache<ulong, DateTime>>();

        private IEventQueue m_eqModule;
        private IRegionCombinerModule m_regionCombinerModule;

        #region ISharedRegionModule

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public virtual string Name
        {
            get { return "BasicEntityTransferModule"; }
        }

        public virtual void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("EntityTransferModule", "");
                if (name == Name)
                {
                    InitialiseCommon(source);
                    m_log.DebugFormat("[ENTITY TRANSFER MODULE]: {0} enabled.", Name);
                }
            }
        }

        /// <summary>
        /// Initialize config common for this module and any descendents.
        /// </summary>
        /// <param name="source"></param>
        protected virtual void InitialiseCommon(IConfigSource source)
        {
            string transferVersionName = "SIMULATION";
            float maxTransferVersion = 0.2f;

            IConfig transferConfig = source.Configs["EntityTransfer"];
            if (transferConfig != null)
            {
                string rawVersion 
                    = transferConfig.GetString(
                        "MaxOutgoingTransferVersion", 
                        string.Format("{0}/{1}", transferVersionName, maxTransferVersion));

                string[] rawVersionComponents = rawVersion.Split(new char[] { '/' });

                bool versionValid = false;

                if (rawVersionComponents.Length >= 2)
                    versionValid = float.TryParse(rawVersionComponents[1], out maxTransferVersion);

                if (!versionValid)
                {
                    m_log.ErrorFormat(
                        "[ENTITY TRANSFER MODULE]: MaxOutgoingTransferVersion {0} is invalid, using {1}", 
                        rawVersion, string.Format("{0}/{1}", transferVersionName, maxTransferVersion));
                }
                else
                {
                    transferVersionName = rawVersionComponents[0];

                    m_log.InfoFormat(
                        "[ENTITY TRANSFER MODULE]: MaxOutgoingTransferVersion set to {0}", 
                        string.Format("{0}/{1}", transferVersionName, maxTransferVersion));
                }

                DisableInterRegionTeleportCancellation 
                    = transferConfig.GetBoolean("DisableInterRegionTeleportCancellation", false);

                WaitForAgentArrivedAtDestination
                    = transferConfig.GetBoolean("wait_for_callback", WaitForAgentArrivedAtDestinationDefault);
                
                MaxTransferDistance = transferConfig.GetInt("max_distance", DefaultMaxTransferDistance);
            }
            else
            {
                MaxTransferDistance = DefaultMaxTransferDistance;
            }

            OutgoingTransferVersionName = transferVersionName;
            MaxOutgoingTransferVersion = maxTransferVersion;

            m_entityTransferStateMachine = new EntityTransferStateMachine(this);

            m_Enabled = true;
        }

        public virtual void PostInitialise()
        {
        }

        public virtual void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            Scene = scene;

            m_interRegionTeleportAttempts = 
                new Stat(
                    "InterRegionTeleportAttempts",
                    "Number of inter-region teleports attempted.",
                    "This does not count attempts which failed due to pre-conditions (e.g. target simulator refused access).\n"
                        + "You can get successfully teleports by subtracting aborts, cancels and teleport failures from this figure.",
                    "",
                    "entitytransfer",
                    Scene.Name,
                    StatType.Push,
                    null,
                    StatVerbosity.Debug);

            m_interRegionTeleportAborts = 
                new Stat(
                    "InterRegionTeleportAborts",
                    "Number of inter-region teleports aborted due to client actions.",
                    "The chief action is simultaneous logout whilst teleporting.",
                    "",
                    "entitytransfer",
                    Scene.Name,
                    StatType.Push,
                    null,
                    StatVerbosity.Debug);

            m_interRegionTeleportCancels = 
                new Stat(
                    "InterRegionTeleportCancels",
                    "Number of inter-region teleports cancelled by the client.",
                    null,
                    "",
                    "entitytransfer",
                    Scene.Name,
                    StatType.Push,
                    null,
                    StatVerbosity.Debug);

            m_interRegionTeleportFailures = 
                new Stat(
                    "InterRegionTeleportFailures",
                    "Number of inter-region teleports that failed due to server/client/network issues.",
                    "This number may not be very helpful in open-grid/hg situations as the network connectivity/quality of destinations is uncontrollable.",
                    "",
                    "entitytransfer",
                    Scene.Name,
                    StatType.Push,
                    null,
                    StatVerbosity.Debug);

            StatsManager.RegisterStat(m_interRegionTeleportAttempts);
            StatsManager.RegisterStat(m_interRegionTeleportAborts);
            StatsManager.RegisterStat(m_interRegionTeleportCancels);
            StatsManager.RegisterStat(m_interRegionTeleportFailures);

            scene.RegisterModuleInterface<IEntityTransferModule>(this);
            scene.EventManager.OnNewClient += OnNewClient;
        }

        protected virtual void OnNewClient(IClientAPI client)
        {
            client.OnTeleportHomeRequest += TriggerTeleportHome;
            client.OnTeleportLandmarkRequest += RequestTeleportLandmark;

            if (!DisableInterRegionTeleportCancellation)
                client.OnTeleportCancel += OnClientCancelTeleport;

            client.OnConnectionClosed += OnConnectionClosed;
        }

        public virtual void Close() {}

        public virtual void RemoveRegion(Scene scene) 
        {
            if (m_Enabled)
            {
                StatsManager.DeregisterStat(m_interRegionTeleportAttempts);
                StatsManager.DeregisterStat(m_interRegionTeleportAborts);
                StatsManager.DeregisterStat(m_interRegionTeleportCancels);
                StatsManager.DeregisterStat(m_interRegionTeleportFailures);
            }
        }

        public virtual void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_eqModule = Scene.RequestModuleInterface<IEventQueue>();
            m_regionCombinerModule = Scene.RequestModuleInterface<IRegionCombinerModule>();
        }

        #endregion

        #region Agent Teleports

        private void OnConnectionClosed(IClientAPI client)
        {
            if (client.IsLoggingOut && m_entityTransferStateMachine.UpdateInTransit(client.AgentId, AgentTransferState.Aborting))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Aborted teleport request from {0} in {1} due to simultaneous logout", 
                    client.Name, Scene.Name);
            }
        }

        private void OnClientCancelTeleport(IClientAPI client)
        {
            m_entityTransferStateMachine.UpdateInTransit(client.AgentId, AgentTransferState.Cancelling);

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Received teleport cancel request from {0} in {1}", client.Name, Scene.Name);
        }

        public void Teleport(ScenePresence sp, ulong regionHandle, Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            if (sp.Scene.Permissions.IsGridGod(sp.UUID))
            {
                // This user will be a God in the destination scene, too
                teleportFlags |= (uint)TeleportFlags.Godlike;
            }

            if (!sp.Scene.Permissions.CanTeleport(sp.UUID))
                return;

            string destinationRegionName = "(not found)";

            // Record that this agent is in transit so that we can prevent simultaneous requests and do later detection
            // of whether the destination region completes the teleport.
            if (!m_entityTransferStateMachine.SetInTransit(sp.UUID))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Ignoring teleport request of {0} {1} to {2}@{3} - agent is already in transit.",
                    sp.Name, sp.UUID, position, regionHandle);

                sp.ControllingClient.SendTeleportFailed("Previous teleport process incomplete.  Please retry shortly.");

                return;
            }

            try
            {
                // Reset animations; the viewer does that in teleports.
                sp.Animator.ResetAnimations();

                if (regionHandle == sp.Scene.RegionInfo.RegionHandle)
                {
                    destinationRegionName = sp.Scene.RegionInfo.RegionName;

                    TeleportAgentWithinRegion(sp, position, lookAt, teleportFlags);
                }
                else // Another region possibly in another simulator
                {
                    GridRegion finalDestination = null;
                    try
                    {
                        TeleportAgentToDifferentRegion(
                            sp, regionHandle, position, lookAt, teleportFlags, out finalDestination);
                    }
                    finally
                    {
                        if (finalDestination != null)
                            destinationRegionName = finalDestination.RegionName;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[ENTITY TRANSFER MODULE]: Exception on teleport of {0} from {1}@{2} to {3}@{4}: {5}{6}",
                    sp.Name, sp.AbsolutePosition, sp.Scene.RegionInfo.RegionName, position, destinationRegionName,
                    e.Message, e.StackTrace);

                sp.ControllingClient.SendTeleportFailed("Internal error");
            }
            finally
            {
                m_entityTransferStateMachine.ResetFromTransit(sp.UUID);
            }
        }

        /// <summary>
        /// Teleports the agent within its current region.
        /// </summary>
        /// <param name="sp"></param>
        /// <param name="position"></param>
        /// <param name="lookAt"></param>
        /// <param name="teleportFlags"></param
        private void TeleportAgentWithinRegion(ScenePresence sp, Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Teleport for {0} to {1} within {2}",
                sp.Name, position, sp.Scene.RegionInfo.RegionName);

            // Teleport within the same region
            if (IsOutsideRegion(sp.Scene, position) || position.Z < 0)
            {
                Vector3 emergencyPos = new Vector3(128, 128, 128);

                m_log.WarnFormat(
                    "[ENTITY TRANSFER MODULE]: RequestTeleportToLocation() was given an illegal position of {0} for avatar {1}, {2} in {3}.  Substituting {4}",
                    position, sp.Name, sp.UUID, Scene.Name, emergencyPos);

                position = emergencyPos;
            }

            // TODO: Get proper AVG Height
            float localAVHeight = 1.56f;
            float posZLimit = 22;

            // TODO: Check other Scene HeightField
            if (position.X > 0 && position.X <= (int)Constants.RegionSize && position.Y > 0 && position.Y <= (int)Constants.RegionSize)
            {
                posZLimit = (float)sp.Scene.Heightmap[(int)position.X, (int)position.Y];
            }

            float newPosZ = posZLimit + localAVHeight;
            if (posZLimit >= (position.Z - (localAVHeight / 2)) && !(Single.IsInfinity(newPosZ) || Single.IsNaN(newPosZ)))
            {
                position.Z = newPosZ;
            }

            m_entityTransferStateMachine.UpdateInTransit(sp.UUID, AgentTransferState.Transferring);

            sp.ControllingClient.SendTeleportStart(teleportFlags);

            sp.ControllingClient.SendLocalTeleport(position, lookAt, teleportFlags);
            sp.Velocity = Vector3.Zero;
            sp.Teleport(position);

            m_entityTransferStateMachine.UpdateInTransit(sp.UUID, AgentTransferState.ReceivedAtDestination);

            foreach (SceneObjectGroup grp in sp.GetAttachments())
            {
                sp.Scene.EventManager.TriggerOnScriptChangedEvent(grp.LocalId, (uint)Changed.TELEPORT);
            }

            m_entityTransferStateMachine.UpdateInTransit(sp.UUID, AgentTransferState.CleaningUp);
        }

        /// <summary>
        /// Teleports the agent to a different region.
        /// </summary>
        /// <param name='sp'></param>
        /// <param name='regionHandle'>/param>
        /// <param name='position'></param>
        /// <param name='lookAt'></param>
        /// <param name='teleportFlags'></param>
        /// <param name='finalDestination'></param>
        private void TeleportAgentToDifferentRegion(
            ScenePresence sp, ulong regionHandle, Vector3 position,
            Vector3 lookAt, uint teleportFlags, out GridRegion finalDestination)
        {
            uint x = 0, y = 0;
            Utils.LongToUInts(regionHandle, out x, out y);
            GridRegion reg = Scene.GridService.GetRegionByPosition(sp.Scene.RegionInfo.ScopeID, (int)x, (int)y);

            if (reg != null)
            {
                finalDestination = GetFinalDestination(reg);

                if (finalDestination == null)
                {
                    m_log.WarnFormat(
                        "[ENTITY TRANSFER MODULE]: Final destination is having problems. Unable to teleport {0} {1}",
                        sp.Name, sp.UUID);

                    sp.ControllingClient.SendTeleportFailed("Problem at destination");
                    return;
                }

                // Check that these are not the same coordinates
                if (finalDestination.RegionLocX == sp.Scene.RegionInfo.RegionLocX &&
                    finalDestination.RegionLocY == sp.Scene.RegionInfo.RegionLocY)
                {
                    // Can't do. Viewer crashes
                    sp.ControllingClient.SendTeleportFailed("Space warp! You would crash. Move to a different region and try again.");
                    return;
                }

                // Validate assorted conditions
                string reason = string.Empty;
                if (!ValidateGenericConditions(sp, reg, finalDestination, teleportFlags, out reason))
                {
                    sp.ControllingClient.SendTeleportFailed(reason);
                    return;
                }

                //
                // This is it
                //
                DoTeleportInternal(sp, reg, finalDestination, position, lookAt, teleportFlags);
                //
                //
                //
            }
            else
            {
                finalDestination = null;

                // TP to a place that doesn't exist (anymore)
                // Inform the viewer about that
                sp.ControllingClient.SendTeleportFailed("The region you tried to teleport to doesn't exist anymore");

                // and set the map-tile to '(Offline)'
                uint regX, regY;
                Utils.LongToUInts(regionHandle, out regX, out regY);

                MapBlockData block = new MapBlockData();
                block.X = (ushort)(regX / Constants.RegionSize);
                block.Y = (ushort)(regY / Constants.RegionSize);
                block.Access = 254; // == not there

                List<MapBlockData> blocks = new List<MapBlockData>();
                blocks.Add(block);
                sp.ControllingClient.SendMapBlock(blocks, 0);
            }
        }

        // Nothing to validate here
        protected virtual bool ValidateGenericConditions(ScenePresence sp, GridRegion reg, GridRegion finalDestination, uint teleportFlags, out string reason)
        {
            reason = String.Empty;
            return true;
        }

        /// <summary>
        /// Determines whether this instance is within the max transfer distance.
        /// </summary>
        /// <param name="sourceRegion"></param>
        /// <param name="destRegion"></param>
        /// <returns>
        /// <c>true</c> if this instance is within max transfer distance; otherwise, <c>false</c>.
        /// </returns>
        private bool IsWithinMaxTeleportDistance(RegionInfo sourceRegion, GridRegion destRegion)
        {
            if(MaxTransferDistance == 0)
                return true;

//                        m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Source co-ords are x={0} y={1}", curRegionX, curRegionY);
//
//                        m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Final dest is x={0} y={1} {2}@{3}",
//                            destRegionX, destRegionY, finalDestination.RegionID, finalDestination.ServerURI);

            // Insanely, RegionLoc on RegionInfo is the 256m map co-ord whilst GridRegion.RegionLoc is the raw meters position.
            return Math.Abs(sourceRegion.RegionLocX - destRegion.RegionCoordX) <= MaxTransferDistance
                && Math.Abs(sourceRegion.RegionLocY - destRegion.RegionCoordY) <= MaxTransferDistance;
        }

        /// <summary>
        /// Wraps DoTeleportInternal() and manages the transfer state.
        /// </summary>
        public void DoTeleport(
            ScenePresence sp, GridRegion reg, GridRegion finalDestination,
            Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            // Record that this agent is in transit so that we can prevent simultaneous requests and do later detection
            // of whether the destination region completes the teleport.
            if (!m_entityTransferStateMachine.SetInTransit(sp.UUID))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Ignoring teleport request of {0} {1} to {2} ({3}) {4}/{5} - agent is already in transit.",
                    sp.Name, sp.UUID, reg.ServerURI, finalDestination.ServerURI, finalDestination.RegionName, position);

                return;
            }
            
            try
            {
                DoTeleportInternal(sp, reg, finalDestination, position, lookAt, teleportFlags);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[ENTITY TRANSFER MODULE]: Exception on teleport of {0} from {1}@{2} to {3}@{4}: {5}{6}",
                    sp.Name, sp.AbsolutePosition, sp.Scene.RegionInfo.RegionName, position, finalDestination.RegionName,
                    e.Message, e.StackTrace);

                sp.ControllingClient.SendTeleportFailed("Internal error");
            }
            finally
            {
                m_entityTransferStateMachine.ResetFromTransit(sp.UUID);
            }
        }

        /// <summary>
        /// Teleports the agent to another region.
        /// This method doesn't manage the transfer state; the caller must do that.
        /// </summary>
        private void DoTeleportInternal(
            ScenePresence sp, GridRegion reg, GridRegion finalDestination,
            Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            if (reg == null || finalDestination == null)
            {
                sp.ControllingClient.SendTeleportFailed("Unable to locate destination");
                return;
            }

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Teleporting {0} {1} from {2} to {3} ({4}) {5}/{6}",
                sp.Name, sp.UUID, sp.Scene.RegionInfo.RegionName,
                reg.ServerURI, finalDestination.ServerURI, finalDestination.RegionName, position);

            RegionInfo sourceRegion = sp.Scene.RegionInfo;

            if (!IsWithinMaxTeleportDistance(sourceRegion, finalDestination))
            {
                sp.ControllingClient.SendTeleportFailed(
                    string.Format(
                      "Can't teleport to {0} ({1},{2}) from {3} ({4},{5}), destination is more than {6} regions way",
                      finalDestination.RegionName, finalDestination.RegionCoordX, finalDestination.RegionCoordY,
                      sourceRegion.RegionName, sourceRegion.RegionLocX, sourceRegion.RegionLocY,
                      MaxTransferDistance));

                return;
            }

            uint newRegionX = (uint)(reg.RegionHandle >> 40);
            uint newRegionY = (((uint)(reg.RegionHandle)) >> 8);
            uint oldRegionX = (uint)(sp.Scene.RegionInfo.RegionHandle >> 40);
            uint oldRegionY = (((uint)(sp.Scene.RegionInfo.RegionHandle)) >> 8);

            ulong destinationHandle = finalDestination.RegionHandle;

            // Let's do DNS resolution only once in this process, please!
            // This may be a costly operation. The reg.ExternalEndPoint field is not a passive field,
            // it's actually doing a lot of work.
            IPEndPoint endPoint = finalDestination.ExternalEndPoint;

            if (endPoint.Address == null)
            {
                sp.ControllingClient.SendTeleportFailed("Remote Region appears to be down");

                return;
            }

            if (!sp.ValidateAttachments())
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Failed validation of all attachments for teleport of {0} from {1} to {2}.  Continuing.",
                    sp.Name, sp.Scene.Name, finalDestination.RegionName);

            string reason;
            string version;
            if (!Scene.SimulationService.QueryAccess(
                finalDestination, sp.ControllingClient.AgentId, Vector3.Zero, out version, out reason))
            {
                sp.ControllingClient.SendTeleportFailed(reason);

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: {0} was stopped from teleporting from {1} to {2} because {3}",
                    sp.Name, sp.Scene.Name, finalDestination.RegionName, reason);

                return;
            }

            // Before this point, teleport 'failure' is due to checkable pre-conditions such as whether the target
            // simulator can be found and is explicitly prepared to allow access.  Therefore, we will not count these
            // as server attempts.
            m_interRegionTeleportAttempts.Value++;

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: {0} max transfer version is {1}/{2}, {3} max version is {4}", 
                sp.Scene.Name, OutgoingTransferVersionName, MaxOutgoingTransferVersion, finalDestination.RegionName, version);

            // Fixing a bug where teleporting while sitting results in the avatar ending up removed from
            // both regions
            if (sp.ParentID != (uint)0)
                sp.StandUp();

            if (DisableInterRegionTeleportCancellation)
                teleportFlags |= (uint)TeleportFlags.DisableCancel;

            // At least on LL 3.3.4, this is not strictly necessary - a teleport will succeed without sending this to
            // the viewer.  However, it might mean that the viewer does not see the black teleport screen (untested).
            sp.ControllingClient.SendTeleportStart(teleportFlags);

            // the avatar.Close below will clear the child region list. We need this below for (possibly)
            // closing the child agents, so save it here (we need a copy as it is Clear()-ed).
            //List<ulong> childRegions = avatar.KnownRegionHandles;
            // Compared to ScenePresence.CrossToNewRegion(), there's no obvious code to handle a teleport
            // failure at this point (unlike a border crossing failure).  So perhaps this can never fail
            // once we reach here...
            //avatar.Scene.RemoveCapsHandler(avatar.UUID);

            string capsPath = String.Empty;

            AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);
            AgentCircuitData agentCircuit = sp.ControllingClient.RequestClientInfo();
            agentCircuit.startpos = position;
            agentCircuit.child = true;
            agentCircuit.Appearance = sp.Appearance;
            if (currentAgentCircuit != null)
            {
                agentCircuit.ServiceURLs = currentAgentCircuit.ServiceURLs;
                agentCircuit.IPAddress = currentAgentCircuit.IPAddress;
                agentCircuit.Viewer = currentAgentCircuit.Viewer;
                agentCircuit.Channel = currentAgentCircuit.Channel;
                agentCircuit.Mac = currentAgentCircuit.Mac;
                agentCircuit.Id0 = currentAgentCircuit.Id0;
            }

            if (NeedsNewAgent(sp.DrawDistance, oldRegionX, newRegionX, oldRegionY, newRegionY))
            {
                // brand new agent, let's create a new caps seed
                agentCircuit.CapsPath = CapsUtil.GetRandomCapsObjectPath();
            }

            // We're going to fallback to V1 if the destination gives us anything smaller than 0.2 or we're forcing
            // use of the earlier protocol
            float versionNumber = 0.1f;
            string[] versionComponents = version.Split(new char[] { '/' });
            if (versionComponents.Length >= 2)
                float.TryParse(versionComponents[1], out versionNumber);

            if (versionNumber == 0.2f && MaxOutgoingTransferVersion >= versionNumber)
                TransferAgent_V2(sp, agentCircuit, reg, finalDestination, endPoint, teleportFlags, oldRegionX, newRegionX, oldRegionY, newRegionY, version, out reason);
            else
                TransferAgent_V1(sp, agentCircuit, reg, finalDestination, endPoint, teleportFlags, oldRegionX, newRegionX, oldRegionY, newRegionY, version, out reason);           
        }

        private void TransferAgent_V1(ScenePresence sp, AgentCircuitData agentCircuit, GridRegion reg, GridRegion finalDestination,
            IPEndPoint endPoint, uint teleportFlags, uint oldRegionX, uint newRegionX, uint oldRegionY, uint newRegionY, string version, out string reason)
        {
            ulong destinationHandle = finalDestination.RegionHandle;
            AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Using TP V1 for {0} going from {1} to {2}", 
                sp.Name, Scene.Name, finalDestination.RegionName);

            // Let's create an agent there if one doesn't exist yet. 
            // NOTE: logout will always be false for a non-HG teleport.
            bool logout = false;
            if (!CreateAgent(sp, reg, finalDestination, agentCircuit, teleportFlags, out reason, out logout))
            {
                m_interRegionTeleportFailures.Value++;

                sp.ControllingClient.SendTeleportFailed(String.Format("Teleport refused: {0}", reason));

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Teleport of {0} from {1} to {2} was refused because {3}",
                    sp.Name, sp.Scene.RegionInfo.RegionName, finalDestination.RegionName, reason);

                return;
            }

            if (m_entityTransferStateMachine.GetAgentTransferState(sp.UUID) == AgentTransferState.Cancelling)
            {
                m_interRegionTeleportCancels.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Cancelled teleport of {0} to {1} from {2} after CreateAgent on client request",
                    sp.Name, finalDestination.RegionName, sp.Scene.Name);

                return;
            }
            else if (m_entityTransferStateMachine.GetAgentTransferState(sp.UUID) == AgentTransferState.Aborting)
            {
                m_interRegionTeleportAborts.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after CreateAgent due to previous client close.",
                    sp.Name, finalDestination.RegionName, sp.Scene.Name);

                return;
            }

            // Past this point we have to attempt clean up if the teleport fails, so update transfer state.
            m_entityTransferStateMachine.UpdateInTransit(sp.UUID, AgentTransferState.Transferring);

            // OK, it got this agent. Let's close some child agents
            sp.CloseChildAgents(newRegionX, newRegionY);

            IClientIPEndpoint ipepClient;
            string capsPath = String.Empty;
            if (NeedsNewAgent(sp.DrawDistance, oldRegionX, newRegionX, oldRegionY, newRegionY))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Determined that region {0} at {1},{2} needs new child agent for incoming agent {3} from {4}",
                    finalDestination.RegionName, newRegionX, newRegionY, sp.Name, Scene.Name);

                //sp.ControllingClient.SendTeleportProgress(teleportFlags, "Creating agent...");
                #region IP Translation for NAT
                // Uses ipepClient above
                if (sp.ClientView.TryGet(out ipepClient))
                {
                    endPoint.Address = NetworkUtil.GetIPFor(ipepClient.EndPoint, endPoint.Address);
                }
                #endregion
                capsPath = finalDestination.ServerURI + CapsUtil.GetCapsSeedPath(agentCircuit.CapsPath);

                if (m_eqModule != null)
                {
                    // The EnableSimulator message makes the client establish a connection with the destination
                    // simulator by sending the initial UseCircuitCode UDP packet to the destination containing the
                    // correct circuit code.
                    m_eqModule.EnableSimulator(destinationHandle, endPoint, sp.UUID);

                    // XXX: Is this wait necessary?  We will always end up waiting on UpdateAgent for the destination
                    // simulator to confirm that it has established communication with the viewer.
                    Thread.Sleep(200);

                    // At least on LL 3.3.4 for teleports between different regions on the same simulator this appears
                    // unnecessary - teleport will succeed and SEED caps will be requested without it (though possibly
                    // only on TeleportFinish).  This is untested for region teleport between different simulators
                    // though this probably also works.
                    m_eqModule.EstablishAgentCommunication(sp.UUID, endPoint, capsPath);
                }
                else
                {
                    // XXX: This is a little misleading since we're information the client of its avatar destination,
                    // which may or may not be a neighbour region of the source region.  This path is probably little
                    // used anyway (with EQ being the one used).  But it is currently being used for test code.
                    sp.ControllingClient.InformClientOfNeighbour(destinationHandle, endPoint);
                }
            }
            else
            {
                agentCircuit.CapsPath = sp.Scene.CapsModule.GetChildSeed(sp.UUID, reg.RegionHandle);
                capsPath = finalDestination.ServerURI + CapsUtil.GetCapsSeedPath(agentCircuit.CapsPath);
            }

            // Let's send a full update of the agent. This is a synchronous call.
            AgentData agent = new AgentData();
            sp.CopyTo(agent);
            agent.Position = agentCircuit.startpos;
            SetCallbackURL(agent, sp.Scene.RegionInfo);


            // We will check for an abort before UpdateAgent since UpdateAgent will require an active viewer to 
            // establish th econnection to the destination which makes it return true.
            if (m_entityTransferStateMachine.GetAgentTransferState(sp.UUID) == AgentTransferState.Aborting)
            {
                m_interRegionTeleportAborts.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} before UpdateAgent",
                    sp.Name, finalDestination.RegionName, sp.Scene.Name);

                return;
            }

            // A common teleport failure occurs when we can send CreateAgent to the 
            // destination region but the viewer cannot establish the connection (e.g. due to network issues between
            // the viewer and the destination).  In this case, UpdateAgent timesout after 10 seconds, although then
            // there's a further 10 second wait whilst we attempt to tell the destination to delete the agent in Fail().
            if (!UpdateAgent(reg, finalDestination, agent, sp))
            {
                if (m_entityTransferStateMachine.GetAgentTransferState(sp.UUID) == AgentTransferState.Aborting)
                {
                    m_interRegionTeleportAborts.Value++;

                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after UpdateAgent due to previous client close.",
                        sp.Name, finalDestination.RegionName, sp.Scene.Name);

                    return;
                }

                m_log.WarnFormat(
                    "[ENTITY TRANSFER MODULE]: UpdateAgent failed on teleport of {0} to {1}.  Keeping avatar in {2}",
                    sp.Name, finalDestination.RegionName, sp.Scene.Name);

                Fail(sp, finalDestination, logout, currentAgentCircuit.SessionID.ToString(), "Connection between viewer and destination region could not be established.");
                return;
            }

            if (m_entityTransferStateMachine.GetAgentTransferState(sp.UUID) == AgentTransferState.Cancelling)
            {
                m_interRegionTeleportCancels.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Cancelled teleport of {0} to {1} from {2} after UpdateAgent on client request",
                    sp.Name, finalDestination.RegionName, sp.Scene.Name);

                CleanupFailedInterRegionTeleport(sp, currentAgentCircuit.SessionID.ToString(), finalDestination);

                return;
            }

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Sending new CAPS seed url {0} from {1} to {2}",
                capsPath, sp.Scene.RegionInfo.RegionName, sp.Name);

            // We need to set this here to avoid an unlikely race condition when teleporting to a neighbour simulator,
            // where that neighbour simulator could otherwise request a child agent create on the source which then 
            // closes our existing agent which is still signalled as root.
            sp.IsChildAgent = true;

            // OK, send TPFinish to the client, so that it starts the process of contacting the destination region
            if (m_eqModule != null)
            {
                m_eqModule.TeleportFinishEvent(destinationHandle, 13, endPoint, 0, teleportFlags, capsPath, sp.UUID);
            }
            else
            {
                sp.ControllingClient.SendRegionTeleport(destinationHandle, 13, endPoint, 4,
                                                            teleportFlags, capsPath);
            }

            // TeleportFinish makes the client send CompleteMovementIntoRegion (at the destination), which
            // trigers a whole shebang of things there, including MakeRoot. So let's wait for confirmation
            // that the client contacted the destination before we close things here.
            if (!m_entityTransferStateMachine.WaitForAgentArrivedAtDestination(sp.UUID))
            {
                if (m_entityTransferStateMachine.GetAgentTransferState(sp.UUID) == AgentTransferState.Aborting)
                {
                    m_interRegionTeleportAborts.Value++;

                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after WaitForAgentArrivedAtDestination due to previous client close.",
                        sp.Name, finalDestination.RegionName, sp.Scene.Name);

                    return;
                }

                m_log.WarnFormat(
                    "[ENTITY TRANSFER MODULE]: Teleport of {0} to {1} from {2} failed due to no callback from destination region.  Returning avatar to source region.",
                    sp.Name, finalDestination.RegionName, sp.Scene.RegionInfo.RegionName);

                Fail(sp, finalDestination, logout, currentAgentCircuit.SessionID.ToString(), "Destination region did not signal teleport completion.");

                return;
            }

            m_entityTransferStateMachine.UpdateInTransit(sp.UUID, AgentTransferState.CleaningUp);

            // For backwards compatibility
            if (version == "Unknown" || version == string.Empty)
            {
                // CrossAttachmentsIntoNewRegion is a synchronous call. We shouldn't need to wait after it
                m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Old simulator, sending attachments one by one...");
                CrossAttachmentsIntoNewRegion(finalDestination, sp, true);
            }

            // May need to logout or other cleanup
            AgentHasMovedAway(sp, logout);

            // Well, this is it. The agent is over there.
            KillEntity(sp.Scene, sp.LocalId);

            // Now let's make it officially a child agent
            sp.MakeChildAgent();

            // Finally, let's close this previously-known-as-root agent, when the jump is outside the view zone

            if (NeedsClosing(sp.DrawDistance, oldRegionX, newRegionX, oldRegionY, newRegionY, reg))
            {
                if (!sp.Scene.IncomingPreCloseClient(sp))
                    return;

                // We need to delay here because Imprudence viewers, unlike v1 or v3, have a short (<200ms, <500ms) delay before
                // they regard the new region as the current region after receiving the AgentMovementComplete
                // response.  If close is sent before then, it will cause the viewer to quit instead.
                //
                // This sleep can be increased if necessary.  However, whilst it's active,
                // an agent cannot teleport back to this region if it has teleported away.
                Thread.Sleep(2000);

                sp.Scene.CloseAgent(sp.UUID, false);
            }
            else
            {
                // now we have a child agent in this region. 
                sp.Reset();
            }
        }

        private void TransferAgent_V2(ScenePresence sp, AgentCircuitData agentCircuit, GridRegion reg, GridRegion finalDestination,
            IPEndPoint endPoint, uint teleportFlags, uint oldRegionX, uint newRegionX, uint oldRegionY, uint newRegionY, string version, out string reason)
        {
            ulong destinationHandle = finalDestination.RegionHandle;
            AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);

            // Let's create an agent there if one doesn't exist yet. 
            // NOTE: logout will always be false for a non-HG teleport.
            bool logout = false;
            if (!CreateAgent(sp, reg, finalDestination, agentCircuit, teleportFlags, out reason, out logout))
            {
                m_interRegionTeleportFailures.Value++;

                sp.ControllingClient.SendTeleportFailed(String.Format("Teleport refused: {0}", reason));

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Teleport of {0} from {1} to {2} was refused because {3}",
                    sp.Name, sp.Scene.RegionInfo.RegionName, finalDestination.RegionName, reason);

                return;
            }

            if (m_entityTransferStateMachine.GetAgentTransferState(sp.UUID) == AgentTransferState.Cancelling)
            {
                m_interRegionTeleportCancels.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Cancelled teleport of {0} to {1} from {2} after CreateAgent on client request",
                    sp.Name, finalDestination.RegionName, sp.Scene.Name);

                return;
            }
            else if (m_entityTransferStateMachine.GetAgentTransferState(sp.UUID) == AgentTransferState.Aborting)
            {
                m_interRegionTeleportAborts.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after CreateAgent due to previous client close.",
                    sp.Name, finalDestination.RegionName, sp.Scene.Name);

                return;
            }

            // Past this point we have to attempt clean up if the teleport fails, so update transfer state.
            m_entityTransferStateMachine.UpdateInTransit(sp.UUID, AgentTransferState.Transferring);

            IClientIPEndpoint ipepClient;
            string capsPath = String.Empty;
            if (NeedsNewAgent(sp.DrawDistance, oldRegionX, newRegionX, oldRegionY, newRegionY))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Determined that region {0} at {1},{2} needs new child agent for agent {3} from {4}",
                    finalDestination.RegionName, newRegionX, newRegionY, sp.Name, Scene.Name);

                //sp.ControllingClient.SendTeleportProgress(teleportFlags, "Creating agent...");
                #region IP Translation for NAT
                // Uses ipepClient above
                if (sp.ClientView.TryGet(out ipepClient))
                {
                    endPoint.Address = NetworkUtil.GetIPFor(ipepClient.EndPoint, endPoint.Address);
                }
                #endregion
                capsPath = finalDestination.ServerURI + CapsUtil.GetCapsSeedPath(agentCircuit.CapsPath);
            }
            else
            {
                agentCircuit.CapsPath = sp.Scene.CapsModule.GetChildSeed(sp.UUID, reg.RegionHandle);
                capsPath = finalDestination.ServerURI + CapsUtil.GetCapsSeedPath(agentCircuit.CapsPath);
            }

            // We need to set this here to avoid an unlikely race condition when teleporting to a neighbour simulator,
            // where that neighbour simulator could otherwise request a child agent create on the source which then 
            // closes our existing agent which is still signalled as root.
            //sp.IsChildAgent = true;

            // New protocol: send TP Finish directly, without prior ES or EAC. That's what happens in the Linden grid
            if (m_eqModule != null)
                m_eqModule.TeleportFinishEvent(destinationHandle, 13, endPoint, 0, teleportFlags, capsPath, sp.UUID);
            else
                sp.ControllingClient.SendRegionTeleport(destinationHandle, 13, endPoint, 4,
                                                            teleportFlags, capsPath);

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Sending new CAPS seed url {0} from {1} to {2}",
                capsPath, sp.Scene.RegionInfo.RegionName, sp.Name);

            // Let's send a full update of the agent. 
            AgentData agent = new AgentData();
            sp.CopyTo(agent);
            agent.Position = agentCircuit.startpos;
            agent.SenderWantsToWaitForRoot = true;
            //SetCallbackURL(agent, sp.Scene.RegionInfo);

            // Reset the do not close flag.  This must be done before the destination opens child connections (here
            // triggered by UpdateAgent) to avoid race conditions.  However, we also want to reset it as late as possible
            // to avoid a situation where an unexpectedly early call to Scene.NewUserConnection() wrongly results
            // in no close.
            sp.DoNotCloseAfterTeleport = false;

            // Send the Update. If this returns true, we know the client has contacted the destination
            // via CompleteMovementIntoRegion, so we can let go.
            // If it returns false, something went wrong, and we need to abort.
            if (!UpdateAgent(reg, finalDestination, agent, sp))
            {
                if (m_entityTransferStateMachine.GetAgentTransferState(sp.UUID) == AgentTransferState.Aborting)
                {
                    m_interRegionTeleportAborts.Value++;

                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after UpdateAgent due to previous client close.",
                        sp.Name, finalDestination.RegionName, sp.Scene.Name);

                    return;
                }

                m_log.WarnFormat(
                    "[ENTITY TRANSFER MODULE]: UpdateAgent failed on teleport of {0} to {1}.  Keeping avatar in {2}",
                    sp.Name, finalDestination.RegionName, sp.Scene.Name);

                Fail(sp, finalDestination, logout, currentAgentCircuit.SessionID.ToString(), "Connection between viewer and destination region could not be established.");
                return;
            }
            
            m_entityTransferStateMachine.UpdateInTransit(sp.UUID, AgentTransferState.CleaningUp);

            // Need to signal neighbours whether child agents may need closing irrespective of whether this
            // one needed closing.  We also need to close child agents as quickly as possible to avoid complicated
            // race conditions with rapid agent releporting (e.g. from A1 to a non-neighbour B, back
            // to a neighbour A2 then off to a non-neighbour C).  Closing child agents any later requires complex
            // distributed checks to avoid problems in rapid reteleporting scenarios and where child agents are
            // abandoned without proper close by viewer but then re-used by an incoming connection.
            sp.CloseChildAgents(newRegionX, newRegionY);

            // May need to logout or other cleanup
            AgentHasMovedAway(sp, logout);

            // Well, this is it. The agent is over there.
            KillEntity(sp.Scene, sp.LocalId);

            // Now let's make it officially a child agent
            sp.MakeChildAgent();

            // Finally, let's close this previously-known-as-root agent, when the jump is outside the view zone
            if (NeedsClosing(sp.DrawDistance, oldRegionX, newRegionX, oldRegionY, newRegionY, reg))
            {
                if (!sp.Scene.IncomingPreCloseClient(sp))
                    return;

                // RED ALERT!!!!
                // PLEASE DO NOT DECREASE THIS WAIT TIME UNDER ANY CIRCUMSTANCES.
                // THE VIEWERS SEEM TO NEED SOME TIME AFTER RECEIVING MoveAgentIntoRegion
                // BEFORE THEY SETTLE IN THE NEW REGION.
                // DECREASING THE WAIT TIME HERE WILL EITHER RESULT IN A VIEWER CRASH OR
                // IN THE AVIE BEING PLACED IN INFINITY FOR A COUPLE OF SECONDS.
                Thread.Sleep(15000);
            
                // OK, it got this agent. Let's close everything
                // If we shouldn't close the agent due to some other region renewing the connection 
                // then this will be handled in IncomingCloseAgent under lock conditions
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Closing agent {0} in {1} after teleport", sp.Name, Scene.Name);

                sp.Scene.CloseAgent(sp.UUID, false);
            }
            else
            {
                // now we have a child agent in this region. 
                sp.Reset();
            }
        }

        /// <summary>
        /// Clean up an inter-region teleport that did not complete, either because of simulator failure or cancellation.
        /// </summary>
        /// <remarks>
        /// All operations here must be idempotent so that we can call this method at any point in the teleport process
        /// up until we send the TeleportFinish event quene event to the viewer.
        /// <remarks>
        /// <param name='sp'> </param>
        /// <param name='finalDestination'></param>
        protected virtual void CleanupFailedInterRegionTeleport(ScenePresence sp, string auth_token, GridRegion finalDestination)
        {
            m_entityTransferStateMachine.UpdateInTransit(sp.UUID, AgentTransferState.CleaningUp);

            if (sp.IsChildAgent) // We had set it to child before attempted TP (V1)
            {
                sp.IsChildAgent = false;
                ReInstantiateScripts(sp);

                EnableChildAgents(sp);
            }
            // Finally, kill the agent we just created at the destination.
            // XXX: Possibly this should be done asynchronously.
            Scene.SimulationService.CloseAgent(finalDestination, sp.UUID, auth_token);
        }

        /// <summary>
        /// Signal that the inter-region teleport failed and perform cleanup.
        /// </summary>
        /// <param name='sp'></param>
        /// <param name='finalDestination'></param>
        /// <param name='logout'></param>
        /// <param name='reason'>Human readable reason for teleport failure.  Will be sent to client.</param>
        protected virtual void Fail(ScenePresence sp, GridRegion finalDestination, bool logout, string auth_code, string reason)
        {
            CleanupFailedInterRegionTeleport(sp, auth_code, finalDestination);

            m_interRegionTeleportFailures.Value++;

            sp.ControllingClient.SendTeleportFailed(
                string.Format(
                    "Problems connecting to destination {0}, reason: {1}", finalDestination.RegionName, reason));

            sp.Scene.EventManager.TriggerTeleportFail(sp.ControllingClient, logout);
        }

        protected virtual bool CreateAgent(ScenePresence sp, GridRegion reg, GridRegion finalDestination, AgentCircuitData agentCircuit, uint teleportFlags, out string reason, out bool logout)
        {
            logout = false;
            bool success = Scene.SimulationService.CreateAgent(finalDestination, agentCircuit, teleportFlags, out reason);

            if (success)
                sp.Scene.EventManager.TriggerTeleportStart(sp.ControllingClient, reg, finalDestination, teleportFlags, logout);

            return success;
        }

        protected virtual bool UpdateAgent(GridRegion reg, GridRegion finalDestination, AgentData agent, ScenePresence sp)
        {
            return Scene.SimulationService.UpdateAgent(finalDestination, agent);
        }

        protected virtual void SetCallbackURL(AgentData agent, RegionInfo region)
        {
            agent.CallbackURI = region.ServerURI + "agent/" + agent.AgentID.ToString() + "/" + region.RegionID.ToString() + "/release/";

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Set release callback URL to {0} in {1}",
                agent.CallbackURI, region.RegionName);
        }

        /// <summary>
        /// Clean up operations once an agent has moved away through cross or teleport.
        /// </summary>
        /// <param name='sp'></param>
        /// <param name='logout'></param>
        protected virtual void AgentHasMovedAway(ScenePresence sp, bool logout)
        {
            if (sp.Scene.AttachmentsModule != null)
                sp.Scene.AttachmentsModule.DeleteAttachmentsFromScene(sp, true);
        }

        protected void KillEntity(Scene scene, uint localID)
        {
            scene.SendKillObject(new List<uint> { localID });
        }

        protected virtual GridRegion GetFinalDestination(GridRegion region)
        {
            return region;
        }

        protected virtual bool NeedsNewAgent(float drawdist, uint oldRegionX, uint newRegionX, uint oldRegionY, uint newRegionY)
        {
            if (m_regionCombinerModule != null && m_regionCombinerModule.IsRootForMegaregion(Scene.RegionInfo.RegionID))
            {
                Vector2 swCorner, neCorner;
                GetMegaregionViewRange(out swCorner, out neCorner);

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Megaregion view of {0} is from {1} to {2} with new agent check for {3},{4}",
                    Scene.Name, swCorner, neCorner, newRegionX, newRegionY);

                return !(newRegionX >= swCorner.X && newRegionX <= neCorner.X && newRegionY >= swCorner.Y && newRegionY <= neCorner.Y);
            }
            else
            {
                return Util.IsOutsideView(drawdist, oldRegionX, newRegionX, oldRegionY, newRegionY);
            }
        }

        protected virtual bool NeedsClosing(float drawdist, uint oldRegionX, uint newRegionX, uint oldRegionY, uint newRegionY, GridRegion reg)
        {
            return Util.IsOutsideView(drawdist, oldRegionX, newRegionX, oldRegionY, newRegionY);
        }

        protected virtual bool IsOutsideRegion(Scene s, Vector3 pos)
        {
            if (s.TestBorderCross(pos, Cardinals.N))
                return true;
            if (s.TestBorderCross(pos, Cardinals.S))
                return true;
            if (s.TestBorderCross(pos, Cardinals.E))
                return true;
            if (s.TestBorderCross(pos, Cardinals.W))
                return true;

            return false;
        }

        #endregion

        #region Landmark Teleport
        /// <summary>
        /// Tries to teleport agent to landmark.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionHandle"></param>
        /// <param name="position"></param>
        public virtual void RequestTeleportLandmark(IClientAPI remoteClient, AssetLandmark lm)
        {
            GridRegion info = Scene.GridService.GetRegionByUUID(UUID.Zero, lm.RegionID);

            if (info == null)
            {
                // can't find the region: Tell viewer and abort
                remoteClient.SendTeleportFailed("The teleport destination could not be found.");
                return;
            }
            ((Scene)(remoteClient.Scene)).RequestTeleportLocation(remoteClient, info.RegionHandle, lm.Position, 
                Vector3.Zero, (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaLandmark));
        }

        #endregion 

        #region Teleport Home

        public virtual void TriggerTeleportHome(UUID id, IClientAPI client)     
        {                                                                       
            TeleportHome(id, client);                                           
        }                                                                       
                          
        public virtual bool TeleportHome(UUID id, IClientAPI client)
        {
            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Request to teleport {0} {1} home", client.Name, client.AgentId);

            //OpenSim.Services.Interfaces.PresenceInfo pinfo = Scene.PresenceService.GetAgent(client.SessionId);
            GridUserInfo uinfo = Scene.GridUserService.GetGridUserInfo(client.AgentId.ToString());

            if (uinfo != null)
            {
                GridRegion regionInfo = Scene.GridService.GetRegionByUUID(UUID.Zero, uinfo.HomeRegionID);
                if (regionInfo == null)
                {
                    // can't find the Home region: Tell viewer and abort
                    client.SendTeleportFailed("Your home region could not be found.");
                    return false;
                }
                
                m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Home region of {0} is {1} ({2}-{3})",
                    client.Name, regionInfo.RegionName, regionInfo.RegionCoordX, regionInfo.RegionCoordY);

                // a little eekie that this goes back to Scene and with a forced cast, will fix that at some point...
                ((Scene)(client.Scene)).RequestTeleportLocation(
                    client, regionInfo.RegionHandle, uinfo.HomePosition, uinfo.HomeLookAt,
                    (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaHome));
                return true;
            }
            else
            {
                m_log.ErrorFormat(
                    "[ENTITY TRANSFER MODULE]: No grid user information found for {0} {1}.  Cannot send home.",
                    client.Name, client.AgentId);
            }
            return false;
        }

        #endregion


        #region Agent Crossings

        public bool Cross(ScenePresence agent, bool isFlying)
        {
            Scene scene = agent.Scene;
            Vector3 pos = agent.AbsolutePosition;

//            m_log.DebugFormat(
//                "[ENTITY TRANSFER MODULE]: Crossing agent {0} at pos {1} in {2}", agent.Name, pos, scene.Name);

            Vector3 newpos = new Vector3(pos.X, pos.Y, pos.Z);
            uint neighbourx = scene.RegionInfo.RegionLocX;
            uint neighboury = scene.RegionInfo.RegionLocY;
            const float boundaryDistance = 1.7f;
            Vector3 northCross = new Vector3(0, boundaryDistance, 0);
            Vector3 southCross = new Vector3(0, -1 * boundaryDistance, 0);
            Vector3 eastCross = new Vector3(boundaryDistance, 0, 0);
            Vector3 westCross = new Vector3(-1 * boundaryDistance, 0, 0);

            // distance into new region to place avatar
            const float enterDistance = 0.5f;

            if (scene.TestBorderCross(pos + westCross, Cardinals.W))
            {
                if (scene.TestBorderCross(pos + northCross, Cardinals.N))
                {
                    Border b = scene.GetCrossedBorder(pos + northCross, Cardinals.N);
                    neighboury += (uint)(int)(b.BorderLine.Z / (int)Constants.RegionSize);
                }
                else if (scene.TestBorderCross(pos + southCross, Cardinals.S))
                {
                    Border b = scene.GetCrossedBorder(pos + southCross, Cardinals.S);
                    if (b.TriggerRegionX == 0 && b.TriggerRegionY == 0)
                    {
                        neighboury--;
                        newpos.Y = Constants.RegionSize - enterDistance;
                    }
                    else
                    {
                        agent.IsInTransit = true;

                        neighboury = b.TriggerRegionY;
                        neighbourx = b.TriggerRegionX;

                        Vector3 newposition = pos;
                        newposition.X += (scene.RegionInfo.RegionLocX - neighbourx) * Constants.RegionSize;
                        newposition.Y += (scene.RegionInfo.RegionLocY - neighboury) * Constants.RegionSize;
                        agent.ControllingClient.SendAgentAlertMessage(
                            String.Format("Moving you to region {0},{1}", neighbourx, neighboury), false);
                        InformClientToInitiateTeleportToLocation(agent, neighbourx, neighboury, newposition, scene);
                        return true;
                    }
                }

                Border ba = scene.GetCrossedBorder(pos + westCross, Cardinals.W);
                if (ba.TriggerRegionX == 0 && ba.TriggerRegionY == 0)
                {
                    neighbourx--;
                    newpos.X = Constants.RegionSize - enterDistance;
                }
                else
                {
                    agent.IsInTransit = true;

                    neighboury = ba.TriggerRegionY;
                    neighbourx = ba.TriggerRegionX;

                    Vector3 newposition = pos;
                    newposition.X += (scene.RegionInfo.RegionLocX - neighbourx) * Constants.RegionSize;
                    newposition.Y += (scene.RegionInfo.RegionLocY - neighboury) * Constants.RegionSize;
                    agent.ControllingClient.SendAgentAlertMessage(
                            String.Format("Moving you to region {0},{1}", neighbourx, neighboury), false);
                    InformClientToInitiateTeleportToLocation(agent, neighbourx, neighboury, newposition, scene);

                    return true;
                }

            }
            else if (scene.TestBorderCross(pos + eastCross, Cardinals.E))
            {
                Border b = scene.GetCrossedBorder(pos + eastCross, Cardinals.E);
                neighbourx += (uint)(int)(b.BorderLine.Z / (int)Constants.RegionSize);
                newpos.X = enterDistance;

                if (scene.TestBorderCross(pos + southCross, Cardinals.S))
                {
                    Border ba = scene.GetCrossedBorder(pos + southCross, Cardinals.S);
                    if (ba.TriggerRegionX == 0 && ba.TriggerRegionY == 0)
                    {
                        neighboury--;
                        newpos.Y = Constants.RegionSize - enterDistance;
                    }
                    else
                    {
                        agent.IsInTransit = true;

                        neighboury = ba.TriggerRegionY;
                        neighbourx = ba.TriggerRegionX;
                        Vector3 newposition = pos;
                        newposition.X += (scene.RegionInfo.RegionLocX - neighbourx) * Constants.RegionSize;
                        newposition.Y += (scene.RegionInfo.RegionLocY - neighboury) * Constants.RegionSize;
                        agent.ControllingClient.SendAgentAlertMessage(
                            String.Format("Moving you to region {0},{1}", neighbourx, neighboury), false);
                        InformClientToInitiateTeleportToLocation(agent, neighbourx, neighboury, newposition, scene);
                        return true;
                    }
                }
                else if (scene.TestBorderCross(pos + northCross, Cardinals.N))
                {
                    Border c = scene.GetCrossedBorder(pos + northCross, Cardinals.N);
                    neighboury += (uint)(int)(c.BorderLine.Z / (int)Constants.RegionSize);
                    newpos.Y = enterDistance;
                }
            }
            else if (scene.TestBorderCross(pos + southCross, Cardinals.S))
            {
                Border b = scene.GetCrossedBorder(pos + southCross, Cardinals.S);
                if (b.TriggerRegionX == 0 && b.TriggerRegionY == 0)
                {
                    neighboury--;
                    newpos.Y = Constants.RegionSize - enterDistance;
                }
                else
                {
                    agent.IsInTransit = true;

                    neighboury = b.TriggerRegionY;
                    neighbourx = b.TriggerRegionX;
                    Vector3 newposition = pos;
                    newposition.X += (scene.RegionInfo.RegionLocX - neighbourx) * Constants.RegionSize;
                    newposition.Y += (scene.RegionInfo.RegionLocY - neighboury) * Constants.RegionSize;
                    agent.ControllingClient.SendAgentAlertMessage(
                            String.Format("Moving you to region {0},{1}", neighbourx, neighboury), false);
                    InformClientToInitiateTeleportToLocation(agent, neighbourx, neighboury, newposition, scene);
                    return true;
                }
            }
            else if (scene.TestBorderCross(pos + northCross, Cardinals.N))
            {
                Border b = scene.GetCrossedBorder(pos + northCross, Cardinals.N);
                neighboury += (uint)(int)(b.BorderLine.Z / (int)Constants.RegionSize);
                newpos.Y = enterDistance;
            }

            /*

            if (pos.X < boundaryDistance) //West
            {
                neighbourx--;
                newpos.X = Constants.RegionSize - enterDistance;
            }
            else if (pos.X > Constants.RegionSize - boundaryDistance) // East
            {
                neighbourx++;
                newpos.X = enterDistance;
            }

            if (pos.Y < boundaryDistance) // South
            {
                neighboury--;
                newpos.Y = Constants.RegionSize - enterDistance;
            }
            else if (pos.Y > Constants.RegionSize - boundaryDistance) // North
            {
                neighboury++;
                newpos.Y = enterDistance;
            }
            */

            ulong neighbourHandle = Utils.UIntsToLong((uint)(neighbourx * Constants.RegionSize), (uint)(neighboury * Constants.RegionSize));

            int x = (int)(neighbourx * Constants.RegionSize), y = (int)(neighboury * Constants.RegionSize);

            ExpiringCache<ulong, DateTime> r;
            DateTime banUntil;

            if (m_bannedRegions.TryGetValue(agent.ControllingClient.AgentId, out r))
            {
                if (r.TryGetValue(neighbourHandle, out banUntil))
                {
                    if (DateTime.Now < banUntil)
                        return false;
                    r.Remove(neighbourHandle);
                }
            }
            else
            {
                r = null;
            }

            GridRegion neighbourRegion = scene.GridService.GetRegionByPosition(scene.RegionInfo.ScopeID, (int)x, (int)y);

            string reason;
            string version;
            if (!scene.SimulationService.QueryAccess(neighbourRegion, agent.ControllingClient.AgentId, newpos, out version, out reason))
            {
                agent.ControllingClient.SendAlertMessage("Cannot region cross into banned parcel");
                if (r == null)
                {
                    r = new ExpiringCache<ulong, DateTime>();
                    r.Add(neighbourHandle, DateTime.Now + TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

                    m_bannedRegions.Add(agent.ControllingClient.AgentId, r, TimeSpan.FromSeconds(45));
                }
                else
                {
                    r.Add(neighbourHandle, DateTime.Now + TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
                }
                return false;
            }

            agent.IsInTransit = true;

            CrossAgentToNewRegionDelegate d = CrossAgentToNewRegionAsync;
            d.BeginInvoke(agent, newpos, neighbourx, neighboury, neighbourRegion, isFlying, version, CrossAgentToNewRegionCompleted, d);

            return true;
        }


        public delegate void InformClientToInitiateTeleportToLocationDelegate(ScenePresence agent, uint regionX, uint regionY,
                                                            Vector3 position,
                                                            Scene initiatingScene);

        private void InformClientToInitiateTeleportToLocation(ScenePresence agent, uint regionX, uint regionY, Vector3 position, Scene initiatingScene)
        {

            // This assumes that we know what our neighbours are.

            InformClientToInitiateTeleportToLocationDelegate d = InformClientToInitiateTeleportToLocationAsync;
            d.BeginInvoke(agent, regionX, regionY, position, initiatingScene,
                          InformClientToInitiateTeleportToLocationCompleted,
                          d);
        }

        public void InformClientToInitiateTeleportToLocationAsync(ScenePresence agent, uint regionX, uint regionY, Vector3 position,
            Scene initiatingScene)
        {
            Thread.Sleep(10000);

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Auto-reteleporting {0} to correct megaregion location {1},{2},{3} from {4}", 
                agent.Name, regionX, regionY, position, initiatingScene.Name);

            agent.Scene.RequestTeleportLocation(
                agent.ControllingClient, 
                Utils.UIntsToLong(regionX * (uint)Constants.RegionSize, regionY * (uint)Constants.RegionSize), 
                position, 
                agent.Lookat, 
                (uint)Constants.TeleportFlags.ViaLocation);

            /*
            IMessageTransferModule im = initiatingScene.RequestModuleInterface<IMessageTransferModule>();
            if (im != null)
            {
                UUID gotoLocation = Util.BuildFakeParcelID(
                    Util.UIntsToLong(
                                              (regionX *
                                               (uint)Constants.RegionSize),
                                              (regionY *
                                               (uint)Constants.RegionSize)),
                    (uint)(int)position.X,
                    (uint)(int)position.Y,
                    (uint)(int)position.Z);

                GridInstantMessage m
                    = new GridInstantMessage(
                        initiatingScene,
                        UUID.Zero,
                        "Region",
                        agent.UUID,
                        (byte)InstantMessageDialog.GodLikeRequestTeleport,
                        false,
                        "",
                        gotoLocation,
                        false,
                        new Vector3(127, 0, 0),
                        new Byte[0],
                        false);

                im.SendInstantMessage(m, delegate(bool success)
                {
                    m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Client Initiating Teleport sending IM success = {0}", success);
                });

            }
            */
        }

        private void InformClientToInitiateTeleportToLocationCompleted(IAsyncResult iar)
        {
            InformClientToInitiateTeleportToLocationDelegate icon =
                (InformClientToInitiateTeleportToLocationDelegate)iar.AsyncState;
            icon.EndInvoke(iar);
        }

        public delegate ScenePresence CrossAgentToNewRegionDelegate(ScenePresence agent, Vector3 pos, uint neighbourx, uint neighboury, GridRegion neighbourRegion, bool isFlying, string version);

        /// <summary>
        /// This Closes child agents on neighbouring regions
        /// Calls an asynchronous method to do so..  so it doesn't lag the sim.
        /// </summary>
        protected ScenePresence CrossAgentToNewRegionAsync(
            ScenePresence agent, Vector3 pos, uint neighbourx, uint neighboury, GridRegion neighbourRegion,
            bool isFlying, string version)
        {
            if (neighbourRegion == null)
                return agent;

            if (!m_entityTransferStateMachine.SetInTransit(agent.UUID))
            {
                m_log.ErrorFormat(
                    "[ENTITY TRANSFER MODULE]: Problem crossing user {0} to new region {1} from {2} - agent is already in transit",
                    agent.Name, neighbourRegion.RegionName, agent.Scene.RegionInfo.RegionName);
                return agent;
            }

            bool transitWasReset = false;

            try
            {
                ulong neighbourHandle = Utils.UIntsToLong((uint)(neighbourx * Constants.RegionSize), (uint)(neighboury * Constants.RegionSize));

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Crossing agent {0} {1} to {2}-{3} running version {4}",
                    agent.Firstname, agent.Lastname, neighbourx, neighboury, version);

                Scene m_scene = agent.Scene;

                if (!agent.ValidateAttachments())
                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Failed validation of all attachments for region crossing of {0} from {1} to {2}.  Continuing.",
                        agent.Name, agent.Scene.RegionInfo.RegionName, neighbourRegion.RegionName);

                pos = pos + agent.Velocity;
                Vector3 vel2 = new Vector3(agent.Velocity.X, agent.Velocity.Y, 0);

                agent.RemoveFromPhysicalScene();

                AgentData cAgent = new AgentData();
                agent.CopyTo(cAgent);
                cAgent.Position = pos;
                if (isFlying)
                    cAgent.ControlFlags |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY;

                // We don't need the callback anymnore
                cAgent.CallbackURI = String.Empty;

                // Beyond this point, extra cleanup is needed beyond removing transit state
                m_entityTransferStateMachine.UpdateInTransit(agent.UUID, AgentTransferState.Transferring);

                if (!m_scene.SimulationService.UpdateAgent(neighbourRegion, cAgent))
                {
                    // region doesn't take it
                    m_entityTransferStateMachine.UpdateInTransit(agent.UUID, AgentTransferState.CleaningUp);

                    m_log.WarnFormat(
                        "[ENTITY TRANSFER MODULE]: Region {0} would not accept update for agent {1} on cross attempt.  Returning to original region.", 
                        neighbourRegion.RegionName, agent.Name);

                    ReInstantiateScripts(agent);
                    agent.AddToPhysicalScene(isFlying);

                    return agent;
                }

                //m_log.Debug("BEFORE CROSS");
                //Scene.DumpChildrenSeeds(UUID);
                //DumpKnownRegions();
                string agentcaps;
                if (!agent.KnownRegions.TryGetValue(neighbourRegion.RegionHandle, out agentcaps))
                {
                    m_log.ErrorFormat("[ENTITY TRANSFER MODULE]: No ENTITY TRANSFER MODULE information for region handle {0}, exiting CrossToNewRegion.",
                                     neighbourRegion.RegionHandle);
                    return agent;
                }

                // No turning back
                agent.IsChildAgent = true;

                string capsPath = neighbourRegion.ServerURI + CapsUtil.GetCapsSeedPath(agentcaps);

                m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Sending new CAPS seed url {0} to client {1}", capsPath, agent.UUID);

                if (m_eqModule != null)
                {
                    m_eqModule.CrossRegion(
                        neighbourHandle, pos, vel2 /* agent.Velocity */, neighbourRegion.ExternalEndPoint,
                        capsPath, agent.UUID, agent.ControllingClient.SessionId);
                }
                else
                {
                    agent.ControllingClient.CrossRegion(neighbourHandle, pos, agent.Velocity, neighbourRegion.ExternalEndPoint,
                                                capsPath);
                }

                // SUCCESS!
                m_entityTransferStateMachine.UpdateInTransit(agent.UUID, AgentTransferState.ReceivedAtDestination);

                // Unlike a teleport, here we do not wait for the destination region to confirm the receipt.
                m_entityTransferStateMachine.UpdateInTransit(agent.UUID, AgentTransferState.CleaningUp);

                agent.MakeChildAgent();

                // FIXME: Possibly this should occur lower down after other commands to close other agents,
                // but not sure yet what the side effects would be.
                m_entityTransferStateMachine.ResetFromTransit(agent.UUID);
                transitWasReset = true;

                // now we have a child agent in this region. Request all interesting data about other (root) agents
                agent.SendOtherAgentsAvatarDataToMe();
                agent.SendOtherAgentsAppearanceToMe();

                // Backwards compatibility. Best effort
                if (version == "Unknown" || version == string.Empty)
                {
                    m_log.DebugFormat("[ENTITY TRANSFER MODULE]: neighbor with old version, passing attachments one by one...");
                    Thread.Sleep(3000); // wait a little now that we're not waiting for the callback
                    CrossAttachmentsIntoNewRegion(neighbourRegion, agent, true);
                }

                // Next, let's close the child agent connections that are too far away.
                agent.CloseChildAgents(neighbourx, neighboury);

                AgentHasMovedAway(agent, false);
    
                //m_log.Debug("AFTER CROSS");
                //Scene.DumpChildrenSeeds(UUID);
                //DumpKnownRegions();
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[ENTITY TRANSFER MODULE]: Problem crossing user {0} to new region {1} from {2}.  Exception {3}{4}",
                    agent.Name, neighbourRegion.RegionName, agent.Scene.RegionInfo.RegionName, e.Message, e.StackTrace);

                // TODO: Might be worth attempting other restoration here such as reinstantiation of scripts, etc.
            }
            finally
            {
                if (!transitWasReset)
                    m_entityTransferStateMachine.ResetFromTransit(agent.UUID);
            }

            return agent;
        }
         
        private void CrossAgentToNewRegionCompleted(IAsyncResult iar)
        {
            CrossAgentToNewRegionDelegate icon = (CrossAgentToNewRegionDelegate)iar.AsyncState;
            ScenePresence agent = icon.EndInvoke(iar);

            //// If the cross was successful, this agent is a child agent
            //if (agent.IsChildAgent)
            //    agent.Reset();
            //else // Not successful
            //    agent.RestoreInCurrentScene();

            // In any case
            agent.IsInTransit = false;

            m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Crossing agent {0} {1} completed.", agent.Firstname, agent.Lastname);
        }

        #endregion

        #region Enable Child Agent

        /// <summary>
        /// This informs a single neighbouring region about agent "avatar".
        /// Calls an asynchronous method to do so..  so it doesn't lag the sim.
        /// </summary>
        /// <param name="sp"></param>
        /// <param name="region"></param>
        public void EnableChildAgent(ScenePresence sp, GridRegion region)
        {
            m_log.DebugFormat("[ENTITY TRANSFER]: Enabling child agent in new neighbour {0}", region.RegionName);

            AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);
            AgentCircuitData agent = sp.ControllingClient.RequestClientInfo();
            agent.BaseFolder = UUID.Zero;
            agent.InventoryFolder = UUID.Zero;
            agent.startpos = new Vector3(128, 128, 70);
            agent.child = true;
            agent.Appearance = sp.Appearance;
            agent.CapsPath = CapsUtil.GetRandomCapsObjectPath();

            agent.ChildrenCapSeeds = new Dictionary<ulong, string>(sp.Scene.CapsModule.GetChildrenSeeds(sp.UUID));
            //m_log.DebugFormat("[XXX] Seeds 1 {0}", agent.ChildrenCapSeeds.Count);

            if (!agent.ChildrenCapSeeds.ContainsKey(sp.Scene.RegionInfo.RegionHandle))
                agent.ChildrenCapSeeds.Add(sp.Scene.RegionInfo.RegionHandle, sp.ControllingClient.RequestClientInfo().CapsPath);
            //m_log.DebugFormat("[XXX] Seeds 2 {0}", agent.ChildrenCapSeeds.Count);

            sp.AddNeighbourRegion(region.RegionHandle, agent.CapsPath);
            //foreach (ulong h in agent.ChildrenCapSeeds.Keys)
            //    m_log.DebugFormat("[XXX] --> {0}", h);
            //m_log.DebugFormat("[XXX] Adding {0}", region.RegionHandle);
            agent.ChildrenCapSeeds.Add(region.RegionHandle, agent.CapsPath);

            if (sp.Scene.CapsModule != null)
            {
                sp.Scene.CapsModule.SetChildrenSeed(sp.UUID, agent.ChildrenCapSeeds);
            }

            if (currentAgentCircuit != null)
            {
                agent.ServiceURLs = currentAgentCircuit.ServiceURLs;
                agent.IPAddress = currentAgentCircuit.IPAddress;
                agent.Viewer = currentAgentCircuit.Viewer;
                agent.Channel = currentAgentCircuit.Channel;
                agent.Mac = currentAgentCircuit.Mac;
                agent.Id0 = currentAgentCircuit.Id0;
            }

            InformClientOfNeighbourDelegate d = InformClientOfNeighbourAsync;
            d.BeginInvoke(sp, agent, region, region.ExternalEndPoint, true,
                          InformClientOfNeighbourCompleted,
                          d);
        }
        #endregion

        #region Enable Child Agents

        private delegate void InformClientOfNeighbourDelegate(
            ScenePresence avatar, AgentCircuitData a, GridRegion reg, IPEndPoint endPoint, bool newAgent);

        /// <summary>
        /// This informs all neighbouring regions about agent "avatar".
        /// </summary>
        /// <param name="sp"></param>
        public void EnableChildAgents(ScenePresence sp)
        {
            List<GridRegion> neighbours = new List<GridRegion>();
            RegionInfo m_regionInfo = sp.Scene.RegionInfo;

            if (m_regionInfo != null)
            {
                neighbours = RequestNeighbours(sp, m_regionInfo.RegionLocX, m_regionInfo.RegionLocY);
            }
            else
            {
                m_log.Debug("[ENTITY TRANSFER MODULE]: m_regionInfo was null in EnableChildAgents, is this a NPC?");
            }

            /// We need to find the difference between the new regions where there are no child agents
            /// and the regions where there are already child agents. We only send notification to the former.
            List<ulong> neighbourHandles = NeighbourHandles(neighbours); // on this region
            neighbourHandles.Add(sp.Scene.RegionInfo.RegionHandle);  // add this region too
            List<ulong> previousRegionNeighbourHandles;

            if (sp.Scene.CapsModule != null)
            {
                previousRegionNeighbourHandles =
                    new List<ulong>(sp.Scene.CapsModule.GetChildrenSeeds(sp.UUID).Keys);
            }
            else
            {
                previousRegionNeighbourHandles = new List<ulong>();
            }

            List<ulong> newRegions = NewNeighbours(neighbourHandles, previousRegionNeighbourHandles);
            List<ulong> oldRegions = OldNeighbours(neighbourHandles, previousRegionNeighbourHandles);

//            Dump("Current Neighbors", neighbourHandles);
//            Dump("Previous Neighbours", previousRegionNeighbourHandles);
//            Dump("New Neighbours", newRegions);
//            Dump("Old Neighbours", oldRegions);

            /// Update the scene presence's known regions here on this region
            sp.DropOldNeighbours(oldRegions);

            /// Collect as many seeds as possible
            Dictionary<ulong, string> seeds;
            if (sp.Scene.CapsModule != null)
                seeds = new Dictionary<ulong, string>(sp.Scene.CapsModule.GetChildrenSeeds(sp.UUID));
            else
                seeds = new Dictionary<ulong, string>();

            //m_log.Debug(" !!! No. of seeds: " + seeds.Count);
            if (!seeds.ContainsKey(sp.Scene.RegionInfo.RegionHandle))
                seeds.Add(sp.Scene.RegionInfo.RegionHandle, sp.ControllingClient.RequestClientInfo().CapsPath);

            /// Create the necessary child agents
            List<AgentCircuitData> cagents = new List<AgentCircuitData>();
            foreach (GridRegion neighbour in neighbours)
            {
                if (neighbour.RegionHandle != sp.Scene.RegionInfo.RegionHandle)
                {
                    AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);
                    AgentCircuitData agent = sp.ControllingClient.RequestClientInfo();
                    agent.BaseFolder = UUID.Zero;
                    agent.InventoryFolder = UUID.Zero;
                    agent.startpos = sp.AbsolutePosition + CalculateOffset(sp, neighbour);
                    agent.child = true;
                    agent.Appearance = sp.Appearance;
                    if (currentAgentCircuit != null)
                    {
                        agent.ServiceURLs = currentAgentCircuit.ServiceURLs;
                        agent.IPAddress = currentAgentCircuit.IPAddress;
                        agent.Viewer = currentAgentCircuit.Viewer;
                        agent.Channel = currentAgentCircuit.Channel;
                        agent.Mac = currentAgentCircuit.Mac;
                        agent.Id0 = currentAgentCircuit.Id0;
                    }

                    if (newRegions.Contains(neighbour.RegionHandle))
                    {
                        agent.CapsPath = CapsUtil.GetRandomCapsObjectPath();
                        sp.AddNeighbourRegion(neighbour.RegionHandle, agent.CapsPath);
                        seeds.Add(neighbour.RegionHandle, agent.CapsPath);
                    }
                    else
                    {
                        agent.CapsPath = sp.Scene.CapsModule.GetChildSeed(sp.UUID, neighbour.RegionHandle);
                    }

                    cagents.Add(agent);
                }
            }

            /// Update all child agent with everyone's seeds
            foreach (AgentCircuitData a in cagents)
            {
                a.ChildrenCapSeeds = new Dictionary<ulong, string>(seeds);
            }

            if (sp.Scene.CapsModule != null)
            {
                sp.Scene.CapsModule.SetChildrenSeed(sp.UUID, seeds);
            }
            sp.KnownRegions = seeds;
            //avatar.Scene.DumpChildrenSeeds(avatar.UUID);
            //avatar.DumpKnownRegions();

            bool newAgent = false;
            int count = 0;
            foreach (GridRegion neighbour in neighbours)
            {
                //m_log.WarnFormat("--> Going to send child agent to {0}", neighbour.RegionName);
                // Don't do it if there's already an agent in that region
                if (newRegions.Contains(neighbour.RegionHandle))
                    newAgent = true;
                else
                    newAgent = false;
//                    continue;

                if (neighbour.RegionHandle != sp.Scene.RegionInfo.RegionHandle)
                {
                    try
                    {
                        // Let's put this back at sync, so that it doesn't clog 
                        // the network, especially for regions in the same physical server.
                        // We're really not in a hurry here.
                        InformClientOfNeighbourAsync(sp, cagents[count], neighbour, neighbour.ExternalEndPoint, newAgent);
                        //InformClientOfNeighbourDelegate d = InformClientOfNeighbourAsync;
                        //d.BeginInvoke(sp, cagents[count], neighbour, neighbour.ExternalEndPoint, newAgent,
                        //              InformClientOfNeighbourCompleted,
                        //              d);
                    }

                    catch (ArgumentOutOfRangeException)
                    {
                        m_log.ErrorFormat(
                           "[ENTITY TRANSFER MODULE]: Neighbour Regions response included the current region in the neighbour list.  The following region will not display to the client: {0} for region {1} ({2}, {3}).",
                           neighbour.ExternalHostName,
                           neighbour.RegionHandle,
                           neighbour.RegionLocX,
                           neighbour.RegionLocY);
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[ENTITY TRANSFER MODULE]: Could not resolve external hostname {0} for region {1} ({2}, {3}).  {4}",
                            neighbour.ExternalHostName,
                            neighbour.RegionHandle,
                            neighbour.RegionLocX,
                            neighbour.RegionLocY,
                            e);

                        // FIXME: Okay, even though we've failed, we're still going to throw the exception on,
                        // since I don't know what will happen if we just let the client continue

                        // XXX: Well, decided to swallow the exception instead for now.  Let us see how that goes.
                        // throw e;

                    }
                }
                count++;
            }
        }

        Vector3 CalculateOffset(ScenePresence sp, GridRegion neighbour)
        {
            int rRegionX = (int)sp.Scene.RegionInfo.RegionLocX;
            int rRegionY = (int)sp.Scene.RegionInfo.RegionLocY;
            int tRegionX = neighbour.RegionLocX / (int)Constants.RegionSize;
            int tRegionY = neighbour.RegionLocY / (int)Constants.RegionSize;
            int shiftx = (rRegionX - tRegionX) * (int)Constants.RegionSize;
            int shifty = (rRegionY - tRegionY) * (int)Constants.RegionSize;
            return new Vector3(shiftx, shifty, 0f);
        }

        private void InformClientOfNeighbourCompleted(IAsyncResult iar)
        {
            InformClientOfNeighbourDelegate icon = (InformClientOfNeighbourDelegate)iar.AsyncState;
            icon.EndInvoke(iar);
            //m_log.WarnFormat(" --> InformClientOfNeighbourCompleted");
        }

        /// <summary>
        /// Async component for informing client of which neighbours exist
        /// </summary>
        /// <remarks>
        /// This needs to run asynchronously, as a network timeout may block the thread for a long while
        /// </remarks>
        /// <param name="remoteClient"></param>
        /// <param name="a"></param>
        /// <param name="regionHandle"></param>
        /// <param name="endPoint"></param>
        private void InformClientOfNeighbourAsync(ScenePresence sp, AgentCircuitData a, GridRegion reg,
                                                  IPEndPoint endPoint, bool newAgent)
        {
            // Let's wait just a little to give time to originating regions to catch up with closing child agents
            // after a cross here
            Thread.Sleep(500);

            Scene scene = sp.Scene;

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Informing {0} {1} about neighbour {2} {3} at ({4},{5})",
                sp.Name, sp.UUID, reg.RegionName, endPoint, reg.RegionCoordX, reg.RegionCoordY);

            string capsPath = reg.ServerURI + CapsUtil.GetCapsSeedPath(a.CapsPath);

            string reason = String.Empty;

            bool regionAccepted = scene.SimulationService.CreateAgent(reg, a, (uint)TeleportFlags.Default, out reason);

            if (regionAccepted && newAgent)
            {
                if (m_eqModule != null)
                {
                    #region IP Translation for NAT
                    IClientIPEndpoint ipepClient;
                    if (sp.ClientView.TryGet(out ipepClient))
                    {
                        endPoint.Address = NetworkUtil.GetIPFor(ipepClient.EndPoint, endPoint.Address);
                    }
                    #endregion

                    m_log.DebugFormat("[ENTITY TRANSFER MODULE]: {0} is sending {1} EnableSimulator for neighbour region {2} @ {3} " +
                        "and EstablishAgentCommunication with seed cap {4}",
                        scene.RegionInfo.RegionName, sp.Name, reg.RegionName, reg.RegionHandle, capsPath);

                    m_eqModule.EnableSimulator(reg.RegionHandle, endPoint, sp.UUID);
                    m_eqModule.EstablishAgentCommunication(sp.UUID, endPoint, capsPath);
                }
                else
                {
                    sp.ControllingClient.InformClientOfNeighbour(reg.RegionHandle, endPoint);
                    // TODO: make Event Queue disablable!
                }

                m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Completed inform {0} {1} about neighbour {2}", sp.Name, sp.UUID, endPoint);
            }

            if (!regionAccepted)
                m_log.WarnFormat(
                    "[ENTITY TRANSFER MODULE]: Region {0} did not accept {1} {2}: {3}",
                    reg.RegionName, sp.Name, sp.UUID, reason);
        }

        /// <summary>
        /// Gets the range considered in view of this megaregion (assuming this is a megaregion).
        /// </summary>
        /// <remarks>Expressed in 256m units</remarks>
        /// <param name='swCorner'></param>
        /// <param name='neCorner'></param>
        private void GetMegaregionViewRange(out Vector2 swCorner, out Vector2 neCorner)
        {
            Border[] northBorders = Scene.NorthBorders.ToArray();
            Border[] eastBorders = Scene.EastBorders.ToArray();

            Vector2 extent = Vector2.Zero;
            for (int i = 0; i < eastBorders.Length; i++)
            {
                extent.X = (eastBorders[i].BorderLine.Z > extent.X) ? eastBorders[i].BorderLine.Z : extent.X;
            }
            for (int i = 0; i < northBorders.Length; i++)
            {
                extent.Y = (northBorders[i].BorderLine.Z > extent.Y) ? northBorders[i].BorderLine.Z : extent.Y;
            }

            // Loss of fraction on purpose
            extent.X = ((int)extent.X / (int)Constants.RegionSize);
            extent.Y = ((int)extent.Y / (int)Constants.RegionSize);

            swCorner.X = Scene.RegionInfo.RegionLocX - 1;
            swCorner.Y = Scene.RegionInfo.RegionLocY - 1;
            neCorner.X = Scene.RegionInfo.RegionLocX + extent.X;
            neCorner.Y = Scene.RegionInfo.RegionLocY + extent.Y;
        }

        /// <summary>
        /// Return the list of regions that are considered to be neighbours to the given scene.
        /// </summary>
        /// <param name="pScene"></param>
        /// <param name="pRegionLocX"></param>
        /// <param name="pRegionLocY"></param>
        /// <returns></returns>        
        protected List<GridRegion> RequestNeighbours(ScenePresence avatar, uint pRegionLocX, uint pRegionLocY)
        {
            Scene pScene = avatar.Scene;
            RegionInfo m_regionInfo = pScene.RegionInfo;

            // Leaving this as a "megaregions" computation vs "non-megaregions" computation; it isn't
            // clear what should be done with a "far view" given that megaregions already extended the
            // view to include everything in the megaregion
            if (m_regionCombinerModule == null || !m_regionCombinerModule.IsRootForMegaregion(Scene.RegionInfo.RegionID))
            {
                int dd = avatar.DrawDistance < Constants.RegionSize ? (int)Constants.RegionSize : (int)avatar.DrawDistance;

                int startX = (int)pRegionLocX * (int)Constants.RegionSize - dd + (int)(Constants.RegionSize/2);
                int startY = (int)pRegionLocY * (int)Constants.RegionSize - dd + (int)(Constants.RegionSize/2);

                int endX = (int)pRegionLocX * (int)Constants.RegionSize + dd + (int)(Constants.RegionSize/2);
                int endY = (int)pRegionLocY * (int)Constants.RegionSize + dd + (int)(Constants.RegionSize/2);

                List<GridRegion> neighbours =
                    avatar.Scene.GridService.GetRegionRange(m_regionInfo.ScopeID, startX, endX, startY, endY);

                neighbours.RemoveAll(delegate(GridRegion r) { return r.RegionID == m_regionInfo.RegionID; });
                return neighbours;
            }
            else
            {
                Vector2 swCorner, neCorner;
                GetMegaregionViewRange(out swCorner, out neCorner);

                List<GridRegion> neighbours 
                    = pScene.GridService.GetRegionRange(
                        m_regionInfo.ScopeID, 
                        (int)swCorner.X * (int)Constants.RegionSize, 
                        (int)neCorner.X * (int)Constants.RegionSize,
                        (int)swCorner.Y * (int)Constants.RegionSize,
                        (int)neCorner.Y * (int)Constants.RegionSize);

                neighbours.RemoveAll(delegate(GridRegion r) { return r.RegionID == m_regionInfo.RegionID; });

                return neighbours;
            }
        }

        private List<ulong> NewNeighbours(List<ulong> currentNeighbours, List<ulong> previousNeighbours)
        {
            return currentNeighbours.FindAll(delegate(ulong handle) { return !previousNeighbours.Contains(handle); });
        }

        //        private List<ulong> CommonNeighbours(List<ulong> currentNeighbours, List<ulong> previousNeighbours)
        //        {
        //            return currentNeighbours.FindAll(delegate(ulong handle) { return previousNeighbours.Contains(handle); });
        //        }

        private List<ulong> OldNeighbours(List<ulong> currentNeighbours, List<ulong> previousNeighbours)
        {
            return previousNeighbours.FindAll(delegate(ulong handle) { return !currentNeighbours.Contains(handle); });
        }

        private List<ulong> NeighbourHandles(List<GridRegion> neighbours)
        {
            List<ulong> handles = new List<ulong>();
            foreach (GridRegion reg in neighbours)
            {
                handles.Add(reg.RegionHandle);
            }
            return handles;
        }

//        private void Dump(string msg, List<ulong> handles)
//        {
//            m_log.InfoFormat("-------------- HANDLE DUMP ({0}) ---------", msg);
//            foreach (ulong handle in handles)
//            {
//                uint x, y;
//                Utils.LongToUInts(handle, out x, out y);
//                x = x / Constants.RegionSize;
//                y = y / Constants.RegionSize;
//                m_log.InfoFormat("({0}, {1})", x, y);
//            }
//        }

        #endregion

        #region Agent Arrived

        public void AgentArrivedAtDestination(UUID id)
        {
            m_entityTransferStateMachine.SetAgentArrivedAtDestination(id);
        }

        #endregion

        #region Object Transfers

        /// <summary>
        /// Move the given scene object into a new region depending on which region its absolute position has moved
        /// into.
        ///
        /// This method locates the new region handle and offsets the prim position for the new region
        /// </summary>
        /// <param name="attemptedPosition">the attempted out of region position of the scene object</param>
        /// <param name="grp">the scene object that we're crossing</param>
        public void Cross(SceneObjectGroup grp, Vector3 attemptedPosition, bool silent)
        {
            if (grp == null)
                return;
            if (grp.IsDeleted)
                return;

            Scene scene = grp.Scene;
            if (scene == null)
                return;

            if (grp.RootPart.DIE_AT_EDGE)
            {
                // We remove the object here
                try
                {
                    scene.DeleteSceneObject(grp, false);
                }
                catch (Exception)
                {
                    m_log.Warn("[DATABASE]: exception when trying to remove the prim that crossed the border.");
                }
                return;
            }

            int thisx = (int)scene.RegionInfo.RegionLocX;
            int thisy = (int)scene.RegionInfo.RegionLocY;
            Vector3 EastCross = new Vector3(0.1f, 0, 0);
            Vector3 WestCross = new Vector3(-0.1f, 0, 0);
            Vector3 NorthCross = new Vector3(0, 0.1f, 0);
            Vector3 SouthCross = new Vector3(0, -0.1f, 0);


            // use this if no borders were crossed!
            ulong newRegionHandle
                        = Util.UIntsToLong((uint)((thisx) * Constants.RegionSize),
                                           (uint)((thisy) * Constants.RegionSize));

            Vector3 pos = attemptedPosition;

            int changeX = 1;
            int changeY = 1;

            if (scene.TestBorderCross(attemptedPosition + WestCross, Cardinals.W))
            {
                if (scene.TestBorderCross(attemptedPosition + SouthCross, Cardinals.S))
                {

                    Border crossedBorderx = scene.GetCrossedBorder(attemptedPosition + WestCross, Cardinals.W);

                    if (crossedBorderx.BorderLine.Z > 0)
                    {
                        pos.X = ((pos.X + crossedBorderx.BorderLine.Z));
                        changeX = (int)(crossedBorderx.BorderLine.Z / (int)Constants.RegionSize);
                    }
                    else
                        pos.X = ((pos.X + Constants.RegionSize));

                    Border crossedBordery = scene.GetCrossedBorder(attemptedPosition + SouthCross, Cardinals.S);
                    //(crossedBorderx.BorderLine.Z / (int)Constants.RegionSize)

                    if (crossedBordery.BorderLine.Z > 0)
                    {
                        pos.Y = ((pos.Y + crossedBordery.BorderLine.Z));
                        changeY = (int)(crossedBordery.BorderLine.Z / (int)Constants.RegionSize);
                    }
                    else
                        pos.Y = ((pos.Y + Constants.RegionSize));



                    newRegionHandle
                        = Util.UIntsToLong((uint)((thisx - changeX) * Constants.RegionSize),
                                           (uint)((thisy - changeY) * Constants.RegionSize));
                    // x - 1
                    // y - 1
                }
                else if (scene.TestBorderCross(attemptedPosition + NorthCross, Cardinals.N))
                {
                    Border crossedBorderx = scene.GetCrossedBorder(attemptedPosition + WestCross, Cardinals.W);

                    if (crossedBorderx.BorderLine.Z > 0)
                    {
                        pos.X = ((pos.X + crossedBorderx.BorderLine.Z));
                        changeX = (int)(crossedBorderx.BorderLine.Z / (int)Constants.RegionSize);
                    }
                    else
                        pos.X = ((pos.X + Constants.RegionSize));


                    Border crossedBordery = scene.GetCrossedBorder(attemptedPosition + SouthCross, Cardinals.S);
                    //(crossedBorderx.BorderLine.Z / (int)Constants.RegionSize)

                    if (crossedBordery.BorderLine.Z > 0)
                    {
                        pos.Y = ((pos.Y + crossedBordery.BorderLine.Z));
                        changeY = (int)(crossedBordery.BorderLine.Z / (int)Constants.RegionSize);
                    }
                    else
                        pos.Y = ((pos.Y + Constants.RegionSize));

                    newRegionHandle
                        = Util.UIntsToLong((uint)((thisx - changeX) * Constants.RegionSize),
                                           (uint)((thisy + changeY) * Constants.RegionSize));
                    // x - 1
                    // y + 1
                }
                else
                {
                    Border crossedBorderx = scene.GetCrossedBorder(attemptedPosition + WestCross, Cardinals.W);

                    if (crossedBorderx.BorderLine.Z > 0)
                    {
                        pos.X = ((pos.X + crossedBorderx.BorderLine.Z));
                        changeX = (int)(crossedBorderx.BorderLine.Z / (int)Constants.RegionSize);
                    }
                    else
                        pos.X = ((pos.X + Constants.RegionSize));

                    newRegionHandle
                        = Util.UIntsToLong((uint)((thisx - changeX) * Constants.RegionSize),
                                           (uint)(thisy * Constants.RegionSize));
                    // x - 1
                }
            }
            else if (scene.TestBorderCross(attemptedPosition + EastCross, Cardinals.E))
            {
                if (scene.TestBorderCross(attemptedPosition + SouthCross, Cardinals.S))
                {

                    pos.X = ((pos.X - Constants.RegionSize));
                    Border crossedBordery = scene.GetCrossedBorder(attemptedPosition + SouthCross, Cardinals.S);
                    //(crossedBorderx.BorderLine.Z / (int)Constants.RegionSize)

                    if (crossedBordery.BorderLine.Z > 0)
                    {
                        pos.Y = ((pos.Y + crossedBordery.BorderLine.Z));
                        changeY = (int)(crossedBordery.BorderLine.Z / (int)Constants.RegionSize);
                    }
                    else
                        pos.Y = ((pos.Y + Constants.RegionSize));


                    newRegionHandle
                        = Util.UIntsToLong((uint)((thisx + changeX) * Constants.RegionSize),
                                           (uint)((thisy - changeY) * Constants.RegionSize));
                    // x + 1
                    // y - 1
                }
                else if (scene.TestBorderCross(attemptedPosition + NorthCross, Cardinals.N))
                {
                    pos.X = ((pos.X - Constants.RegionSize));
                    pos.Y = ((pos.Y - Constants.RegionSize));
                    newRegionHandle
                        = Util.UIntsToLong((uint)((thisx + changeX) * Constants.RegionSize),
                                           (uint)((thisy + changeY) * Constants.RegionSize));
                    // x + 1
                    // y + 1
                }
                else
                {
                    pos.X = ((pos.X - Constants.RegionSize));
                    newRegionHandle
                        = Util.UIntsToLong((uint)((thisx + changeX) * Constants.RegionSize),
                                           (uint)(thisy * Constants.RegionSize));
                    // x + 1
                }
            }
            else if (scene.TestBorderCross(attemptedPosition + SouthCross, Cardinals.S))
            {
                Border crossedBordery = scene.GetCrossedBorder(attemptedPosition + SouthCross, Cardinals.S);
                //(crossedBorderx.BorderLine.Z / (int)Constants.RegionSize)

                if (crossedBordery.BorderLine.Z > 0)
                {
                    pos.Y = ((pos.Y + crossedBordery.BorderLine.Z));
                    changeY = (int)(crossedBordery.BorderLine.Z / (int)Constants.RegionSize);
                }
                else
                    pos.Y = ((pos.Y + Constants.RegionSize));

                newRegionHandle
                    = Util.UIntsToLong((uint)(thisx * Constants.RegionSize), (uint)((thisy - changeY) * Constants.RegionSize));
                // y - 1
            }
            else if (scene.TestBorderCross(attemptedPosition + NorthCross, Cardinals.N))
            {

                pos.Y = ((pos.Y - Constants.RegionSize));
                newRegionHandle
                    = Util.UIntsToLong((uint)(thisx * Constants.RegionSize), (uint)((thisy + changeY) * Constants.RegionSize));
                // y + 1
            }

            // Offset the positions for the new region across the border
            Vector3 oldGroupPosition = grp.RootPart.GroupPosition;

            // If we fail to cross the border, then reset the position of the scene object on that border.
            uint x = 0, y = 0;
            Utils.LongToUInts(newRegionHandle, out x, out y);
            GridRegion destination = scene.GridService.GetRegionByPosition(scene.RegionInfo.ScopeID, (int)x, (int)y);

            if (destination == null || !CrossPrimGroupIntoNewRegion(destination, pos, grp, silent))
            {
                m_log.InfoFormat("[ENTITY TRANSFER MODULE] cross region transfer failed for object {0}",grp.UUID);

                // We are going to move the object back to the old position so long as the old position
                // is in the region
                oldGroupPosition.X = Util.Clamp<float>(oldGroupPosition.X,1.0f,(float)Constants.RegionSize-1);
                oldGroupPosition.Y = Util.Clamp<float>(oldGroupPosition.Y,1.0f,(float)Constants.RegionSize-1);
                oldGroupPosition.Z = Util.Clamp<float>(oldGroupPosition.Z,1.0f,4096.0f);

                grp.RootPart.GroupPosition = oldGroupPosition;

                // Need to turn off the physics flags, otherwise the object will continue to attempt to
                // move out of the region creating an infinite loop of failed attempts to cross
                grp.UpdatePrimFlags(grp.RootPart.LocalId,false,grp.IsTemporary,grp.IsPhantom,false);

                if (grp.RootPart.KeyframeMotion != null)
                    grp.RootPart.KeyframeMotion.CrossingFailure();

                grp.ScheduleGroupForFullUpdate();
            }
        }


        /// <summary>
        /// Move the given scene object into a new region
        /// </summary>
        /// <param name="newRegionHandle"></param>
        /// <param name="grp">Scene Object Group that we're crossing</param>
        /// <returns>
        /// true if the crossing itself was successful, false on failure
        /// FIMXE: we still return true if the crossing object was not successfully deleted from the originating region
        /// </returns>
        protected bool CrossPrimGroupIntoNewRegion(GridRegion destination, Vector3 newPosition, SceneObjectGroup grp, bool silent)
        {
            //m_log.Debug("  >>> CrossPrimGroupIntoNewRegion <<<");

            bool successYN = false;
            grp.RootPart.ClearUpdateSchedule();
            //int primcrossingXMLmethod = 0;

            if (destination != null)
            {
                //string objectState = grp.GetStateSnapshot();

                //successYN
                //    = m_sceneGridService.PrimCrossToNeighboringRegion(
                //        newRegionHandle, grp.UUID, m_serialiser.SaveGroupToXml2(grp), primcrossingXMLmethod);
                //if (successYN && (objectState != "") && m_allowScriptCrossings)
                //{
                //    successYN = m_sceneGridService.PrimCrossToNeighboringRegion(
                //            newRegionHandle, grp.UUID, objectState, 100);
                //}

                //// And the new channel...
                //if (m_interregionCommsOut != null)
                //    successYN = m_interregionCommsOut.SendCreateObject(newRegionHandle, grp, true);
                if (Scene.SimulationService != null)
                    successYN = Scene.SimulationService.CreateObject(destination, newPosition, grp, true);

                if (successYN)
                {
                    // We remove the object here
                    try
                    {
                        grp.Scene.DeleteSceneObject(grp, silent);
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[ENTITY TRANSFER MODULE]: Exception deleting the old object left behind on a border crossing for {0}, {1}",
                            grp, e);
                    }
                }
                else
                {
                    if (!grp.IsDeleted)
                    {
                        PhysicsActor pa = grp.RootPart.PhysActor;
                        if (pa != null)
                            pa.CrossingFailure();
                    }

                    m_log.ErrorFormat("[ENTITY TRANSFER MODULE]: Prim crossing failed for {0}", grp);
                }
            }
            else
            {
                m_log.Error("[ENTITY TRANSFER MODULE]: destination was unexpectedly null in Scene.CrossPrimGroupIntoNewRegion()");
            }

            return successYN;
        }

        /// <summary>
        /// Cross the attachments for an avatar into the destination region.
        /// </summary>
        /// <remarks>
        /// This is only invoked for simulators released prior to April 2011.  Versions of OpenSimulator since then
        /// transfer attachments in one go as part of the ChildAgentDataUpdate data passed in the update agent call.
        /// </remarks>
        /// <param name='destination'></param>
        /// <param name='sp'></param>
        /// <param name='silent'></param>
        protected void CrossAttachmentsIntoNewRegion(GridRegion destination, ScenePresence sp, bool silent)
        {
            List<SceneObjectGroup> attachments = sp.GetAttachments();

//            m_log.DebugFormat(
//                "[ENTITY TRANSFER MODULE]: Crossing {0} attachments into {1} for {2}",
//                m_attachments.Count, destination.RegionName, sp.Name);

            foreach (SceneObjectGroup gobj in attachments)
            {
                // If the prim group is null then something must have happened to it!
                if (gobj != null && !gobj.IsDeleted)
                {
                    SceneObjectGroup clone = (SceneObjectGroup)gobj.CloneForNewScene();
                    clone.RootPart.GroupPosition = gobj.RootPart.AttachedPos;
                    clone.IsAttachment = false;

                    //gobj.RootPart.LastOwnerID = gobj.GetFromAssetID();
                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Sending attachment {0} to region {1}",
                        clone.UUID, destination.RegionName);

                    CrossPrimGroupIntoNewRegion(destination, Vector3.Zero, clone, silent);
                }
            }

            sp.ClearAttachments();
        }

        #endregion

        #region Misc

        public bool IsInTransit(UUID id)
        {
            return m_entityTransferStateMachine.GetAgentTransferState(id) != null;
        }

        protected void ReInstantiateScripts(ScenePresence sp)
        {
            int i = 0;
            if (sp.InTransitScriptStates.Count > 0)
            {
                List<SceneObjectGroup> attachments = sp.GetAttachments();

                foreach (SceneObjectGroup sog in attachments)
                {
                    if (i < sp.InTransitScriptStates.Count)
                    {
                        sog.SetState(sp.InTransitScriptStates[i++], sp.Scene);
                        sog.CreateScriptInstances(0, false, sp.Scene.DefaultScriptEngine, 0);
                        sog.ResumeScripts();
                    }
                    else
                        m_log.ErrorFormat(
                            "[ENTITY TRANSFER MODULE]: InTransitScriptStates.Count={0} smaller than Attachments.Count={1}",
                            sp.InTransitScriptStates.Count, attachments.Count);
                }

                sp.InTransitScriptStates.Clear();
            }
        }
        #endregion

    }
}
