﻿#if !NOSOCKET
using NBitcoin.Protocol.Behaviors;
using NBitcoin.Protocol.Filters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NBitcoin.Protocol
{
	public enum NodeState : int
	{
		Failed,
		Offline,
		Disconnecting,
		Connected,
		HandShaked
	}

	public class NodeDisconnectReason
	{
		public string Reason
		{
			get;
			set;
		}
		public Exception Exception
		{
			get;
			set;
		}
	}

	public class NodeRequirement
	{
		public ProtocolVersion? MinVersion
		{
			get;
			set;
		}
		public NodeServices RequiredServices
		{
			get;
			set;
		}
	}


	public delegate void NodeEventHandler(Node node);
	public delegate void NodeEventMessageIncoming(Node node, IncomingMessage message);
	public delegate void NodeStateEventHandler(Node node, NodeState oldState);
	public class Node : IDisposable
	{
		internal class SentMessage
		{
			public Payload Payload;
			public TaskCompletionSource<bool> Completion;
			public Guid ActivityId;
		}
		public class NodeConnection
		{
			private readonly Node _Node;
			public Node Node
			{
				get
				{
					return _Node;
				}
			}
			readonly Socket _Socket;
			public Socket Socket
			{
				get
				{
					return _Socket;
				}
			}
			private readonly ManualResetEvent _Disconnected;
			public ManualResetEvent Disconnected
			{
				get
				{
					return _Disconnected;
				}
			}
			private readonly CancellationTokenSource _Cancel;
			public CancellationTokenSource Cancel
			{
				get
				{
					return _Cancel;
				}
			}
#if NOTRACESOURCE
			internal
#else
			public
#endif
 TraceCorrelation TraceCorrelation
			{
				get
				{
					return Node.TraceCorrelation;
				}
			}

			public NodeConnection(Node node, Socket socket)
			{
				_Node = node;
				_Socket = socket;
				_Disconnected = new ManualResetEvent(false);
				_Cancel = new CancellationTokenSource();
			}

			internal BlockingCollection<SentMessage> Messages = new BlockingCollection<SentMessage>(new ConcurrentQueue<SentMessage>());
			public void BeginListen()
			{
				new Thread(() =>
				{
					SentMessage processing = null;
					Exception unhandledException = null;
					bool isVerbose = NodeServerTrace.Trace.Switch.ShouldTrace(TraceEventType.Verbose);
					try
					{
						foreach(var kv in Messages.GetConsumingEnumerable(Cancel.Token))
						{
							processing = kv;
							var payload = kv.Payload;
							var message = new Message();
							message.Magic = _Node.Network.Magic;
							message.Payload = payload;
							if(isVerbose)
							{
								Trace.CorrelationManager.ActivityId = kv.ActivityId;
								if(kv.ActivityId != TraceCorrelation.Activity)
								{
									NodeServerTrace.Transfer(TraceCorrelation.Activity);
									Trace.CorrelationManager.ActivityId = TraceCorrelation.Activity;
								}
								NodeServerTrace.Verbose("Sending message " + message);
							}
							var bytes = message.ToBytes(_Node.Version);
							_Node.Counter.AddWritten(bytes.LongLength);
							var ar = Socket.BeginSend(bytes, 0, bytes.Length, SocketFlags.None, null, null);
							WaitHandle.WaitAny(new WaitHandle[] { ar.AsyncWaitHandle, Cancel.Token.WaitHandle }, -1);
							if(!Cancel.Token.IsCancellationRequested)
							{
								Socket.EndSend(ar);
								processing.Completion.SetResult(true);
								processing = null;
							}
						}
					}
					catch(OperationCanceledException)
					{
					}
					catch(Exception ex)
					{
						unhandledException = ex;
					}

					if(processing != null)
						Messages.Add(processing);

					foreach(var pending in Messages)
					{
						if(isVerbose)
						{
							Trace.CorrelationManager.ActivityId = pending.ActivityId;
							if(pending != processing && pending.ActivityId != TraceCorrelation.Activity)
								NodeServerTrace.Transfer(TraceCorrelation.Activity);
							Trace.CorrelationManager.ActivityId = TraceCorrelation.Activity;
							NodeServerTrace.Verbose("The connection cancelled before the message was sent");
						}
						pending.Completion.SetException(new OperationCanceledException("The peer has been disconnected"));
					}
					Messages = new BlockingCollection<SentMessage>(new ConcurrentQueue<SentMessage>());
					NodeServerTrace.Information("Stop sending");
					Cleanup(unhandledException);
				}).Start();
				new Thread(() =>
				{
					_ListenerThreadId = Thread.CurrentThread.ManagedThreadId;
					using(TraceCorrelation.Open(false))
					{
						NodeServerTrace.Information("Listening");
						Exception unhandledException = null;
						byte[] buffer = _Node._ReuseBuffer ? new byte[1024 * 1024] : null;
						var stream = new Message.CustomNetworkStream(Socket, false);
						try
						{
							while(!Cancel.Token.IsCancellationRequested)
							{
								PerformanceCounter counter;

								var message = Message.ReadNext(stream, Node.Network, Node.Version, Cancel.Token, buffer, out counter);
								if(NodeServerTrace.Trace.Switch.ShouldTrace(TraceEventType.Verbose))
									NodeServerTrace.Verbose("Receiving message : " + message.Command + " (" + message.Payload + ")");
								Node.LastSeen = DateTimeOffset.UtcNow;
								Node.Counter.Add(counter);
								Node.OnMessageReceived(new IncomingMessage()
								{
									Message = message,
									Socket = Socket,
									Node = Node
								});
							}
						}
						catch(OperationCanceledException)
						{
						}
						catch(Exception ex)
						{
							unhandledException = ex;
						}
						NodeServerTrace.Information("Stop listening");
						Cleanup(unhandledException);
					}
				}).Start();
			}

			int _CleaningUp;
			public int _ListenerThreadId;
			private void Cleanup(Exception unhandledException)
			{
				if(Interlocked.CompareExchange(ref _CleaningUp, 1, 0) == 1)
					return;
				if(!Cancel.IsCancellationRequested)
				{
					Node.State = NodeState.Failed;
					NodeServerTrace.Error("Connection to server stopped unexpectedly", unhandledException);
					Node.DisconnectReason = new NodeDisconnectReason()
					{
						Reason = "Unexpected exception while connecting to socket",
						Exception = unhandledException
					};
				}

				if(Node.State != NodeState.Failed)
					Node.State = NodeState.Offline;

				_Cancel.Cancel();
				Utils.SafeCloseSocket(Socket);
				_Disconnected.Set(); //Set before behavior detach to prevent deadlock
				foreach(var behavior in _Node.Behaviors)
				{
					try
					{
						behavior.Detach();
					}
					catch(Exception ex)
					{
						NodeServerTrace.Error("Error while detaching behavior " + behavior.GetType().FullName, ex);
					}
				}
			}

		}

		public DateTimeOffset ConnectedAt
		{
			get;
			private set;
		}

		volatile NodeState _State = NodeState.Offline;
		public NodeState State
		{
			get
			{
				return _State;
			}
			private set
			{
				TraceCorrelation.LogInside(() => NodeServerTrace.Information("State changed from " + _State + " to " + value));
				var previous = _State;
				_State = value;
				if(previous != _State)
				{
					OnStateChanged(previous);
					if(value == NodeState.Failed || value == NodeState.Offline)
					{
						TraceCorrelation.LogInside(() => NodeServerTrace.Trace.TraceEvent(TraceEventType.Stop, 0, "Communication closed"));
						OnDisconnected();
					}
				}
			}
		}

		public event NodeStateEventHandler StateChanged;
		private void OnStateChanged(NodeState previous)
		{
			var stateChanged = StateChanged;
			if(stateChanged != null)
			{
				foreach(var handler in stateChanged.GetInvocationList().Cast<NodeStateEventHandler>())
				{
					try
					{
						handler.DynamicInvoke(this, previous);
					}
					catch(TargetInvocationException ex)
					{
						TraceCorrelation.LogInside(() => NodeServerTrace.Error("Error while StateChanged event raised", ex.InnerException));
					}
				}
			}
		}

		private readonly NodeFiltersCollection _Filters = new NodeFiltersCollection();
		public NodeFiltersCollection Filters
		{
			get
			{
				return _Filters;
			}
		}

		public event NodeEventMessageIncoming MessageReceived;
		protected void OnMessageReceived(IncomingMessage message)
		{
			var version = message.Message.Payload as VersionPayload;
			if(version != null && State == NodeState.HandShaked)
			{
				if((uint)message.Node.Version >= 70002)
					message.Node.SendMessageAsync(new RejectPayload()
					{
						Code = RejectCode.DUPLICATE
					});
			}

			var last = new ActionFilter((m, n) =>
			{
				MessageProducer.PushMessage(m);
				var messageReceived = MessageReceived;
				if(messageReceived != null)
				{
					foreach(var handler in messageReceived.GetInvocationList().Cast<NodeEventMessageIncoming>())
					{
						try
						{
							handler.DynamicInvoke(this, m);
						}
						catch(TargetInvocationException ex)
						{
							TraceCorrelation.LogInside(() => NodeServerTrace.Error("Error while OnMessageReceived event raised", ex.InnerException), false);
						}
					}
				}
			});

			var enumerator = Filters.Concat(new[] { last }).GetEnumerator();
			FireFilters(enumerator, message);
		}


		private void OnSendingMessage(Payload payload, Action final)
		{
			var enumerator = Filters.Concat(new[] { new ActionFilter(null, (n, p, a) => final()) }).GetEnumerator();
			FireFilters(enumerator, payload);
		}

		private void FireFilters(IEnumerator<INodeFilter> enumerator, Payload payload)
		{
			if(enumerator.MoveNext())
			{
				var filter = enumerator.Current;
				try
				{
					filter.OnSendingMessage(this, payload, () => FireFilters(enumerator, payload));
				}
				catch(Exception ex)
				{
					TraceCorrelation.LogInside(() => NodeServerTrace.Error("Unhandled exception raised by a node filter (OnSendingMessage)", ex.InnerException), false);
				}
			}
		}


		private void FireFilters(IEnumerator<INodeFilter> enumerator, IncomingMessage message)
		{
			if(enumerator.MoveNext())
			{
				var filter = enumerator.Current;
				try
				{
					filter.OnReceivingMessage(message, () => FireFilters(enumerator, message));
				}
				catch(Exception ex)
				{
					TraceCorrelation.LogInside(() => NodeServerTrace.Error("Unhandled exception raised by a node filter (OnReceivingMessage)", ex.InnerException), false);
				}
			}
		}

		public event NodeEventHandler Disconnected;
		private void OnDisconnected()
		{
			var disconnected = Disconnected;
			if(disconnected != null)
			{
				foreach(var handler in disconnected.GetInvocationList().Cast<NodeEventHandler>())
				{
					try
					{
						handler.DynamicInvoke(this);
					}
					catch(TargetInvocationException ex)
					{
						TraceCorrelation.LogInside(() => NodeServerTrace.Error("Error while Disconnected event raised", ex.InnerException));
					}
				}
			}
		}


		internal readonly NodeConnection _Connection;



		/// <summary>
		/// Connect to a random node on the network
		/// </summary>
		/// <param name="network">The network to connect to</param>
		/// <param name="addrman">The addrman used for finding peers</param>
		/// <param name="parameters">The parameters used by the found node</param>
		/// <param name="connectedAddresses">The already connected addresses, the new address will be select outside of existing groups</param>
		/// <returns></returns>
		public static Node Connect(Network network, AddressManager addrman, NodeConnectionParameters parameters = null, IPAddress[] connectedAddresses = null)
		{
			parameters = parameters ?? new NodeConnectionParameters();
			AddressManagerBehavior.SetAddrman(parameters, addrman);
			return Connect(network, parameters, connectedAddresses);
		}

		/// <summary>
		/// Connect to a random node on the network
		/// </summary>
		/// <param name="network">The network to connect to</param>
		/// <param name="parameters">The parameters used by the found node, use AddressManagerBehavior.GetAddrman for finding peers</param>
		/// <param name="connectedAddresses">The already connected addresses, the new address will be select outside of existing groups</param>
		/// <returns></returns>
		public static Node Connect(Network network, NodeConnectionParameters parameters = null, IPAddress[] connectedAddresses = null)
		{
			connectedAddresses = connectedAddresses ?? new IPAddress[0];
			parameters = parameters ?? new NodeConnectionParameters();
			var addrman = AddressManagerBehavior.GetAddrman(parameters) ?? new AddressManager();
			DateTimeOffset start = DateTimeOffset.UtcNow;
			while(true)
			{
				parameters.ConnectCancellation.ThrowIfCancellationRequested();
				if(addrman.Count == 0 || DateTimeOffset.UtcNow - start > TimeSpan.FromSeconds(30))
				{
					addrman.DiscoverPeers(network, parameters);
				}
				NetworkAddress addr = null;
				while(true)
				{
					addr = addrman.Select();
					if(addr == null)
						break;
					if(!addr.Endpoint.Address.IsValid())
						continue;
					var groupExist = connectedAddresses.Any(a => a.GetGroup().SequenceEqual(addr.Endpoint.Address.GetGroup()));
					if(groupExist)
						continue;
					break;
				}
				if(addr == null)
					continue;
				try
				{
					var timeout = new CancellationTokenSource(5000);
					var param2 = parameters.Clone();
					param2.ConnectCancellation = CancellationTokenSource.CreateLinkedTokenSource(parameters.ConnectCancellation, timeout.Token).Token;
					var node = Node.Connect(network, addr.Endpoint, param2);
					return node;
				}
				catch(OperationCanceledException ex)
				{
					if(ex.CancellationToken == parameters.ConnectCancellation)
						throw;
				}
				catch(SocketException)
				{
					parameters.ConnectCancellation.WaitHandle.WaitOne(500);
				}
			}
		}

		/// <summary>
		/// Connect to the node of this machine
		/// </summary>
		/// <param name="network"></param>
		/// <param name="parameters"></param>
		/// <returns></returns>
		public static Node ConnectToLocal(Network network,
								NodeConnectionParameters parameters)
		{
			return Connect(network, Utils.ParseIpEndpoint("localhost", network.DefaultPort), parameters);
		}

		public static Node ConnectToLocal(Network network,
								ProtocolVersion myVersion = ProtocolVersion.PROTOCOL_VERSION,
								bool isRelay = true,
								CancellationToken cancellation = default(CancellationToken))
		{
			return ConnectToLocal(network, new NodeConnectionParameters()
			{
				ConnectCancellation = cancellation,
				IsRelay = isRelay,
				Version = myVersion
			});
		}

		public static Node Connect(Network network,
								 string endpoint, NodeConnectionParameters parameters)
		{
			return Connect(network, Utils.ParseIpEndpoint(endpoint, network.DefaultPort), parameters);
		}

		public static Node Connect(Network network,
								 string endpoint,
								 ProtocolVersion myVersion = ProtocolVersion.PROTOCOL_VERSION,
								bool isRelay = true,
								CancellationToken cancellation = default(CancellationToken))
		{
			return Connect(network, Utils.ParseIpEndpoint(endpoint, network.DefaultPort), myVersion, isRelay, cancellation);
		}

		public static Node Connect(Network network,
							 NetworkAddress endpoint,
							 NodeConnectionParameters parameters)
		{
			return new Node(endpoint, network, parameters);
		}

		public static Node Connect(Network network,
							 IPEndPoint endpoint,
							 NodeConnectionParameters parameters)
		{
			var peer = new NetworkAddress()
			{
				Time = DateTimeOffset.UtcNow,
				Endpoint = endpoint
			};

			return new Node(peer, network, parameters);
		}

		public static Node Connect(Network network,
								 IPEndPoint endpoint,
								 ProtocolVersion myVersion = ProtocolVersion.PROTOCOL_VERSION,
								bool isRelay = true,
								CancellationToken cancellation = default(CancellationToken))
		{
			return Connect(network, endpoint, new NodeConnectionParameters()
			{
				ConnectCancellation = cancellation,
				IsRelay = isRelay,
				Version = myVersion,
				Services = NodeServices.Nothing,
			});
		}

		internal Node(NetworkAddress peer, Network network, NodeConnectionParameters parameters)
		{
			parameters = parameters ?? new NodeConnectionParameters();
			VersionPayload version = parameters.CreateVersion(peer.Endpoint, network);
			var addrman = AddressManagerBehavior.GetAddrman(parameters);
			Inbound = false;
			_Behaviors = new NodeBehaviorsCollection(this);
			_MyVersion = version;
			Version = _MyVersion.Version;
			Network = network;
			_Peer = peer;
			LastSeen = peer.Time;

			var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
#if !NOIPDUALMODE
			socket.DualMode = true;
#endif
			_Connection = new NodeConnection(this, socket);
			socket.ReceiveBufferSize = parameters.ReceiveBufferSize;
			socket.SendBufferSize = parameters.SendBufferSize;
			using(TraceCorrelation.Open())
			{
				try
				{
					var ar = socket.BeginConnect(Peer.Endpoint, null, null);
					WaitHandle.WaitAny(new WaitHandle[] { ar.AsyncWaitHandle, parameters.ConnectCancellation.WaitHandle });
					parameters.ConnectCancellation.ThrowIfCancellationRequested();
					socket.EndConnect(ar);
					_RemoteSocketAddress = ((IPEndPoint)socket.RemoteEndPoint).Address;
					_RemoteSocketPort = ((IPEndPoint)socket.RemoteEndPoint).Port;
					State = NodeState.Connected;
					ConnectedAt = DateTimeOffset.UtcNow;
					NodeServerTrace.Information("Outbound connection successfull");
					if(addrman != null)
						addrman.Attempt(Peer);
				}
				catch(OperationCanceledException)
				{
					Utils.SafeCloseSocket(socket);
					NodeServerTrace.Information("Connection to node cancelled");
					State = NodeState.Offline;
					if(addrman != null)
						addrman.Attempt(Peer);
					throw;
				}
				catch(Exception ex)
				{
					Utils.SafeCloseSocket(socket);
					NodeServerTrace.Error("Error connecting to the remote endpoint ", ex);
					DisconnectReason = new NodeDisconnectReason()
					{
						Reason = "Unexpected exception while connecting to socket",
						Exception = ex
					};
					State = NodeState.Failed;
					if(addrman != null)
						addrman.Attempt(Peer);
					throw;
				}
				InitDefaultBehaviors(parameters);
				_Connection.BeginListen();
			}
		}
		internal Node(NetworkAddress peer, Network network, NodeConnectionParameters parameters, Socket socket, VersionPayload peerVersion)
		{
			_RemoteSocketAddress = ((IPEndPoint)socket.RemoteEndPoint).Address;
			_RemoteSocketPort = ((IPEndPoint)socket.RemoteEndPoint).Port;
			Inbound = true;
			_Behaviors = new NodeBehaviorsCollection(this);
			_MyVersion = parameters.CreateVersion(peer.Endpoint, network);
			Network = network;
			_Peer = peer;
			_Connection = new NodeConnection(this, socket);
			_PeerVersion = peerVersion;
			LastSeen = peer.Time;
			ConnectedAt = DateTimeOffset.UtcNow;
			TraceCorrelation.LogInside(() =>
			{
				NodeServerTrace.Information("Connected to advertised node " + _Peer.Endpoint);
				State = NodeState.Connected;
			});
			InitDefaultBehaviors(parameters);
			Version = peerVersion.Version;
			_Connection.BeginListen();
		}

		IPAddress _RemoteSocketAddress;
		public IPAddress RemoteSocketAddress
		{
			get
			{
				return _RemoteSocketAddress;
			}
		}

		int _RemoteSocketPort;
		public int RemoteSocketPort
		{
			get
			{
				return _RemoteSocketPort;
			}
		}

		public bool Inbound
		{
			get;
			private set;
		}

		bool _ReuseBuffer;
		private void InitDefaultBehaviors(NodeConnectionParameters parameters)
		{
			IsTrusted = parameters.IsTrusted != null ? parameters.IsTrusted.Value : Peer.Endpoint.Address.IsLocal();
			Advertize = parameters.Advertize;
			Version = parameters.Version;
			_ReuseBuffer = parameters.ReuseBuffer;

			_Behaviors.DelayAttach = true;
			foreach(var behavior in parameters.TemplateBehaviors)
			{
				_Behaviors.Add((NodeBehavior)((ICloneable)behavior).Clone());
			}
			_Behaviors.DelayAttach = false;
		}

		private readonly NodeBehaviorsCollection _Behaviors;
		public NodeBehaviorsCollection Behaviors
		{
			get
			{
				return _Behaviors;
			}
		}

		private readonly NetworkAddress _Peer;
		public NetworkAddress Peer
		{
			get
			{
				return _Peer;
			}
		}

		public DateTimeOffset LastSeen
		{
			get;
			private set;
		}

		TraceCorrelation _TraceCorrelation = null;
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
#if NOTRACESOURCE
		internal
#else
		public
#endif
 TraceCorrelation TraceCorrelation
		{
			get
			{
				if(_TraceCorrelation == null)
				{
					_TraceCorrelation = new TraceCorrelation(NodeServerTrace.Trace, "Communication with " + Peer.Endpoint.ToString());
				}
				return _TraceCorrelation;
			}
		}

		/// <summary>
		/// Send a message to the peer asynchronously
		/// </summary>
		/// <param name="payload">The payload to send</param>
		/// <param name="System.OperationCanceledException">The node has been disconnected</param>
		public Task SendMessageAsync(Payload payload)
		{
			if(payload == null)
				throw new ArgumentNullException("payload");
			TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
			if(!IsConnected)
			{
				completion.SetException(new OperationCanceledException("The peer has been disconnected"));
				return completion.Task;
			}
			var activity = Trace.CorrelationManager.ActivityId;
			Action final = () =>
			{
				_Connection.Messages.Add(new SentMessage()
				{
					Payload = payload,
					ActivityId = activity,
					Completion = completion
				});
			};
			OnSendingMessage(payload, final);
			return completion.Task;
		}



		/// <summary>
		/// Send a message to the peer synchronously
		/// </summary>
		/// <param name="payload">The payload to send</param>
		/// <exception cref="System.ArgumentNullException">Payload is null</exception>
		/// <param name="System.OperationCanceledException">The node has been disconnected, or the cancellation token has been set to canceled</param>
		public void SendMessage(Payload payload, CancellationToken cancellation = default(CancellationToken))
		{
			try
			{
				SendMessageAsync(payload).Wait(cancellation);
			}
			catch(AggregateException aex)
			{
				ExceptionDispatchInfo.Capture(aex.InnerException).Throw();
				throw;
			}
		}

		private PerformanceCounter _Counter;
		public PerformanceCounter Counter
		{
			get
			{
				if(_Counter == null)
					_Counter = new PerformanceCounter();
				return _Counter;
			}
		}

		public ProtocolVersion Version
		{
			get;
			private set;
		}

		public bool IsConnected
		{
			get
			{
				return State == NodeState.Connected || State == NodeState.HandShaked;
			}
		}

		private readonly MessageProducer<IncomingMessage> _MessageProducer = new MessageProducer<IncomingMessage>();
		public MessageProducer<IncomingMessage> MessageProducer
		{
			get
			{
				return _MessageProducer;
			}
		}

		public TPayload ReceiveMessage<TPayload>(TimeSpan timeout) where TPayload : Payload
		{
			var source = new CancellationTokenSource();
			source.CancelAfter(timeout);
			return ReceiveMessage<TPayload>(source.Token);
		}



		public TPayload ReceiveMessage<TPayload>(CancellationToken cancellationToken = default(CancellationToken)) where TPayload : Payload
		{
			using(var listener = new NodeListener(this))
			{
				return listener.ReceivePayload<TPayload>(cancellationToken);
			}
		}

		/// <summary>
		/// Send addr unsollicited message of the AddressFrom peer when passing to Handshaked state
		/// </summary>
		public bool Advertize
		{
			get;
			set;
		}

		private readonly VersionPayload _MyVersion;
		public VersionPayload MyVersion
		{
			get
			{
				return _MyVersion;
			}
		}

		VersionPayload _PeerVersion;
		public VersionPayload PeerVersion
		{
			get
			{
				return _PeerVersion;
			}
		}

		public void VersionHandshake(CancellationToken cancellationToken = default(CancellationToken))
		{
			VersionHandshake(null, cancellationToken);
		}
		public void VersionHandshake(NodeRequirement requirements, CancellationToken cancellationToken = default(CancellationToken))
		{
			requirements = requirements ?? new NodeRequirement();
			using(var listener = CreateListener()
									.Where(p => p.Message.Payload is VersionPayload ||
												p.Message.Payload is RejectPayload ||
												p.Message.Payload is VerAckPayload))
			{

				SendMessageAsync(MyVersion);
				var payload = listener.ReceivePayload<Payload>(cancellationToken);
				if(payload is RejectPayload)
				{
					throw new ProtocolException("Handshake rejected : " + ((RejectPayload)payload).Reason);
				}
				var version = (VersionPayload)payload;
				_PeerVersion = version;
				Version = version.Version;
				if(!version.AddressReceiver.Address.Equals(MyVersion.AddressFrom.Address))
				{
					NodeServerTrace.Warning("Different external address detected by the node " + version.AddressReceiver.Address + " instead of " + MyVersion.AddressFrom.Address);
				}
				if(version.Version < ProtocolVersion.MIN_PEER_PROTO_VERSION)
				{
					NodeServerTrace.Warning("Outdated version " + version.Version + " disconnecting");
					Disconnect("Outdated version");
					return;
				}
				if(requirements.MinVersion != null)
				{
					if(version.Version < requirements.MinVersion.Value)
					{
						Disconnect("The peer does not support the version requirement");
						return;
					}
				}
				if((requirements.RequiredServices & version.Services) != requirements.RequiredServices)
				{
					Disconnect("The peer does not support the required services requirement");
					return;
				}

				SendMessageAsync(new VerAckPayload());
				listener.ReceivePayload<VerAckPayload>(cancellationToken);
				State = NodeState.HandShaked;
				if(Advertize && MyVersion.AddressFrom.Address.IsRoutable(true))
				{
					SendMessageAsync(new AddrPayload(new NetworkAddress(MyVersion.AddressFrom)
					{
						Time = DateTimeOffset.UtcNow
					}));
				}

			}
		}



		/// <summary>
		/// 
		/// </summary>
		/// <param name="cancellation"></param>
		public void RespondToHandShake(CancellationToken cancellation = default(CancellationToken))
		{
			using(TraceCorrelation.Open())
			{
				using(var list = CreateListener().Where(m => m.Message.Payload is VerAckPayload || m.Message.Payload is RejectPayload))
				{
					NodeServerTrace.Information("Responding to handshake");
					SendMessageAsync(MyVersion);
					var message = list.ReceiveMessage(cancellation);
					var reject = message.Message.Payload as RejectPayload;
					if(reject != null)
						throw new ProtocolException("Version rejected " + reject.Code + " : " + reject.Reason);
					SendMessageAsync(new VerAckPayload());
					State = NodeState.HandShaked;
				}
			}
		}

		public void Disconnect()
		{
			Disconnect(null, null);
		}

		int _Disconnecting;

		public void Disconnect(string reason, Exception exception = null)
		{
			DisconnectAsync(reason, exception);
			AssertNoListeningThread();
			_Connection.Disconnected.WaitOne();
		}

		private void AssertNoListeningThread()
		{
			if(_Connection._ListenerThreadId == Thread.CurrentThread.ManagedThreadId)
				throw new InvalidOperationException("Using Disconnect on this thread would result in a deadlock, use DisconnectAsync instead");
		}
		public void DisconnectAsync()
		{
			DisconnectAsync(null, null);
		}
		public void DisconnectAsync(string reason, Exception exception = null)
		{
			if(!IsConnected)
				return;
			if(Interlocked.CompareExchange(ref _Disconnecting, 1, 0) == 1)
				return;
			using(TraceCorrelation.Open())
			{
				NodeServerTrace.Information("Disconnection request " + reason);
				State = NodeState.Disconnecting;
				_Connection.Cancel.Cancel();
				if(DisconnectReason == null)
					DisconnectReason = new NodeDisconnectReason()
					{
						Reason = reason,
						Exception = exception
					};
			}
		}

		public NodeDisconnectReason DisconnectReason
		{
			get;
			private set;
		}

		public override string ToString()
		{
			return String.Format("{0} ({1})",  State, Peer.Endpoint);
		}

		private Socket Socket
		{
			get
			{
				return _Connection.Socket;
			}
		}

		/// <summary>
		/// Get the chain of headers from the peer (thread safe)
		/// </summary>
		/// <param name="hashStop">The highest block wanted</param>
		/// <param name="cancellationToken"></param>
		/// <returns>The chain of headers</returns>
		public ConcurrentChain GetChain(uint256 hashStop = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			ConcurrentChain chain = new ConcurrentChain(Network);
			SynchronizeChain(chain, hashStop, cancellationToken);
			return chain;
		}
		public IEnumerable<ChainedBlock> GetHeadersFromFork(ChainedBlock currentTip,
														uint256 hashStop = null,
														CancellationToken cancellationToken = default(CancellationToken))
		{
			AssertState(NodeState.HandShaked, cancellationToken);

			NodeServerTrace.Information("Building chain");
			using(var listener = this.CreateListener().OfType<HeadersPayload>())
			{
				while(true)
				{
					//Get before last so, at the end, we should only receive 1 header equals to this one (so we will not have race problems with concurrent GetChains)
					var awaited = currentTip.Previous == null ? currentTip.GetLocator() : currentTip.Previous.GetLocator();
					SendMessageAsync(new GetHeadersPayload()
					{
						BlockLocators = awaited,
						HashStop = hashStop
					});

					while(true)
					{
						bool isOurs = false;
						var headersCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); //30 sec delay before reasking GetHeaders
						headersCancel.CancelAfter(TimeSpan.FromMinutes(1.0));
						HeadersPayload headers = null;
						try
						{
							headers = listener.ReceivePayload<HeadersPayload>(headersCancel.Token);
						}
						catch(OperationCanceledException)
						{
							if(cancellationToken.IsCancellationRequested)
								throw;
							break; //Send a new GetHeaders
						}
						if(headers.Headers.Count == 0 && PeerVersion.StartHeight == 0 && currentTip.HashBlock == Network.GenesisHash) //In the special case where the remote node is at height 0 as well as us, then the headers count will be 0
							yield break;
						if(headers.Headers.Count == 1 && headers.Headers[0].GetHash() == currentTip.HashBlock)
							yield break;
						foreach(var header in headers.Headers)
						{
							var h = header.GetHash();
							if(h == currentTip.HashBlock)
								continue;
							if(header.HashPrevBlock == currentTip.HashBlock)
							{
								isOurs = true;
								currentTip = new ChainedBlock(header, h, currentTip);
								yield return currentTip;
								if(currentTip.HashBlock == hashStop)
									yield break;
							}
							else
								break; //Not our headers, continue receive
						}
						if(isOurs)
							break;  //Go ask for next header
					}
				}
			}
		}


		/// <summary>
		/// Synchronize a given Chain to the tip of this node if its height is higher. (Thread safe)
		/// </summary>
		/// <param name="chain">The chain to synchronize</param>
		/// <param name="hashStop">The location until which it synchronize</param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public IEnumerable<ChainedBlock> SynchronizeChain(ChainBase chain, uint256 hashStop = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			var oldTip = chain.Tip;
			var headers = GetHeadersFromFork(oldTip, hashStop, cancellationToken).ToList();
			if(headers.Count == 0)
				return new ChainedBlock[0];
			var newTip = headers[headers.Count - 1];
			if(!IsTrusted)
			{
				if(newTip.Height <= oldTip.Height)
					throw new ProtocolException("No tip should have been recieved older than the local one");
				foreach(var header in headers)
				{
					if(!header.Validate(Network))
						throw new ProtocolException("An header which does not pass proof of work verificaiton has been received");
				}
			}
			chain.SetTip(newTip);
			return headers;
		}

		/// <summary>
		/// Will verify proof of work during chain operations
		/// </summary>
		public bool IsTrusted
		{
			get;
			set;
		}

		public IEnumerable<Block> GetBlocks(uint256 hashStop = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			var genesis = new ChainedBlock(Network.GetGenesis().Header, 0);
			return GetBlocksFromFork(genesis, hashStop, cancellationToken);
		}


		public IEnumerable<Block> GetBlocksFromFork(ChainedBlock currentTip, uint256 hashStop = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			using(var listener = CreateListener())
			{
				SendMessageAsync(new GetBlocksPayload()
				{
					BlockLocators = currentTip.GetLocator(),
				});

				var headers = GetHeadersFromFork(currentTip, hashStop, cancellationToken);

				foreach(var block in GetBlocks(headers.Select(b => b.HashBlock), cancellationToken))
				{
					yield return block;
				}
			}
			//GetBlocks(neededBlocks.ToEnumerable(false).Select(e => e.HashBlock), cancellationToken);
		}

		public IEnumerable<Block> GetBlocks(IEnumerable<ChainedBlock> blocks, CancellationToken cancellationToken = default(CancellationToken))
		{
			return GetBlocks(blocks.Select(c => c.HashBlock), cancellationToken);
		}

		public IEnumerable<Block> GetBlocks(IEnumerable<uint256> neededBlocks, CancellationToken cancellationToken = default(CancellationToken))
		{
			AssertState(NodeState.HandShaked, cancellationToken);

			int simultaneous = 70;
			using(var listener = CreateListener()
								.OfType<BlockPayload>())
			{
				foreach(var invs in neededBlocks
									.Select(b => new InventoryVector()
										{
											Type = InventoryType.MSG_BLOCK,
											Hash = b
										})
									.Partition(() => simultaneous))
				{

					var remaining = new Queue<uint256>(invs.Select(k => k.Hash));
					SendMessageAsync(new GetDataPayload(invs.ToArray()));

					int maxQueued = 0;
					while(remaining.Count != 0)
					{
						var block = listener.ReceivePayload<BlockPayload>(cancellationToken).Object;
						maxQueued = Math.Max(listener.MessageQueue.Count, maxQueued);
						if(remaining.Peek() == block.GetHash())
						{
							remaining.Dequeue();
							yield return block;
						}
					}
					if(maxQueued < 10)
						simultaneous *= 2;
					else
						simultaneous /= 2;
					simultaneous = Math.Max(10, simultaneous);
					simultaneous = Math.Min(10000, simultaneous);
				}
			}
		}

		/// <summary>
		/// Create a listener that will queue messages until diposed
		/// </summary>
		/// <returns>The listener</returns>
		/// <exception cref="System.InvalidOperationException">Thrown if used on the listener's thread, as it would result in a deadlock</exception>
		public NodeListener CreateListener()
		{
			AssertNoListeningThread();
			return new NodeListener(this);
		}


		private void AssertState(NodeState nodeState, CancellationToken cancellationToken = default(CancellationToken))
		{
			if(nodeState == NodeState.HandShaked && State == NodeState.Connected)
				this.VersionHandshake(cancellationToken);
			if(nodeState != State)
				throw new InvalidOperationException("Invalid Node state, needed=" + nodeState + ", current= " + State);
		}

		public uint256[] GetMempool(CancellationToken cancellationToken = default(CancellationToken))
		{
			AssertState(NodeState.HandShaked);
			using(var listener = CreateListener().OfType<InvPayload>())
			{
				this.SendMessageAsync(new MempoolPayload());
				return listener.ReceivePayload<InvPayload>(cancellationToken).Inventory.Select(i => i.Hash).ToArray();
			}
		}

		public Transaction[] GetMempoolTransactions(CancellationToken cancellationToken = default(CancellationToken))
		{
			return GetMempoolTransactions(GetMempool(), cancellationToken);
		}

		Transaction[] GetMempoolTransactions(uint256[] txIds, CancellationToken cancellationToken = default(CancellationToken))
		{
			AssertState(NodeState.HandShaked);
			if(txIds.Length == 0)
				return new Transaction[0];
			List<Transaction> result = new List<Transaction>();
			using(var listener = CreateListener().Where(m => m.Message.Payload is TxPayload || m.Message.Payload is NotFoundPayload))
			{
				foreach(var batch in txIds.Partition(500))
				{
					this.SendMessageAsync(new GetDataPayload(batch.Select(txid => new InventoryVector()
					{
						Type = InventoryType.MSG_TX,
						Hash = txid
					}).ToArray()));
					try
					{
						while(result.Count < batch.Count)
						{
							CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10.0));
							var payload = listener.ReceivePayload<Payload>(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token).Token);
							if(payload is NotFoundPayload)
								result.Add(null);
							else
								result.Add(((TxPayload)payload).Object);
						}
					}
					catch(OperationCanceledException)
					{
						if(cancellationToken.IsCancellationRequested)
						{
							throw;
						}
					}
				}
			}
			return result.Where(r => r != null).ToArray();
		}

		public Network Network
		{
			get;
			set;
		}

		#region IDisposable Members

		public void Dispose()
		{
			Disconnect("Node disposed");
		}

		#endregion

		/// <summary>
		/// Emit a ping and wait the pong
		/// </summary>
		/// <param name="cancellation"></param>
		/// <returns>Latency</returns>
		public TimeSpan PingPong(CancellationToken cancellation = default(CancellationToken))
		{
			using(var listener = CreateListener().OfType<PongPayload>())
			{
				var ping = new PingPayload()
				{
					Nonce = RandomUtils.GetUInt64()
				};
				var before = DateTimeOffset.UtcNow;
				SendMessageAsync(ping);

				while(listener.ReceivePayload<PongPayload>(cancellation).Nonce != ping.Nonce)
				{
				}
				var after = DateTimeOffset.UtcNow;
				return after - before;
			}
		}
	}
}
#endif