// Copyright (c) 2018 Alachisoft
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//    http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.


using System;
using System.Collections;
using System.Threading;


using Alachisoft.NCache.Caching.AutoExpiration;
using Alachisoft.NCache.Caching.Topologies.Local;
using Alachisoft.NCache.Caching.Statistics;
using Alachisoft.NCache.Caching.Util;
using Alachisoft.NCache.Caching.DatasourceProviders;
using Alachisoft.NCache.Common.DataReader;
using Alachisoft.NCache.Config;

using Alachisoft.NCache.Runtime.Exceptions;

using Alachisoft.NCache.Util;

using Alachisoft.NCache.Common;
using Alachisoft.NCache.Common.Threading;
using Alachisoft.NCache.Common.DataStructures;
using Alachisoft.NCache.Common.Net;

using Alachisoft.NCache.Common.Monitoring;
using Alachisoft.NCache.Caching.Topologies.Clustered.Operations;
using Alachisoft.NCache.Caching.Topologies.Clustered.Results;
using System.Net;
using Alachisoft.NCache.Caching.Queries;
using System.Collections.Generic;

using Alachisoft.NCache.Common.Logger;
using Alachisoft.NCache.Common.Enum;

using Alachisoft.NCache.Persistence;
using Alachisoft.NGroups.Util;
using Alachisoft.NGroups.Blocks;
using Alachisoft.NCache.Caching.Queries.Continuous.StateTransfer;
using Alachisoft.NGroups;
using Alachisoft.NCache.Runtime.Caching;

using Alachisoft.NCache.Common.DataStructures.Clustered;
using Alachisoft.NCache.Common.Events;

using Alachisoft.NCache.Caching.Messaging;
using Alachisoft.NCache.Common.Resources;

namespace Alachisoft.NCache.Caching.Topologies.Clustered
{
    /// <summary>
    /// This class provides the partitioned cluster cache primitives. 
    /// </summary>
    internal class ReplicatedServerCache :
        ReplicatedCacheBase,
        IPresenceAnnouncement,
        ICacheEventsListener
    {
        /// <summary> Call balancing helper object. </summary>
        private IActivityDistributor _callBalancer;

        /// <summary> The periodic update task. </summary>
        private PeriodicPresenceAnnouncer _taskUpdate;

        private PeriodicStatsUpdater _localStatsUpdater;

        private StateTransferTask _stateTransferTask = null;

        /// <summary> keeps track of all server members excluding itself </summary>
        protected ArrayList _otherServers = ArrayList.Synchronized(new ArrayList(11));
        private ArrayList _nodesInStateTransfer = new ArrayList();
        private bool _allowEventRaiseLocally;
        private Latch _stateTransferLatch = new Latch((byte)ReplicatedStateTransferStatus.UNDER_STATE_TRANSFER);


        private AsyncItemReplicator _asyncReplicator = null;



        private bool threadRunning = true;
        private int confirmClusterStartUP = 3;

        protected IPAddress _srvrJustLeft = null;
        private SubscriptionRefresherTask _subscriptionTask;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="cacheSchemes">collection of cache schemes (config properties).</param>
        /// <param name="properties">properties collection for this cache.</param>
        /// <param name="listener">cache events listener</param>
        public ReplicatedServerCache(IDictionary cacheClasses, IDictionary properties, ICacheEventsListener listener, CacheRuntimeContext context)
            : base(properties, listener, context)
        {
            _stats.ClassName = "replicated-server";
            Initialize(cacheClasses, properties);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cacheClasses"></param>
        /// <param name="properties"></param>
        /// <param name="listener"></param>
        /// <param name="context"></param>
        /// <param name="clusterListener"></param>
        /// <param name="userId"></param>
        /// <param name="password"></param>
        public ReplicatedServerCache(IDictionary cacheClasses, IDictionary properties, ICacheEventsListener listener, CacheRuntimeContext context, IClusterEventsListener clusterListener)
            : base(properties, listener, context, clusterListener)
        {
            _stats.ClassName = "replicated-server";
        }

        #region	/                 --- IDisposable ---           /

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or 
        /// resetting unmanaged resources.
        /// </summary>
        public override void Dispose()
        {
            if (_stateTransferTask != null)
            {
                _stateTransferTask.StopProcessing();
            }
            if (_taskUpdate != null)
            {
                _taskUpdate.Cancel();
                _taskUpdate = null;
            }

            if (_localStatsUpdater != null)
            {
                _localStatsUpdater.Cancel();
                _localStatsUpdater = null;
            }

            if (_internalCache != null)
            {
                _internalCache.Dispose();
                _internalCache = null;
            }


            threadRunning = false;

            if (CQManager != null)
            {
                CQManager.Dispose();
                CQManager = null;
            }

            base.Dispose();
        }

        #endregion


        #region	/                 --- Initialization ---           /

        /// <summary>
        /// Method that allows the object to initialize itself. Passes the property map down 
        /// the object hierarchy so that other objects may configure themselves as well..
        /// </summary>
        /// <param name="cacheSchemes">collection of cache schemes (config properties).</param>
        /// <param name="properties">properties collection for this cache.</param>
        protected override void Initialize(IDictionary cacheClasses, IDictionary properties)
        {
            if (properties == null)
                throw new ArgumentNullException("properties");

            try
            {
                base.Initialize(cacheClasses, properties);

                IDictionary frontCacheProps = ConfigHelper.GetCacheScheme(cacheClasses, properties, "internal-cache");
                string cacheType = Convert.ToString(frontCacheProps["type"]).ToLower();
                ActiveQueryAnalyzer analyzer = new ActiveQueryAnalyzer(this, frontCacheProps, _context.CacheRoot.Name, this.Context, Context._dataSharingKnownTypesforNet);
                if (cacheType.CompareTo("local-cache") == 0)
                {
                    _internalCache = CacheBase.Synchronized(new IndexedLocalCache(cacheClasses, this, frontCacheProps, this, _context, analyzer));
                }
                else if (cacheType.CompareTo("overflow-cache") == 0)
                {
                    _internalCache = CacheBase.Synchronized(new IndexedOverflowCache(cacheClasses, this, frontCacheProps, this, _context, analyzer));
                }
                else
                {
                    throw new ConfigurationException("invalid or non-local class specified in partitioned cache");
                }

                _stats.Nodes = ArrayList.Synchronized(new ArrayList());
                _callBalancer = new CallBalancer();

                InitializeCluster(properties, Name, MCAST_DOMAIN, new Identity(true, (_context.Render != null ? _context.Render.Port : 0), (_context.Render != null ? _context.Render.IPAddress : null)));
                _stats.GroupName = Cluster.ClusterName;

                postInstrumentatedData(_stats, Name);

                HasStarted();
            }
            catch (ConfigurationException)
            {
                Dispose();
                throw;
            }
            catch (Exception e)
            {
                Dispose();
                throw new ConfigurationException("Configuration Error: " + e.Message, e);
            }

        }

        public override void Initialize(IDictionary cacheClasses, IDictionary properties, bool twoPhaseInitialization)
        {
            if (properties == null)
                throw new ArgumentNullException("properties");

            try
            {
                base.Initialize(cacheClasses, properties);

                IDictionary frontCacheProps = ConfigHelper.GetCacheScheme(cacheClasses, properties, "internal-cache");

                string cacheType = Convert.ToString(frontCacheProps["type"]).ToLower();
                if (cacheType.CompareTo("local-cache") == 0)
                {

                    ActiveQueryAnalyzer analyzer = new ActiveQueryAnalyzer(this, frontCacheProps, _context.CacheRoot.Name, this.Context, Context._dataSharingKnownTypesforNet);
                    _internalCache = CacheBase.Synchronized(new IndexedLocalCache(cacheClasses, this, frontCacheProps, this, _context, analyzer));

                }
                else
                {
                    throw new ConfigurationException("invalid or non-local class specified in partitioned cache");
                }


                _stats.Nodes = ArrayList.Synchronized(new ArrayList());
                _callBalancer = new CallBalancer();

                InitializeCluster(properties, Name, MCAST_DOMAIN, new Identity(true, (_context.Render != null ? _context.Render.Port : 0), (_context.Render != null ? _context.Render.IPAddress : null)), twoPhaseInitialization, false);
                _stats.GroupName = Cluster.ClusterName;




                postInstrumentatedData(_stats, Name);

                for (int i = 0; i < confirmClusterStartUP; i++)
                {
                    ConfirmClusterStartUP(false, i + 1);
                }


                HasStarted();
            }
            catch (ConfigurationException)
            {
                Dispose();
                throw;
            }
            catch (Exception e)
            {
                Dispose();
                throw new ConfigurationException("Configuration Error: " + e.Message, e);
            }


        }

        #endregion

        /// <summary>
        /// Return the next node in call balacing order.
        /// </summary>
        /// <returns></returns>
        private Address GetNextNode()
        {
            NodeInfo node = _callBalancer.SelectNode(_stats, null);
            return node == null ? null : node.Address;
        }


        #region	/                 --- Overrides for ClusteredCache ---           /

        /// <summary>
        /// Called after the membership has been changed. Lets the members do some
        /// member oriented tasks.
        /// </summary>
        public override void OnAfterMembershipChange()
        {
            base.OnAfterMembershipChange();
            _context.ExpiryMgr.AllowClusteredExpiry = Cluster.IsCoordinator;
            Cluster.ViewInstallationLatch.SetStatusBit(ViewStatus.COMPLETE, ViewStatus.INPROGRESS | ViewStatus.NONE);

            if (_taskUpdate == null)
            {
                _taskUpdate = new PeriodicPresenceAnnouncer(this, _statsReplInterval);
                _context.TimeSched.AddTask(_taskUpdate);

                StartStateTransfer();
            }

            if (_localStatsUpdater == null)
            {
                _localStatsUpdater = new PeriodicStatsUpdater(this);
                _context.TimeSched.AddTask(_localStatsUpdater);
            }

            if (Cluster.IsCoordinator)
            {



                if (_context.DsMgr != null && _context.DsMgr.IsWriteBehindEnabled)
                    _context.DsMgr.StartWriteBehindProcessor();
                if (_context.MessageManager != null) _context.MessageManager.StartMessageProcessing();
            }




            //async replicator is used to replicate the update index operations to other replica nodes.
            if (Cluster.Servers.Count > 1)
            {
                if (_asyncReplicator == null) _asyncReplicator = new AsyncItemReplicator(Context, new TimeSpan(0, 0, 2));
                _asyncReplicator.Start();
                Context.NCacheLog.CriticalInfo("OnAfterMembershipChange", "async-replicator started.");

                if (_subscriptionTask == null)
                {
                    _subscriptionTask = new SubscriptionRefresherTask(this, _context);
                    _context.TimeSched.AddTask(_subscriptionTask);
                }
            }
            else
            {
                if (_asyncReplicator != null)
                {
                    _asyncReplicator.Stop(false);
                    _asyncReplicator = null;
                    Context.NCacheLog.CriticalInfo("OnAfterMembershipChange", "async-replicator stopped.");
                }

                if (_subscriptionTask != null)
                {
                    _subscriptionTask.Cancle();
                    _subscriptionTask = null;
                }
            }

            UpdateCacheStatistics();
        }

        public override void WindUpReplicatorTask()
        {
            if (_asyncReplicator != null)
            {
                _asyncReplicator.WindUpTask();
            }
        }

        public override void WaitForReplicatorTask(long interval)
        {
            if (_asyncReplicator != null)
            {
                _asyncReplicator.WaitForShutDown(interval);
            }
        }

        /// <summary>
        /// Called when a new member joins the group.
        /// </summary>
        /// 		/// <param name="address">address of the joining member</param>
        /// <param name="identity">additional identity information</param>
        /// <returns>true if the node joined successfuly</returns>
        public override bool OnMemberJoined(Address address, NodeIdentity identity)
        {
            if (!base.OnMemberJoined(address, identity) || !((Identity)identity).HasStorage)
                return false;

            NodeInfo info = new NodeInfo(address as Address);
            if (identity.RendererAddress != null)
                info.RendererAddress = new Address(identity.RendererAddress, identity.RendererPort);

            info.IsInproc = identity.RendererPort == 0;
            info.SubgroupName = identity.SubGroupName;
            _stats.Nodes.Add(info);


            if (LocalAddress.CompareTo(address) == 0)
            {
                _stats.LocalNode = info;
            }
            else
            {
                lock (_nodesInStateTransfer)
                {
                    if (!_nodesInStateTransfer.Contains(address))
                        _nodesInStateTransfer.Add(address);
                }

                //add into the list of other servers.
                if (!_otherServers.Contains(address))
                    _otherServers.Add(address);
            }
            if (!info.IsInproc)
            {
                AddServerInformation(address, identity.RendererPort, info.ConnectedClients.Count);


            }

            if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("ReplicatedCache.OnMemberJoined()", "Replication increased: " + address);
            return true;
        }

        /// <summary>
        /// Called when an existing member leaves the group.
        /// </summary>
        /// <param name="address">address of the joining member</param>
        /// <returns>true if the node left successfuly</returns>
        public override bool OnMemberLeft(Address address, NodeIdentity identity)
        {
            if (!base.OnMemberLeft(address, identity))
                return false;

            NodeInfo info = _stats.GetNode(address as Address);

            if (_context.ConnectedClients != null)
                _context.ConnectedClients.ClientsDisconnected(info.ConnectedClients, info.Address, DateTime.Now);

            _stats.Nodes.Remove(info);

            //remove into the list of other servers.
            _otherServers.Remove(address);

            if (!info.IsInproc)
            {
                RemoveServerInformation(address, identity.RendererPort);

            }

            if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("ReplicatedCache.OnMemberLeft()", "Replica Removed: " + address);
            return true;
        }

        /// <summary>
        /// Handles the function requests.
        /// </summary>
        /// <param name="func"></param>
        /// <returns></returns>
        public override object HandleClusterMessage(Address src, Function func)
        {

            switch (func.Opcode)
            {
                case (int)OpCodes.PeriodicUpdate:
                    return handlePresenceAnnouncement(src, func.Operand);

                case (int)OpCodes.ReqStatus:
                    return this.handleReqStatus();

                case (int)OpCodes.GetCount:
                    return handleCount();



                case (int)OpCodes.Contains:
                    return handleContains(func.Operand);

                case (int)OpCodes.Get:
                    return handleGet(func.Operand);

                case (int)OpCodes.Insert:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);

                    return handleInsert(src, func.Operand, func.UserPayload);

                case (int)OpCodes.Add:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleAdd(src, func.Operand, func.UserPayload);

                case (int)OpCodes.AddHint:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleAddHint(src, func.Operand);

                case (int)OpCodes.AddSyncDependency:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleAddSyncDependency(src, func.Opcode, func.Operand);

                case (int)OpCodes.Remove:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);

                    return handleRemove(src, func.Operand);

                case (int)OpCodes.RemoveRange:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleRemoveRange(func.Operand);

                case (int)OpCodes.Clear:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleClear(src, func.Operand);

                case (int)OpCodes.GetKeys:
                    return handleGetKeys(func.Operand);

                case (int)OpCodes.KeyList:
                    return handleKeyList();

                case (int)OpCodes.NotifyAdd:
                    return handleNotifyAdd(func.Operand);

                case (int)OpCodes.NotifyUpdate:
                    return handleNotifyUpdate(func.Operand);

                case (int)OpCodes.NotifyRemoval:
                    return handleNotifyRemoval(func.Operand);

                case (int)OpCodes.Search:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleSearch(func.Operand);

                case (int)OpCodes.SearchEntries:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleSearchEntries(func.Operand);

                case (int)OpCodes.DeleteQuery:
                    return handleDeleteQuery(func.Operand);

                case (int)OpCodes.GetDataGroupInfo:
                    return handleGetGroupInfo(func.Operand);

                case (int)OpCodes.GetGroup:
                    return handleGetGroup(func.Operand);

                case (int)OpCodes.UpdateIndice:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleUpdateIndice(func.Operand);

                case (int)OpCodes.LockKey:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleLock(func.Operand);

                case (int)OpCodes.UnLockKey:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    handleUnLockKey(func.Operand);
                    break;

                case (int)OpCodes.IsLocked:
                    return handleIsLocked(func.Operand);

                case (int)OpCodes.GetTag:
                    return handleGetTag(func.Operand);

                case (int)OpCodes.ReplicateOperations:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleReplicateOperations(src, func.Operand, func.UserPayload);

                case (int)OpCodes.GetNextChunk:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleGetNextChunk(src, func.Operand);

                case (int)OpCodes.UnRegisterCQ:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleUnRegisterCQ(func.Operand);

                case (int)OpCodes.RegisterCQ:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleRegisterCQ(func.Operand);

                case (int)OpCodes.SearchCQ:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleSearchCQ(func.Operand);

                case (int)OpCodes.SearchEntriesCQ:
                    _stateTransferLatch.WaitForAny((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED);
                    return handleSearchEntriesCQ(func.Operand);

#if COMMUNITY
                case (int)OpCodes.SignalEndOfStateTxfr:
                    handleSignalEndOfStateTxfr(src);
                    break;
#endif

                case (int)OpCodes.GetKeysByTag:
                    return handleGetKeysByTag(func.Operand);

                case (int)OpCodes.NotifyCustomRemoveCallback:
                    return handleNotifyRemoveCallback(func.Operand);
                    break;
                case (int)OpCodes.NotifyCustomUpdateCallback:
                    return handleNotifyUpdateCallback(func.Operand);

            }
            return base.HandleClusterMessage(src, func);
        }


        private void handleSignalEndOfStateTxfr(Address requestingNode)
        {
            if (Cluster.IsCoordinator)
            {
                byte b1 = 0, b2 = 1, b3 = 2;
            }
            lock (_nodesInStateTransfer)
            {
                _nodesInStateTransfer.Remove(requestingNode);
                _allowEventRaiseLocally = true;
            }


        }
        private object handleRegisterCQ(object info)
        {
            if (_internalCache != null)
            {
                object[] data = (object[])info;
                ContinuousQuery query = data[0] as ContinuousQuery;
                if (CQManager.Exists(query))
                {
                    CQManager.Update(query, data[1] as string, data[2] as string, (bool)data[3], (bool)data[4], (bool)data[5], (QueryDataFilters)data[7]);
                }
                else
                {
                    _internalCache.RegisterCQ(query, data[6] as OperationContext);
                    CQManager.Register(query, data[1] as string, data[2] as string, (bool)data[3], (bool)data[4], (bool)data[5], (QueryDataFilters)data[7]);

                }
            }

            return null;
        }

        private object handleUnRegisterCQ(object info)
        {
            if (_internalCache != null)
            {
                object[] data = (object[])info;
                string queryId = data[0] as string;
                if (CQManager.UnRegister(queryId, data[1] as string, data[2] as string))
                {
                    _internalCache.UnRegisterCQ(queryId);
                }
            }

            return null;
        }

        private object handleSearchEntriesCQ(object info)
        {
            object[] data = (object[])info;
            ContinuousQuery query = data[0] as ContinuousQuery;
            return Local_SearchEntriesCQ(query.CommandText, query.AttributeValues, data[1] as string, data[2] as string, (bool)data[3], (bool)data[4], (bool)data[5], data[6] as OperationContext, (QueryDataFilters)data[7]);
        }

        private object handleSearchCQ(object info)
        {
            object[] data = (object[])info;
            ContinuousQuery query = data[0] as ContinuousQuery;
            return Local_SearchCQ(query.CommandText, query.AttributeValues, data[1] as string, data[2] as string, (bool)data[3], (bool)data[4], (bool)data[5], data[6] as OperationContext, (QueryDataFilters)data[7]);
        }

        #endregion

        #region	/                 --- Statistics Replication ---           /

        /// <summary>
        /// Periodic update (PULL model), i.e. on demand fetch of information from every node.
        /// </summary>
        private bool DetermineClusterStatus()
        {
            try
            {
                if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("ReplicatedServerCache.DetermineClusterStatus", " determine cluster status");
                Function func = new Function((int)OpCodes.ReqStatus, null);
                RspList results = Cluster.BroadcastToServers(func, GroupRequest.GET_ALL, false);

                for (int i = 0; i < results.size(); i++)
                {
                    Rsp rsp = (Rsp)results.elementAt(i);
                    NodeInfo nodeInfo = rsp.Value as NodeInfo;
                    if (nodeInfo != null)
                    {
                        handlePresenceAnnouncement(rsp.Sender as Address, nodeInfo);
                    }
                }
            }
            ///This is Madness..
            ///No .. this was previously maintained as it is i.e. using exception as flow control
            ///Null can be rooted down to MirrorManager.GetGroupInfo where null is intentionally thrown and here instead of providing a check ...
            ///This flow can be found in every DetermineCluster function of every topology
            catch (NullReferenceException) { }
            catch (Exception e)
            {
                Context.NCacheLog.Error("ReplicatedCache.DetermineClusterStatus()", e.ToString());
            }
            return false;
        }




        /// <summary>
        /// Handler for Periodic update (PUSH model).
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="obj"></param>
        /// <returns></returns>
        private object handlePresenceAnnouncement(Address sender, object obj)
        {
            NodeInfo info = null;
            lock (Servers.SyncRoot)
            {
                NodeInfo other = obj as NodeInfo;
                info = _stats.GetNode(sender as Address);
                if (other != null && info != null)
                {
                    Context.NCacheLog.Debug("Replicated.handlePresenceAnnouncement()", "sender = " + sender + " stats = " + other.Statistics);
                    if (!IsReplicationSequenced(info, other)) return null;
                    info.Statistics = other.Statistics;
                    info.NodeGuid = other.NodeGuid;
                    info.StatsReplicationCounter = other.StatsReplicationCounter;

                    info.ConnectedClients = other.ConnectedClients;
                    info.Status = other.Status;
                }
            }
            UpdateCacheStatistics();

            return null;
        }

        public override CacheStatistics CombineClusterStatistics(ClusterCacheStatistics s)
        {
            CacheStatistics c = ClusterHelper.CombineReplicatedStatistics(s);
            return c;
        }

        /// <summary>
        /// Update the list of the clients connected to this node
        /// and replicate it over the entire cluster.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="isInproc"></param>
        /// <param name="clientInfo"></param>
        public override void ClientConnected(string client, bool isInproc, ClientInfo clientInfo)
        {
            base.ClientConnected(client, isInproc, clientInfo);
            if (_context.ConnectedClients != null) UpdateClientStatus(client, true, clientInfo);
            AnnouncePresence(false);
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

        }

        /// <summary>
        /// Update the list of the clients connected to this node
        /// and replicate it over the entire cluster.
        /// </summary>
        /// <param name="client"></param>
        public override void ClientDisconnected(string client, bool isInproc)
        {
            base.ClientDisconnected(client, isInproc);
            if (_context.ConnectedClients != null) UpdateClientStatus(client, false, null);
            AnnouncePresence(false);
        }

        public override ArrayList DetermineClientConnectivity(ArrayList clients)
        {
            ArrayList result = null;
            if (clients == null) return null;
            if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("Client-Death-Detection.DetermineClientConnectivity()", "going to determine client connectivity in cluster");
            try
            {
                DetermineClusterStatus();//updating stats
                result = clients;
                foreach (string client in clients)
                {
                    foreach (NodeInfo node in _stats.Nodes)
                    {
                        if (node.ConnectedClients.Contains(client))
                        {
                            if (result.Contains(client))
                                result.Remove(client);
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Context.NCacheLog.Error("Client-Death-Detection.DetermineClientConnectivity()", e.ToString());
            }
            finally
            {
                if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("Client-Death-Detection.DetermineClientConnectivity()", "determining client connectivity in cluster completed");
            }
            return result;
        }

        #endregion

        #region	/                 --- State Transfer ---           /

        #region	/                 --- StateTransferTask ---           /

        /// <summary>
        /// Asynchronous state tranfer job.
        /// </summary>
        protected class StateTransferTask : AsyncProcessor.IAsyncTask
        {
            /// <summary> The partition base class </summary>
            private ReplicatedServerCache _parent = null;

            /// <summary> A promise object to wait on. </summary>
            private Promise _promise = null;

            private ILogger _ncacheLog;
            string _cacheserver = "NCache";
            ILogger NCacheLog
            {
                get { return _ncacheLog; }
            }

            private bool _stopProcessing = false;

            /// <summary>
            /// Constructor
            /// </summary>
            /// <param name="parent"></param>
            public StateTransferTask(ReplicatedServerCache parent)
            {
                _parent = parent;
                _ncacheLog = parent.Context.NCacheLog;

                _promise = new Promise();
            }

            public void StopProcessing()
            {
                _stopProcessing = true;
            }

            /// <summary>
            /// Wait until a result is available for the state transfer task.
            /// </summary>
            /// <param name="timeout"></param>
            /// <returns></returns>
            public object WaitUntilCompletion(long timeout)
            {
                return _promise.WaitResult(timeout);
            }

            /// <summary>
            /// Signal the end of state transfer.
            /// </summary>
            /// <param name="result">transfer result</param>
            /// <returns></returns>
            private void SignalEndOfTransfer(object result)
            {
                _parent.EndStateTransfer(_parent.Local_Count());
                _promise.SetResult(result);
            }

            /// <summary>
            /// Do the state transfer now.
            /// </summary>
            void AsyncProcessor.IAsyncTask.Process()
            {
                object result = null;
                bool logEvent = false;
                try
                {
                    _parent._statusLatch.SetStatusBit(NodeStatus.Initializing, NodeStatus.Running);
                    _parent.DetermineClusterStatus();


                    _parent.TransferTopicState();

                    try
                    {
                        ContinuousQueryStateTransferManager cqStateTxfrMgr = new ContinuousQueryStateTransferManager(_parent, _parent.QueryAnalyzer);
                        cqStateTxfrMgr.TransferState(_parent.Cluster.Coordinator);
                    }
                    catch (Exception ex)
                    {
                        _parent.Context.NCacheLog.Error("StateTransfer.Process", "Transfering Continuous Query State: " + ex.ToString());
                    }

                    DataSourceReplicationManager dsRepMgr = null;
                    try
                    {
                        dsRepMgr = new DataSourceReplicationManager(_parent, _parent.Context.DsMgr, _parent.Context.NCacheLog);
                        dsRepMgr.ReplicateWriteBehindQueue();

                    }
                    catch (Exception exc)
                    {
                        NCacheLog.Error("StateTransfer.Process", "Transfering queue: " + exc.ToString());
                    }
                    finally
                    {
                        if (dsRepMgr != null) dsRepMgr.Dispose();
                    }


                    // Fetch the list of keys from coordinator and open an enumerator
                    IDictionaryEnumerator ie = _parent.Clustered_GetEnumerator(_parent.Cluster.Coordinator);
                    if (ie == null)
                    {
                        TransferMessages();
                        // there are no keys, the cache is empty
                        _parent._stateTransferLatch.SetStatusBit((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED, (byte)ReplicatedStateTransferStatus.UNDER_STATE_TRANSFER);
                        return;
                    }

                    Hashtable keysTable = new Hashtable();

                    while (ie.MoveNext())
                    {
                        keysTable.Add(ie.Key, null);
                    }
                    NCacheLog.CriticalInfo("ReplicatedServerCache.StateTransfer", "Transfered keys list" + keysTable.Count);
                    _parent._internalCache.SetStateTransferKeyList(keysTable);
                    _parent._stateTransferLatch.SetStatusBit((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED, (byte)ReplicatedStateTransferStatus.UNDER_STATE_TRANSFER);

                    ie.Reset();

                    Hashtable itemsHavingKeyDependency = new Hashtable();
                    bool loggedonce = false;
                    while (ie.MoveNext())
                    {
                        if (!loggedonce)
                        {
                            NCacheLog.CriticalInfo("ReplicatedServerCache.StateTransfer", "State transfer has started");
                            AppUtil.LogEvent(_cacheserver, "\"" + _parent._context.SerializationContext + "\"" + " has started state transfer.", System.Diagnostics.EventLogEntryType.Information, EventCategories.Information, EventID.StateTransferStart);
                            loggedonce = logEvent = true;
                        }

                        if (!_stopProcessing)
                        {
                            object key = ie.Key;
                            // if the object is already there, skip a network call
                            if (_parent.Local_Contains(key, new OperationContext(OperationContextFieldName.OperationType, OperationContextOperationType.CacheOperation)))
                            {
                                continue;
                            }
                            // fetches the object remotely
                            CacheEntry val = ie.Value as CacheEntry;
                            if (val != null)
                            {
                                if (val.KeysIAmDependingOn != null && val.KeysIAmDependingOn.Length > 0)
                                {
                                    itemsHavingKeyDependency.Add(key, val);
                                    continue;
                                }
                                try
                                {
                                    // doing an Add ensures that the object is not updated
                                    // if it had already been added while we were fetching it.
                                    if (!_stopProcessing)
                                    {
                                        OperationContext operationContext = new OperationContext(OperationContextFieldName.OperationType, OperationContextOperationType.CacheOperation);
                                        _parent.Local_Add(key, val, null, null, false, operationContext);
                                        _parent.Context.PerfStatsColl.IncrementStateTxfrPerSecStats();

                                    }
                                    else
                                    {
                                        result = _parent.Local_Count();
                                        if (NCacheLog.IsInfoEnabled) NCacheLog.Info("ReplicatedServerCache.StateTransfer", "  state transfer was stopped by the parent.");

                                        return;
                                    }
                                }
                                catch (Exception)
                                {
                                    // object already there so skip it.
                                }
                            }
                        }
                        else
                        {
                            result = _parent.Local_Count();
                            if (NCacheLog.IsInfoEnabled) NCacheLog.Info("ReplicatedServerCache.StateTransfer", "  state transfer was stopped by the parent.");
                            return;
                        }
                    }


                    if (!_stopProcessing)
                    {
                        ArrayList keysHavingdependency = new ArrayList(itemsHavingKeyDependency.Keys);
                        while (keysHavingdependency.Count != 0)
                        {
                            for (int i = 0; i < keysHavingdependency.Count; i++)
                            {
                                CacheEntry valueToAdd = itemsHavingKeyDependency[keysHavingdependency[i]] as CacheEntry;
                                if (valueToAdd != null)
                                {
                                    try
                                    {
                                        // doing an Add ensures that the object is not updated
                                        // if it had already been added while we were fetching it.
                                        if (!_stopProcessing)
                                        {
                                            object[] dependentKeys = valueToAdd.KeysIAmDependingOn;
                                            int Count = 0;
                                            for (int x = 0; x < dependentKeys.Length; x++)
                                            {
                                                if (_parent.Local_Contains(dependentKeys[x].ToString(), new OperationContext(OperationContextFieldName.OperationType, OperationContextOperationType.CacheOperation)))
                                                    Count++;
                                            }
                                            if (dependentKeys.Length == Count)
                                            {
                                                string keyToBeAdded = keysHavingdependency[i].ToString();
                                                keysHavingdependency.Remove(keyToBeAdded);

                                                OperationContext operationContext = new OperationContext(OperationContextFieldName.OperationType, OperationContextOperationType.CacheOperation);
                                                _parent.Local_Add(keyToBeAdded, valueToAdd, null, null, false, operationContext);
                                                _parent.Context.PerfStatsColl.IncrementStateTxfrPerSecStats();

                                            }
                                        }
                                        else
                                        {
                                            result = _parent.Local_Count();
                                            if (NCacheLog.IsInfoEnabled) NCacheLog.Info("ReplicatedServerCache.StateTransfer", "  state transfer was stopped by the parent.");
                                            return;
                                        }

                                    }
                                    catch (Exception)
                                    {
                                        // object already there so skip it.
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        result = _parent.Local_Count();
                        if (NCacheLog.IsInfoEnabled) NCacheLog.Info("ReplicatedServerCache.StateTransfer", "  state transfer was stopped by the parent.");
                        return;
                    }
                    result = _parent.Local_Count();

                    TransferMessages();

                }
                catch (Exception e)
                {
                    result = e;
                }
                finally
                {
                    _parent._stateTransferLatch.SetStatusBit((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED, (byte)ReplicatedStateTransferStatus.UNDER_STATE_TRANSFER);
                    _parent._internalCache.UnSetStateTransferKeyList();

                    SignalEndOfTransfer(result);
                    if (logEvent)
                    {
                        NCacheLog.CriticalInfo("ReplicatedServerCache.StateTransfer", "State transfer has ended");

                        if (result is System.Exception)
                        {
                            AppUtil.LogEvent(_cacheserver, "\"" + _parent._context.SerializationContext + "\"" + " has ended state transfer prematurely.", System.Diagnostics.EventLogEntryType.Error, EventCategories.Error, EventID.StateTransferError);
                        }
                        else
                        {
                            AppUtil.LogEvent(_cacheserver, "\"" + _parent._context.SerializationContext + "\"" + " has completed state transfer.", System.Diagnostics.EventLogEntryType.Information, EventCategories.Information, EventID.StateTransferStop);
                        }
                    }

                }
            }

            private void TransferMessages()
            {
                OrderedDictionary topicWiseMessagees = _parent.GetMessageList(0);

                if (_parent.NCacheLog.IsInfoEnabled)
                    _parent.NCacheLog.Info("StateTransferTask.TransferMessages", " message transfer started");

                if (topicWiseMessagees != null)
                {
                    foreach (DictionaryEntry topicWiseMessage in topicWiseMessagees)
                    {
                        ClusteredArrayList messageList = topicWiseMessage.Value as ClusteredArrayList;
                        if (!_stopProcessing)
                        {
                            if (_parent.NCacheLog.IsInfoEnabled)
                                _parent.NCacheLog.Info("StateTransferTask.TransferMessages", " topic : " + topicWiseMessage.Key + " messaeg count : " + messageList.Count);

                            foreach (string messageId in messageList)
                            {
                                if (!_stopProcessing)
                                {
                                    try
                                    {
                                        TransferrableMessage message = _parent.GetTransferrableMessage(topicWiseMessage.Key as string, messageId);

                                        if (message != null)
                                        {
                                            _parent.InternalCache.StoreTransferrableMessage(topicWiseMessage.Key as string, message);
                                        }
                                    }
                                    catch (Exception e)
                                    {
                                        _parent.NCacheLog.Error("StateTransferTask.TransferMessages", e.ToString());
                                    }

                                }
                                else
                                    break;

                            }
                        }
                        else
                            break;
                    }
                }

                if (_parent.NCacheLog.IsInfoEnabled)
                    _parent.NCacheLog.Info("StateTransferTask.TransferMessages", " message transfer ended");

            }
        }



        #endregion

        /// <summary>
        /// Fetch state from a cluster member. If the node is the coordinator there is
        /// no need to do the state transfer.
        /// </summary>
        protected void StartStateTransfer()
        {
            if (!Cluster.IsCoordinator)
            {
                /// Tell everyone that we are not fully-functional, i.e., initilizing.
                if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("ReplicatedCache.StartStateTransfer()", "Requesting state transfer " + LocalAddress);

                /// Start the initialization(state trasfer) task.
                if (_stateTransferTask == null) _stateTransferTask = new StateTransferTask(this);
                _context.AsyncProc.Enqueue(_stateTransferTask);

                /// Un-comment the following line to do it synchronously.
                /// object v = stateTransferTask.WaitUntilCompletion(-1);
            }
            else
            {
                _stateTransferLatch.SetStatusBit((byte)ReplicatedStateTransferStatus.STATE_TRANSFER_COMPLETED, (byte)ReplicatedStateTransferStatus.UNDER_STATE_TRANSFER);
                _allowEventRaiseLocally = true;
                _statusLatch.SetStatusBit(NodeStatus.Running, NodeStatus.Initializing);
                UpdateCacheStatistics();
                AnnouncePresence(true);
            }
        }

        /// <summary>
        /// Fetch state from a cluster member. If the node is the coordinator there is
        /// no need to do the state transfer.
        /// </summary>
        protected void EndStateTransfer(object result)
        {
            if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("ReplicatedCache.EndStateTransfer()", "State Txfr ended: " + result);
            if (result is Exception)
            {
                /// What to do? if we failed the state transfer?. Proabably we'll keep
                /// servicing in degraded mode? For the time being we don't!
            }

            /// Set the status to fully-functional (Running) and tell everyone about it.
            _statusLatch.SetStatusBit(NodeStatus.Running, NodeStatus.Initializing);
            Function fun = new Function((int)OpCodes.SignalEndOfStateTxfr, new object(), false);
            if (Cluster != null) Cluster.BroadcastToServers(fun, GroupRequest.GET_ALL, true);

            UpdateCacheStatistics();
            AnnouncePresence(true);
        }

        #endregion

        #region	/                 --- ICache ---           /

        #region	/                 --- Replicated ICache.Count ---           /

        /// <summary>
        /// returns the number of objects contained in the cache.
        /// </summary>
        public override long Count
        {
            get
            {
                if (_internalCache == null) throw new InvalidOperationException();
                long count = 0;
                if (IsInStateTransfer())
                {
                    count = Clustered_Count(GetDestInStateTransfer());
                }
                else
                {
                    count = Local_Count();
                }
                return count;
            }
        }

        public override long SessionCount
        {
            get
            {
                /// Wait until the object enters any running status
                _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

                if (_internalCache == null) throw new InvalidOperationException();
                /// If we are in state transfer, we return the count from some other
                /// functional node.
                if (_statusLatch.IsAnyBitsSet(NodeStatus.Initializing))
                {
                    return Clustered_SessionCount();
                }
                return Local_SessionCount();
            }
        }

        public override IPAddress ServerJustLeft
        {
            get { return _srvrJustLeft; }
            set { _srvrJustLeft = value; }
        }

        public override int ServersCount
        {
            get { return Cluster.ValidMembers.Count; }
        }

        public override bool IsServerNodeIp(Address clientAddress)
        {
            foreach (Address addr in Cluster.Servers)
            {
                if (addr.IpAddress.Equals(clientAddress.IpAddress))
                    return true;
            }
            return false;
        }



        /// <summary>
        /// Returns the count of local cache items only.
        /// </summary>
        /// <returns>count of items.</returns>
        private long Local_SessionCount()
        {
            if (_internalCache != null)
                return _internalCache.SessionCount;
            return 0;
        }


        private long Clustered_SessionCount()
        {
            Address targetNode = GetNextNode();
            return Clustered_SessionCount(targetNode);
        }


        /// <summary>
        /// Hanlde cluster-wide Get(key) requests.
        /// </summary>
        /// <param name="info">the object containing parameters for this operation.</param>
        /// <returns>object to be sent back to the requestor.</returns>
        private object handleCount()
        {
            try
            {
                return Local_Count();
            }
            catch (Exception)
            {
                if (_clusteredExceptions) throw;
            }
            return 0;
        }



        #endregion

        #region	/                 --- Replicated ICache.Contains ---           /

        /// <summary>
        /// Determines whether the cache contains a specific key.
        /// </summary>
        /// <param name="key">The key to locate in the cache.</param>
        /// <returns>true if the cache contains an element with the specified key; otherwise, false.</returns>
        public override bool Contains(object key, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.Cont", "");
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();

            bool contains = false;

            if (IsInStateTransfer())
            {
                contains = Safe_Clustered_Contains(key, operationContext) != null;
            }
            else
            {
                contains = Local_Contains(key, operationContext);
            }

            return contains;
        }


        /// <summary>
        /// Determines whether the cache contains the specified keys.
        /// </summary>
        /// <param name="keys">The keys to locate in the cache.</param>
        /// <returns>true if the cache contains an element with the specified key; otherwise, false.</returns>
        public override Hashtable Contains(object[] keys, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.ContBlk", "");
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();

            Hashtable result = new Hashtable();
            Hashtable tbl = Local_Contains(keys, operationContext);
            ArrayList list = null;

            if (tbl != null && tbl.Count > 0)
            {
                list = (ArrayList)tbl["items-found"];
            }

            /// If we failed and during state transfer, we check from some other 
            /// functional node as well.
            if (list != null && list.Count < keys.Length && (_statusLatch.IsAnyBitsSet(NodeStatus.Initializing)))
            {
                object[] rKeys = new object[keys.Length - list.Count];
                int i = 0;
                IEnumerator ir = keys.GetEnumerator();
                while (ir.MoveNext())
                {
                    object key = ir.Current;
                    if (list.Contains(key) == false)
                    {
                        rKeys[i] = key;
                        i++;
                    }
                }

                Hashtable clusterTbl = Clustered_Contains(rKeys, operationContext);
                ArrayList clusterList = null;

                if (clusterTbl != null && clusterTbl.Count > 0)
                {
                    clusterList = (ArrayList)clusterTbl["items-found"];
                }

                IEnumerator ie = clusterList.GetEnumerator();
                while (ie.MoveNext())
                {
                    list.Add(ie.Current);
                }
            }
            result["items-found"] = list;
            return result;
        }


        /// <summary>
        /// Determines whether the local cache contains a specific key.
        /// </summary>
        /// <param name="key">The key to locate in the cache.</param>
        /// <returns>true if the cache contains an element with the specified key; otherwise, false.</returns>
        private bool Local_Contains(object key, OperationContext operationContext)
        {
            bool retVal = false;
            if (_internalCache != null)
                retVal = _internalCache.Contains(key, operationContext);
            return retVal;
        }

        /// <summary>
        /// Determines whether the local cache contains the specified keys.
        /// </summary>
        /// <param name="keys">The keys to locate in the cache.</param>
        /// <returns>List of keys available in cache</returns>
        private Hashtable Local_Contains(object[] keys, OperationContext operationContext)
        {
            Hashtable tbl = new Hashtable();
            if (_internalCache != null)
            {
                tbl = _internalCache.Contains(keys, operationContext);
            }
            return tbl;
        }

        /// <summary>
        /// Determines whether the cluster contains a specific key.
        /// </summary>
        /// <param name="key">The key to locate in the cache.</param>
        /// <returns>address of the node that contains the specified key; otherwise, null.</returns>
        private Address Clustered_Contains(object key, OperationContext operationContext)
        {
            Address targetNode = GetNextNode();
            if (targetNode == null) return null;
            return Clustered_Contains(targetNode, key, operationContext);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        private Address Safe_Clustered_Contains(object key, OperationContext operationContext)
        {
            try
            {
                return Clustered_Contains(key, operationContext);
            }
            catch (Alachisoft.NGroups.SuspectedException e)
            {
                return Clustered_Contains(key, operationContext);
            }

            catch (Runtime.Exceptions.TimeoutException e)
            {
                return Clustered_Contains(key, operationContext);
            }
        }
        /// <summary>
        /// Determines whether the cluster contains the specified keys.
        /// </summary>
        /// <param name="key">The keys to locate in the cache.</param>
        /// <returns>list of keys and their addresses</returns>
        private Hashtable Clustered_Contains(object[] keys, OperationContext operationContext)
        {
            Address targetNode = GetNextNode();
            return Clustered_Contains(targetNode, keys, operationContext);
        }


        /// <summary>
        /// Hanlde cluster-wide Contain(key(s)) requests.
        /// </summary>
        /// <param name="info">the object containing parameters for this operation.</param>
        /// <returns>object to be sent back to the requestor.</returns>
        private object handleContains(object info)
        {
            try
            {
                OperationContext operationContext = null;
                if (info is object[])
                {
                    object[] objs = info as object[];
                    if (objs[0] is object[] && objs.Length > 1)
                        operationContext = objs[1] as OperationContext;

                    if (objs[0] is object[])
                    {
                        return Local_Contains((object[])objs[0], operationContext);
                    }
                    else
                    {
                        if (Local_Contains(objs[0], operationContext))
                            return true;
                    }

                }
                else
                {
                    if (Local_Contains(info, operationContext))
                        return true;
                }

            }
            catch (Exception)
            {
                if (_clusteredExceptions) throw;
            }
            return null;
        }

        #endregion


        #region/            ---Delete Query ---             /

        public override DeleteQueryResultSet DeleteQuery(string query, IDictionary values, bool notify, bool isUserOperation, ItemRemoveReason ir, OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();
            DeleteQueryResultSet result;

            if (Cluster.Servers.Count > 1)
            {
                result = Clustered_DeleteQuery(this.Servers, query, values, notify, isUserOperation, ir, operationContext);
            }
            else
            {
                result = Local_DeleteQuery(query, values, notify, isUserOperation, ir, operationContext);
            }

            return result;
        }

        private DeleteQueryResultSet Local_DeleteQuery(string query, IDictionary values, bool notify, bool isUserOperation, ItemRemoveReason ir, OperationContext operationContext)
        {
            if (_internalCache == null)
                throw new InvalidOperationException();

            return _internalCache.DeleteQuery(query, values, notify, isUserOperation, ir, operationContext);
        }

        private DeleteQueryResultSet handleDeleteQuery(object info)
        {
            DeleteQueryResultSet result = new DeleteQueryResultSet();
            if (_internalCache != null)
            {
                object[] data = (object[])info;
                return Local_DeleteQuery(data[0] as string, data[1] as IDictionary, (bool)data[2], (bool)data[3], (ItemRemoveReason)data[4], data[5] as OperationContext);
            }
            return result;
        }

        #endregion


        #region /         --- Replicated Cache Search ---          /

        protected override QueryResultSet Local_Search(string queryText, IDictionary values, OperationContext operationContext)
        {
            QueryResultSet resultSet = null;

            if (_internalCache == null)
                throw new InvalidOperationException();

            resultSet = _internalCache.Search(queryText, values, operationContext);

            return resultSet;
        }

        protected override QueryResultSet Local_SearchEntries(string queryText, IDictionary values, OperationContext operationContext)
        {
            QueryResultSet resultSet = null;

            if (_internalCache == null)
                throw new InvalidOperationException();

            resultSet = _internalCache.SearchEntries(queryText, values, operationContext);

            return resultSet;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="info"></param>
        /// <returns></returns>
        public QueryResultSet handleSearch(object info)
        {
            if (_internalCache != null)
            {
                ArrayList keyList = new ArrayList();
                object[] data = (object[])info;
                return _internalCache.Search(data[0] as string, data[1] as IDictionary, data[2] as OperationContext);
            }
            return null;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="info"></param>
        /// <returns></returns>
        public QueryResultSet handleSearchEntries(object info)
        {
            if (_internalCache != null)
            {
                object[] data = (object[])info;
                OperationContext operationContext = null;
                if (data.Length > 2)
                    operationContext = data[2] as OperationContext;

                return _internalCache.SearchEntries(data[0] as string, data[1] as IDictionary, operationContext);
            }
            return null;
        }

        public QueryResultSet Clustered_SearchEntries(string queryText, IDictionary values, OperationContext operationContext)
        {
            return Clustered_SearchEntries(Cluster.Coordinator, queryText, values, true, operationContext);
        }

        #endregion


        #region	/                 --- Replicated ICache.Clear ---           /

        /// <summary>
        /// Removes all entries from the store.
        /// </summary>
        /// <remarks>
        /// This method invokes <see cref="handleClear"/> on every node in the cluster, 
        /// which then fires OnCacheCleared locally. The <see cref="handleClear"/> method on the 
        /// coordinator will also trigger a cluster-wide notification to the clients.
        /// </remarks>
        public override void Clear(CallbackEntry cbEntry, DataSourceUpdateOptions updateOptions, OperationContext operationContext)
        {
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_statusLatch.IsAnyBitsSet(NodeStatus.Initializing))
            {
                if (_stateTransferTask != null) _stateTransferTask.StopProcessing();
                _statusLatch.WaitForAny(NodeStatus.Running);
            }

            if (_internalCache == null) throw new InvalidOperationException();

            string taskId = null;
            if (updateOptions == DataSourceUpdateOptions.WriteBehind)
                taskId = Cluster.LocalAddress.ToString() + ":" + NextSequence().ToString();

            if (Cluster.Servers.Count > 1)
                Clustered_Clear(cbEntry, taskId, false, operationContext);
            else
                handleClear(Cluster.LocalAddress, new object[] { cbEntry, taskId, operationContext });

            if (taskId != null)
            {
                SignalTaskState(Cluster.Servers, taskId, null, OpCode.Clear, WriteBehindAsyncProcessor.TaskState.Execute, operationContext);
            }
        }

        /// <summary>
        /// Clears the local cache only. 
        /// </summary>
        private void Local_Clear(Address src, CallbackEntry cbEntry, string taskId, OperationContext operationContext)
        {
            if (_internalCache != null)
            {
                ClearCQManager();
                _internalCache.Clear(null, DataSourceUpdateOptions.None, operationContext);
                if (taskId != null)
                {
                    CacheEntry entry = new CacheEntry(cbEntry, null, null);
                    if (operationContext.Contains(OperationContextFieldName.ReadThruProviderName))
                    {
                        entry.ProviderName = (string)operationContext.GetValueByField(OperationContextFieldName.ReadThruProviderName);
                    }
                    base.AddWriteBehindTask(src, null, entry, taskId, OpCode.Clear, operationContext);
                }

                UpdateCacheStatistics();
            }
        }


        /// <summary>
        /// Removes all entries from the store.
        /// </summary>
        /// <remarks>
        /// <see cref="handleClear"/> is called on every server node in a cluster. Therefore in order to ensure 
        /// that only one notification is generated for the cluster, only the coordinator node 
        /// replicates the notification to the clients. 
        /// <para>
        /// <b>Note: </b> The notification to the servers is handled in their <see cref="OnCacheCleared"/> callback.
        /// Therefore the servers generate the notifications locally. Only the clients need to be 
        /// notified.
        /// </para>
        /// </remarks>
        private object handleClear(Address src, object operand)
        {
            try
            {
                object[] args = operand as object[];
                CallbackEntry cbEntry = null;
                string taskId = null;
                OperationContext operationContext = null;

                if (args.Length > 0)
                    cbEntry = args[0] as CallbackEntry;
                if (args.Length > 1)
                    taskId = args[1] as string;
                if (args.Length > 2)
                    operationContext = args[2] as OperationContext;

                Local_Clear(src, cbEntry, taskId, operationContext);
                /// Only the coordinator replicates notification.
                RaiseCacheClearNotifier();
            }
            catch (Exception)
            {
                if (_clusteredExceptions) throw;
            }
            return null;
        }

        #endregion

        #region	/                 --- Replicated ICache.Get ---           /

        public override CacheEntry GetGroup(object key, string group, string subGroup, ref ulong version, ref object lockId, ref DateTime lockDate, LockExpiration lockExpiration, LockAccessType accessType, OperationContext operationContext)
        {
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();

            /// If we are in state transfer, we check locally first and then 
            /// to make sure we do a clustered call and fetch from some other 
            /// functional node.
            CacheEntry e = Local_GetGroup(key, group, subGroup, ref version, ref lockId, ref lockDate, lockExpiration, accessType, operationContext);
            if ((e == null) && (_statusLatch.IsAnyBitsSet(NodeStatus.Initializing)))
            {
                e = Clustered_GetGroup(key, group, subGroup, ref lockId, ref lockDate, accessType);
            }

            if (e == null)
            {
                _stats.BumpMissCount();
            }
            else
            {
                _stats.BumpHitCount();
                // update the indexes on other nodes in the cluster
                if ((e.ExpirationHint != null && e.ExpirationHint.IsVariant) /*|| (e.EvictionHint !=null && e.EvictionHint.IsVariant)*/ )
                {
                    UpdateIndices(key, true, operationContext);
                }
            }
            return e;
        }
        private Hashtable Local_GetGroup(object[] keys, string group, string subGroup, OperationContext operationContext)
        {
            if (_internalCache != null)
                return _internalCache.GetGroup(keys, group, subGroup, operationContext);

            return null;

        }
        /// <summary>
        /// Retrieve the object from the cache for the given group or sub group.
        /// </summary>
        private CacheEntry Local_GetGroup(object key, string group, string subGroup, ref ulong version, ref object lockId, ref DateTime lockDate, LockExpiration lockExpiration, LockAccessType accessType, OperationContext operationContext)
        {
            if (_internalCache != null)
                return _internalCache.GetGroup(key, group, subGroup, ref version, ref lockId, ref lockDate, lockExpiration, accessType, operationContext);

            return null;
        }


        /// <summary>
        /// Retrieve the objects from the cluster. Used during state trasfer, when the cache
        /// is loading state from other members of the cluster.
        /// </summary>
        /// <param name="keys">keys of the entries.</param>
        private CacheEntry Clustered_GetGroup(object key, string group, string subGroup, ref object lockId, ref DateTime lockDate, LockAccessType accessType)
        {
            /// Fetch address of a fully functional node. There should always be one
            /// fully functional node in the cluster (coordinator is alway fully-functional).
            Address targetNode = GetNextNode();
            if (targetNode != null)
                return Clustered_GetGroup(targetNode, group, subGroup, ref lockId, ref lockDate, accessType);
            return null;
        }

        /// <summary>
        /// Hanlde cluster-wide GetKeys(group, subGroup) requests.
        /// </summary>
        /// <param name="info">the object containing parameters for this operation.</param>
        /// <returns>object to be sent back to the requestor.</returns>
        private object handleGetKeys(object info)
        {
            try
            {
                object[] objs = (object[])info;
                string group = objs[0] as string;
                string subGroup = objs[1] as string;
                OperationContext operationContext = null;
                if (objs.Length > 2)
                    operationContext = objs[2] as OperationContext;

                return Local_GetGroupKeys(group, subGroup, operationContext);
            }
            catch (Exception)
            {
                if (_clusteredExceptions) throw;
            }
            return null;
        }

        protected override HashVector Local_GetTagData(string[] tags, TagComparisonType comparisonType, OperationContext operationContext)
        {
            HashVector retVal = null;

            if (_internalCache == null) throw new InvalidOperationException();

            retVal = _internalCache.GetTagData(tags, comparisonType, operationContext);
            return retVal;
        }

        protected override ICollection Local_GetTagKeys(string[] tags, TagComparisonType comparisonType, OperationContext operationContext)
        {


            if (_internalCache == null) throw new InvalidOperationException();

            return _internalCache.GetTagKeys(tags, comparisonType, operationContext);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="info"></param>
        /// <returns></returns>
        public IDictionary handleGetTag(object info)
        {
            if (_internalCache != null)
            {
                object[] data = (object[])info;

                OperationContext operationContext = null;
                if (data.Length > 2)
                    operationContext = data[2] as OperationContext;

                return _internalCache.GetTagData(data[0] as string[], (TagComparisonType)data[1], operationContext);
            }
            return null;
        }


        /// <summary>
        /// Retrieve the object from the cache. A string key is passed as parameter.
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <returns>cache entry.</returns>
        public override CacheEntry Get(object key, ref ulong version, ref object lockId, ref DateTime lockDate, LockExpiration lockExpiration, LockAccessType access, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.Get", "");
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();
            CacheEntry e = null;

            if (access == LockAccessType.COMPARE_VERSION || access == LockAccessType.MATCH_VERSION)
            {
                if (Cluster.Servers.Count > 1)
                {
                    e = Local_Get(key, false, operationContext);
                    if (e != null)
                    {

                        //if the item does not exist at the speicified version.
                        if (access == LockAccessType.MATCH_VERSION)
                        {
                            if (!e.CompareVersion(version))
                            {
                                e = null;
                            }
                        }

                        //if the item in the cache is not newer than the specified version.
                        else if (access == LockAccessType.COMPARE_VERSION)
                        {
                            if (!e.IsNewer(version))
                            {
                                e = null;
                                version = 0;
                            }
                            else
                            {

                                //set the latest version.
                                version = e.Version;
                            }
                        }
                    }
                }
                else
                {
                    e = Local_Get(key, ref version, ref lockId, ref lockDate, lockExpiration, access, operationContext);
                }
            }
            else if (access == LockAccessType.ACQUIRE || access == LockAccessType.DONT_ACQUIRE)
            {
                if (Cluster.Servers.Count > 1)
                {
                    e = Local_Get(key, false, operationContext);
                    if (e != null)
                    {
                        if (access == LockAccessType.DONT_ACQUIRE)
                        {
                            if (e.IsItemLocked() && !e.CompareLock(lockId))
                            {
                                lockId = e.LockId;
                                lockDate = e.LockDate;
                                e = null;
                            }
                            else
                            {
                                lockDate = e.LockDate; //compare lock does not set the lockdate internally.
                            }
                        }
                        else if (!e.IsLocked(ref lockId, ref lockDate))
                        {
                            if (Clustered_Lock(key, lockExpiration, ref lockId, ref lockDate, operationContext))
                            {
                                e = Local_Get(key, ref version, ref lockId, ref lockDate, lockExpiration, LockAccessType.IGNORE_LOCK, operationContext);
                            }
                            else
                            {
                                e = null;
                            }
                        }
                        else
                        {
                            //dont send the entry back if it is locked.
                            e = null;
                        }
                    }
                    else if (_statusLatch.IsAnyBitsSet(NodeStatus.Initializing))
                    {
                        if (access == LockAccessType.ACQUIRE)
                        {
                            if (Clustered_Lock(key, lockExpiration, ref lockId, ref lockDate, operationContext))
                            {
                                e = Clustered_Get(key, ref lockId, ref lockDate, access, operationContext);
                            }
                            else
                            {
                                e = null;
                            }
                        }
                        else
                        {
                            e = Clustered_Get(key, ref lockId, ref lockDate, access, operationContext);
                        }
                    }
                }
                else
                {
                    e = Local_Get(key, ref version, ref lockId, ref lockDate, lockExpiration, access, operationContext);
                }
            }
            else
            {
                if (access == LockAccessType.GET_VERSION) e = Local_Get(key, ref version, ref lockId, ref lockDate, lockExpiration, access, operationContext);
                else e = Local_Get(key, operationContext);

                if (e == null && _statusLatch.IsAnyBitsSet(NodeStatus.Initializing))
                {
                    e = Clustered_Get(key, ref lockId, ref lockDate, LockAccessType.IGNORE_LOCK, operationContext);
                }
            }

            if (e == null)
            {
                _stats.BumpMissCount();
            }
            else
            {
                _stats.BumpHitCount();
                // update the indexes on other nodes in the cluster
                if ((e.ExpirationHint != null && e.ExpirationHint.IsVariant) /*|| (e.EvictionHint !=null && e.EvictionHint.IsVariant)*/ )
                {
                    UpdateIndices(key, true, operationContext);
                    Local_Get(key, operationContext); //to update the index locally.
                }
            }
            return e;
        }

        /// <summary>
        /// Retrieve the objects from the cache. An array of keys is passed as parameter.
        /// </summary>
        /// <param name="keys">keys of the entries.</param>
        /// <returns>cache entries.</returns>
        public override IDictionary Get(object[] keys, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.GetBlk", "");

            if (_internalCache == null) throw new InvalidOperationException();

            HashVector table = null;

            if (IsInStateTransfer())
            {
                ArrayList dests = GetDestInStateTransfer();
                table = Clustered_Get(dests[0] as Address, keys, operationContext);
            }
            else
            {
                table = Local_Get(keys, operationContext);
            }

            if (table != null)
            {

                ClusteredArrayList updateIndiceKeyList = null;
                IDictionaryEnumerator ine = table.GetEnumerator();
                while (ine.MoveNext())
                {
                    if (operationContext.CancellationToken != null && operationContext.CancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(ExceptionsResource.OperationFailed);
                    CacheEntry e = (CacheEntry)ine.Value;
                    if (e == null)
                    {
                        _stats.BumpMissCount();
                    }
                    else
                    {
                        if (updateIndiceKeyList == null) updateIndiceKeyList = new ClusteredArrayList();
                        _stats.BumpHitCount();
                        // update the indexes on other nodes in the cluster
                        if ((e.ExpirationHint != null && e.ExpirationHint.IsVariant))
                        {
                            updateIndiceKeyList.Add(ine.Key);
                        }
                    }
                }
                if (updateIndiceKeyList != null && updateIndiceKeyList.Count > 0)
                {
                    UpdateIndices(updateIndiceKeyList.ToArray(), true, operationContext);
                }
            }

            return table;
        }



        /// <summary>
        /// Retrieve the list of keys fron the cache for the given group or sub group.
        /// </summary>
        protected override ArrayList Local_GetGroupKeys(string group, string subGroup, OperationContext operationContext)
        {
            if (_internalCache != null)
                return _internalCache.GetGroupKeys(group, subGroup, operationContext);

            return null;
        }

        protected override PollingResult Local_Poll(OperationContext context)
        {
            if (_internalCache != null)
                return _internalCache.Poll(context);
            return null;
        }

        protected override void Local_RegisterPollingNotification(short callbackId, OperationContext context)
        {
            if (_internalCache != null)
                _internalCache.RegisterPollingNotification(callbackId, context);
        }

        /// <summary>
        /// Retrieve the list of keys fron the cache for the given group or sub group.
        /// </summary>
        protected override HashVector Local_GetGroupData(string group, string subGroup, OperationContext operationContext)
        {
            if (_internalCache != null)
                return _internalCache.GetGroupData(group, subGroup, operationContext);

            return null;
        }



        /// <summary>
        /// Perform a dummy get operation on cluster that triggers index updates on all
        /// the nodes in the cluster, has no other particular purpose.
        /// </summary>
        /// <param name="key">key of the entry.</param>
        public override void UpdateIndices(object key, OperationContext operationContext)
        {
            try
            {
                if (Cluster.Servers != null && Cluster.Servers.Count > 1)
                {
                    Function func = new Function((int)OpCodes.UpdateIndice, new object[] { key, operationContext });
                    Cluster.Multicast(_otherServers, func, GroupRequest.GET_ALL, false);
                }
            }
            catch (Exception e)
            {
                Context.NCacheLog.Error("ReplicatedCache.UpdateIndices()", e.ToString());
            }
        }

        protected override void UpdateIndices(object key, bool async, OperationContext operationContext)
        {
            if (_asyncReplicator != null && Cluster.Servers.Count > 1)
            {
                try
                {
                    _asyncReplicator.AddUpdateIndexKey(key);
                }
                catch (Exception) { }
            }
        }

        protected void UpdateIndices(object[] keys, bool async, OperationContext operationContext)
        {
            if (_asyncReplicator != null && Cluster.Servers.Count > 1)
            {
                try
                {
                    foreach (object key in keys)
                    {
                        UpdateIndices(key, async, operationContext);
                    }
                }
                catch (Exception) { }
            }
        }

        private void RemoveUpdateIndexOperation(object key)
        {
            if (key != null)
            {
                try
                {

                    if (_asyncReplicator != null) _asyncReplicator.RemoveUpdateIndexKey(key);

                }
                catch (Exception) { }
            }
        }

        public override void ReplicateOperations(IList opCodes, IList info, IList userPayLoads, IList compilationInfo, ulong seqId, long viewId)
        {
            try
            {
                if (Cluster.Servers != null && Cluster.Servers.Count > 1)
                {
                    Function func = new Function((int)OpCodes.ReplicateOperations, new object[] { opCodes, info, compilationInfo }, false);
                    func.UserPayload = ((ClusteredArrayList)userPayLoads).ToArray();
                    Cluster.Multicast(_otherServers, func, GroupRequest.GET_ALL, false);
                }
            }
            catch (CacheException e)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new GeneralFailureException(e.Message, e);
            }
        }

        public object handleReplicateOperations(Address src, object info, Array userPayLoad)
        {
            try
            {
                IList objs = (IList)info;
                IList opCodes = (IList)objs[0];
                IList keys = (IList)objs[1];
                OperationContext operationContext = null;

                for (int i = 0; i < opCodes.Count; i++)
                {
                    switch ((int)opCodes[i])
                    {
                        case (int)OpCodes.UpdateIndice:
                            IList data = (IList)info;
                            if (data != null && data.Count > 3)
                                operationContext = data[3] as OperationContext;
                            return handleUpdateIndice(new object[] { keys[i], operationContext });
                    }
                }
            }
            catch (Exception e)
            {
                throw;
            }
            return null;
        }

        /// <summary>
        /// Retrieve the object from the cluster. Used during state trasfer, when the cache
        /// is loading state from other members of the cluster.
        /// </summary>
        /// <param name="key">key of the entry.</param>
        private CacheEntry Clustered_Get(object key, ref object lockId, ref DateTime lockDate, LockAccessType accessType, OperationContext operationContext)
        {
            /// Fetch address of a fully functional node. There should always be one
            /// fully functional node in the cluster (coordinator is alway fully-functional).
            Address targetNode = GetNextNode();
            if (targetNode != null)
                return Clustered_Get(targetNode, key, ref lockId, ref lockDate, accessType, operationContext);
            return null;
        }

        private CacheEntry Safe_Clustered_Get(object key, ref object lockId, ref DateTime lockDate, LockAccessType accessType, OperationContext operationContext)
        {
            try
            {
                return Clustered_Get(key, ref lockId, ref lockDate, accessType, operationContext);
            }
            catch (Alachisoft.NGroups.SuspectedException e)
            {
                //nTrace.error("ReplicatedServer.Safe_Clustered_Get", e.ToString());
                return Clustered_Get(key, ref lockId, ref lockDate, accessType, operationContext);
            }

            catch (Runtime.Exceptions.TimeoutException e)
            {
                //nTrace.error("ReplicatedServer.Safe_Clustered_Get", e.ToString());
                return Clustered_Get(key, ref lockId, ref lockDate, accessType, operationContext);
            }

        }


        /// <summary>
        /// Retrieve the object from the local cache only. 
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <returns>cache entry.</returns>
        private CacheEntry Local_Get(object key, OperationContext operationContext)
        {
            CacheEntry retVal = null;
            if (_internalCache != null)
                retVal = _internalCache.Get(key, operationContext);
            return retVal;
        }

        private CacheEntry Local_Get(object key, bool isUserOperation, OperationContext operationContext)
        {
            Object lockId = null;
            DateTime lockDate = DateTime.Now;
            ulong version = 0;

            CacheEntry retVal = null;
            if (_internalCache != null)
                retVal = _internalCache.Get(key, isUserOperation, ref version, ref lockId, ref lockDate, null, LockAccessType.IGNORE_LOCK, operationContext);

            return retVal;
        }

        /// <summary>
        /// Retrieve the object from the local cache only. 
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <returns>cache entry.</returns>
        private CacheEntry Local_Get(object key, ref ulong version, ref object lockId, ref DateTime lockDate, LockExpiration lockExpiration, LockAccessType access, OperationContext operationContext)
        {
            CacheEntry retVal = null;
            if (_internalCache != null)
                retVal = _internalCache.Get(key, ref version, ref lockId, ref lockDate, lockExpiration, access, operationContext);
            return retVal;
        }

        /// <summary>
        /// Retrieve the objects from the local cache only. 
        /// </summary>
        /// <param name="keys">keys of the entries.</param>
        /// <returns>cache entries.</returns>
        private HashVector Local_Get(object[] keys, OperationContext operationContext)
        {
            HashVector retVal = null;
            if (_internalCache != null)
                retVal = (HashVector)_internalCache.Get(keys, operationContext);
            return retVal;
        }

        /// <summary>
        /// Hanlde cluster-wide Get(key) requests.
        /// </summary>
        /// <param name="info">the object containing parameters for this operation.</param>
        /// <returns>object to be sent back to the requestor.</returns>
        private object handleGetGroup(object info)
        {
            try
            {
                object[] package = (object[])info;

                string group = (string)package[1];
                string subGroup = (string)package[2];
                object lockId = package[3];
                DateTime lockDate = (DateTime)package[4];
                LockAccessType accessType = (LockAccessType)package[5];
                ulong version = (ulong)package[6];
                LockExpiration lockExpiration = (LockExpiration)package[7];
                OperationContext operationContext = null;

                if (package.Length > 4)
                    operationContext = package[8] as OperationContext;
                else
                    operationContext = package[3] as OperationContext;

                if (package[0] is object[])
                {
                    object[] keys = (object[])package[0];

                    return Local_GetGroup(keys, group, subGroup, operationContext);
                }
                else
                {
                    OperationResponse opRes = new OperationResponse();
                    object[] response = new object[4];

                    object key = package[0];
                    CacheEntry entry = Local_GetGroup(key, group, subGroup, ref version, ref lockId, ref lockDate, lockExpiration, accessType, operationContext);
                    if (entry != null)
                    {
                        UserBinaryObject ubObject = (UserBinaryObject)(entry.Value is CallbackEntry ? ((CallbackEntry)entry.Value).Value : entry.Value);
                        opRes.UserPayload = ubObject.Data;
                        response[0] = entry.Clone();
                    }
                    response[1] = lockId;
                    response[2] = lockDate;
                    response[3] = version;
                    opRes.SerializablePayload = response;

                    return opRes;
                }
            }
            catch (Exception)
            {
                if (_clusteredExceptions) throw;
            }
            return null;
        }

        /// <summary>
        /// Hanlde cluster-wide Get(key) requests.
        /// </summary>
        /// <param name="info">the object containing parameters for this operation.</param>
        /// <returns>object to be sent back to the requestor.</returns>
        private object handleGet(object info)
        {
            try
            {
                object[] objs = info as object[];
                OperationContext operationContext = null;
                bool isUserOperation = true;
                if (objs.Length > 1)
                {
                    operationContext = objs[1] as OperationContext;
                }

                if (objs.Length > 2)
                {
                    isUserOperation = (bool)objs[2];
                }


                if (objs[0] is object[])
                {
                    return Local_Get(((object[])objs[0]), operationContext);
                }
                else
                {
                    CacheEntry entry = Local_Get(objs[0], isUserOperation, operationContext);
                    /* send value and entry seperaty*/
                    OperationResponse opRes = new OperationResponse();
                    if (entry != null)
                    {
                        if (_context.InMemoryDataFormat.Equals(DataFormat.Binary))
                        {
                            UserBinaryObject ubObject = (UserBinaryObject)(entry.Value is CallbackEntry ? ((CallbackEntry)entry.Value).Value : entry.Value);
                            opRes.UserPayload = ubObject.Data;
                            opRes.SerializablePayload = entry.CloneWithoutValue();
                        }
                        else
                        {
                            opRes.UserPayload = null;
                            opRes.SerializablePayload = entry.Clone();
                        }
                    }

                    return opRes;
                }
            }
            catch (Exception)
            {
                if (_clusteredExceptions) throw;
            }
            return null;
        }

        #endregion

        #region /                   --- ReplicatedServer.GetGroupInfo ---                      /

        /// <summary>
        /// Gets the data group info the item.
        /// </summary>
        /// <param name="key">Key of the item</param>
        /// <returns>Data group info of the item</returns>
        public override DataGrouping.GroupInfo GetGroupInfo(object key, OperationContext operationContext)
        {
            _statusLatch.WaitForAny(NodeStatus.Running | NodeStatus.Running);

            DataGrouping.GroupInfo info;
            info = Local_GetGroupInfo(key, operationContext);
            if (info == null && _statusLatch.Status.IsAnyBitSet(NodeStatus.Initializing))
            {
                ClusteredOperationResult result = Clustered_GetGroupInfo(key, operationContext);
                if (result != null)
                {
                    info = result.Result as DataGrouping.GroupInfo;
                }
            }

            return info;
        }

        /// <summary>
        /// Gets data group info the items
        /// </summary>
        /// <param name="keys">Keys of the items</param>
        /// <returns>IDictionary of the data grup info the items</returns>
        public override Hashtable GetGroupInfoBulk(object[] keys, OperationContext operationContext)
        {
            _statusLatch.WaitForAny(NodeStatus.Running);

            Hashtable infoTable;
            infoTable = Local_GetGroupInfoBulk(keys, operationContext);
            if ((infoTable == null || infoTable.Count < keys.Length) && _statusLatch.Status.IsAnyBitSet(NodeStatus.Initializing))
            {
                ICollection result = (Hashtable)Clustered_GetGroupInfoBulk(keys, operationContext);
                ClusteredOperationResult opRes;
                Hashtable infos;
                Hashtable max = null;
                if (result != null)
                {
                    IEnumerator ie = result.GetEnumerator();
                    while (ie.MoveNext())
                    {
                        opRes = (ClusteredOperationResult)ie.Current;
                        if (opRes != null)
                        {
                            infos = (Hashtable)opRes.Result;
                            if (max == null)
                                max = infos;
                            else if (infos.Count > max.Count)
                                max = infos;

                        }
                    }
                }
                infoTable = max;
            }
            return infoTable;
        }

        /// <summary>
        /// Gets the data group info the item.
        /// </summary>
        /// <param name="key">Key of the item</param>
        /// <returns>Data group info of the item</returns>
        private DataGrouping.GroupInfo Local_GetGroupInfo(Object key, OperationContext operationContext)
        {
            if (_internalCache != null)
                return _internalCache.GetGroupInfo(key, operationContext);
            return null;
        }

        /// <summary>
        /// Gets data group info the items
        /// </summary>
        /// <param name="keys">Keys of the items</param>
        /// <returns>IDictionary of the data grup info the items</returns>
        private Hashtable Local_GetGroupInfoBulk(Object[] keys, OperationContext operationContext)
        {
            if (_internalCache != null)
                return _internalCache.GetGroupInfoBulk(keys, operationContext);
            return null;
        }


        /// <summary>
        /// Handles the request for data group of the item. Only the coordinator
        /// of a subcluter will reply.
        /// </summary>
        /// <param name="info">Key(s) of the item(s)</param>
        /// <returns>Data group info of the item(s)</returns>
        private object handleGetGroupInfo(object info)
        {
            object result = null;
            OperationContext operationContext = null;
            object[] args = (object[])info;
            if (args.Length > 1)
                operationContext = args[1] as OperationContext;

            if (args[0] is object[])
                result = Local_GetGroupInfoBulk((object[])args[0], operationContext);
            else
                result = Local_GetGroupInfo(args[0], operationContext);

            return result;
        }

        #endregion

        #region	/                 --- Replicated ICache.Add ---           /


        /// <summary>
        /// Adds a pair of key and value to the cache. Throws an exception or reports error 
        /// if the specified key already exists in the cache.
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <param name="cacheEntry">the cache entry.</param>
        /// <returns>returns the result of operation.</returns>
        /// <remarks>
        /// This method invokes <see cref="handleAdd"/> on every server-node in the cluster. If the operation
        /// fails on any one node the whole operation is considered to have failed and is rolled-back.
        /// Moreover the node initiating this request (this method) also triggers a cluster-wide item-add 
        /// notificaion.
        /// </remarks>
        public override CacheAddResult Add(object key, CacheEntry cacheEntry, bool notify, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.Add_1", "");

            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();
            if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("Replicated.Add()", "Key = " + key);

            if (Local_Contains(key, operationContext)) return CacheAddResult.KeyExists;
            CacheAddResult result = CacheAddResult.Success;
            Exception thrown = null;

            string taskId = null;
            if (cacheEntry.Flag != null && cacheEntry.Flag.IsBitSet(BitSetConstants.WriteBehind))
                taskId = Cluster.LocalAddress.ToString() + ":" + NextSequence().ToString();

            try
            {
                if (Cluster.Servers.Count > 1)
                {
                    //CacheEntry remote = cacheEntry.FlattenedClone(_context.SerializationContext);
                    // Try to add to the local node and the cluster.
                    //nTrace.error("ReplicatedCache.add", "ADD ;" + key);
                    result = Clustered_Add(key, cacheEntry, taskId, operationContext);
                    if (result == CacheAddResult.KeyExists)
                    {
                        return result;
                    }
                }
                else
                    result = Local_Add(key, cacheEntry, Cluster.LocalAddress, taskId, true, operationContext);
            }
            catch (Exception e)
            {
                thrown = e;
            }

            if (result != CacheAddResult.Success || thrown != null)
            {
                bool timeout = false;
                bool rollback = true;
                //				nTrace.warn("Replicated.Add()", "rolling back, since result was " + result);
                try
                {
                    if (result == CacheAddResult.FullTimeout)
                    {
                        timeout = true;
                        rollback = false;
                    }
                    if (result == CacheAddResult.PartialTimeout)
                    {
                        timeout = true;
                    }
                    if (rollback)
                    {
                        if (Cluster.Servers.Count > 1)
                        {
                            Clustered_Remove(key, ItemRemoveReason.Removed, null, null, null, false, null, 0, LockAccessType.IGNORE_LOCK, operationContext);
                        }
                        else
                        {
                            Local_Remove(key, ItemRemoveReason.Removed, null, null, null, null, true, null, 0, LockAccessType.IGNORE_LOCK, operationContext);
                        }
                    }
                }
                catch (Exception) { }

                // muds : throw actual exception that was caused due to add operation.
                if (thrown != null) throw thrown;
                if (timeout)
                {

                    throw new Alachisoft.NCache.Common.Exceptions.TimeoutException("Operation timeout.");

                }
            }
            else
            {
                if (taskId != null)
                {
                    if (result == CacheAddResult.Success)
                    {
                        SignalTaskState(Cluster.Servers, taskId, cacheEntry.ProviderName, OpCode.Add, WriteBehindAsyncProcessor.TaskState.Execute, operationContext);
                    }
                    else
                    {
                        SignalTaskState(Cluster.Servers, taskId, cacheEntry.ProviderName, OpCode.Add, WriteBehindAsyncProcessor.TaskState.Remove, operationContext);
                    }
                }
            }

            return result;
        }


        /// <summary>
        /// Add ExpirationHint against the given key
        /// </summary>
        /// <param name="key"></param>
        /// <param name="eh"></param>
        /// <returns></returns>
        public override bool Add(object key, ExpirationHint eh, OperationContext operationContext)
        {
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();

            if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("Replicated.Add()", "Key = " + key);

            if (Local_Contains(key, operationContext) == false) return false;
            bool result = false;
            Exception thrown = null;
            try
            {
                if (Cluster.Servers.Count > 1)
                {
                    //CacheEntry remote = cacheEntry.FlattenedClone(_context.SerializationContext);
                    // Try to add to the local node and the cluster.
                    result = Clustered_Add(key, eh, operationContext);
                    if (result == false)
                    {
                        return result;
                    }
                }
                else
                    result = Local_Add(key, eh, operationContext);
            }
            catch (Exception e)
            {
                thrown = e;
            }

            return result;
        }

        /// <summary>
        /// Add ExpirationHint against the given key
        /// </summary>
        /// <param name="key"></param>
        /// <param name="eh"></param>
        /// <returns></returns>
        public override bool Add(object key, CacheSynchronization.CacheSyncDependency syncDependency, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.Add_2", "");
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();

            if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("Replicated.Add()", "Key = " + key);

            if (Local_Contains(key, operationContext) == false) return false;
            bool result = false;
            Exception thrown = null;
            try
            {
                if (Cluster.Servers.Count > 1)
                {
                    // Try to add to the local node and the cluster.
                    result = Clustered_Add(key, syncDependency, operationContext);
                    if (result == false)
                    {
                        return result;
                    }
                }
                else
                    result = Local_Add(key, syncDependency, operationContext);
            }
            catch (Exception e)
            {
                thrown = e;
            }

            return result;
        }





        /// <summary>
        /// Adds a pair of key and value to the cache. Throws an exception or reports error 
        /// if the specified key already exists in the cache.
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <param name="cacheEntry">the cache entry.</param>
        /// <returns>returns the result of operation.</returns>
        /// <remarks>
        /// This method invokes <see cref="handleAdd"/> on every server-node in the cluster. If the operation
        /// fails on any one node the whole operation is considered to have failed and is rolled-back.
        /// Moreover the node initiating this request (this method) also triggers a cluster-wide item-add 
        /// notificaion.
        /// </remarks>
        public override Hashtable Add(object[] keys, CacheEntry[] cacheEntries, bool notify, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.AddBlk", "");
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();

            object[] failedKeys = null;
            Hashtable addResult = new Hashtable();
            Hashtable tmp = new Hashtable();
            string providerName = null;

            Hashtable existingKeys = Local_Contains(keys, operationContext);
            ArrayList list = new ArrayList();
            if (existingKeys != null && existingKeys.Count > 0)
                list = existingKeys["items-found"] as ArrayList;

            int failCount = list.Count;
            if (failCount > 0)
            {
                IEnumerator ie = list.GetEnumerator();
                while (ie.MoveNext())
                {
                    addResult[ie.Current] = new OperationFailedException("The specified key already exists.");
                }

                // all keys failed, so return.
                if (failCount == keys.Length)
                {
                    return addResult;
                }

                object[] newKeys = new object[keys.Length - failCount];
                CacheEntry[] entries = new CacheEntry[keys.Length - failCount];

                int i = 0;
                int j = 0;

                IEnumerator im = keys.GetEnumerator();
                while (im.MoveNext())
                {
                    object key = im.Current;
                    if (!list.Contains(key))
                    {
                        newKeys[j] = key;
                        entries[j] = cacheEntries[i];
                        j++;
                    }
                    i++;
                }

                keys = newKeys;
                cacheEntries = entries;
            }

            string taskId = null;
            if (cacheEntries[0].Flag != null && cacheEntries[0].Flag.IsBitSet(BitSetConstants.WriteBehind))
                taskId = Cluster.LocalAddress.ToString() + ":" + NextSequence().ToString();

            Exception thrown = null;
            try
            {
                if (Cluster.Servers.Count > 1)
                {
                    // Try to add to the local node and the cluster.
                    tmp = Clustered_Add(keys, cacheEntries, taskId, operationContext);
                }
                else
                {
                    tmp = Local_Add(keys, cacheEntries, Cluster.LocalAddress, taskId, true, operationContext);
                }
            }
            catch (Exception inner)
            {
                Context.NCacheLog.Error("Replicated.Clustered_Add()", inner.ToString());
                for (int i = 0; i < keys.Length; i++)
                {
                    tmp[keys[i]] = new OperationFailedException(inner.Message, inner);
                }
                thrown = inner;
            }


            if (thrown != null)
            {
                if (Cluster.Servers.Count > 1)
                {
                    Clustered_Remove(keys, ItemRemoveReason.Removed, operationContext);
                }
                else
                {
                    Local_Remove(keys, ItemRemoveReason.Removed, null, null, null, null, false, operationContext);
                }
            }
            else
            {
                failCount = 0;
                ArrayList failKeys = new ArrayList();
                IDictionaryEnumerator ide = tmp.GetEnumerator();
                Hashtable writeBehindTable = new Hashtable();

                while (ide.MoveNext())
                {
                    if (ide.Value is CacheAddResult)
                    {
                        CacheAddResult res = (CacheAddResult)ide.Value;
                        switch (res)
                        {
                            case CacheAddResult.Failure:
                            case CacheAddResult.KeyExists:
                            case CacheAddResult.NeedsEviction:
                                failCount++;
                                failKeys.Add(ide.Key);
                                addResult[ide.Key] = ide.Value;
                                break;

                            case CacheAddResult.Success:
                                addResult[ide.Key] = ide.Value;
                                writeBehindTable.Add(ide.Key, null);
                                break;
                        }
                    }
                    else //it means value is exception
                    {
                        failCount++;
                        failKeys.Add(ide.Key);
                        addResult[ide.Key] = ide.Value;
                    }
                }

                if (failCount > 0)
                {
                    object[] keysToRemove = new object[failCount];
                    failKeys.CopyTo(keysToRemove, 0);

                    if (Cluster.Servers.Count > 1)
                    {
                        Clustered_Remove(keysToRemove, ItemRemoveReason.Removed, null, null, null, false, operationContext);
                    }
                    else
                    {
                        Local_Remove(keysToRemove, ItemRemoveReason.Removed, null, null, null, null, false, operationContext);
                    }
                }
                if (taskId != null)
                {
                    if (writeBehindTable.Count > 0)
                    {
                        SignalBulkTaskState(Cluster.Servers, taskId, cacheEntries[0].ProviderName, addResult, OpCode.Add, WriteBehindAsyncProcessor.TaskState.Execute);
                    }
                    else
                    {
                        SignalTaskState(Cluster.Servers, taskId, cacheEntries[0].ProviderName, OpCode.Add, WriteBehindAsyncProcessor.TaskState.Remove, operationContext);
                    }
                }
            }

            return addResult;
        }



        /// <summary>
        /// Add the object to the local cache. 
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <returns>cache entry.</returns>
        /// <remarks>
        /// Does an Add locally, however the generated notification is discarded, since it is specially handled
        /// in <see cref="Add"/>.
        /// </remarks>
        private CacheAddResult Local_Add(object key, CacheEntry cacheEntry, Address src, string taskId, bool notify, OperationContext operationContext)
        {
            CacheAddResult retVal = CacheAddResult.Failure;
            CacheEntry clone = null;
            if (taskId != null && cacheEntry.HasQueryInfo)
                clone = (CacheEntry)cacheEntry.Clone();
            else
                clone = cacheEntry;

            if (_internalCache != null)
            {
                object[] keys = null;
                try
                {
                    #region -- PART I -- Cascading Dependency Operation
                    keys = cacheEntry.KeysIAmDependingOn;
                    if (keys != null)
                    {
                        Hashtable goodKeysTable = _internalCache.Contains(keys, operationContext);

                        if (!goodKeysTable.ContainsKey("items-found"))
                            throw new OperationFailedException("One of the dependency keys does not exist.");

                        if (goodKeysTable["items-found"] == null)
                            throw new OperationFailedException("One of the dependency keys does not exist.");

                        if (goodKeysTable["items-found"] == null || (((ArrayList)goodKeysTable["items-found"]).Count != keys.Length))
                            throw new OperationFailedException("One of the dependency keys does not exist.");

                    }
                    #endregion

                    retVal = _internalCache.Add(key, cacheEntry, notify, operationContext);
                }
                catch (Exception e)
                {
                    throw;
                }

                #region -- PART II -- Cascading Dependency Operation
                if (retVal == CacheAddResult.Success && keys != null)
                {
                    Hashtable table = new Hashtable();
                    for (int i = 0; i < keys.Length; i++)
                    {
                        if (table[keys[i]] == null)
                            table.Add(keys[i], new ArrayList());
                        ((ArrayList)table[keys[i]]).Add(key);
                    }
                    try
                    {

                        //Fix for NCache Bug4981
                        object generateQueryInfo = operationContext.GetValueByField(OperationContextFieldName.GenerateQueryInfo);
                        if (generateQueryInfo == null)
                        {
                            operationContext.Add(OperationContextFieldName.GenerateQueryInfo, true);
                        }
                        table = _internalCache.AddDepKeyList(table, operationContext);

                        if (generateQueryInfo == null)
                        {
                            operationContext.RemoveValueByField(OperationContextFieldName.GenerateQueryInfo);
                        }
                    }
                    catch (Exception e)
                    {
                        throw e;
                    }

                    if (table != null)
                    {
                        IDictionaryEnumerator en = table.GetEnumerator();
                        while (en.MoveNext())
                        {
                            if (en.Value is bool && !((bool)en.Value))
                            {
                                throw new OperationFailedException("One of the dependency keys does not exist.");
                            }
                        }
                    }
                }
                #endregion
            }

            if (retVal == CacheAddResult.Success && taskId != null)
            {
                AddWriteBehindTask(src, key as string, clone, taskId, OpCode.Add, operationContext);
            }

            return retVal;
        }


        /// <summary>
        /// Add the ExpirationHint against the given key
        /// </summary>
        /// <param name="key"></param>
        /// <param name="hint"></param>
        /// <returns></returns>
        private bool Local_Add(object key, ExpirationHint hint, OperationContext operationContext)
        {
            bool retVal = false;
            if (_internalCache != null)
            {
                #region -- PART I -- Cascading Dependency Operation
                CacheEntry cacheEntry = new CacheEntry();
                cacheEntry.ExpirationHint = hint;
                object[] keys = cacheEntry.KeysIAmDependingOn;

                if (keys != null)
                {
                    Hashtable goodKeysTable = Contains(keys, operationContext);

                    if (!goodKeysTable.ContainsKey("items-found"))
                        throw new OperationFailedException("One of the dependency keys does not exist.");

                    if (goodKeysTable["items-found"] == null)
                        throw new OperationFailedException("One of the dependency keys does not exist.");

                    if (((ArrayList)goodKeysTable["items-found"]).Count != keys.Length)
                        throw new OperationFailedException("One of the dependency keys does not exist.");

                }
                #endregion

                try
                {
                    retVal = _internalCache.Add(key, hint, operationContext);
                }
                catch (Exception e)
                {
                    throw e;
                }

                #region -- PART II -- Cascading Dependency Operation
                if (retVal && keys != null)
                {
                    Hashtable table = new Hashtable();
                    for (int i = 0; i < keys.Length; i++)
                    {
                        if (table[keys[i]] == null)
                            table.Add(keys[i], new ArrayList());
                        ((ArrayList)table[keys[i]]).Add(key);
                    }
                    try
                    {
                        table = _internalCache.AddDepKeyList(table, operationContext);
                    }
                    catch (Exception e)
                    {
                        throw e;
                    }

                    if (table != null)
                    {
                        IDictionaryEnumerator en = table.GetEnumerator();
                        while (en.MoveNext())
                        {
                            if (en.Value is bool && !((bool)en.Value))
                            {
                                throw new OperationFailedException("One of the dependency keys does not exist.");
                            }
                        }
                    }
                }
                #endregion

            }
            return retVal;
        }

        /// <summary>
        /// Add the ExpirationHint against the given key
        /// </summary>
        /// <param name="key"></param>
        /// <param name="hint"></param>
        /// <returns></returns>
        private bool Local_Add(object key, CacheSynchronization.CacheSyncDependency syncDependency, OperationContext operationContext)
        {
            bool retVal = false;
            if (_internalCache != null)
            {
                retVal = _internalCache.Add(key, syncDependency, operationContext);
            }
            return retVal;
        }

        /// <summary>
        /// Add the object to the local cache. 
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <returns>cache entry.</returns>
        /// <remarks>
        /// Does an Add locally, however the generated notification is discarded, since it is specially handled
        /// in <see cref="Add"/>.
        /// </remarks>
        private Hashtable Local_Add(object[] keys, CacheEntry[] cacheEntries, Address src, string taskId, bool notify, OperationContext operationContext)
        {
            Hashtable table = new Hashtable();

            ArrayList goodKeysList = new ArrayList();
            ArrayList goodEntriesList = new ArrayList();

            ArrayList badKeysList = new ArrayList();

            CacheEntry[] clone = null;

            if (taskId != null)
            {
                clone = new CacheEntry[cacheEntries.Length];
                for (int i = 0; i < cacheEntries.Length; i++)
                {
                    if (cacheEntries[i].HasQueryInfo)
                        clone[i] = (CacheEntry)cacheEntries[i].Clone();
                    else
                        clone[i] = cacheEntries[i];
                }
            }

            if (_internalCache != null)
            {
                #region -- PART I -- Cascading Dependency Operation
                for (int i = 0; i < cacheEntries.Length; i++)
                {
                    object[] tempKeys = cacheEntries[i].KeysIAmDependingOn;
                    if (tempKeys != null)
                    {
                        Hashtable goodKeysTable = Contains(tempKeys, operationContext);

                        if (goodKeysTable.ContainsKey("items-found") && goodKeysTable["items-found"] != null && tempKeys.Length == ((ArrayList)goodKeysTable["items-found"]).Count)
                        {
                            goodKeysList.Add(keys[i]);
                            goodEntriesList.Add(cacheEntries[i]);
                        }
                        else
                        {
                            badKeysList.Add(keys[i]);
                        }
                    }
                    else
                    {
                        goodKeysList.Add(keys[i]);
                        goodEntriesList.Add(cacheEntries[i]);
                    }
                }
                #endregion

                CacheEntry[] goodEntries = new CacheEntry[goodEntriesList.Count];
                goodEntriesList.CopyTo(goodEntries);

                table = _internalCache.Add(goodKeysList.ToArray(), goodEntries, notify, operationContext);

                #region --Part II-- Cascading Dependency Operations
                for (int i = 0; i < goodKeysList.Count; i++)
                {
                    CacheAddResult retVal = (CacheAddResult)table[goodKeysList[i]];
                    object[] depKeys = goodEntries[i].KeysIAmDependingOn;
                    if (retVal == CacheAddResult.Success && depKeys != null)
                    {
                        Hashtable depTable = new Hashtable();
                        for (int k = 0; k < depKeys.Length; k++)
                        {
                            if (depTable[depKeys[k]] == null)
                                depTable.Add(depKeys[k], new ArrayList());
                            ((ArrayList)depTable[depKeys[k]]).Add(goodKeysList[i]);
                        }

                        Hashtable tempTable = null;
                        try
                        {
                            object generateQueryInfo = operationContext.GetValueByField(OperationContextFieldName.GenerateQueryInfo);
                            if (generateQueryInfo == null)
                            {
                                operationContext.Add(OperationContextFieldName.GenerateQueryInfo, true);
                            }

                            tempTable = _internalCache.AddDepKeyList(depTable, operationContext);

                            if (generateQueryInfo == null)
                            {
                                operationContext.RemoveValueByField(OperationContextFieldName.GenerateQueryInfo);
                            }

                        }
                        catch (Exception e)
                        {
                            throw e;
                        }

                        if (tempTable != null)
                        {
                            IDictionaryEnumerator en = tempTable.GetEnumerator();
                            while (en.MoveNext())
                            {
                                if (en.Value is bool && !((bool)en.Value))
                                {
                                    table[goodKeysList[i]] = new OperationFailedException("Error setting up key dependency.");
                                }
                            }
                        }
                    }
                }

                for (int i = 0; i < badKeysList.Count; i++)
                {
                    table.Add(badKeysList[i], new OperationFailedException("One of the dependency keys does not exist."));
                }
                #endregion

            }

            if (taskId != null && table != null)
            {
                Hashtable successfullEntries = new Hashtable();
                for (int i = 0; i < keys.Length; i++)
                {
                    object value = table[keys[i]];
                    if (value is CacheAddResult && (CacheAddResult)value == CacheAddResult.Success)
                    {
                        successfullEntries.Add(keys[i], clone[i]);
                    }
                }

                if (successfullEntries.Count > 0)
                {
                    AddWriteBehindTask(src, successfullEntries, null, taskId, OpCode.Add);
                }
            }

            return table;
        }

        /// <summary>
        /// Add the object to the cluster. 
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <returns>cache entry.</returns>
        /// <remarks>
        /// This method invokes <see cref="handleAdd"/> on every server-node in the cluster. If the operation
        /// fails on any one node the whole operation is considered to have failed and is rolled-back.
        /// </remarks>
        private CacheAddResult Clustered_Add(object key, CacheEntry cacheEntry, string taskId, OperationContext operationContext)
        {
            return Clustered_Add(Cluster.Servers, key, cacheEntry, taskId, operationContext);
        }

        /// <summary>
        /// Add the ExpirationHint to the cluster. 
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <returns>cache entry.</returns>
        /// <remarks>
        /// This method invokes <see cref="handleAddHint"/> on every server-node in the cluster. If the operation
        /// fails on any one node the whole operation is considered to have failed and is rolled-back.
        /// </remarks>
        private bool Clustered_Add(object key, ExpirationHint eh, OperationContext operationContext)
        {
            return Clustered_Add(Cluster.Servers, key, eh, operationContext);
        }

        /// <summary>
        /// Add the ExpirationHint to the cluster. 
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <returns>cache entry.</returns>
        /// <remarks>
        /// This method invokes <see cref="handleAddHint"/> on every server-node in the cluster. If the operation
        /// fails on any one node the whole operation is considered to have failed and is rolled-back.
        /// </remarks>
        private bool Clustered_Add(object key, CacheSynchronization.CacheSyncDependency syncDependency, OperationContext operationContext)
        {
            return Clustered_Add(Cluster.Servers, key, syncDependency, operationContext);
        }

        /// <summary>
        /// Add the object to the cluster. 
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <returns>cache entry.</returns>
        /// <remarks>
        /// This method invokes <see cref="handleAdd"/> on every server-node in the cluster. If the operation
        /// fails on any one node the whole operation is considered to have failed and is rolled-back.
        /// </remarks>
        private Hashtable Clustered_Add(object[] keys, CacheEntry[] cacheEntries, string taskId, OperationContext operationContext)
        {
            return Clustered_Add(Cluster.Servers, keys, cacheEntries, taskId, operationContext);
        }

        private object handleUpdateIndice(object key)
        {
            //we do a get operation on the item so that its relevent index in epxiration/eviction
            //is updated.
            handleGet(key);
            return null;
        }

        /// <summary>
        /// Hanlde cluster-wide Add(key) requests.
        /// </summary>
        /// <param name="info">the object containing parameters for this operation.</param>
        /// <returns>object to be sent back to the requestor.</returns>
        private object handleAdd(Address src, object info, Array userPayload)
        {
            try
            {
                object[] objs = (object[])info;

                string taskId = null;
                OperationContext oc = null;

                if (objs.Length > 2)
                    taskId = objs[2] != null ? objs[2] as string : null;

                if (objs.Length > 3)
                    oc = objs[3] as OperationContext;

                if (objs[0] is object[])
                {
                    object[] keys = (object[])objs[0];
                    CacheEntry[] entries = objs[1] as CacheEntry[];
                    if (objs.Length > 2)
                        taskId = objs[2] != null ? objs[2] as string : null;
                    Hashtable results = Local_Add(keys, entries, src, taskId, true, oc);

                    return results;
                }
                else
                {
                    CacheAddResult result = CacheAddResult.Failure;
                    object key = objs[0];
                    CacheEntry e = objs[1] as CacheEntry;
                    if (userPayload != null)
                    {
                        e.Value = userPayload;
                    }
                    {
                        result = Local_Add(key, e, src, taskId, true, oc);
                    }

                    return result;
                }
            }
            catch (Exception e)
            {
                if (_clusteredExceptions) throw;
            }
            return CacheAddResult.Failure;
        }


        /// <summary>
        /// Hanlde cluster-wide AddHint(key) requests.
        /// </summary>
        /// <param name="info">the object containing parameters for this operation.</param>
        /// <returns>object to be sent back to the requestor.</returns>
        private object handleAddHint(Address src, object info)
        {
            try
            {
                object[] objs = (object[])info;
                object key = objs[0];
                ExpirationHint eh = objs[1] as ExpirationHint;
                OperationContext oc = objs[2] as OperationContext;

                if (src.CompareTo(LocalAddress) != 0)
                {
                    if (eh != null && !eh.IsRoutable)
                    {
                        NodeExpiration expiry = new NodeExpiration(src);
                        return Local_Add(key, expiry, oc);
                    }
                }
                return Local_Add(key, eh, oc);
            }
            catch (Exception e)
            {
                if (_clusteredExceptions) throw;
            }
            return false;
        }

        /// <summary>
        /// Hanlde cluster-wide AddHint(key) requests.
        /// </summary>
        /// <param name="info">the object containing parameters for this operation.</param>
        /// <returns>object to be sent back to the requestor.</returns>
        private object handleAddSyncDependency(Address src, object info, object operandInfo)
        {
            try
            {
                object[] objs = (object[])info;
                object key = objs[0];
                CacheSynchronization.CacheSyncDependency syncDependency = objs[1] as CacheSynchronization.CacheSyncDependency;

                object[] operandObjs = (object[])operandInfo;
                OperationContext oc = operandObjs[2] as OperationContext;

                if (src.CompareTo(LocalAddress) != 0)
                {
                    if (syncDependency != null)
                    {
                        NodeExpiration expiry = new NodeExpiration(src);
                        return Local_Add(key, expiry, oc);
                    }
                }
                return Local_Add(key, syncDependency, oc);
            }
            catch (Exception e)
            {
                if (_clusteredExceptions) throw;
            }
            return false;
        }

        #endregion

        #region	/                 --- Replicated ICache.Insert ---           /

        /// <summary>
        /// Adds a pair of key and value to the cache. If the specified key already exists 
        /// in the cache; it is updated, otherwise a new item is added to the cache.
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <param name="cacheEntry">the cache entry.</param>
        /// <returns>returns the result of operation.</returns>
        /// <remarks>
        /// This method invokes <see cref="handleInsert"/> on every server-node in the cluster. If the operation
        /// fails on any one node the whole operation is considered to have failed and is rolled-back.
        /// Moreover the node initiating this request (this method) also triggers a cluster-wide 
        /// item-update notificaion.
        /// </remarks>
        public override CacheInsResultWithEntry Insert(object key, CacheEntry cacheEntry, bool notify, object lockId, ulong version, LockAccessType accessType, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.Insert", "");
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();


            if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("Replicated.Insert()", "Key = " + key);

            CacheEntry pEntry = null;
            CacheInsResultWithEntry retVal = new CacheInsResultWithEntry();
            Exception thrown = null;
            bool groupMisMatchException = false;

            string taskId = null;
            if (cacheEntry.Flag != null && cacheEntry.Flag.IsBitSet(BitSetConstants.WriteBehind))
                taskId = Cluster.LocalAddress.ToString() + ":" + NextSequence().ToString();

            try
            {
                // We get the actual item to raise custom call back with the item.
                //Get internally catters for the state-transfer scenarios. 
                pEntry = Get(key, operationContext);
                retVal.Entry = pEntry;

                if (pEntry != null)
                {
                    if (accessType != LockAccessType.IGNORE_LOCK)
                    {
                        if (accessType == LockAccessType.COMPARE_VERSION)
                        {
                            if (!pEntry.CompareVersion(version))
                            {
                                retVal.Entry = null;
                                retVal.Result = CacheInsResult.VersionMismatch;
                                return retVal;
                            }
                            accessType = LockAccessType.IGNORE_LOCK;
                        }
                        else
                        {
                            if (pEntry.IsItemLocked() && !pEntry.CompareLock(lockId))
                            {
                                //throw new LockingException("Item is locked.");
                                retVal.Entry = null;
                                retVal.Result = CacheInsResult.ItemLocked;
                                return retVal;
                            }
                        }
                    }

                    DataGrouping.GroupInfo oldInfo = pEntry.GroupInfo;
                    DataGrouping.GroupInfo newInfo = cacheEntry.GroupInfo;

                    if (!CacheHelper.CheckDataGroupsCompatibility(newInfo, oldInfo))
                    {
                        groupMisMatchException = true;
                        throw new Exception("Data group of the inserted item does not match the existing item's data group.");
                    }
                }
                if (Cluster.Servers.Count > 1)
                {
                    // Try to add to the local node and the cluster.
                    retVal = Clustered_Insert(key, cacheEntry, taskId, lockId, accessType, operationContext);


                    //if coordinator has sent the previous entry, use that one...
                    //otherwise send back the localy got previous entry...
                    if (retVal.Entry != null)
                        pEntry = retVal.Entry;
                    else
                        retVal.Entry = pEntry;
                }
                else
                    retVal = Local_Insert(key, cacheEntry, Cluster.LocalAddress, taskId, true, lockId, version, accessType, operationContext);
            }
            catch (Exception e)
            {
                thrown = e;
            }

            // Try to insert to the local node and the cluster.
            if ((retVal.Result == CacheInsResult.NeedsEviction || retVal.Result == CacheInsResult.Failure || retVal.Result == CacheInsResult.FullTimeout || retVal.Result == CacheInsResult.PartialTimeout) || thrown != null)
            {
                Context.NCacheLog.Warn("Replicated.Insert()", "rolling back, since result was " + retVal.Result);
                bool rollback = true;
                bool timeout = false;
                if (groupMisMatchException)
                    rollback = false;
                if (retVal.Result == CacheInsResult.PartialTimeout)
                {
                    timeout = true;
                }
                else if (retVal.Result == CacheInsResult.FullTimeout)
                {
                    timeout = true;
                    rollback = false;
                }

                if (rollback)
                {
                    Thread.Sleep(2000);
                    /// failed on the cluster, so remove locally as well.
                    if (Cluster.Servers.Count > 1)
                    {
                        Clustered_Remove(key, ItemRemoveReason.Removed, null, null, null, false, null, 0, LockAccessType.IGNORE_LOCK, operationContext);
                    }
                }
                if (timeout)
                {

                    throw new Runtime.Exceptions.TimeoutException("Operation timeout.");


                }
                if (thrown != null) throw thrown;
            }
            if (notify && retVal.Result == CacheInsResult.SuccessOverwrite)
            {
                RemoveUpdateIndexOperation(key);
            }

            if (taskId != null)
            {
                if (retVal.Result == CacheInsResult.Success || retVal.Result == CacheInsResult.SuccessOverwrite)
                {
                    SignalTaskState(Cluster.Servers, taskId, cacheEntry.ProviderName, OpCode.Update, WriteBehindAsyncProcessor.TaskState.Execute, operationContext);
                }
                else
                {
                    SignalTaskState(Cluster.Servers, taskId, cacheEntry.ProviderName, OpCode.Update, WriteBehindAsyncProcessor.TaskState.Remove, operationContext);
                }
            }
            return retVal;
        }

        /// <summary>
        /// Adds key and value pairs to the cache. If any of the specified key already exists 
        /// in the cache; it is updated, otherwise a new item is added to the cache.
        /// </summary>
        /// <param name="keys">keys of the entries.</param>
        /// <param name="cacheEntries">the cache entries.</param>
        /// <returns>the list of keys that failed to add.</returns>
        /// <remarks>
        /// This method invokes <see cref="handleInsert"/> on every server-node in the cluster. If the operation
        /// fails on any one node the whole operation is considered to have failed and is rolled-back.
        /// Moreover the node initiating this request (this method) also triggers a cluster-wide 
        /// item-update notificaion.
        /// </remarks>
        public override Hashtable Insert(object[] keys, CacheEntry[] cacheEntries, bool notify, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.InsertBlk", "");
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();

            Hashtable insertResults = null;

            string taskId = null;
            if (cacheEntries[0].Flag != null && cacheEntries[0].Flag.IsBitSet(BitSetConstants.WriteBehind))
                taskId = Cluster.LocalAddress.ToString() + ":" + NextSequence().ToString();

            if (Cluster.Servers.Count > 1)
                insertResults = Clustered_Insert(keys, cacheEntries, taskId, notify, operationContext);
            else
            {
                //                      
                HashVector pEntries = null;

                pEntries = (HashVector)Get(keys, operationContext); //dont remove , But why create an extra copy when you dont even use it?!

                Hashtable existingItems;
                Hashtable jointTable = new Hashtable();
                Hashtable failedTable = new Hashtable();
                Hashtable insertable = new Hashtable();
                ArrayList inserted = new ArrayList();
                ArrayList added = new ArrayList();

                ClusteredOperationResult opResult;
                object[] validKeys;
                object[] failedKeys;
                CacheEntry[] validEnteries;
                int index = 0;
                object key;
                Address node = null;

                for (int i = 0; i < keys.Length; i++)
                {
                    jointTable.Add(keys[i], cacheEntries[i]);
                }

                existingItems = Local_GetGroupInfoBulk(keys, operationContext);
                if (existingItems != null && existingItems.Count > 0)
                {
                    insertable = CacheHelper.GetInsertableItems(existingItems, jointTable);
                    IDictionaryEnumerator ide;
                    if (insertable != null)
                    {
                        index = 0;
                        validKeys = new object[insertable.Count];
                        validEnteries = new CacheEntry[insertable.Count];
                        ide = insertable.GetEnumerator();
                        while (ide.MoveNext())
                        {
                            key = ide.Key;
                            validKeys[index] = key;
                            validEnteries[index] = (CacheEntry)ide.Value;
                            jointTable.Remove(key);
                            inserted.Add(key);
                            index += 1;
                        }

                        if (validKeys.Length > 0)
                        {
                            try
                            {
                                insertResults = Local_Insert(validKeys, validEnteries, Cluster.LocalAddress, taskId, true, operationContext);
                            }
                            catch (Exception e)
                            {
                                Context.NCacheLog.Error("ReplicatedServerCache.Insert(Keys)", e.ToString());
                                for (int i = 0; i < validKeys.Length; i++)
                                {
                                    failedTable.Add(validKeys[i], e);
                                    inserted.Remove(validKeys[i]);
                                }
                                Clustered_Remove(validKeys, ItemRemoveReason.Removed, null, null, null, false, operationContext);
                            }

                            #region modifiedcode
                            if (insertResults != null)
                            {
                                IDictionaryEnumerator ie = insertResults.GetEnumerator();
                                while (ie.MoveNext())
                                {
                                    if (ie.Value is CacheInsResultWithEntry)
                                    {
                                        CacheInsResultWithEntry res = ie.Value as CacheInsResultWithEntry;
                                        switch (res.Result)
                                        {
                                            case CacheInsResult.Failure:
                                                failedTable[ie.Key] = new OperationFailedException("Generic operation failure; not enough information is available.");
                                                break;
                                            case CacheInsResult.NeedsEviction:
                                                failedTable[ie.Key] = new OperationFailedException("The cache is full and not enough items could be evicted.");
                                                break;
                                            case CacheInsResult.IncompatibleGroup:
                                                failedTable[ie.Key] = new OperationFailedException("Data group of the inserted item does not match the existing item's data group");
                                                break;
                                            case CacheInsResult.DependencyKeyNotExist:
                                                failedTable[ie.Key] = new OperationFailedException("One of the dependency keys does not exist.");
                                                break;
                                        }
                                    }
                                }
                            }
                            #endregion
                        }
                    }
                    ide = existingItems.GetEnumerator();
                    while (ide.MoveNext())
                    {
                        key = ide.Key;
                        if (jointTable.Contains(key))
                        {
                            failedTable.Add(key, new OperationFailedException("Data group of the inserted item does not match the existing item's data group"));
                            jointTable.Remove(key);
                        }
                    }
                }

                Hashtable localInsertResult = null;
                if (jointTable.Count > 0)
                {
                    index = 0;
                    validKeys = new object[jointTable.Count];
                    validEnteries = new CacheEntry[jointTable.Count];
                    added = new ArrayList();
                    IDictionaryEnumerator ide = jointTable.GetEnumerator();
                    while (ide.MoveNext())
                    {
                        key = ide.Key;
                        validKeys[index] = key;
                        validEnteries[index] = (CacheEntry)ide.Value;
                        added.Add(key);
                        index += 1;
                    }
                    localInsertResult = Local_Insert(validKeys, validEnteries, Cluster.LocalAddress, taskId, notify, operationContext);
                }

                if (localInsertResult != null)
                {
                    IDictionaryEnumerator ide = localInsertResult.GetEnumerator();
                    CacheInsResultWithEntry result = null;// CacheInsResult.Failure;
                    while (ide.MoveNext())
                    {
                        if (ide.Value is Exception)
                        {
                            failedTable.Add(ide.Key, ide.Value);
                            added.Remove(ide.Key);
                        }
                        else if (ide.Value is CacheInsResultWithEntry)
                        {
                            result = (CacheInsResultWithEntry)ide.Value;
                            if (result.Result == CacheInsResult.NeedsEviction)
                            {
                                failedTable.Add(ide.Key, new OperationFailedException("The cache is full and not enough items could be evicted."));
                                added.Remove(ide.Key);
                            }
                            else if (result.Result == CacheInsResult.DependencyKeyNotExist)
                            {
                                failedTable.Add(ide.Key, new OperationFailedException("One of the dependency keys does not exist."));
                                added.Remove(ide.Key);
                            }
                        }
                    }

                    insertResults = localInsertResult;
                }
                //Events will be raised from LocalCacheBase
                //CacheEntry pEntry;
                if (notify)
                {
                    IEnumerator ideInsterted = inserted.GetEnumerator();
                    while (ideInsterted.MoveNext())
                    {
                        if (operationContext.CancellationToken != null && operationContext.CancellationToken.IsCancellationRequested)
                            throw new OperationCanceledException(ExceptionsResource.OperationFailed);
                        key = ideInsterted.Current;
                        RemoveUpdateIndexOperation(key);
                    }
                }

                //This function is expected to only return Failed Keys therfore removing Dependencies within the insetBulk call
                _context.CacheImpl.RemoveCascadingDependencies(insertResults, operationContext);
            }

            if (taskId != null && insertResults != null)
            {
                Hashtable writeBehindTable = new Hashtable();
                for (int i = 0; i < keys.Length; i++)
                {
                    if (!insertResults.ContainsKey(keys[i]))
                    {
                        writeBehindTable.Add(keys[i], cacheEntries[i]);
                    }
                }

                if (writeBehindTable.Count > 0)
                {
                    base.SignalBulkTaskState(Cluster.Servers, taskId, cacheEntries[0].ProviderName, writeBehindTable, OpCode.Update, WriteBehindAsyncProcessor.TaskState.Execute);
                }
                else
                {
                    base.SignalTaskState(Cluster.Servers, taskId, cacheEntries[0].ProviderName, OpCode.Update, WriteBehindAsyncProcessor.TaskState.Remove, operationContext);
                }
            }

            return insertResults;
        }

        /// <summary>
        /// Adds a pair of key and value to the cache. If the specified key already exists 
        /// in the cache; it is updated, otherwise a new item is added to the cache.
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <returns>cache entry.</returns>
        /// <remarks>
        /// Does an Insert locally, however the generated notification is discarded, since it is 
        /// specially handled in <see cref="Insert"/>.
        /// </remarks>
        private CacheInsResultWithEntry Local_Insert(object key, CacheEntry cacheEntry, Address src, string taskId, bool notify, object lockId, ulong version, LockAccessType accessType, OperationContext operationContext)
        {
            CacheInsResultWithEntry retVal = new CacheInsResultWithEntry();

            CacheEntry clone = null;
            if (taskId != null && cacheEntry.HasQueryInfo)
                clone = (CacheEntry)cacheEntry.Clone();
            else
                clone = cacheEntry;
            try
            {
                if (_internalCache != null)
                {

                    #region -- PART I -- Cascading Dependency Operation
                    object[] dependingKeys = cacheEntry.KeysIAmDependingOn;
                    if (dependingKeys != null)
                    {
                        Hashtable goodKeysTable = Contains(dependingKeys, operationContext);

                        if (!goodKeysTable.ContainsKey("items-found"))
                            throw new OperationFailedException("One of the dependency keys does not exist.");

                        if (goodKeysTable["items-found"] == null)
                            throw new OperationFailedException("One of the dependency keys does not exist.");

                        if (dependingKeys.Length != ((ArrayList)goodKeysTable["items-found"]).Count)
                            throw new OperationFailedException("One of the dependency keys does not exist.");

                    }
                    #endregion

                    retVal = _internalCache.Insert(key, cacheEntry, notify, lockId, version, accessType, operationContext);

                    #region -- PART II -- Cascading Dependency Operation
                    if (retVal.Result == CacheInsResult.Success || retVal.Result == CacheInsResult.SuccessOverwrite)
                    {
                        Hashtable table = null;
                        if (retVal.Entry != null && retVal.Entry.KeysIAmDependingOn != null)
                        {
                            table = GetFinalKeysList(retVal.Entry.KeysIAmDependingOn, cacheEntry.KeysIAmDependingOn);
                            object[] oldKeys = (object[])table["oldKeys"];
                            Hashtable oldKeysTable = new Hashtable();
                            for (int i = 0; i < oldKeys.Length; i++)
                            {
                                if (!oldKeysTable.Contains(oldKeys[i]))
                                {
                                    oldKeysTable.Add(oldKeys[i], new ArrayList());
                                }
                                ((ArrayList)oldKeysTable[oldKeys[i]]).Add(key);
                            }

                            //Fix for NCache Bug4981
                            object generateQueryInfo = operationContext.GetValueByField(OperationContextFieldName.GenerateQueryInfo);
                            if (generateQueryInfo == null)
                            {
                                operationContext.Add(OperationContextFieldName.GenerateQueryInfo, true);
                            }

                            _internalCache.RemoveDepKeyList(oldKeysTable, operationContext);

                            object[] newKeys = (object[])table["newKeys"];
                            oldKeysTable.Clear();
                            for (int i = 0; i < newKeys.Length; i++)
                            {
                                if (!oldKeysTable.Contains(newKeys[i]))
                                {
                                    oldKeysTable.Add(newKeys[i], new ArrayList());
                                }
                                ((ArrayList)oldKeysTable[newKeys[i]]).Add(key);
                            }

                            table = _internalCache.AddDepKeyList(oldKeysTable, operationContext);

                            if (generateQueryInfo == null)
                            {
                                operationContext.RemoveValueByField(OperationContextFieldName.GenerateQueryInfo);
                            }

                        }
                        else if (cacheEntry.KeysIAmDependingOn != null)
                        {
                            object[] newKeys = cacheEntry.KeysIAmDependingOn;
                            Hashtable newKeysTable = new Hashtable();
                            for (int i = 0; i < newKeys.Length; i++)
                            {
                                if (!newKeysTable.Contains(newKeys[i]))
                                {
                                    newKeysTable.Add(newKeys[i], new ArrayList());
                                }
                                ((ArrayList)newKeysTable[newKeys[i]]).Add(key);
                            }

                            //Fix for NCache Bug4981
                            object generateQueryInfo = operationContext.GetValueByField(OperationContextFieldName.GenerateQueryInfo);
                            if (generateQueryInfo == null)
                            {
                                operationContext.Add(OperationContextFieldName.GenerateQueryInfo, true);
                            }

                            table = _internalCache.AddDepKeyList(newKeysTable, operationContext);

                            if (generateQueryInfo == null)
                            {
                                operationContext.RemoveValueByField(OperationContextFieldName.GenerateQueryInfo);
                            }

                        }

                        if (table != null)
                        {
                            IDictionaryEnumerator en = table.GetEnumerator();
                            while (en.MoveNext())
                            {
                                if (en.Value is bool && !((bool)en.Value))
                                {
                                    throw new OperationFailedException("Error setting up key depenedncy.");
                                }
                            }
                        }
                    }
                    #endregion

                }
            }
            catch (Exception e)
            {
                if (_clusteredExceptions) throw;
            }

            if (retVal != null && (retVal.Result == CacheInsResult.Success || retVal.Result == CacheInsResult.SuccessOverwrite) && taskId != null)
            {
                AddWriteBehindTask(src, key as string, clone, taskId, OpCode.Update, operationContext);
            }

            return retVal;
        }

        /// <summary>
        /// Adds key and value pairs to the cache. If any of the specified key already exists 
        /// in the cache; it is updated, otherwise a new item is added to the cache.
        /// </summary>
        /// <param name="keys">keys of the entries.</param>
        /// <returns>cache entries.</returns>
        /// <remarks>
        /// Does an Insert locally, however the generated notification is discarded, since it is 
        /// specially handled in <see cref="Insert"/>.
        /// </remarks>
        private Hashtable Local_Insert(object[] keys, CacheEntry[] cacheEntries, Address src, string taskId, bool notify, OperationContext operationContext)
        {
            Hashtable retVal = null;
            Exception thrown = null;

            Hashtable badEntriesTable = new Hashtable();
            //Hashtable goodEntriesTable = new Hashtable();
            ArrayList goodKeysList = new ArrayList();
            ArrayList goodEntriesList = new ArrayList();
            ArrayList badKeysList = new ArrayList();

            CacheEntry[] clone = null;
            if (taskId != null)
            {
                clone = new CacheEntry[cacheEntries.Length];
                for (int i = 0; i < cacheEntries.Length; i++)
                {
                    if (cacheEntries[i].HasQueryInfo)
                        clone[i] = (CacheEntry)cacheEntries[i].Clone();
                    else
                        clone[i] = cacheEntries[i];
                }
            }

            try
            {
                if (_internalCache != null)
                {
                    #region -- PART I -- Cascading Dependency Operation
                    for (int i = 0; i < cacheEntries.Length; i++)
                    {
                        object[] tempKeys = cacheEntries[i].KeysIAmDependingOn;
                        if (tempKeys != null)
                        {
                            Hashtable goodKeysTable = Contains(tempKeys, operationContext);


                            if (goodKeysTable.ContainsKey("items-found") && goodKeysTable["items-found"] != null && tempKeys.Length == ((ArrayList)goodKeysTable["items-found"]).Count)
                            {
                                goodKeysList.Add(keys[i]);
                                goodEntriesList.Add(cacheEntries[i]);
                            }
                            else
                            {
                                badKeysList.Add(keys[i]);
                            }
                        }
                        else
                        {
                            goodKeysList.Add(keys[i]);
                            goodEntriesList.Add(cacheEntries[i]);
                        }
                    }
                    #endregion


                    CacheEntry[] goodEntries = new CacheEntry[goodEntriesList.Count];
                    goodEntriesList.CopyTo(goodEntries);

                    retVal = _internalCache.Insert(goodKeysList.ToArray(), goodEntries, notify, operationContext);

                    #region -- PART II -- Cascading Dependency Operation

                    //Fix for NCache Bug4981
                    object generateQueryInfo = operationContext.GetValueByField(OperationContextFieldName.GenerateQueryInfo);
                    if (generateQueryInfo == null)
                    {
                        operationContext.Add(OperationContextFieldName.GenerateQueryInfo, true);
                    }

                    for (int i = 0; i < goodKeysList.Count; i++)
                    {
                        if (operationContext.CancellationToken != null && operationContext.CancellationToken.IsCancellationRequested)
                            throw new OperationCanceledException(ExceptionsResource.OperationFailed); CacheInsResultWithEntry result = retVal[goodKeysList[i]] as CacheInsResultWithEntry;

                        if (result != null)
                        {
                            if (result.Result == CacheInsResult.Success || result.Result == CacheInsResult.SuccessOverwrite)
                            {
                                Hashtable table = null;
                                if (result.Entry != null && result.Entry.KeysIAmDependingOn != null)
                                {
                                    table = GetFinalKeysList(result.Entry.KeysIAmDependingOn, goodEntries[i].KeysIAmDependingOn);
                                    object[] oldKeys = (object[])table["oldKeys"];
                                    Hashtable oldKeysTable = new Hashtable();
                                    for (int j = 0; j < oldKeys.Length; j++)
                                    {
                                        if (!oldKeysTable.Contains(oldKeys[j]))
                                        {
                                            oldKeysTable.Add(oldKeys[j], new ArrayList());
                                        }
                                        ((ArrayList)oldKeysTable[oldKeys[j]]).Add(goodKeysList[i]);
                                    }

                                    _internalCache.RemoveDepKeyList(oldKeysTable, operationContext);

                                    object[] newKeys = (object[])table["newKeys"];
                                    oldKeysTable.Clear();
                                    for (int j = 0; j < newKeys.Length; j++)
                                    {
                                        if (!oldKeysTable.Contains(newKeys[j]))
                                        {
                                            oldKeysTable.Add(newKeys[j], new ArrayList());
                                        }
                                        ((ArrayList)oldKeysTable[newKeys[j]]).Add(goodKeysList[i]);
                                    }

                                    table = _internalCache.AddDepKeyList(oldKeysTable, operationContext);
                                }
                                else if (goodEntries[i].KeysIAmDependingOn != null)
                                {
                                    object[] newKeys = goodEntries[i].KeysIAmDependingOn;
                                    Hashtable newKeysTable = new Hashtable();
                                    for (int j = 0; j < newKeys.Length; j++)
                                    {
                                        if (!newKeysTable.Contains(newKeys[j]))
                                        {
                                            newKeysTable.Add(newKeys[j], new ArrayList());
                                        }
                                        ((ArrayList)newKeysTable[newKeys[j]]).Add(goodKeysList[i]);
                                    }

                                    table = _internalCache.AddDepKeyList(newKeysTable, operationContext);
                                }

                                if (table != null)
                                {
                                    IDictionaryEnumerator en = table.GetEnumerator();
                                    while (en.MoveNext())
                                    {
                                        if (en.Value is bool && !((bool)en.Value))
                                        {
                                            CacheInsResultWithEntry resultWithEntry = new CacheInsResultWithEntry();
                                            resultWithEntry.Result = CacheInsResult.DependencyKeyError;

                                            retVal[goodKeysList[i]] = resultWithEntry;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (generateQueryInfo == null)
                    {
                        operationContext.RemoveValueByField(OperationContextFieldName.GenerateQueryInfo);
                    }

                    #endregion

                    for (int i = 0; i < badKeysList.Count; i++)
                    {
                        CacheInsResultWithEntry resultWithEntry = new CacheInsResultWithEntry();
                        resultWithEntry.Result = CacheInsResult.DependencyKeyNotExist;

                        retVal.Add(badKeysList[i], resultWithEntry);
                    }

                }
            }
            catch (Exception e)
            {
                if (_clusteredExceptions) throw;
            }

            if (taskId != null && retVal != null)
            {
                Hashtable successfullEntries = new Hashtable();
                for (int i = 0; i < keys.Length; i++)
                {
                    object value = retVal[keys[i]];
                    if (value is CacheInsResultWithEntry && (((CacheInsResultWithEntry)value).Result == CacheInsResult.Success || ((CacheInsResultWithEntry)value).Result == CacheInsResult.SuccessOverwrite))
                    {
                        successfullEntries.Add(keys[i], clone[i]);
                    }
                }

                if (successfullEntries.Count > 0)
                {
                    AddWriteBehindTask(src, successfullEntries, null, taskId, OpCode.Update);
                }
            }

            return retVal;
        }

        /// <summary>
        /// Add the object to the cluster. 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="cacheEntry"></param>
        /// <returns></returns>
        /// <remarks>
        /// This method invokes <see cref="handleInsert"/> on every server-node in the cluster. If 
        /// the operation fails on any one node the whole operation is considered to have failed 
        /// and is rolled-back.
        /// </remarks>
        private CacheInsResultWithEntry Clustered_Insert(object key, CacheEntry cacheEntry, string taskId, object lockId, LockAccessType accesssType, OperationContext operationContext)
        {
            return Clustered_Insert(Cluster.Servers, key, cacheEntry, taskId, lockId, accesssType, operationContext);
        }

        /// <summary>
        /// Add the objects to the cluster. 
        /// </summary>
        /// <param name="keys"></param>
        /// <param name="cacheEntries"></param>
        /// <returns></returns>
        /// <remarks>
        /// This method invokes <see cref="handleInsert"/> on every server-node in the cluster. If 
        /// the operation fails on any one node the whole operation is considered to have failed 
        /// and is rolled-back.
        /// </remarks>
        protected override Hashtable Clustered_Insert(object[] keys, CacheEntry[] cacheEntries, OperationContext operationContext)
        {
            return Clustered_Insert(Cluster.Servers, keys, cacheEntries, null, operationContext);
        }

        /// <summary>
        /// Adds a pair of key and value to the cache. If the specified key already exists 
        /// in the cache; it is updated, otherwise a new item is added to the cache.
        /// </summary>
        /// <param name="info">the object containing parameters for this operation.</param>
        /// <returns>object to be sent back to the requestor.</returns>
        private object handleInsert(Address src, object info, Array userPayload)
        {
            try
            {
                object[] objs = (object[])info;
                bool returnEntry = false;
                string taskId = null;
                OperationContext operationContext = null;

                if (objs.Length > 2)
                    taskId = objs[2] != null ? objs[2] as string : null;

                //KS: operation Context
                if (objs.Length == 3)
                {
                    operationContext = objs[2] as OperationContext;
                }
                else if (objs.Length == 4)
                {
                    operationContext = objs[3] as OperationContext;
                }


                //if client node is requesting for the previous cache entry
                //then cluster coordinator must send it back...
                if (objs.Length == 7)
                {
                    returnEntry = (bool)objs[3] && Cluster.IsCoordinator;
                    operationContext = objs[6] as OperationContext;
                }



                if (objs[0] is object[])
                {
                    object[] keys = (object[])objs[0];
                    CacheEntry[] entries = objs[1] as CacheEntry[];
                    return Local_Insert(keys, entries, src, taskId, true, operationContext);
                }
                else
                {
                    object key = objs[0];
                    CacheEntry e = objs[1] as CacheEntry;
                    if (userPayload != null)
                        e.Value = userPayload;
                    object lockId = null;
                    LockAccessType accessType = LockAccessType.IGNORE_LOCK;
                    ulong version = 0;
                    if (objs.Length == 7)
                    {
                        lockId = objs[4];
                        accessType = (LockAccessType)objs[5];
                    }
                    CacheInsResultWithEntry resultWithEntry = Local_Insert(key, e, src, taskId, true, lockId, version, accessType, operationContext);



                    /* send value and entry seperately*/
                    OperationResponse opRes = new OperationResponse();
                    if (resultWithEntry.Entry != null)
                    {
                        if (resultWithEntry.Entry.Value is CallbackEntry)
                        {
                            opRes.UserPayload = null;
                        }
                        else
                        {

                            //we need not to send this entry back... it is needed only for custom notifications and/or key dependencies...
                            if (resultWithEntry.Entry.KeysDependingOnMe == null || resultWithEntry.Entry.KeysDependingOnMe.Count == 0) returnEntry = false;
                            opRes.UserPayload = null;
                        }

                        if (returnEntry)
                            resultWithEntry.Entry = resultWithEntry.Entry.CloneWithoutValue() as CacheEntry;
                        else
                            resultWithEntry.Entry = null;
                    }

                    opRes.SerializablePayload = resultWithEntry;
                    return opRes;
                }
            }
            catch (Exception e)
            {
                if (_clusteredExceptions) throw;
            }
            return CacheInsResult.Failure;
        }

        private object handleGetKeysByTag(object info)
        {
            if (_internalCache != null)
            {
                object[] data = (object[])info;
                ICollection keys = _internalCache.GetTagKeys(data[0] as string[], (TagComparisonType)data[1], data[2] as OperationContext);
                return new ArrayList(keys);
            }

            return null;
        }


        #endregion

        #region	/                 --- Replicated ICache.Remove ---           /

        public override object RemoveSync(object[] keys, ItemRemoveReason reason, bool notify, OperationContext operationContext)
        {
            object result = null;
            try
            {
                if (Cluster.Servers.Count > 1)
                    result = Clustered_Remove(keys, reason, operationContext);
                else
                    result = handleRemoveRange(new object[] { keys, reason, operationContext });
            }

            catch (Runtime.Exceptions.TimeoutException)
            {
                //we retry the operation.
                Thread.Sleep(2000);
                if (Cluster.Servers.Count > 1)
                    result = Clustered_Remove(keys, reason, operationContext);
                else
                    result = handleRemoveRange(new object[] { keys, reason, operationContext });
            }
            catch (Exception e)
            {
                Context.NCacheLog.Error("ReplicatedCache.RemoveSync", e.ToString());
                throw;
            }
            return result;
        }
        /// <summary>
        /// Removes the object and key pair from the cache. The key is specified as parameter.
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <returns>cache entry.</returns>
        /// <remarks>
        /// Remove notifications in a repicated cluster are handled differently. If there is an 
        /// explicit request for Remove, the node initiating the request triggers the notifications.
        /// Expirations and Evictions are replicated and again the node initiating the replication
        /// triggers the cluster-wide notification.
        /// </remarks>
        public override CacheEntry Remove(object key, ItemRemoveReason ir, bool notify, object lockId, ulong version, LockAccessType accessType, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.Remove", "");
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();

            object actualKey = key;
            DataSourceUpdateOptions updateOptions = DataSourceUpdateOptions.None;
            CallbackEntry cbEntry = null;
            string providerName = null;

            if (key is object[])
            {
                object[] package = key as object[];
                actualKey = package[0];
                updateOptions = (DataSourceUpdateOptions)package[1];
                cbEntry = package[2] as CallbackEntry;
                if (package.Length > 3)
                {
                    providerName = package[3] as string;
                }
            }

            CacheEntry e = null;

            if (accessType == LockAccessType.COMPARE_VERSION)
            {
                e = Get(key, operationContext);
                if (e != null)
                {
                    if (!e.CompareVersion(version))
                        throw new LockingException("Item in the cache does not exist at the specified version.");
                }
            }
            else if (accessType != LockAccessType.IGNORE_LOCK)
            {
                //Get internally catters for the state-transfer scenarios.
                e = Get(key, operationContext);
                if (e != null)
                {
                    if (e.IsItemLocked() && !e.CompareLock(lockId))
                    {
                        //this exception directly goes to user. 
                        throw new LockingException("Item is locked.");
                    }
                }
            }

            string taskId = null;
            if (updateOptions == DataSourceUpdateOptions.WriteBehind)
                taskId = Cluster.LocalAddress.ToString() + ":" + NextSequence().ToString();
            try
            {
                if (Cluster.Servers.Count > 1)
                    e = Clustered_Remove(actualKey, ir, cbEntry, taskId, providerName, false, lockId, version, accessType, operationContext);
                else
                    e = Local_Remove(actualKey, ir, Cluster.LocalAddress, cbEntry, taskId, providerName, true, lockId, version, accessType, operationContext);
            }

            catch (Runtime.Exceptions.TimeoutException)
            {
                Thread.Sleep(2000);

                if (Cluster.Servers.Count > 1)
                    e = Clustered_Remove(actualKey, ir, cbEntry, taskId, providerName, false, lockId, version, accessType, operationContext);
                else
                    e = Local_Remove(actualKey, ir, Cluster.LocalAddress, cbEntry, taskId, providerName, true, lockId, version, accessType, operationContext);
            }
            //events will be raised from LocalCacheBase
            if (e != null && notify)
            {
                RemoveUpdateIndexOperation(key);
            }

            if (taskId != null)
            {
                if (e != null)
                {
                    SignalTaskState(Cluster.Servers, taskId, providerName, OpCode.Remove, WriteBehindAsyncProcessor.TaskState.Execute, operationContext);
                }
            }

            return e;
        }

        /// <summary>
        /// Removes the objects and key pairs from the cache. The keys are specified as parameter.
        /// </summary>
        /// <param name="keys">keys of the entries.</param>
        /// <returns>keys that actually removed from the cache</returns>
        /// <remarks>
        /// Remove notifications in a repicated cluster are handled differently. If there is an 
        /// explicit request for Remove, the node initiating the request triggers the notifications.
        /// Expirations and Evictions are replicated and again the node initiating the replication
        /// triggers the cluster-wide notification.
        /// </remarks>
        public override Hashtable Remove(IList keys, ItemRemoveReason ir, bool notify, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.RemoveBlk", "");
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();

            DataSourceUpdateOptions updateOptions = DataSourceUpdateOptions.None;
            CallbackEntry cbEntry = null;
            Hashtable writeBehindTable = new Hashtable();
            string providerName = null;

            if (keys[0] is object[])
            {
                object[] package = keys[0] as object[];
                keys[0] = package[0];
                updateOptions = (DataSourceUpdateOptions)package[1];
                cbEntry = package[2] as CallbackEntry;
                if (package.Length > 3)
                {
                    providerName = package[3] as string;
                }
            }

            string taskId = null;
            if (updateOptions == DataSourceUpdateOptions.WriteBehind)
                taskId = Cluster.LocalAddress.ToString() + ":" + NextSequence().ToString();

            Hashtable removed = null;
            if (Cluster.Servers.Count > 1)
                removed = Clustered_Remove(keys, ir, cbEntry, taskId, providerName, false, operationContext);
            else
                removed = Local_Remove(keys, ir, Cluster.LocalAddress, cbEntry, taskId, providerName, true, operationContext);

            if (removed.Count > 0)
            {
                IDictionaryEnumerator ide = removed.GetEnumerator();
                while (ide.MoveNext())
                {
                    if (operationContext.CancellationToken != null && operationContext.CancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(ExceptionsResource.OperationFailed);
                    object key = ide.Key;
                    CacheEntry e = (CacheEntry)ide.Value;
                    if (e != null)
                    {
                        RemoveUpdateIndexOperation(ide.Key);
                        writeBehindTable.Add(ide.Key, ide.Value);
                    }
                }
            }

            if (taskId != null && writeBehindTable.Count > 0)
            {
                SignalBulkTaskState(Cluster.Servers, taskId, null, writeBehindTable, OpCode.Remove, WriteBehindAsyncProcessor.TaskState.Execute);
            }
            else if (taskId != null && writeBehindTable.Count == 0)
            {
                SignalBulkTaskState(Cluster.Servers, taskId, null, writeBehindTable, OpCode.Remove, WriteBehindAsyncProcessor.TaskState.Remove);
            }

            return removed;
        }


        /// <summary>
        /// Remove the object from the local cache only. 
        /// </summary>
        /// <param name="key">key of the entry.</param>
        /// <param name="ir"></param>
        /// <param name="notify"></param>
        /// <returns>cache entry.</returns>
        private CacheEntry Local_Remove(object key, ItemRemoveReason ir, Address src, CallbackEntry cbEntry, string taskId, string providerName, bool notify, object lockId, ulong version, LockAccessType accessType, OperationContext operationContext)
        {
            CacheEntry retVal = null;
            if (_internalCache != null)
            {
                retVal = _internalCache.Remove(key, ir, notify, lockId, version, accessType, operationContext);

                if (retVal != null)
                {
                    Object[] oldKeys = retVal.KeysIAmDependingOn;
                    if (oldKeys != null)
                    {
                        Hashtable oldKeysTable = new Hashtable();
                        for (int i = 0; i < oldKeys.Length; i++)
                        {
                            if (!oldKeysTable.Contains(oldKeys[i]))
                            {
                                oldKeysTable.Add(oldKeys[i], new ArrayList());
                            }
                            ((ArrayList)oldKeysTable[oldKeys[i]]).Add(key);
                        }
                        _internalCache.RemoveDepKeyList(oldKeysTable, operationContext);
                    }
                }
            }

            if (retVal != null && taskId != null)
            {
                CacheEntry cloned = retVal;
                cloned.ProviderName = providerName;
                if (cbEntry != null)
                {
                    cloned = retVal.Clone() as CacheEntry;
                    cloned.ProviderName = providerName;
                    if (cloned.Value is CallbackEntry)
                    {
                        ((CallbackEntry)cloned.Value).WriteBehindOperationCompletedCallback = cbEntry.WriteBehindOperationCompletedCallback;
                    }
                    else
                    {
                        cbEntry.Value = cloned.Value;
                        cloned.Value = cbEntry;
                    }
                }
                base.AddWriteBehindTask(src, key as string, cloned, taskId, OpCode.Remove, operationContext);
            }

            return retVal;
        }

        private Hashtable Local_Cascaded_Remove(object key, CacheEntry e, ItemRemoveReason ir, Address src, bool notify, OperationContext operationContext)
        {
            Hashtable removedItems = new Hashtable();
            if (e != null && e.KeysDependingOnMe != null)
            {
                Hashtable entriesTable = new Hashtable();
                string[] nextRemovalKeys = new string[e.KeysDependingOnMe.Count];
                e.KeysDependingOnMe.Keys.CopyTo(nextRemovalKeys, 0);

                while (nextRemovalKeys != null && nextRemovalKeys.Length > 0)
                {
                    entriesTable = Local_Remove(nextRemovalKeys, ir, src, null, null, null, notify, operationContext);//_context.CacheImpl.Remove(nextRemovalKeys, ItemRemoveReason.DependencyChanged, true);
                    if (entriesTable != null)
                    {
                        IDictionaryEnumerator ide = entriesTable.GetEnumerator();
                        if (ide.MoveNext())
                        {
                            removedItems[ide.Key] = ide.Value;
                        }
                    }
                    nextRemovalKeys = ExtractKeys(entriesTable);
                }
            }
            return removedItems;
        }


        /// <summary>
        /// Remove the objects from the local cache only. 
        /// </summary>
        /// <param name="keys">keys of the entries.</param>
        /// <param name="ir"></param>
        /// <param name="notify"></param>
        /// <returns>keys and values that actualy removed from the cache</returns>
        private Hashtable Local_Remove(IList keys, ItemRemoveReason ir, Address src, CallbackEntry cbEntry, string taskId, string providerName, bool notify, OperationContext operationContext)
        {
            Hashtable retVal = null;
            Hashtable writeBehindTable = new Hashtable();
            if (_internalCache != null)
            {
                retVal = _internalCache.Remove(keys, ir, notify, operationContext);

                for (int i = 0; i < keys.Count; i++)
                {
                    if (operationContext.CancellationToken != null && operationContext.CancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(ExceptionsResource.OperationFailed);
                    if (retVal[keys[i]] is CacheEntry)
                    {
                        CacheEntry entry = (CacheEntry)retVal[keys[i]];
                        entry.ProviderName = providerName;
                        Object[] oldKeys = entry.KeysIAmDependingOn;
                        if (oldKeys != null)
                        {
                            Hashtable oldKeysTable = new Hashtable();
                            for (int j = 0; j < oldKeys.Length; j++)
                            {
                                if (!oldKeysTable.Contains(oldKeys[j]))
                                {
                                    oldKeysTable.Add(oldKeys[j], new ArrayList());
                                }
                                ((ArrayList)oldKeysTable[oldKeys[j]]).Add(keys[i]);
                            }
                            _internalCache.RemoveDepKeyList(oldKeysTable, operationContext);
                        }
                        if (taskId != null)
                            writeBehindTable.Add(keys[i], entry);
                    }
                }
            }

            if (writeBehindTable.Count > 0 && taskId != null)
            {
                AddWriteBehindTask(src, writeBehindTable, cbEntry, taskId, OpCode.Remove);
            }

            return retVal;
        }

        protected override Hashtable Local_RemoveTag(string[] tags, TagComparisonType tagComparisonType, bool notify, OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();
            ICollection list = Local_GetTagKeys(tags, tagComparisonType, operationContext);
            if (list != null && list.Count > 0)
            {
                object[] grpKeys = MiscUtil.GetArrayFromCollection(list);
                return Remove(grpKeys, ItemRemoveReason.Removed, notify, operationContext);
            }
            return null;
        }


        /// <summary>
        /// Remove the group from cache.
        /// </summary>
        protected override Hashtable Local_RemoveGroup(string group, string subGroup, bool notify, OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();
            ArrayList list = Local_GetGroupKeys(group, subGroup, operationContext);
            if (list != null && list.Count > 0)
            {
                object[] grpKeys = MiscUtil.GetArrayFromCollection(list);
                return Remove(grpKeys, ItemRemoveReason.Removed, notify, operationContext);
            }
            return null;
        }


        /// <summary>
        /// Hanlde cluster-wide Remove(key) requests.
        /// </summary>
        /// <param name="info">the object containing parameters for this operation.</param>
        /// <returns>object to be sent back to the requestor.</returns>
        /// <remarks>
        /// Removes an item locally; however without generating notifications. 
        /// <para>
        /// <b>Note:</b> When a client invokes <see cref="handleRemove"/>; it is the clients reponsibility
        /// to actaully initiate the notification.
        /// </para>
        /// </remarks>
        private object handleRemove(Address src, object info)
        {
            try
            {
                object result = null;

                if (info is object[])
                {
                    object[] args = info as Object[];
                    string taskId = null;
                    CallbackEntry cbEntry = null;
                    string providerName = null;
                    OperationContext operationContext = null;

                    if (args.Length > 3)
                        cbEntry = args[3] as CallbackEntry;
                    if (args.Length > 4)
                        taskId = args[4] as string;
                    if (args.Length > 8)
                        providerName = args[8] as string;

                    if (args.Length > 9)
                        operationContext = args[9] as OperationContext;
                    else if (args.Length > 6)
                        operationContext = args[6] as OperationContext;
                    else if (args.Length > 2)
                        operationContext = args[2] as OperationContext;


                    if (args != null && args.Length > 0)
                    {
                        object tmp = args[0];
                        if (tmp is Object[])
                        {
                            if (args.Length > 5)
                            {
                                providerName = args[5] as string;
                            }
                            result = Local_Remove((object[])tmp, ItemRemoveReason.Removed, src, cbEntry, taskId, providerName, true, operationContext);

                        }
                        else
                        {
                            object lockId = args[5];
                            LockAccessType accessType = (LockAccessType)args[6];
                            ulong version = (ulong)args[7];



                            CacheEntry entry = Local_Remove(tmp, ItemRemoveReason.Removed, src, cbEntry, taskId, providerName, true, lockId, version, accessType, operationContext);
                            /* send value and entry seperaty*/
                            OperationResponse opRes = new OperationResponse();
                            if (entry != null)
                            {
                                if (_context.InMemoryDataFormat.Equals(DataFormat.Object))
                                {
                                    opRes.UserPayload = null;
                                    opRes.SerializablePayload = entry.Clone();
                                }
                                else
                                {
                                    UserBinaryObject ubObject = (UserBinaryObject)(entry.Value is CallbackEntry ? ((CallbackEntry)entry.Value).Value : entry.Value);
                                    opRes.UserPayload = ubObject.Data;
                                    opRes.SerializablePayload = entry.CloneWithoutValue();
                                }
                            }
                            result = opRes;
                        }
                    }
                }

                /// Only the coordinator returns the object, this saves a lot of traffic because
                /// only one reply actually contains the object other replies are simply dummy objects.
                if (Cluster.IsCoordinator)
                    return result;
            }
            catch (Exception)
            {
                if (_clusteredExceptions) throw;
            }
            return null;
        }

        /// <summary>
        /// Hanlde cluster-wide Remove(key[]) requests.
        /// </summary>
        /// <param name="info">the object containing parameters for this operation.</param>
        /// <returns>object to be sent back to the requestor.</returns>
        /// <remarks>
        /// Removes a range of items locally; however without generating notifications.
        /// <para>
        /// <b>Note:</b> When a client invokes <see cref="handleRemoveRange"/>; it is the clients 
        /// responsibility to actaully initiate the notification.
        /// </para>
        /// </remarks>
        private object handleRemoveRange(object info)
        {
            try
            {
                object[] objs = (object[])info;
                OperationContext operationContext = null;
                if (objs.Length > 2)
                    operationContext = objs[2] as OperationContext;

                if (objs[0] is object[])
                {
                    object[] keys = (object[])objs[0];
                    ItemRemoveReason ir = (ItemRemoveReason)objs[1];

                    Hashtable totalRemovedItems = new Hashtable();
                    CacheEntry entry = null;
                    IDictionaryEnumerator ide = null;

                    if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("Replicated.handleRemoveRange()", "Keys = " + keys.Length.ToString());

                    Hashtable removedItems = Local_Remove(keys, ir, null, null, null, null, true, operationContext);

                    if (removedItems != null)
                    {
                        totalRemovedItems = removedItems;
                        Hashtable cascRemovedItems = InternalCache.Cascaded_remove(removedItems, ir, true, false, operationContext);
                        if (cascRemovedItems != null && cascRemovedItems.Count > 0)
                        {
                            ide = cascRemovedItems.GetEnumerator();
                            while (ide.MoveNext())
                            {
                                if (!totalRemovedItems.Contains(ide.Key))
                                    totalRemovedItems.Add(ide.Key, ide.Value);
                            }
                        }
                    }
                }


            }
            catch (Exception)
            {
                if (_clusteredExceptions) throw;
            }
            return null;
        }

        #endregion

        #region	/                 --- Replicated ICache.GetEnumerator ---           /

        /// <summary>
        /// Returns a .NET IEnumerator interface so that a client should be able
        /// to iterate over the elements of the cache store.
        /// </summary>
        /// <returns>IDictionaryEnumerator enumerator.</returns>
        public override IDictionaryEnumerator GetEnumerator()
        {
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();

            if ((_statusLatch.IsAnyBitsSet(NodeStatus.Initializing)))
                return Clustered_GetEnumerator(Cluster.Coordinator.Clone() as Address);

            return new LazyKeysetEnumerator(this, (object[])handleKeyList(), false);
        }

        public override EnumerationDataChunk GetNextChunk(EnumerationPointer pointer, OperationContext operationContext)
        {
            /*All clustered operation have been removed as they use to return duplicate keys because the snapshots
            created on all the nodes of replicated for a particular enumerator were not sorted and in case of node up and down we might 
            get duplicate keys when a request is routed to another client. whenever a node leaves the cluster we will throw Enumeration has been modified exception to the client.*/

            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null)
                throw new InvalidOperationException();

            EnumerationDataChunk nextChunk = null;

            if (Cluster.Servers.Count > 1)
            {

                //Enumeration Pointer came to this node on rebalance of clients and node is currently in state transfer or pointer came to this node and node is intitialized but doesnt have snapshot
                if ((_statusLatch.IsAnyBitsSet(NodeStatus.Initializing) && (pointer.ChunkId > 0 || !InternalCache.HasEnumerationPointer(pointer)))
                    || (!_statusLatch.IsAnyBitsSet(NodeStatus.Initializing) && pointer.ChunkId > 0 && !InternalCache.HasEnumerationPointer(pointer)))
                {
                    throw new OperationFailedException("Enumeration Has been Modified");
                }
                else //Node is initialized with data and has snapshot should return the next chunk locally
                {
                    nextChunk = InternalCache.GetNextChunk(pointer, operationContext);
                }

                //Dispose the pointer on all nodes as this is the last chunk for this particular enumeration pointer
                if (pointer.IsSocketServerDispose && nextChunk == null)
                {
                    pointer.isDisposable = true;
                    InternalCache.GetNextChunk(pointer, operationContext);
                }
                else if (nextChunk.IsLastChunk)
                {
                    pointer = nextChunk.Pointer;
                    pointer.isDisposable = true;
                    InternalCache.GetNextChunk(pointer, operationContext);
                }
            }
            else if (pointer.ChunkId > 0 && !InternalCache.HasEnumerationPointer(pointer))
            {
                throw new OperationFailedException("Enumeration Has been Modified");
            }
            else
            {
                nextChunk = InternalCache.GetNextChunk(pointer, operationContext);
            }

            return nextChunk;
        }

        private EnumerationDataChunk Clustered_GetNextChunk(ArrayList dests, EnumerationPointer pointer, OperationContext operationContext)
        {
            try
            {
                Function func = new Function((int)OpCodes.GetNextChunk, new object[] { pointer, operationContext }, false);

                RspList results = Cluster.BroadcastToMultiple(dests,
                   func,
                   GroupRequest.GET_ALL, false);

                ClusterHelper.ValidateResponses(results, typeof(EnumerationDataChunk), Name);

                /// Get a single resulting enumeration data chunk from a request
                EnumerationDataChunk nextChunk = ClusterHelper.FindAtomicEnumerationDataChunkReplicated(results) as EnumerationDataChunk;

                return nextChunk;
            }
            catch (CacheException e)
            {
                ;
                throw;
            }
            catch (Exception e)
            {
                throw new GeneralFailureException(e.Message, e);
            }
        }


        /// <summary>
        /// Hanlde cluster-wide KeyList requests.
        /// </summary>
        /// <returns>object to be sent back to the requestor.</returns>
        private object handleKeyList()
        {
            try
            {
                return MiscUtil.GetKeyset(_internalCache, Convert.ToInt32(Cluster.Timeout));
            }
            catch (Exception)
            {
                if (_clusteredExceptions) throw;
            }
            return null;
        }

        private EnumerationDataChunk handleGetNextChunk(Address src, object info)
        {
            object[] package = info as object[];
            EnumerationPointer pointer = package[0] as EnumerationPointer;
            OperationContext operationContext = package[1] as OperationContext;

            //if a request for a an enumerator whos in middle of its enumeration comes to coordinator
            //and we cannot find it we will throw enumeration modified exception.
            if (this.Cluster.IsCoordinator && pointer.ChunkId > 0 && !pointer.IsSocketServerDispose && !InternalCache.HasEnumerationPointer(pointer))
            {
                throw new OperationFailedException("Enumeration Has been Modified");
            }

            EnumerationDataChunk nextChunk = InternalCache.GetNextChunk(pointer, operationContext);
            return nextChunk;
        }

        #endregion

        #endregion
        #region /               --- Key based notification  ---     /

        public override object handleRegisterKeyNotification(object operand)
        {
            object[] operands = operand as object[];
            if (operands != null)
            {
                object Keys = operands[0];
                CallbackInfo updateCallback = operands[1] as CallbackInfo;
                CallbackInfo removeCallback = operands[2] as CallbackInfo;
                OperationContext operationContext = operands[3] as OperationContext;
                if (_internalCache != null)
                {
                    if (Keys is object[])
                    {
                        _internalCache.RegisterKeyNotification((string[])Keys, updateCallback, removeCallback, operationContext);
                    }
                    else
                    {
                        _internalCache.RegisterKeyNotification((string)Keys, updateCallback, removeCallback, operationContext);
                    }
                }
            }
            return null;
        }

        public override PollingResult handlePoll(object operand)
        {
            object[] operands = operand as object[];
            if (operands != null)
            {
                OperationContext operationContext = operands[0] as OperationContext;
                if (_internalCache != null)
                {
                    return _internalCache.Poll(operationContext);
                }
            }
            return null;
        }

        public override object handleRegisterPollingNotification(object operand)
        {
            object[] operands = operand as object[];
            if (operands != null)
            {
                short callbackId = (short)operands[0];
                OperationContext operationContext = operands[1] as OperationContext;
                if (_internalCache != null)
                {
                    _internalCache.RegisterPollingNotification(callbackId, operationContext);
                }
            }
            return null;
        }

        public override object handleUnregisterKeyNotification(object operand)
        {
            OperationContext operationContext = null;
            object[] operands = operand as object[];
            if (operands != null)
            {
                object Keys = operands[0];
                CallbackInfo updateCallback = operands[1] as CallbackInfo;
                CallbackInfo removeCallback = operands[2] as CallbackInfo;
                if (operands.Length > 2)
                    operationContext = operands[3] as OperationContext;

                if (_internalCache != null)
                {
                    if (Keys is object[])
                    {
                        _internalCache.UnregisterKeyNotification((string[])Keys, updateCallback, removeCallback, operationContext);
                    }
                    else
                    {
                        _internalCache.UnregisterKeyNotification((string)Keys, updateCallback, removeCallback, operationContext);
                    }
                }
            }
            return null;
        }

        #endregion



        public override bool IsEvictionAllowed
        {
            get
            {
                return Cluster.IsCoordinator;
            }
            set
            {
                base.IsEvictionAllowed = value;
            }
        }
        #region	/                 --- ICacheEventsListener ---           /

        #region	/                 --- OnCacheCleared ---           /

        /// <summary> 
        /// Fire when the cache is cleared. 
        /// </summary>
        void ICacheEventsListener.OnCacheCleared(OperationContext operationContext, EventContext eventContext)
        {
            // do local notifications only, every node does that, so we get a replicated notification.

            if (_context.PersistenceMgr != null)
            {
                Alachisoft.NCache.Persistence.Event perEvent = new Alachisoft.NCache.Persistence.Event();
                perEvent.PersistedEventId = eventContext.EventID;
                perEvent.PersistedEventId.EventType = EventType.CACHE_CLEARED_EVENT;
                _context.PersistenceMgr.AddToPersistedEvent(perEvent);
            }

            UpdateCacheStatistics();
            NotifyCacheCleared(true, operationContext, eventContext);
        }

        /// <summary>
        /// Hanlder for clustered cache clear notification.
        /// </summary>
        /// <param name="info">packaged information</param>
        /// <returns>null</returns>
        private object handleNotifyCacheCleared(OperationContext operationContext, EventContext eventContext)
        {
            NotifyCacheCleared(true, operationContext, eventContext);
            return null;
        }


        #endregion

        #region	/                 --- OnItemAdded ---           /

        /// <summary> 
        /// Fired when an item is added to the cache. 
        /// </summary>
        void ICacheEventsListener.OnItemAdded(object key, OperationContext operationContext, EventContext eventContext)
        {
            // specially handled in Add.
            try
            {
                FilterEventContextForGeneralDataEvents(Runtime.Events.EventType.ItemAdded, eventContext);


                //persist events
                if (_context.PersistenceMgr != null)
                {
                    Alachisoft.NCache.Persistence.Event perEvent = new Alachisoft.NCache.Persistence.Event();
                    perEvent.PersistedEventInfo.Key = (String)key;
                    perEvent.PersistedEventId = eventContext.EventID;
                    perEvent.PersistedEventId.EventType = EventType.ITEM_ADDED_EVENT;
                    _context.PersistenceMgr.AddToPersistedEvent(perEvent);
                }

                NotifyItemAdded(key, true, operationContext, eventContext);
                if (NCacheLog.IsInfoEnabled) NCacheLog.Info("ReplicatedCache.OnItemAdded()", "key: " + key.ToString());
            }
            catch (Exception e)
            {
                if (NCacheLog.IsInfoEnabled) NCacheLog.Info("ReplicatedCache.OnItemAdded()", e.ToString());
            }
        }

        /// <summary>
        /// Hanlder for clustered item added notification.
        /// </summary>
        /// <param name="info">packaged information</param>
        /// <returns>null</returns>
        private object handleNotifyAdd(object info)
        {
            object[] args = info as object[];
            NotifyItemAdded(args[0], true, (OperationContext)args[1], (EventContext)args[2]);
            return null;
        }

        #endregion

        #region /          --- OnCustomEvent ---           /

        void ICacheEventsListener.OnCustomEvent(object notifId, object data, OperationContext operationContext, EventContext eventContext)
        {
        }

        #endregion

        /// <summary>
        /// Fire when hasmap changes when 
        /// - new node joins
        /// - node leaves
        /// - manual/automatic load balance
        /// </summary>
        /// <param name="newHashmap">new hashmap</param>
        void ICacheEventsListener.OnHashmapChanged(NewHashmap newHashmap, bool updateClientMap)
        {
        }

        #region /          --- OnWriteBehindTaskCompletedEvent ---           /

        /// <summary>
        /// 
        /// </summary>
        /// <param name="operationCode"></param>
        /// <param name="result"></param>
        /// <param name="cbEntry"></param>
        void ICacheEventsListener.OnWriteBehindOperationCompletedCallback(OpCode operationCode, object result, CallbackEntry cbEntry)
        {
        }

        #endregion

        #region	/                 --- OnItemUpdated ---           /

        /// <summary> 
        /// handler for item updated event.
        /// </summary>
        void ICacheEventsListener.OnItemUpdated(object key, OperationContext operationContext, EventContext eventContext)
        {
            // specially handled in Update.
            try
            {
                FilterEventContextForGeneralDataEvents(Runtime.Events.EventType.ItemUpdated, eventContext);


                if (_context.PersistenceMgr != null)
                {
                    Alachisoft.NCache.Persistence.Event perEvent = new Alachisoft.NCache.Persistence.Event();
                    perEvent.PersistedEventId = eventContext.EventID;
                    perEvent.PersistedEventInfo.Key = (String)key;
                    perEvent.PersistedEventId.EventType = EventType.ITEM_UPDATED_EVENT;
                    _context.PersistenceMgr.AddToPersistedEvent(perEvent);
                }


                NotifyItemUpdated(key, true, operationContext, eventContext);
                if (NCacheLog.IsInfoEnabled) NCacheLog.Info("ReplicatedCache.OnItemUpdated()", "key: " + key.ToString());
            }
            catch (Exception e)
            {
                if (NCacheLog.IsInfoEnabled) NCacheLog.Info("ReplicatedCache.OnItemUpdated()", "Key: " + key.ToString() + " Error: " + e.ToString());
            }
        }

        /// <summary>
        /// Hanlder for clustered item updated notification.
        /// </summary>
        /// <param name="info">packaged information</param>
        /// <returns>null</returns>
        private object handleNotifyUpdate(object info)
        {
            object[] args = info as object[];
            if (args != null)
            {
                OperationContext opContext = null;
                EventContext evContext = null;
                if (args.Length > 1)
                    opContext = args[1] as OperationContext;
                if (args.Length > 2)
                    evContext = args[2] as EventContext;

                NotifyItemUpdated(args[0], true, opContext, evContext);
            }
            else
                NotifyItemUpdated(info, true, null, null);

            return null;
        }


        #endregion

        #region	/                 --- OnItemRemoved ---           /

        /// <summary> 
        /// Fired when an item is removed from the cache.
        /// </summary>
        void ICacheEventsListener.OnItemRemoved(object key, object val, ItemRemoveReason reason, OperationContext operationContext, EventContext eventContext)
        {
            ((ICacheEventsListener)this).OnItemsRemoved(new object[] { key }, new object[] { val }, reason, operationContext, new EventContext[] { eventContext });
        }

        /// <summary> 
        /// Fired when multiple items are removed from the cache. 
        /// </summary>
        /// <remarks>
        /// If the items are removed due to evictions or expiration, this method replicates the
        /// removals, i.e., makes sure those items get removed from every node in the cluster. It
        /// then also triggers the cluster-wide item remove notification.
        /// </remarks>
        void ICacheEventsListener.OnItemsRemoved(object[] keys, object[] values, ItemRemoveReason reason, OperationContext operationContext, EventContext[] eventContexts)
        {
            try
            {
                // do not notify if explicitly removed by Remove()

                if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("Replicated.OnItemsRemoved()", "items evicted/expired, now replicating");
                if (IsItemRemoveNotifier)
                {
                    CacheEntry entry;
                    for (int i = 0; i < keys.Length; i++)
                    {

                        entry = (CacheEntry)values[i];
                        object value = entry.Value;

                        FilterEventContextForGeneralDataEvents(Runtime.Events.EventType.ItemRemoved, eventContexts[i]);
                        //value is contained inside eventContext
                        object data = new object[] { keys[i], reason, operationContext, eventContexts[i] };

                        if (_context.PersistenceMgr != null)
                        {
                            Alachisoft.NCache.Persistence.Event perEvent = new Alachisoft.NCache.Persistence.Event();
                            perEvent.PersistedEventId = eventContexts[i].EventID;
                            perEvent.PersistedEventInfo.Key = (String)keys[i];
                            perEvent.PersistedEventId.EventType = EventType.ITEM_REMOVED_EVENT;
                            _context.PersistenceMgr.AddToPersistedEvent(perEvent);
                        }

                        if (Cluster.IsCoordinator && _nodesInStateTransfer.Count > 0)
                        {
                            RaiseItemRemoveNotifier((ArrayList)_nodesInStateTransfer.Clone(), data);
                        }

                        if (_allowEventRaiseLocally)
                        {
                            NotifyItemRemoved(keys[i], null, reason, true, operationContext, eventContexts[i]);
                        }

                    }
                }
            }
            catch (Exception e)
            {
                Context.NCacheLog.Warn("Replicated.OnItemsRemoved", "failed: " + e.ToString());
            }
            finally
            {
                UpdateCacheStatistics();
            }
        }
        private void RaiseItemRemoveNotifier(ArrayList servers, Object data)
        {
            try
            {
                Function func = new Function((int)OpCodes.NotifyRemoval, data);
                Cluster.Multicast(servers, func, GroupRequest.GET_NONE, false, Cluster.Timeout);
            }
            catch (Exception e)
            {
                Context.NCacheLog.Error("ReplicatedCache.ItemRemoveNotifier()", e.ToString());
            }
        }

        /// <summary>
        /// Hanlder for clustered item removal notification.
        /// </summary>
        /// <param name="info">packaged information</param>
        /// <returns>null</returns>
        private object handleNotifyRemoval(object info)
        {
            object[] objs = (object[])info;
            OperationContext operationContext = null;
            if (objs.Length > 2)
                operationContext = objs[2] as OperationContext;

            NotifyItemRemoved(objs[0], null, (ItemRemoveReason)objs[1], true, operationContext, (EventContext)objs[3]);
            return null;
        }

        #endregion

        #region	/                 --- OnCustomUpdateCallback ---           /

        /// <summary> 
        /// handler for item update callback event.
        /// </summary>
        void ICacheEventsListener.OnCustomUpdateCallback(object key, object value, OperationContext operationContext, EventContext eventContext)
        {
            // specially handled in Add.
            try
            {
                if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("Replicated.OnCustomUpdateCallback()", "");

                if (value != null && value is ArrayList)
                {
                    ArrayList itemUpdateCallbackListener = (ArrayList)value;

                    if (_context.PersistenceMgr != null)
                    {
                        if (itemUpdateCallbackListener != null && itemUpdateCallbackListener.Count > 0)
                        {
                            Alachisoft.NCache.Persistence.Event perEvent = new Alachisoft.NCache.Persistence.Event();
                            perEvent.PersistedEventId = eventContext.EventID;
                            perEvent.PersistedEventInfo.Key = (String)key;
                            perEvent.PersistedEventId.EventType = EventType.ITEM_UPDATED_CALLBACK;
                            perEvent.PersistedEventInfo.CallBackInfoList = itemUpdateCallbackListener;

                            _context.PersistenceMgr.AddToPersistedEvent(perEvent);
                        }
                    }

                    if (Cluster.IsCoordinator && _nodesInStateTransfer.Count > 0)
                    {
                        Object data = new object[] { key, itemUpdateCallbackListener.Clone(), operationContext, eventContext };
                        RaiseUpdateCallbackNotifier((ArrayList)_nodesInStateTransfer.Clone(), data);
                    }

                    if (_allowEventRaiseLocally)
                    {
                        NotifyCustomUpdateCallback(key, itemUpdateCallbackListener, true, operationContext, eventContext);
                    }
                }
            }
            catch (Exception e)
            {
                Context.NCacheLog.Warn("Replicated.OnCustomUpdated", "failed: " + e.ToString());
            }
            finally
            {
                UpdateCacheStatistics();
            }
        }

        private void RaiseUpdateCallbackNotifier(ArrayList servers, Object data)
        {
            try
            {
                if (Cluster.Servers != null && Cluster.Servers.Count > 1)
                {
                    Function func = new Function((int)OpCodes.NotifyCustomUpdateCallback, data);
                    Cluster.Multicast(servers, func, GroupRequest.GET_NONE, false, Cluster.Timeout);
                }
            }
            catch (Exception e)
            {
                Context.NCacheLog.Error("ReplicatedCache.CustomUpdateCallback()", e.ToString());
            }
        }

        private object handleNotifyUpdateCallback(object info)
        {
            object[] objs = (object[])info;
            ArrayList callbackListeners = objs[1] as ArrayList;
            NotifyCustomUpdateCallback(objs[0], objs[1], true, (OperationContext)objs[2], (EventContext)objs[3]);
            return null;
        }
        #endregion

        void ICacheEventsListener.OnActiveQueryChanged(object key, QueryChangeType changeType, System.Collections.Generic.List<CQCallbackInfo> activeQueries, OperationContext operationContext, EventContext eventContext)
        {
            RaiseCQCallbackNotifier((string)key, changeType, activeQueries, operationContext, eventContext);
        }

        protected override void RaiseCQCallbackNotifier(string key, QueryChangeType changeType, List<CQCallbackInfo> queries, OperationContext operationContext, EventContext eventContext)
        {
            //persist events
            foreach (CQCallbackInfo info in queries)
            {
                // CQManager.GetClients(info.CQId) returns null for some queryID
                // This is a CQ bug that some queries are unable to get registered in case of POR/Part/Rep (more than one node)
                // queryID is generated by server and kept at two different locations differently -> CQManager and Query predicates. 
                // Inconsistent data structure produces this inconsistent scenario - POR
                IList clients = CQManager.GetClients(info.CQId);
                if (clients != null)
                {
                    foreach (string clientId in clients)
                    {
                        if (!CQManager.AllowNotification(info.CQId, clientId, changeType))
                        {
                            continue;
                        }

                        info.ClientIds.Add(clientId);
                        Runtime.Events.EventDataFilter datafilter = CQManager.GetDataFilter(info.CQId, clientId, changeType);
                        info.DataFilters.Add(clientId, datafilter);

                    }
                }

                if (_context.PersistenceMgr != null)
                {
                    Alachisoft.NCache.Persistence.Event perEvent = new Alachisoft.NCache.Persistence.Event();
                    perEvent.PersistedEventId = (EventId)eventContext.EventID.Clone();
                    perEvent.PersistedEventInfo.Key = (String)key;
                    perEvent.PersistedEventId.EventType = EventType.CQ_CALLBACK;
                    perEvent.PersistedEventId.QueryId = info.CQId;
                    perEvent.PersistedEventId.QueryChangeType = changeType;
                    perEvent.PersistedEventInfo.ClientIds = info.ClientIds;

                    _context.PersistenceMgr.AddToPersistedEvent(perEvent);
                }
            }
            NotifyCQUpdateCallback(key, changeType, queries, true, operationContext, eventContext);
        }
        private void RaiseCQCallbackNotifier(ArrayList servers, string key, QueryChangeType changeType, List<CQCallbackInfo> queries, EventContext eventContext)
        {
            try
            {
                Function func = new Function((int)OpCodes.NotifyCQUpdate, new object[] { key, changeType, queries, eventContext });
                Cluster.Multicast(servers, func, GroupRequest.GET_NONE, false, Cluster.Timeout);
            }
            catch (Exception e)
            {
                Context.NCacheLog.Error("ReplicatedCache.CQCallbackNotifier()", e.ToString());
            }
        }

        #region	/                 --- OnCustomRemoveCallback ---           /

        /// <summary> 
        /// handler for item remove callback event.
        /// </summary>
        void ICacheEventsListener.OnCustomRemoveCallback(object key, object value, ItemRemoveReason removalReason, OperationContext operationContext, EventContext eventContext)
        {
            try
            {
                // do not notify if explicitly removed by Remove()                
                if (Context.NCacheLog.IsInfoEnabled) Context.NCacheLog.Info("Replicated.OnCustomRemoveCallback()", "items evicted/expired, now replicating");

                CacheEntry entry = value as CacheEntry;
                CallbackEntry cbEntry = entry != null ? entry.Value as CallbackEntry : null;
                if (cbEntry != null)
                {


                    if (_context.PersistenceMgr != null)
                    {
                        Alachisoft.NCache.Persistence.Event perEvent = new Alachisoft.NCache.Persistence.Event();
                        perEvent.PersistedEventId = eventContext.EventID;
                        perEvent.PersistedEventInfo.Key = (String)key;
                        perEvent.PersistedEventInfo.Flag = cbEntry.Flag;
                        perEvent.PersistedEventInfo.Reason = removalReason;
                        perEvent.PersistedEventInfo.CallBackInfoList = cbEntry.ItemRemoveCallbackListener;
                        UserBinaryObject itemValue = cbEntry.Value as UserBinaryObject; ;
                        perEvent.PersistedEventInfo.Value = itemValue.DataList;

                        _context.PersistenceMgr.AddToPersistedEvent(perEvent);

                    }


                    if (Cluster.IsCoordinator && _nodesInStateTransfer.Count > 0)
                    {
                        Object data = new object[] { key, removalReason, operationContext, eventContext };
                        RaiseRemoveCallbackNotifier((ArrayList)_nodesInStateTransfer.Clone(), data);
                    }

                    if (_allowEventRaiseLocally)
                    {
                        NotifyCustomRemoveCallback(key, entry, removalReason, true, operationContext, eventContext);
                    }
                }
            }
            catch (Exception e)
            {
                Context.NCacheLog.Warn("Replicated.OnItemsRemoved", "failed: " + e.ToString());
            }
            finally
            {
                UpdateCacheStatistics();
            }
        }


        private void RaiseRemoveCallbackNotifier(ArrayList servers, Object data)
        {
            try
            {
                if (Cluster.Servers != null && Cluster.Servers.Count > 1)
                {
                    Function func = new Function((int)OpCodes.NotifyCustomRemoveCallback, data);
                    Cluster.Multicast(servers, func, GroupRequest.GET_NONE, false, Cluster.Timeout);
                }
            }
            catch (Exception e)
            {
                Context.NCacheLog.Error("ReplicatedCache.CustomRemoveCallback()", e.ToString());
            }
        }

        private object handleNotifyRemoveCallback(object info)
        {
            object[] objs = (object[])info;
            NotifyCustomRemoveCallback(objs[0], null, (ItemRemoveReason)objs[1], true, (OperationContext)objs[2], (EventContext)objs[3]);
            return null;
        }
        #endregion

        void ICacheEventsListener.OnPollNotify(string clientId, short callbackId, Runtime.Events.EventType eventType)
        {
            try
            {
                RaisePollRequestNotifier(clientId, callbackId, eventType);
            }
            catch (Exception e)
            {
                Context.NCacheLog.Warn("Replicated.OnPollNotify", "failed: " + e.ToString());
            }
        }


        #endregion




        #region Lock

        private bool Local_CanLock(object key, ref object lockId, ref DateTime lockDate, OperationContext operationContext)
        {
            CacheEntry e = Get(key, operationContext);
            if (e != null)
            {
                return !e.IsLocked(ref lockId, ref lockDate);
            }
            else
            {
                lockId = null;
            }
            return false;
        }

        public override LockOptions Lock(object key, LockExpiration lockExpiration, ref object lockId, ref DateTime lockDate, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.Lock", "");
            if (Cluster.Servers.Count > 1)
            {
                Clustered_Lock(key, lockExpiration, ref lockId, ref lockDate, operationContext);
                return new LockOptions(lockId, lockDate);
            }
            else
            {
                return Local_Lock(key, lockExpiration, lockId, lockDate, operationContext);
            }
        }

        private LockOptions handleLock(object info)
        {
            object[] param = (object[])info;
            OperationContext operationContext = null;
            if (param.Length > 4)
                operationContext = param[4] as OperationContext;

            return Local_Lock(param[0], (LockExpiration)param[3], param[1], (DateTime)param[2], operationContext);
        }

        private LockOptions Local_Lock(object key, LockExpiration lockExpiration, object lockId, DateTime lockDate, OperationContext operationContext)
        {
            if (_internalCache != null)
                return _internalCache.Lock(key, lockExpiration, ref lockId, ref lockDate, operationContext);
            return null;
        }

        public override void UnLock(object key, object lockId, bool isPreemptive, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.Unlock", "");
            if (Cluster.Servers.Count > 1)
            {
                Clustered_UnLock(key, lockId, isPreemptive, operationContext);
            }
            else
            {
                Local_UnLock(key, lockId, isPreemptive, operationContext);
            }
        }

        private void Local_UnLock(object key, object lockId, bool isPreemptive, OperationContext operationContext)
        {
            if (_internalCache != null)
                _internalCache.UnLock(key, lockId, isPreemptive, operationContext);
        }

        private void handleUnLockKey(object info)
        {
            object[] package = info as object[];
            if (package != null)
            {
                object key = package[0];
                object lockId = package[1];
                bool isPreemptive = (bool)package[2];
                OperationContext operationContext = null;
                if (package.Length > 3)
                    operationContext = package[3] as OperationContext;

                Local_UnLock(key, lockId, isPreemptive, operationContext);
            }
        }

        public override LockOptions IsLocked(object key, ref object lockId, ref DateTime lockDate, OperationContext operationContext)
        {
            if (Cluster.Servers.Count > 1)
            {
                return Clustered_IsLocked(key, ref lockId, ref lockDate, operationContext);
            }
            else
            {
                return Local_IsLocked(key, lockId, lockDate, operationContext);
            }
        }

        private LockOptions handleIsLocked(object info)
        {
            object[] param = (object[])info;
            OperationContext operationContext = null;
            if (param.Length > 3)
                operationContext = param[3] as OperationContext;

            return Local_IsLocked(param[0], param[1], (DateTime)param[2], operationContext);
        }

        private LockOptions Local_IsLocked(object key, object lockId, DateTime lockDate, OperationContext operationContext)
        {
            if (_internalCache != null)
                return _internalCache.IsLocked(key, ref lockId, ref lockDate, operationContext);
            return null;
        }
        #endregion


        internal override void StopServices()
        {
            _statusLatch.SetStatusBit(0, NodeStatus.Initializing | NodeStatus.Running);
            if (_asyncReplicator != null)
                _asyncReplicator.Dispose();
            base.StopServices();
        }


        #region /                  --- Stream Operations ---                    /

        public override bool OpenStream(string key, string lockHandle, Alachisoft.NCache.Common.Enum.StreamModes mode, string group, string subGroup, ExpirationHint hint, Alachisoft.NCache.Caching.EvictionPolicies.EvictionHint evictinHint, OperationContext operationContext)
        {
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);
            OpenStreamResult result = null;
            try
            {
                OpenStreamOperation streamOperation = new OpenStreamOperation(key, lockHandle, mode, group, subGroup, hint, evictinHint, operationContext);
                if (Cluster.Servers.Count > 1)
                {
                    Function func = new Function((int)OpCodes.OpenStream, streamOperation, false);
                    RspList rspList = Cluster.BroadcastToServers(func, GroupRequest.GET_ALL, true);
                    ClusterHelper.ValidateResponses(rspList, typeof(OpenStreamResult), _context.SerializationContext);
                    result = ClusterHelper.FindAtomicClusterOperationStatusReplicated(rspList) as OpenStreamResult;

                }
                else
                {
                    result = handleOpenStreamOperation(Cluster.LocalAddress, streamOperation);
                }
            }
            catch (StreamException)
            {
                throw;
            }
            catch (RemoteException)
            {
                throw;
            }
            catch (CacheException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new OperationFailedException(e.Message, e, false);
            }

            if (result != null)
            {
                if (result.ExecutionResult == OpenStreamResult.Result.Completed)
                    return result.LockAcquired;

                if (result.ExecutionResult == OpenStreamResult.Result.ParitalTimeout)
                {
                    Remove(key, ItemRemoveReason.Removed, false, null, false, 0, LockAccessType.IGNORE_LOCK, operationContext);
                }
                if (result.ExecutionResult == OpenStreamResult.Result.FullTimeout)
                    throw new Runtime.Exceptions.TimeoutException("Operation timeout.");

            }
            return false;
        }


        public override void CloseStream(string key, string lockHandle, OperationContext operationContext)
        {
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);
            CloseStreamResult result = null;
            try
            {
                if (Cluster.Servers.Count > 1)
                {
                    CloseStreamOperation streamOperation = new CloseStreamOperation(key, lockHandle, operationContext);
                    Function func = new Function((int)OpCodes.CloseStream, streamOperation, false);

                    RspList rspList = Cluster.BroadcastToServers(func, GroupRequest.GET_ALL, true);

                    ClusterHelper.ValidateResponses(rspList, typeof(CloseStreamResult), _context.SerializationContext);
                    result = ClusterHelper.FindAtomicClusterOperationStatusReplicated(rspList) as CloseStreamResult;

                }
                else
                    InternalCache.CloseStream(key, lockHandle, operationContext);
            }
            catch (StreamException)
            {
                throw;
            }
            catch (RemoteException)
            {
                throw;
            }
            catch (CacheException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new OperationFailedException(e.Message, e, false);
            }

            if (result != null)
            {
                if (result.ExecutionResult == ClusterOperationResult.Result.Completed)
                    return;

                if (result.ExecutionResult == ClusterOperationResult.Result.ParitalTimeout)
                {
                    Remove(key, ItemRemoveReason.Removed, false, null, false, 0, LockAccessType.IGNORE_LOCK, operationContext);
                }
                if (result.ExecutionResult == OpenStreamResult.Result.FullTimeout)

                    throw new Runtime.Exceptions.TimeoutException("Operation timeout.");
            }
        }

        public override int ReadFromStream(ref VirtualArray vBuffer, string key, string lockHandle, int offset, int length, OperationContext operationContext)
        {
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);
            ReadFromStreamResult result = null;
            try
            {

                if (_statusLatch.IsAnyBitsSet(NodeStatus.Initializing) && Cluster.Servers.Count > 1)
                {
                    ReadFromStreamOperation streamOperation = new ReadFromStreamOperation(key, lockHandle, offset, length, operationContext);
                    Function func = new Function((int)OpCodes.ReadFromStream, streamOperation, false);

                    result = Cluster.SendMessage(Cluster.Coordinator, func, GroupRequest.GET_ALL, false) as ReadFromStreamResult;
                }
                else
                {
                    int bytesRead = InternalCache.ReadFromStream(ref vBuffer, key, lockHandle, offset, length, operationContext);
                    return bytesRead;
                }
            }
            catch (StreamException)
            {
                throw;
            }
            catch (RemoteException)
            {
                throw;
            }
            catch (CacheException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new OperationFailedException(e.Message, e, false);
            }

            if (result != null)
            {
                if (result.ExecutionResult == OpenStreamResult.Result.Completed)
                {
                    vBuffer = result.Buffer;
                    return result.BytesRead;
                }

                if (result.ExecutionResult == OpenStreamResult.Result.ParitalTimeout)
                {
                    Remove(key, ItemRemoveReason.Removed, false, null, false, 0, LockAccessType.IGNORE_LOCK, operationContext);
                }
                if (result.ExecutionResult == OpenStreamResult.Result.FullTimeout)
                    throw new Runtime.Exceptions.TimeoutException("Operation timeout.");

            }
            return 0;
        }

        public override void WriteToStream(string key, string lockHandle, VirtualArray vBuffer, int srcOffset, int dstOffset, int length, OperationContext operationContext)
        {
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);
            WriteToStreamResult result = null;
            try
            {
                if (Cluster.Servers.Count > 1)
                {
                    WriteToStreamOperation streamOperation = new WriteToStreamOperation(key, lockHandle, vBuffer, srcOffset, dstOffset, length, operationContext);
                    Function func = new Function((int)OpCodes.WriteToStream, streamOperation, false);

                    RspList rspList = Cluster.BroadcastToServers(func, GroupRequest.GET_ALL, true);

                    ClusterHelper.ValidateResponses(rspList, typeof(WriteToStreamResult), _context.SerializationContext);
                    result = ClusterHelper.FindAtomicClusterOperationStatusReplicated(rspList) as WriteToStreamResult;

                }
                else
                    InternalCache.WriteToStream(key, lockHandle, vBuffer, srcOffset, dstOffset, length, operationContext);
            }
            catch (StreamException)
            {
                throw;
            }
            catch (RemoteException)
            {
                throw;
            }
            catch (CacheException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new OperationFailedException(e.Message, e, false);
            }

            if (result != null)
            {
                if (result.ExecutionResult == OpenStreamResult.Result.Completed)
                    return;

                if (result.ExecutionResult == OpenStreamResult.Result.ParitalTimeout)
                {
                    Remove(key, ItemRemoveReason.Removed, false, null, false, 0, LockAccessType.IGNORE_LOCK, operationContext);
                }
                if (result.ExecutionResult == OpenStreamResult.Result.FullTimeout)
                    throw new Runtime.Exceptions.TimeoutException("Operation timeout.");

            }

        }

        public override long GetStreamLength(string key, string lockHandle, OperationContext operationContext)
        {
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);
            GetStreamLengthResult result = null;
            try
            {
                if (Cluster.Servers.Count > 1)
                {
                    GetStreamLengthOperation streamOperation = new GetStreamLengthOperation(key, lockHandle, operationContext);
                    Function func = new Function((int)OpCodes.GetStreamLength, streamOperation, false);

                    RspList rspList = Cluster.BroadcastToServers(func, GroupRequest.GET_ALL, true);

                    ClusterHelper.ValidateResponses(rspList, typeof(GetStreamLengthResult), _context.SerializationContext);
                    result = ClusterHelper.FindAtomicClusterOperationStatusReplicated(rspList) as GetStreamLengthResult;

                }
                else
                    return InternalCache.GetStreamLength(key, lockHandle, operationContext);
            }
            catch (StreamException)
            {
                throw;
            }
            catch (RemoteException)
            {
                throw;
            }
            catch (CacheException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new OperationFailedException(e.Message, e, false);
            }

            if (result != null)
            {
                if (result.ExecutionResult == GetStreamLengthResult.Result.Completed)
                    return result.Length;

                if (result.ExecutionResult == GetStreamLengthResult.Result.ParitalTimeout)
                {
                    Remove(key, ItemRemoveReason.Removed, false, null, false, 0, LockAccessType.IGNORE_LOCK, operationContext);
                }
                if (result.ExecutionResult == OpenStreamResult.Result.FullTimeout)
                    throw new Runtime.Exceptions.TimeoutException("Operation timeout.");

            }
            return 0;
        }

        protected override OpenStreamResult handleOpenStreamOperation(Address source, OpenStreamOperation operation)
        {
            OpenStreamResult result = null;

            object[] keys = null;
            #region -- PART I -- Cascading Dependency Operation

            if (operation.Mode == Alachisoft.NCache.Common.Enum.StreamModes.Write)
            {
                keys = CacheHelper.GetKeyDependencyTable(operation.ExpirationHint);
                if (keys != null)
                {
                    Hashtable goodKeysTable = _internalCache.Contains(keys, operation.OperationContext);

                    if (!goodKeysTable.ContainsKey("items-found"))
                        throw new OperationFailedException("One of the dependency keys does not exist.");

                    if (goodKeysTable["items-found"] == null)
                        throw new OperationFailedException("One of the dependency keys does not exist.");

                    if (goodKeysTable["items-found"] == null || (((ArrayList)goodKeysTable["items-found"]).Count != keys.Length))
                        throw new OperationFailedException("One of the dependency keys does not exist.");

                }
            }
            #endregion

            result = base.handleOpenStreamOperation(source, operation);

            #region -- PART II -- Cascading Dependency Operation
            if (keys != null && result != null && result.LockAcquired && operation.Mode == Alachisoft.NCache.Common.Enum.StreamModes.Write)
            {
                Hashtable table = new Hashtable();
                for (int i = 0; i < keys.Length; i++)
                {
                    if (table[keys[i]] == null)
                        table.Add(keys[i], new ArrayList());
                    ((ArrayList)table[keys[i]]).Add(operation.Key);
                }
                try
                {
                    table = _internalCache.AddDepKeyList(table, operation.OperationContext);
                }
                catch (Exception e)
                {
                    throw e;
                }

                if (table != null)
                {
                    IDictionaryEnumerator en = table.GetEnumerator();
                    while (en.MoveNext())
                    {
                        if (en.Value is bool && !((bool)en.Value))
                        {
                            Local_Remove(operation.Key, ItemRemoveReason.Removed, source, null, null, null, false, null, 0, LockAccessType.IGNORE_LOCK, operation.OperationContext);
                            throw new OperationFailedException("One of the dependency keys does not exist.");
                        }
                    }
                }

            }
            #endregion
            return result;
        }
        #endregion

        public override void UnRegisterCQ(string serverUniqueId, string clientUniqueId, string clientId)
        {
            /// Wait until the object enters the running status
            _statusLatch.WaitForAny(NodeStatus.Running);

            if (_internalCache == null)
                throw new InvalidOperationException();

            if (Cluster.Servers.Count > 1)
            {
                Clustered_UnRegisterCQ(serverUniqueId, clientUniqueId, clientId);
            }
            else
            {
                Local_UnRegisterCQ(serverUniqueId, clientUniqueId, clientId);
            }
        }

        private void Clustered_UnRegisterCQ(string serverUniqueId, string clientUniqueId, string clientId)
        {
            Clustered_UnRegisterCQ(Cluster.Servers, serverUniqueId, clientUniqueId, clientId, false);
        }

        public void Local_UnRegisterCQ(string serverUniqueId, string clientUniqueId, string clientId)
        {
            if (CQManager.UnRegister(serverUniqueId, clientUniqueId, clientId))
            {
                _internalCache.UnRegisterCQ(serverUniqueId);
            }
        }

        public override string RegisterCQ(string query, IDictionary values, string clientUniqueId, string clientId, bool notifyAdd, bool notifyUpdate, bool notifyRemove, OperationContext operationContext, QueryDataFilters datafilters)
        {
            string queryId = null;
            /// Wait until the object enters the running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null)
                throw new InvalidOperationException();

            if (Cluster.Servers.Count > 1)
            {
                queryId = Clustered_RegisterCQ(query, values, clientUniqueId, clientId, notifyAdd, notifyUpdate, notifyRemove, operationContext, datafilters);
            }
            else
            {
                queryId = Local_RegisterCQ(query, values, clientUniqueId, clientId, notifyAdd, notifyUpdate, notifyRemove, operationContext, datafilters);
            }

            return queryId;
        }

        public string Clustered_RegisterCQ(string query, IDictionary values, string clientUniqueId, string clientId, bool notifyAdd, bool notifyUpdate, bool notifyRemove, OperationContext operationContext, QueryDataFilters datafilters)
        {
            ContinuousQuery cQuery = CQManager.GetCQ(query, values);
            Clustered_RegisterCQ(Cluster.Servers, cQuery, clientUniqueId, clientId, notifyAdd, notifyUpdate, notifyRemove, false, operationContext, datafilters);
            return cQuery.UniqueId;
        }

        public string Local_RegisterCQ(string query, IDictionary values, string clientUniqueId, string clientId, bool notifyAdd, bool notifyUpdate, bool notifyRemove, OperationContext operationContext, QueryDataFilters datafilters)
        {
            string queryId = null;
            ContinuousQuery cQuery = CQManager.GetCQ(query, values);
            if (CQManager.Exists(cQuery))
            {
                CQManager.Update(cQuery, clientId, clientUniqueId, notifyAdd, notifyUpdate, notifyRemove, datafilters);
            }
            else
            {
                _internalCache.RegisterCQ(cQuery, operationContext);
                CQManager.Register(cQuery, clientId, clientUniqueId, notifyAdd, notifyUpdate, notifyRemove, datafilters);
            }
            queryId = cQuery.UniqueId;
            return queryId;
        }

        protected override QueryResultSet Local_SearchCQ(string query, IDictionary values, string clientUniqueId, string clientId, bool notifyAdd, bool notifyUpdate, bool notifyRemove, OperationContext operationContext, QueryDataFilters datafilters)
        {
            QueryResultSet resultSet = null;
            ContinuousQuery cQuery = CQManager.GetCQ(query, values);
            if (Cluster.Servers.Count > 1)
            {
                Clustered_RegisterCQ(Cluster.Servers, cQuery, clientUniqueId, clientId, notifyAdd, notifyUpdate, notifyRemove, true, operationContext, datafilters);
            }
            if (CQManager.Exists(cQuery))
            {
                resultSet = _internalCache.SearchCQ(cQuery.UniqueId, operationContext);
                CQManager.Update(cQuery, clientId, clientUniqueId, notifyAdd, notifyUpdate, notifyRemove, datafilters);
            }
            else
            {
                resultSet = _internalCache.SearchCQ(cQuery, operationContext);
                CQManager.Register(cQuery, clientId, clientUniqueId, notifyAdd, notifyUpdate, notifyRemove, datafilters);
            }
            resultSet.CQUniqueId = cQuery.UniqueId;
            return resultSet;
        }

        protected override QueryResultSet Local_SearchEntriesCQ(string query, IDictionary values, string clientUniqueId, string clientId, bool notifyAdd, bool notifyUpdate, bool notifyRemove, OperationContext operationContext, QueryDataFilters datafilters)
        {
            QueryResultSet resultSet = null;
            ContinuousQuery cQuery = CQManager.GetCQ(query, values);
            if (Cluster.Servers.Count > 1)
            {
                Clustered_RegisterCQ(Cluster.Servers, cQuery, clientUniqueId, clientId, notifyAdd, notifyUpdate, notifyRemove, true, operationContext, datafilters);
            }
            if (CQManager.Exists(cQuery))
            {
                resultSet = _internalCache.SearchEntriesCQ(cQuery.UniqueId, operationContext);
                CQManager.Update(cQuery, clientId, clientUniqueId, notifyAdd, notifyUpdate, notifyRemove, datafilters);
            }
            else
            {
                resultSet = _internalCache.SearchEntriesCQ(cQuery, operationContext);
                CQManager.Register(cQuery, clientId, clientUniqueId, notifyAdd, notifyUpdate, notifyRemove, datafilters);
            }
            resultSet.CQUniqueId = cQuery.UniqueId;
            return resultSet;
        }

        /// <summary>
        /// Retrieve the list of keys from the cache for the given group or sub group.
        /// </summary>
        public override ArrayList GetGroupKeys(string group, string subGroup, OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            ArrayList list = null;
            ArrayList dests = new ArrayList();

            if (IsInStateTransfer())
            {
                list = Clustered_GetGroupKeys(GetDestInStateTransfer(), group, subGroup, operationContext);
            }
            else
            {
                list = Local_GetGroupKeys(group, subGroup, operationContext);
            }

            return list;
        }

        public override Common.Events.PollingResult Poll(OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            PollingResult result = null;
            ArrayList dests = new ArrayList();

            if (IsInStateTransfer())
            {
                result = Clustered_Poll(GetDestInStateTransfer(), operationContext);
            }
            else
            {
                result = Local_Poll(operationContext);
            }

            return result;
        }

        public override void RegisterPollingNotification(short callbackId, OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            ArrayList dests = new ArrayList();

            if (IsInStateTransfer())
            {
                Clustered_RegisterPollingNotification(GetDestInStateTransfer(), callbackId, operationContext, true);
            }
            else
            {
                Local_RegisterPollingNotification(callbackId, operationContext);
            }
        }

        /// <summary>
        /// Retrieve the list of key and value pairs from the cache for the given group or sub group.
        /// </summary>
        public override HashVector GetGroupData(string group, string subGroup, OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            HashVector list = null;

            if (IsInStateTransfer())
            {
                list = Clustered_GetGroupData(GetDestInStateTransfer(), group, subGroup, operationContext);
            }
            else
            {
                list = Local_GetGroupData(group, subGroup, operationContext);
            }
            if (Cluster.Servers.Count > 1)
            {

                if (list != null)
                {

                    ClusteredArrayList updateIndiceKeyList = null;
                    IDictionaryEnumerator ine = list.GetEnumerator();
                    while (ine.MoveNext())
                    {
                        CacheEntry e = (CacheEntry)ine.Value;
                        if (e == null)
                        {
                            _stats.BumpMissCount();
                        }
                        else
                        {
                            if (updateIndiceKeyList == null) updateIndiceKeyList = new ClusteredArrayList();
                            _stats.BumpHitCount();
                            // update the indexes on other nodes in the cluster
                            if ((e.ExpirationHint != null && e.ExpirationHint.IsVariant))
                            {
                                updateIndiceKeyList.Add(ine.Key);
                            }
                        }
                    }
                    if (updateIndiceKeyList != null && updateIndiceKeyList.Count > 0)
                    {
                        UpdateIndices(updateIndiceKeyList.ToArray(), true, operationContext);
                    }
                }

            }

            return list;
        }

        /// <summary>
        /// Retrieve the list of keys from the cache for the given tags.
        /// </summary>
        internal override ICollection GetTagKeys(string[] tags, TagComparisonType comparisonType, OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            ICollection list = null;
            ArrayList dests = new ArrayList();

            if (IsInStateTransfer())
            {
                list = Clustered_GetTagKeys(GetDestInStateTransfer(), tags, comparisonType, operationContext);
            }
            else
            {
                list = Local_GetTagKeys(tags, comparisonType, operationContext);
            }

            return list;
        }

        /// <summary>
        /// Retrieve the list of key and value pairs from the cache for the given tags.
        /// </summary>
        public override HashVector GetTagData(string[] tags, TagComparisonType comparisonType, OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            HashVector list = null;

            if (IsInStateTransfer())
            {
                list = Clustered_GetTagData(GetDestInStateTransfer(), tags, comparisonType, operationContext);
            }
            else
            {
                list = Local_GetTagData(tags, comparisonType, operationContext);
            }
            if (Cluster.Servers.Count > 1)
            {

                if (list != null)
                {

                    ClusteredArrayList updateIndiceKeyList = null;
                    IDictionaryEnumerator ine = list.GetEnumerator();
                    while (ine.MoveNext())
                    {
                        CacheEntry e = (CacheEntry)ine.Value;
                        if (e == null)
                        {
                            _stats.BumpMissCount();
                        }
                        else
                        {
                            if (updateIndiceKeyList == null) updateIndiceKeyList = new ClusteredArrayList();
                            _stats.BumpHitCount();
                            // update the indexes on other nodes in the cluster
                            if ((e.ExpirationHint != null && e.ExpirationHint.IsVariant))
                            {
                                updateIndiceKeyList.Add(ine.Key);
                            }
                        }
                    }
                    if (updateIndiceKeyList != null && updateIndiceKeyList.Count > 0)
                    {
                        UpdateIndices(updateIndiceKeyList.ToArray(), true, operationContext);
                    }
                }

            }

            return list;
        }

        /// Remove the list of key from the cache for the given tags.
        /// </summary>
        public override Hashtable Remove(string[] tags, TagComparisonType comparisonType, bool notify, OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            Hashtable result = null;
            ArrayList dests = new ArrayList();

            if (IsInStateTransfer())
            {
                result = Clustered_RemoveByTag(GetDestInStateTransfer(), tags, comparisonType, notify, operationContext);
            }
            else
            {
                result = Local_RemoveTag(tags, comparisonType, notify, operationContext);
            }

            return result;
        }

        /// <summary>
        /// Remove the group from cache.
        /// </summary>
        /// <param name="group">group to be removed.</param>
        /// <param name="subGroup">subGroup to be removed.</param>
        public override Hashtable Remove(string group, string subGroup, bool notify, OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            Hashtable result = null;
            ArrayList dests = new ArrayList();

            if (IsInStateTransfer())
            {
                result = Clustered_RemoveGroup(GetDestInStateTransfer(), group, subGroup, notify, operationContext);
            }
            else
            {
                result = Local_RemoveGroup(group, subGroup, notify, operationContext);
            }

            return result;
        }

        /// <summary>
        /// Retrieve the list of keys from the cache based on the specified query.
        /// </summary>
        public override QueryResultSet Search(string query, IDictionary values, OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            QueryResultSet result = null;
            ArrayList dests = new ArrayList();

            if (IsInStateTransfer())
            {
                result = Clustered_Search(GetDestInStateTransfer(), query, values, operationContext);
            }
            else
            {
                result = Local_Search(query, values, operationContext);
            }

            return result;
        }

        /// <summary>
        /// Retrieve the list of keys and values from the cache based on the specified query.
        /// </summary>
        public override QueryResultSet SearchEntries(string query, IDictionary values, OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            QueryResultSet result = null;
            ArrayList dests = new ArrayList();


            if (IsInStateTransfer())
            {
                result = Clustered_SearchEntries(GetDestInStateTransfer(), query, values, operationContext);
            }
            else
            {
                result = Local_SearchEntries(query, values, operationContext);
            }

            if (Cluster.Servers.Count > 1)
            {

                if (result != null)
                {
                    if (result.UpdateIndicesKeys != null)
                    {
                        UpdateIndices(((ClusteredArrayList)result.UpdateIndicesKeys).ToArray(), true, operationContext);
                    }
                }

            }

            return result;
        }

        /// <summary>
        /// Retrieve the list of keys from the cache based on the specified query and also register for nitifications.
        /// </summary>
        public override QueryResultSet SearchCQ(string query, IDictionary values, string clientUniqueId, string clientId, bool notifyAdd, bool notifyUpdate, bool notifyRemove, OperationContext operationContext, QueryDataFilters datafilters)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            QueryResultSet result = null;
            ArrayList dests = new ArrayList();

            if (IsInStateTransfer())
            {
                result = Clustered_SearchCQ(GetDestInStateTransfer(), query, values, clientUniqueId, clientId, notifyAdd, notifyUpdate, notifyRemove, operationContext, datafilters);
            }
            else
            {
                result = Local_SearchCQ(query, values, clientUniqueId, clientId, notifyAdd, notifyUpdate, notifyRemove, operationContext, datafilters);
            }

            return result;
        }

        /// <summary>
        /// Retrieve the list of keys and values from the cache based on the specified query and also register for nitifications.
        /// </summary>
        public override QueryResultSet SearchEntriesCQ(string query, IDictionary values, string clientUniqueId, string clientId, bool notifyAdd, bool notifyUpdate, bool notifyRemove, OperationContext operationContext, QueryDataFilters datafilters)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            QueryResultSet result = null;
            ArrayList dests = new ArrayList();

            if (IsInStateTransfer())
            {
                result = Clustered_SearchEntriesCQ(GetDestInStateTransfer(), query, values, clientUniqueId, clientId, notifyAdd, notifyUpdate, notifyRemove, operationContext, datafilters);
            }
            else
            {
                result = Local_SearchEntriesCQ(query, values, clientUniqueId, clientId, notifyAdd, notifyUpdate, notifyRemove, operationContext, datafilters);
            }

            return result;
        }


        public override bool IsInStateTransfer()
        {
            return _statusLatch.IsAnyBitsSet(NodeStatus.Initializing);
        }

        protected override bool VerifyClientViewId(long clientLastViewId)
        {
            return true;
        }

        protected override ArrayList GetDestInStateTransfer()
        {
            ArrayList list = new ArrayList();
            list.Add(this.Cluster.Coordinator);
            return list;
        }

        public override bool IsOperationAllowed(object key, AllowedOperationType opType)
        {
            if (base._shutdownServers != null && base._shutdownServers.Count > 0)
            {
                if (opType == AllowedOperationType.AtomicWrite) return false;

                // reads will be allowed on shut down node too?
                if (IsInStateTransfer())
                {
                    if (base.IsShutdownServer(Cluster.Coordinator))
                    {
                        //if (opType == AllowedOperationType.AtomicRead)
                        return false;
                    }
                }
            }
            return true;
        }

        public override bool IsOperationAllowed(IList keys, AllowedOperationType opType, OperationContext operationContext)
        {
            if (base._shutdownServers != null && base._shutdownServers.Count > 0)
            {
                if (opType == AllowedOperationType.BulkWrite) return false;

                if (IsInStateTransfer())
                {
                    if (base.IsShutdownServer(Cluster.Coordinator))
                    {
                        //if (opType == AllowedOperationType.BulkRead)
                        return false;
                    }
                }

            }

            return true;
        }

        public override bool IsOperationAllowed(AllowedOperationType opType, OperationContext operationContext)
        {
            if (base._shutdownServers != null && base._shutdownServers.Count > 0)
            {
                if (opType == AllowedOperationType.BulkWrite) return false;

                if (opType == AllowedOperationType.BulkRead)
                {
                    if (IsInStateTransfer())
                    {
                        if (base.IsShutdownServer(Cluster.Coordinator))
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }


        public void OnTaskCallback(string taskId, object value, OperationContext operationContext, EventContext eventContext)
        {
            throw new NotSupportedException();
        }
        #region--------------------------------Cache Data Reader----------------------------------------------
        /// <summary>
        /// Open reader based on the query specified.
        /// </summary>
        public override ClusteredList<ReaderResultSet> ExecuteReader(string query, IDictionary values, bool getData, int chunkSize, bool isInproc, OperationContext operationContext)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            ClusteredList<ReaderResultSet> resultList = new ClusteredList<ReaderResultSet>();


            if (IsInStateTransfer())//open reader on cordinator node
            {
                resultList = Clustered_ExecuteReader(GetDestInStateTransfer(), query, values, getData, chunkSize, operationContext);
            }
            else
            {
                ReaderResultSet result = _internalCache.Local_ExecuteReader(query, values, getData, chunkSize, isInproc, operationContext);
                resultList.Add(result);
            }
            return resultList;
        }

        public override List<ReaderResultSet> ExecuteReaderCQ(string query, IDictionary values, bool getData, int chunkSize, string clientUniqueId, string clientId,
            bool notifyAdd, bool notifyUpdate, bool notifyRemove, OperationContext operationContext,
            QueryDataFilters datafilters, bool isInproc)
        {
            if (_internalCache == null) throw new InvalidOperationException();

            List<ReaderResultSet> resultList = new List<ReaderResultSet>();


            if (IsInStateTransfer())//open reader on cordinator node
            {
                resultList = Clustered_ExecuteReaderCQ(GetDestInStateTransfer(), query, values, getData, chunkSize, clientUniqueId, clientId, notifyAdd, notifyUpdate, notifyRemove, operationContext, datafilters, isInproc);
            }
            else
            {
                ReaderResultSet result = _internalCache.Local_ExecuteReaderCQ(query, values, getData, chunkSize, clientUniqueId, clientId, notifyAdd, notifyUpdate, notifyRemove, operationContext, datafilters, isInproc);
                if (result != null)
                    resultList.Add(result);
            }
            return resultList;
        }
        #endregion


        #region	/                 --- Replicated Touch ---           /

        internal override void Touch(List<string> keys, OperationContext operationContext)
        {
            if (ServerMonitor.MonitorActivity) ServerMonitor.LogClientActivity("RepCache.Touch", "");
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null) throw new InvalidOperationException();

            if (IsInStateTransfer())
            {
                ArrayList dests = GetDestInStateTransfer();
                Clustered_Touch(dests[0] as Address, keys, operationContext);
            }
            else
            {
                Local_Touch(keys, operationContext);
            }

            // Replicate touch to other nodes.
            UpdateIndices(keys.ToArray(), true, operationContext);

        }

        #endregion

        #region Pub/Sub
        public override bool AssignmentOperation(MessageInfo messageInfo, SubscriptionInfo subscriptionInfo, TopicOperationType type, OperationContext context)

        {
            if (ServerMonitor.MonitorActivity)
                ServerMonitor.LogClientActivity("Replicated.AssignmentOperation()", "Begins");

            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null)
                throw new InvalidOperationException();

            bool result = false;
            try
            {
                if (Cluster.Servers.Count > 1)
                {
                    AssignmentOperation assignmentOperation = new AssignmentOperation(messageInfo, subscriptionInfo, type, context);
                    Function func = new Function((int)OpCodes.AssignmentOperation, assignmentOperation, false);

                    RspList results = Cluster.BroadcastToMultiple(Cluster.Servers, func, GroupRequest.GET_ALL, true);

                    ClusterHelper.ValidateResponses(results, typeof(bool), Name);

                    /// Check if the operation failed on any node.
                    result = ClusterHelper.FindAtomicResponseReplicated(results);
                }
                else
                {
                    result = _internalCache.AssignmentOperation(messageInfo, subscriptionInfo, type, context);
                }

            }
            catch (Exception e)
            {
                throw new GeneralFailureException(e.Message, e);
            }
            finally
            {
                if (ServerMonitor.MonitorActivity)
                    ServerMonitor.LogClientActivity("Replicated.AssignmentOperation()", "Ends");
            }
            return result;

        }

        public override void RemoveMessages(IList<MessageInfo> messagesTobeRemoved, MessageRemovedReason reason, OperationContext context)

        {
            if (ServerMonitor.MonitorActivity)
                ServerMonitor.LogClientActivity("Replicated.RemoveMessages()", "Begins");


            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null)
                throw new InvalidOperationException();

            try
            {
                if (Cluster.Servers.Count > 1)
                {

                    RemoveMessagesOperation removeOperation = new RemoveMessagesOperation(messagesTobeRemoved, reason, context);
                    Function func = new Function((int)OpCodes.RemoveMessages, removeOperation, false);


                    RspList results = Cluster.BroadcastToMultiple(Cluster.Servers, func, GroupRequest.GET_ALL, true);
                    ClusterHelper.ValidateResponses(results, null, Name);
                }
                else
                {

                    _internalCache.RemoveMessages(messagesTobeRemoved, reason, context);

                }
            }
            catch (Exception e)
            {
                throw new GeneralFailureException(e.Message, e);
            }
            finally
            {
                if (ServerMonitor.MonitorActivity)

                    ServerMonitor.LogClientActivity("Replicated.RemoveMessages()", "Ends");
            }
        }

        public override bool StoreMessage(string topic, Messaging.Message message, OperationContext context)
        {
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);
            try
            {
                if (Cluster.Servers.Count > 1)
                {
                    StoreMessageOperation messageOperation = new StoreMessageOperation(topic, message, context);
                    Function func = new Function((int)OpCodes.StoreMessage, messageOperation, false);

                    RspList rspList = Cluster.BroadcastToServers(func, GroupRequest.GET_ALL, true);

                    ClusterHelper.ValidateResponses(rspList, typeof(bool), _context.SerializationContext);

                }
                else if (InternalCache != null)
                    InternalCache.StoreMessage(topic, message, context);
            }
            catch (StreamException)
            {
                throw;
            }
            catch (RemoteException)
            {
                throw;
            }
            catch (CacheException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new OperationFailedException(e.Message, e, false);
            }

            return true;
        }

        public override void AcknowledgeMessageReceipt(string clientId, IDictionary<string, IList<string>> topicWiseMessageIds, OperationContext operationContext)

        {
            if (ServerMonitor.MonitorActivity)
                ServerMonitor.LogClientActivity("Replicated.AssignmentOperation()", "Begins");
            /// Wait until the object enters any running status
            _statusLatch.WaitForAny(NodeStatus.Initializing | NodeStatus.Running);

            if (_internalCache == null)
                throw new InvalidOperationException();
            try
            {
                if (Cluster.Servers.Count > 1)
                {
                    AcknowledgeMessageOperation assignmentOperation = new AcknowledgeMessageOperation(clientId, topicWiseMessageIds, operationContext);
                    Function func = new Function((int)OpCodes.Message_Acknowldegment, assignmentOperation, false);

                    RspList results = Cluster.BroadcastToMultiple(Cluster.Servers, func, GroupRequest.GET_ALL, true);
                }
                else
                {
                    _internalCache.AcknowledgeMessageReceipt(clientId, topicWiseMessageIds, operationContext);
                }
            }
            catch (Exception e)
            {
                throw new GeneralFailureException(e.Message, e);
            }
            finally
            {
                if (ServerMonitor.MonitorActivity)
                    ServerMonitor.LogClientActivity("Replicated.AssignmentOperation()", "Ends");
            }
        }


        #endregion
        public override void LogBackingSource()
        {
            _context.DsMgr._writeBehindAsyncProcess.GetCacheLogs();

        }
    }

}


