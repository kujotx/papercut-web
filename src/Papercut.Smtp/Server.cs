/*  
 * Papercut
 *
 *  Copyright � 2008 - 2012 Ken Robertson
 *  
 *  Licensed under the Apache License, Version 2.0 (the "License");
 *  you may not use this file except in compliance with the License.
 *  You may obtain a copy of the License at
 *  
 *  http://www.apache.org/licenses/LICENSE-2.0
 *  
 *  Unless required by applicable law or agreed to in writing, software
 *  distributed under the License is distributed on an "AS IS" BASIS,
 *  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *  See the License for the specific language governing permissions and
 *  limitations under the License.
 *  
 */

namespace Papercut.Smtp
{
	#region Using

    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;

    #endregion

	/// <summary>
	/// The server.
	/// </summary>
	public class Server
	{
		#region Constants and Fields

		/// <summary>
		/// The _connections.
		/// </summary>
		private readonly Dictionary<int, Connection> _connections = new Dictionary<int, Connection>();

		/// <summary>
		/// The _address.
		/// </summary>
		private IPAddress _address;

		/// <summary>
		/// The _is ready.
		/// </summary>
		private bool _isReady;

		/// <summary>
		/// The _is running.
		/// </summary>
		private bool _isRunning;

		/// <summary>
		/// The _is starting.
		/// </summary>
		private bool _isStarting;

		/// <summary>
		/// The _listener.
		/// </summary>
		private Socket _listener;

		/// <summary>
		/// The _port.
		/// </summary>
		private int _port;

		/// <summary>
		/// The connection id.
		/// </summary>
		private int connectionID;

		/// <summary>
		/// The timeout thread.
		/// </summary>
		private Thread timeoutThread;

	    private Processor processor;

	    #endregion

		#region Public Methods and Operators

	    public Server(IPAddress address, int port, Processor processor)
	    {
	        this._address = address;
	        this._port = port;
	        this.processor = processor;
	    }

	    /// <summary>
		/// The bind.
		/// </summary>
		public void Bind()
		{
			try
			{
				// If the listener isn't null, close before rebinding
				if (this._listener != null)
				{
					this._listener.Close();
				}

				// Bind to the listening port
				this._listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				this._listener.Bind(new IPEndPoint(this._address, this._port));
				this._listener.Listen(10);
				this._listener.BeginAccept(this.OnClientAccept, null);
				Logger.Write("Server Ready - Listening for new connections " + this._address + ":" + this._port + "...");
			}
			catch (Exception ex)
			{
				Logger.WriteError("Exception thrown in Server.Start()", ex);
				throw;
			}
		}

		/// <summary>
		/// The start.
		/// </summary>
		public void Start()
		{
			Logger.Write("Starting server...");

			try
			{
				// Set it as starting
				this._isRunning = true;
				this._isStarting = true;

				// Start the thread to watch for inactive connections
				if (this.timeoutThread == null)
				{
					this.timeoutThread = new Thread(this.SessionTimeoutWatcher);
					this.timeoutThread.Start();
				}

				// Create and start new listener socket
				this.Bind();

				// Set it as ready
				this._isReady = true;
			}
			catch (Exception ex)
			{
				Logger.WriteError("Exception thrown in Server.Start()", ex);
				throw;
			}
			finally
			{
				// Done starting
				this._isStarting = false;
			}
		}

		/// <summary>
		/// The stop.
		/// </summary>
		public void Stop()
		{
			Logger.Write("Stopping server...");

			try
			{
				// Turn off the running bool
				this._isRunning = false;

				// Stop the listener
				this._listener.Close();

				// Stop the session timeout thread
				this.timeoutThread.Join();

				// Close all open connections
				foreach (Connection connection in this._connections.Values.Where(connection => connection != null))
				{
					connection.Close(false);
				}
			}
			catch (Exception ex)
			{
				Logger.WriteError("Exception thrown in Server.Stop()", ex);
			}
		}

		#endregion

		#region Methods

		/// <summary>
		/// The on client accept.
		/// </summary>
		/// <param name="ar">
		/// The ar.
		/// </param>
		private void OnClientAccept(IAsyncResult ar)
		{
			try
			{
				Socket clientSocket = this._listener.EndAccept(ar);
				Interlocked.Increment(ref this.connectionID);
			    var connection = new Connection(this.connectionID, clientSocket, this.processor.ProcessCommand);
				connection.ConnectionClosed += this.connection_ConnectionClosed;
				this._connections.Add(connection.ConnectionId, connection);
			}
			catch (ObjectDisposedException)
			{
				// This can occur when stopping the service.  Squash it, it only means the listener was stopped.
				return;
			}
			catch (ArgumentException)
			{
				// This can be thrown when updating settings and rebinding the listener.  It mainly means the IAsyncResult
				// wasn't generated by a BeginAccept event.
				return;
			}
			catch (Exception ex)
			{
				Logger.WriteError("Exception thrown in Server.OnClientAccept", ex);
			}
			finally
			{
				if (this._isRunning)
				{
					try
					{
						this._listener.BeginAccept(this.OnClientAccept, null);
					}
					catch
					{
						// This normally happens when trying to rebind to a port that is taken
					}
				}
			}
		}

		/// <summary>
		/// The session timeout watcher.
		/// </summary>
		private void SessionTimeoutWatcher()
		{
			int collectInterval = 5 * 60 - 1; // How often to collect garbage... every 5 mins
			int statusInterval = 20 * 60 - 1; // How often to print status... every 20 mins
			int collectCount = 0;
			int statusCount = 0;

			while (this._isRunning)
			{
				try
				{
					// Check if the program is up and ready to receive connections
					if (!this._isReady)
					{
						// If it is already trying to start, don't have it retry yet
						if (!this._isStarting)
						{
							this.Start();
						}
					}
					else
					{
						// Do garbage collection?
						if (collectCount >= collectInterval)
						{
							// Get the number of current connections
							var keys = new int[this._connections.Count];
							this._connections.Keys.CopyTo(keys, 0);

							// Loop through the connections
							foreach (int key in keys)
							{
								// If they have been idle for too long, disconnect them
								if (DateTime.Now < this._connections[key].LastActivity.AddMinutes(20))
								{
									Logger.Write("Session timeout, disconnecting", this._connections[key].ConnectionId);
									this._connections[key].Close();
								}
							}

							GC.Collect();
							collectCount = 0;
						}
						else
						{
							collectCount++;
						}

						// Print status messages?
						if (statusCount >= statusInterval)
						{
							double memusage = (double)Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
							Logger.Write(
								"Current status: " + this._connections.Count + " connections, " + memusage.ToString("0.#") + "mb memory used");
							statusCount = 0;
						}
						else
						{
							statusCount++;
						}
					}
				}
				catch (Exception ex)
				{
					Logger.WriteError("Exception occurred in Server.SessionTimeoutWatcher()", ex);
				}

				Thread.Sleep(1000);
			}
		}

		/// <summary>
		/// The connection_ connection closed.
		/// </summary>
		/// <param name="sender">
		/// The sender.
		/// </param>
		/// <param name="e">
		/// The e.
		/// </param>
		private void connection_ConnectionClosed(object sender, EventArgs e)
		{
			var connection = sender as Connection;
			if (connection == null)
			{
				return;
			}

			this._connections.Remove(connection.ConnectionId);
		}

		#endregion
	}
}