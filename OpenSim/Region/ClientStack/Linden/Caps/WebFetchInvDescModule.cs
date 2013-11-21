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
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using log4net;
using Nini.Config;
using Mono.Addins;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework.Capabilities;
using OpenSim.Services.Interfaces;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OpenSim.Capabilities.Handlers;

namespace OpenSim.Region.ClientStack.Linden
{
    /// <summary>
    /// This module implements both WebFetchInventoryDescendents and FetchInventoryDescendents2 capabilities.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "WebFetchInvDescModule")]
    public class WebFetchInvDescModule : INonSharedRegionModule
    {
        class aPollRequest
        {
            public PollServiceInventoryEventArgs thepoll;
            public UUID reqID;
            public Hashtable request;
            public ScenePresence presence;
            public List<UUID> folders;
        }

        // private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;

        private IInventoryService m_InventoryService;
        private ILibraryService m_LibraryService;

        private bool m_Enabled;

        private string m_fetchInventoryDescendents2Url;
        private string m_webFetchInventoryDescendentsUrl;

        private static WebFetchInvDescHandler m_webFetchHandler;

        private static Thread[] m_workerThreads = null;

        private static DoubleQueue<aPollRequest> m_queue =
                new DoubleQueue<aPollRequest>();

        #region ISharedRegionModule Members

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ClientStack.LindenCaps"];
            if (config == null)
                return;

            m_fetchInventoryDescendents2Url = config.GetString("Cap_FetchInventoryDescendents2", string.Empty);
            m_webFetchInventoryDescendentsUrl = config.GetString("Cap_WebFetchInventoryDescendents", string.Empty);

            if (m_fetchInventoryDescendents2Url != string.Empty || m_webFetchInventoryDescendentsUrl != string.Empty)
            {
                m_Enabled = true;
            }
        }

        public void AddRegion(Scene s)
        {
            if (!m_Enabled)
                return;

            m_scene = s;
        }

        public void RemoveRegion(Scene s)
        {
            if (!m_Enabled)
                return;

            m_scene.EventManager.OnRegisterCaps -= RegisterCaps;

            foreach (Thread t in m_workerThreads)
                Watchdog.AbortThread(t.ManagedThreadId);

            m_scene = null;
        }

        public void RegionLoaded(Scene s)
        {
            if (!m_Enabled)
                return;

            m_InventoryService = m_scene.InventoryService;
            m_LibraryService = m_scene.LibraryService;

            // We'll reuse the same handler for all requests.
            m_webFetchHandler = new WebFetchInvDescHandler(m_InventoryService, m_LibraryService);

            m_scene.EventManager.OnRegisterCaps += RegisterCaps;

            if (m_workerThreads == null)
            {
                m_workerThreads = new Thread[2];

                for (uint i = 0; i < 2; i++)
                {
                    m_workerThreads[i] = Watchdog.StartThread(DoInventoryRequests,
                            String.Format("InventoryWorkerThread{0}", i),
                            ThreadPriority.Normal,
                            false,
                            true,
                            null,
                            int.MaxValue);
                }
            }
        }

        public void PostInitialise()
        {
        }

        public void Close() { }

        public string Name { get { return "WebFetchInvDescModule"; } }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

        private class PollServiceInventoryEventArgs : PollServiceEventArgs
        {
            private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

            private Dictionary<UUID, Hashtable> responses =
                    new Dictionary<UUID, Hashtable>();

            private Scene m_scene;

            public PollServiceInventoryEventArgs(Scene scene, string url, UUID pId) :
                    base(null, url, null, null, null, pId, int.MaxValue)
            {
                m_scene = scene;

                HasEvents = (x, y) => { lock (responses) return responses.ContainsKey(x); };
                GetEvents = (x, y) =>
                {
                    lock (responses)
                    {
                        try
                        {
                            return responses[x];
                        }
                        finally
                        {
                            responses.Remove(x);
                        }
                    }
                };

                Request = (x, y) =>
                {
                    ScenePresence sp = m_scene.GetScenePresence(Id);
                    if (sp == null)
                    {
                        m_log.ErrorFormat("[INVENTORY]: Unable to find ScenePresence for {0}", Id);
                        return;
                    }

                    aPollRequest reqinfo = new aPollRequest();
                    reqinfo.thepoll = this;
                    reqinfo.reqID = x;
                    reqinfo.request = y;
                    reqinfo.presence = sp;
                    reqinfo.folders = new List<UUID>();

                    // Decode the request here
                    string request = y["body"].ToString();

                    request = request.Replace("<string>00000000-0000-0000-0000-000000000000</string>", "<uuid>00000000-0000-0000-0000-000000000000</uuid>");

                    request = request.Replace("<key>fetch_folders</key><integer>0</integer>", "<key>fetch_folders</key><boolean>0</boolean>");
                    request = request.Replace("<key>fetch_folders</key><integer>1</integer>", "<key>fetch_folders</key><boolean>1</boolean>");

                    Hashtable hash = new Hashtable();
                    try
                    {
                        hash = (Hashtable)LLSD.LLSDDeserialize(Utils.StringToBytes(request));
                    }
                    catch (LLSD.LLSDParseException e)
                    {
                        m_log.ErrorFormat("[INVENTORY]: Fetch error: {0}{1}" + e.Message, e.StackTrace);
                        m_log.Error("Request: " + request);
                        return;
                    }
                    catch (System.Xml.XmlException)
                    {
                        m_log.ErrorFormat("[INVENTORY]: XML Format error");
                    }

                    ArrayList foldersrequested = (ArrayList)hash["folders"];

                    bool highPriority = false;

                    for (int i = 0; i < foldersrequested.Count; i++)
                    {
                        Hashtable inventoryhash = (Hashtable)foldersrequested[i];
                        string folder = inventoryhash["folder_id"].ToString();
                        UUID folderID;
                        if (UUID.TryParse(folder, out folderID))
                        {
                            if (!reqinfo.folders.Contains(folderID))
                            {
                                //TODO: Port COF handling from Avination
                                reqinfo.folders.Add(folderID);
                            }
                        }
                    }

                    if (highPriority)
                        m_queue.EnqueueHigh(reqinfo);
                    else
                        m_queue.EnqueueLow(reqinfo);
                };

                NoEvents = (x, y) =>
                {
/*
                    lock (requests)
                    {
                        Hashtable request = requests.Find(id => id["RequestID"].ToString() == x.ToString());
                        requests.Remove(request);
                    }
*/
                    Hashtable response = new Hashtable();

                    response["int_response_code"] = 500;
                    response["str_response_string"] = "Script timeout";
                    response["content_type"] = "text/plain";
                    response["keepalive"] = false;
                    response["reusecontext"] = false;

                    return response;
                };
            }

            public void Process(aPollRequest requestinfo)
            {
                UUID requestID = requestinfo.reqID;

                Hashtable response = new Hashtable();

                response["int_response_code"] = 200;
                response["content_type"] = "text/plain";
                response["keepalive"] = false;
                response["reusecontext"] = false;

                response["str_response_string"] = m_webFetchHandler.FetchInventoryDescendentsRequest(
                        requestinfo.request["body"].ToString(), String.Empty, String.Empty, null, null);

                lock (responses)
                    responses[requestID] = response;
            }
        }

        private void RegisterCaps(UUID agentID, Caps caps)
        {
            RegisterFetchDescendentsCap(agentID, caps, "FetchInventoryDescendents2", m_fetchInventoryDescendents2Url);
        }

        private void RegisterFetchDescendentsCap(UUID agentID, Caps caps, string capName, string url)
        {
            string capUrl;

            // disable the cap clause
            if (url == "")
            {
                return;
            }
            // handled by the simulator
            else if (url == "localhost")
            {
                capUrl = "/CAPS/" + UUID.Random() + "/";

                // Register this as a poll service
                PollServiceInventoryEventArgs args = new PollServiceInventoryEventArgs(m_scene, capUrl, agentID);
                args.Type = PollServiceEventArgs.EventType.Inventory;

                caps.RegisterPollHandler(capName, args);
            }
            // external handler
            else
            {
                capUrl = url;
                IExternalCapsModule handler = m_scene.RequestModuleInterface<IExternalCapsModule>();
                if (handler != null)
                    handler.RegisterExternalUserCapsHandler(agentID,caps,capName,capUrl);
                else
                    caps.RegisterHandler(capName, capUrl);
            }

            // m_log.DebugFormat(
            //     "[FETCH INVENTORY DESCENDENTS2 MODULE]: Registered capability {0} at {1} in region {2} for {3}",
            //     capName, capUrl, m_scene.RegionInfo.RegionName, agentID);
        }

//        private void DeregisterCaps(UUID agentID, Caps caps)
//        {
//            string capUrl;
//
//            if (m_capsDict.TryGetValue(agentID, out capUrl))
//            {
//                MainServer.Instance.RemoveHTTPHandler("", capUrl);
//                m_capsDict.Remove(agentID);
//            }
//        }

        private void DoInventoryRequests()
        {
            while (true)
            {
                Watchdog.UpdateThread();

                aPollRequest poolreq = m_queue.Dequeue();

                if (poolreq != null && poolreq.thepoll != null)
                    poolreq.thepoll.Process(poolreq);
            }
        }
    }
}
