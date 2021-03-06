﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Owin;
using CommonSerializer;
using Microsoft.Owin;

namespace Kts.Remoting.Server
{
	#region Websocket Method Definitions

	using WebSocketAccept =
		Action
		<
			IDictionary<string, object>, // WebSocket Accept parameters
			Func // WebSocketFunc callback
			<
				IDictionary<string, object>, // WebSocket environment
				Task // Complete
			>
		>;

	using WebSocketFunc =
		Func
		<
			IDictionary<string, object>, // WebSocket Environment
			Task // Complete
		>;

	using WebSocketSendAsync =
		Func
		<
			ArraySegment<byte> /* data */,
			int /* messageType */,
			bool /* endOfMessage */,
			CancellationToken /* cancel */,
			Task
		>;

	using WebSocketReceiveAsync =
		Func
		<
			ArraySegment<byte> /* data */,
			CancellationToken /* cancel */,
			Task
			<
				Tuple
				<
					int /* messageType */,
					bool /* endOfMessage */,
					int /* count */
				>
			>
		>;

	using WebSocketReceiveTuple =
		Tuple
		<
			int /* messageType */,
			bool /* endOfMessage */,
			int /* count */
		>;

	using WebSocketCloseAsync =
		Func
		<
			int /* closeStatus */,
			string /* closeDescription */,
			CancellationToken /* cancel */,
			Task
		>;

	#endregion

	public class ProxyMiddleware : OwinMiddleware
	{
		private readonly WebSocketFactoryOptions _options;
		private WebSocketSendAsync _sendAsync;
		private WebSocketReceiveAsync _receiveAsync;
		private WebSocketCloseAsync _closeAsync;

		public ProxyMiddleware(OwinMiddleware next, WebSocketFactoryOptions options)
			: base(next)
		{
			_options = options;
		}

		public override async Task Invoke(IOwinContext context)
		{
			var accept = context.Get<WebSocketAccept>("websocket.Accept");
			if (accept == null)
			{
				// Bad Request
				context.Response.StatusCode = 400;
				context.Response.Write("Not a valid websocket request");
				return;
			}

			var responseBuffering = context.Get<Action>("server.DisableResponseBuffering");
			if (responseBuffering != null)
				responseBuffering.Invoke();

			var responseCompression = context.Get<Action>("systemweb.DisableResponseCompression");
			if (responseCompression != null)
				responseCompression.Invoke();

			context.Response.Headers.Set("X-Content-Type-Options", "nosniff");

			_sendAsync = context.Get<WebSocketSendAsync>("websocket.SendAsync");
			_receiveAsync = context.Get<WebSocketReceiveAsync>("websocket.ReceiveAsync");
			_closeAsync = context.Get<WebSocketCloseAsync>("websocket.CloseAsync");

			accept.Invoke(null, RunReadLoop);
		}

		private async Task RunReadLoop(IDictionary<string, object> websocketContext)
		{
			_options.FireOnConnected();

			foreach (var kvp in _options.Services)
			{
				// subscribe to events
				// if it's a PropertyChanged event, send the property data

			}

			var buffer = new byte[_options.MessageBufferSize];
			var count = 0;

			// make sure thread username gets propagated to the handler thread
			// should we have a connectionID -- some random number here? maybe it's in the context?
			// connected and disconnected need try/catch
			// disconnected may need the reason
			do
			{
				try
				{
					if (_options.CancellationToken.IsCancellationRequested)
						break;

					WebSocketReceiveTuple received;
					do
					{
						var segment = new ArraySegment<byte>(buffer, count, buffer.Length - count);
						received = await _receiveAsync.Invoke(segment, _options.CancellationToken);
						count += received.Item3;
					} while (!received.Item2 && !_options.CancellationToken.IsCancellationRequested);

					if (_options.CancellationToken.IsCancellationRequested)
						break;

					var isClosed = (received.Item1 & 0x8) > 0;
					if (isClosed)
						break;

					var isUTF8 = (received.Item1 & 0x01) > 0;
					var isCompressed = (received.Item1 & 0x40) > 0;

					// decompress the thing
					// deserialize the thing
					Stream stream = new MemoryStream(buffer, 0, count, false);
					var serializer = isUTF8 ? _options.TextSerializer : _options.BinarySerializer;

					Message message;
                    try
					{
						if (isCompressed)
							stream = new DeflateStream(stream, CompressionMode.Decompress, false);

						message = serializer.Deserialize<Message>(stream);
					}
					finally
					{
						stream.Dispose();
					}

					var service = _options.Services[message.Hub];
					var method = GetMethodDelegateFromCache(service, message);
					method.Method.Parameters
                    try
					{
						if (method == null)
							_options.FireOnError(new MissingMethodException(message.Hub, message.Method));
						else
						{
							var ret = method.Invoke(service, message.Arguments);
							if (ret is Task)
								await (Task)ret;
						}
					}
					catch (Exception ex)
					{
						// any exception here should not be treated as a shutdown request
						_options.FireOnError(ex);
					}
				}
				catch (TaskCanceledException)
				{
					break;
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (ObjectDisposedException)
				{
					break;
				}
				catch (Exception ex)
				{
					if (IsFatalSocketException(ex))
					{
						_options.FireOnError(ex);
					}
					break;
				}
			}
			while (true);

			_options.FireOnDisconnected();
		}


		private static readonly ConcurrentDictionary<Tuple<string, string>, MethodInvoker> _methodCache = new ConcurrentDictionary<Tuple<string, string>, MethodInvoker>();
		private MethodInvoker GetMethodDelegateFromCache(object hub, InvocationMessage message)
		{
			var key = Tuple.Create(message.Hub, message.Method);
			return _methodCache.GetOrAdd(key, id =>
			{
				var methods = hub.GetType().GetMethods().Where(m => string.Equals(m.Name, message.Method, StringComparison.OrdinalIgnoreCase)).ToList();
				if (methods.Count > 1)
				{
					// filter by parameters
					methods = methods.Where(m => m.Parameters().Count == message.Arguments.Length).ToList();
					if (methods.Count > 1 && message.Arguments.All(p => p != null))
					{
						methods = methods.Where(m => m.HasParameterSignature(message.Arguments.Select(p => p.GetType()).ToArray())).ToList();
					}
				}

				if (methods.Count <= 0)
				{
					var property = hub.GetType().GetProperty(message.Method, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public);
					if (property != null)
						return (target, parameters) =>
						{
							property.DelegateForSetPropertyValue().Invoke(target, parameters.SingleOrDefault());
							return null;
						};
				}

				if (methods.Count != 1)
					return null;

				return methods[0].DelegateForCallMethod();
			});
		}

		internal static bool IsFatalSocketException(Exception ex)
		{
			// If this exception is due to the underlying TCP connection going away, treat as a normal close
			// rather than a fatal exception.
			var ce = ex as COMException;
			if (ce != null)
			{
				switch ((uint)ce.ErrorCode)
				{
					case 0x800703e3:
					case 0x800704cd:
					case 0x80070026:
						return false;
				}
			}

			// unknown exception; treat as fatal
			return true;
		}

	}
}

namespace Owin
{
	using Kts.Remoting.Server;

	public class WebSocketFactoryOptions
	{
		internal Dictionary<string, object> Services = new Dictionary<string, object>();
		private int _messageBufferSize = 2000000;
		private CancellationToken _cancellationToken = CancellationToken.None;

		public void AddService<T>(T service)
		{
			AddService(service, typeof(T).Name);
		}

		public void AddService<T>(T service, string name)
		{
			if (service == null)
				throw new ArgumentNullException("service");
			if (string.IsNullOrEmpty(name))
				throw new ArgumentNullException("name");

			Services.Add(name, service);
		}

		/// <summary>
		/// This specifies the size of the buffer allocated to receive the messages. In other words, it is the maximum size in bytes allowed for any message received on the server.
		/// The default value is 2 million bytes (apx. 2MB) and the minimum value is 100 bytes.
		/// </summary>
		public int MessageBufferSize
		{
			get { return _messageBufferSize; }
			set
			{
				if (value <= 100)
					throw new ArgumentOutOfRangeException("value", "Value must be greater than 100 bytes.");
				_messageBufferSize = value;
			}
		}

		/// <summary>
		/// Use this to shutdown all the client connections.
		/// </summary>
		public CancellationToken CancellationToken
		{
			get { return _cancellationToken; }
			set { _cancellationToken = value; }
		}

		/// <summary>
		/// Use the deflate algorithm when sending data to the client.
		/// </summary>
		public bool CompressSentMessages { get; set; }

		public ICommonSerializer Serializer { get; set; }

		/// <summary>
		/// By default, all exceptions are eaten. Subscribe here to see or do something with them.
		/// </summary>
		public event Action<Exception> OnError = delegate { };
		internal void FireOnError(Exception ex) { OnError.Invoke(ex); }

		/// <summary>
		/// Triggers after each client successfully conencts.
		/// </summary>
		public event Action OnConnected = delegate { };
		internal void FireOnConnected() { OnConnected.Invoke(); }

		/// <summary>
		/// Triggers after a client disconnect, be it requested or due to an exception.
		/// </summary>
		public event Action OnDisconnected = delegate { };
		internal void FireOnDisconnected() { OnDisconnected.Invoke(); }
	}

	public static class OwinExtension
	{
		public static void AddWebSocketProxy(this IAppBuilder app, string route, WebSocketFactoryOptions options)
		{
			app.Map(route, config => config.Use<ProxyMiddleware>(options));
		}
	}
}
