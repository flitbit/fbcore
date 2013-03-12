﻿#region COPYRIGHT© 2009-2013 Phillip Clark. All rights reserved.

// For licensing information see License.txt (MIT style licensing).

#endregion

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Threading;

namespace FlitBit.Core.Parallel
{
	/// <summary>
	///   A parallel reactor is used to efficiently trigger actions in parallel in
	///   response to items being pushed to the reactor.
	/// </summary>
	/// <typeparam name="TItem">item type TItem</typeparam>
	public class Reactor<TItem>
	{
		/// <summary>
		///   The default options used by reactors when none are given to the constructor.
		/// </summary>
		public static readonly ReactorOptions DefaultOptions = new ReactorOptions(
			ReactorOptions.DefaultMaxDegreeOfParallelism, false, 0, ReactorOptions.DefaultMaxParallelDepth, 5);

		[ThreadStatic]
		static bool __isForegroundThreadBorrowed;

		readonly WaitCallback _backgroundReactor;

		readonly Object _lock = new Object();
		readonly ReactorOptions _options;
		readonly ConcurrentQueue<TItem> _queue = new ConcurrentQueue<TItem>();
		readonly Action<Reactor<TItem>, TItem> _reactor;
		int _backgroundWorkers = 0;
		int _backgroundWorkersActive = 0;
		int _backgroundWorkersScheduled = 0;
		bool _canceled;

		/// <summary>
		///   Creates a new instance with the default options.
		/// </summary>
		/// <param name="reactor">the reactor's action</param>
		public Reactor(Action<Reactor<TItem>, TItem> reactor)
			: this(reactor, DefaultOptions) { }

		/// <summary>
		///   Creates a new instance.
		/// </summary>
		/// <param name="reactor">the reactor's action</param>
		/// <param name="options">options</param>
		public Reactor(Action<Reactor<TItem>, TItem> reactor, ReactorOptions options)
		{
			Contract.Requires<ArgumentNullException>(reactor != null);

			_reactor = reactor;
			_options = options ?? DefaultOptions;
			_backgroundReactor = new WaitCallback(Background_Reactor);
		}

		/// <summary>
		///   Determines if the reactor is empty. Empty means there are no items
		///   that have not already been reacted too.
		/// </summary>
		public bool IsEmpty
		{
			get { return _queue.IsEmpty; }
		}

		/// <summary>
		///   Indicates whether the reactor is idle.
		/// </summary>
		public bool IsIdle
		{
			get
			{
				lock (_lock)
				{
					return _backgroundWorkersActive == 0
						&& _backgroundWorkersScheduled == 0;
				}
			}
		}

		/// <summary>
		///   Indicates whether the reactor is active.
		/// </summary>
		public bool IsActive
		{
			get { return Thread.VolatileRead(ref _backgroundWorkersActive) > 0; }
		}

		/// <summary>
		///   Indicates whethe the reactor is stopping.
		/// </summary>
		public bool IsCanceled
		{
			get { return Util.VolatileRead(ref _canceled); }
		}

		/// <summary>
		///   Indicates whether the reactor is stopped.
		/// </summary>
		public bool IsStopped
		{
			get { return IsCanceled && IsIdle; }
		}

		/// <summary>
		///   Gets the reactor's options.
		/// </summary>
		public ReactorOptions Options
		{
			get { return _options; }
		}

		event EventHandler<ReactorExceptionArgs> _uncaughtException;

		/// <summary>
		///   Stops a reactor. Once stopped a reactor cannot be restarted.
		/// </summary>
		/// <returns>the reactor (for chaining)</returns>
		public Reactor<TItem> Cancel()
		{
			Util.VolatileWrite(ref _canceled, true);
			return this;
		}

		/// <summary>
		///   Pushes a new item to the reactor.
		/// </summary>
		/// <param name="item">an item</param>
		/// <returns>the reactor (for chaining)</returns>
		public Reactor<TItem> Push(TItem item)
		{
			Contract.Requires<InvalidOperationException>(!IsCanceled);

			if (_queue.Count > _options.MaxParallelDepth)
			{
				if (!__isForegroundThreadBorrowed)
				{
					try
					{
						__isForegroundThreadBorrowed = true;
						Foreground_Reactor(item, _options.DispatchesPerBorrowedThread);
					}
					finally
					{
						__isForegroundThreadBorrowed = false;
					}
				}
			}
			else
			{
				_queue.Enqueue(item);
			}

			CheckBackgroundReactorState();
			return this;
		}

		void CheckBackgroundReactorState()
		{
			if (!IsCanceled)
			{
				lock (_lock)
				{
					var workers = _backgroundWorkersActive + _backgroundWorkersScheduled;
					if (!IsCanceled && _queue.Count > 0
						&& workers < _options.MaxDegreeOfParallelism)
					{
						ThreadPool.QueueUserWorkItem(this._backgroundReactor);
						Interlocked.Increment(ref _backgroundWorkersScheduled);
					}
				}
			}
		}

		/// <summary>
		///   Event fired when uncaught exceptions are encountered by the reactor.
		/// </summary>
		public event EventHandler<ReactorExceptionArgs> UncaughtException
		{
			add { _uncaughtException += value; }
			remove { _uncaughtException -= value; }
		}

		bool OnUncaughtException(Exception err)
		{
			if (_uncaughtException == null)
			{
				return false;
			}

			var args = new ReactorExceptionArgs(err);
			_uncaughtException(this, args);
			return args.Rethrow;
		}

		/// <summary>
		///   Determines if a log event is allowed for the levels given.
		/// </summary>
		/// <param name="levels"></param>
		/// <returns></returns>
		protected virtual bool AllowLogEvent(SourceLevels levels) { return false; }

		/// <summary>
		///   Occurs when logging messages are created on the reactor.
		/// </summary>
		/// <param name="eventType"></param>
		/// <param name="message"></param>
		protected virtual void OnLogMessage(TraceEventType eventType, string message) { }

		void Background_Reactor(object unused_state)
		{
			int itemsHandled = 0, workers, active;

			try
			{
				lock (_lock)
				{
					workers = Interlocked.Increment(ref _backgroundWorkers);
					Interlocked.Decrement(ref _backgroundWorkersScheduled);
					active = Interlocked.Increment(ref _backgroundWorkersActive);
				}
				if (AllowLogEvent(SourceLevels.Verbose))
				{
					OnLogMessage(TraceEventType.Verbose,
											String.Format(
																	 "Entering background reactor logic: {0} of {1}", active, workers)
						);
				}
				TItem item;
				// Continue until signaled or no more items in queue...
				while (!IsCanceled
					&& _queue.TryDequeue(out item))
				{
					itemsHandled++;
					try
					{
						_reactor(this, item);
					}
					catch (Exception e)
					{
						if (AllowLogEvent(SourceLevels.Error))
						{
							OnLogMessage(TraceEventType.Verbose,
													String.Format("Reactor threw an uncaught exception: {0}", e.FormatForLogging()));
						}
						if (OnUncaughtException(e))
						{
							throw;
						}
					}
				}
			}
			finally
			{
				var remaining = Interlocked.Decrement(ref _backgroundWorkersActive);
				try
				{
					if (!IsCanceled
						&& remaining == 0
						&& _queue.Count > 0)
					{
						CheckBackgroundReactorState();
					}
					if (AllowLogEvent(SourceLevels.Verbose))
					{
						OnLogMessage(TraceEventType.Verbose,
												String.Format(
																		 "Exiting background reactor; handled {0} items, {1} remaining workers", itemsHandled,
																		Thread.VolatileRead(ref _backgroundWorkers) - 1)
							);
					}
				}
				finally
				{
					Interlocked.Decrement(ref _backgroundWorkers);
				}
			}
		}

		void Foreground_Reactor(TItem item, int dispatchesPerSequential)
		{
			int itemsHandled = 0, workers, active;

			try
			{
				lock (_lock)
				{
					workers = Interlocked.Increment(ref _backgroundWorkers);
					Interlocked.Decrement(ref _backgroundWorkersScheduled);
					active = Interlocked.Increment(ref _backgroundWorkersActive);
				}
				if (AllowLogEvent(SourceLevels.Verbose))
				{
					OnLogMessage(TraceEventType.Verbose,
											String.Format(
																	 "Entering foreground reactor logic: {0} of {1}", active, workers)
						);
				}
				var it = item;
				do
				{
					itemsHandled++;
					try
					{
						_reactor(this, it);
					}
					catch (Exception e)
					{
						if (AllowLogEvent(SourceLevels.Error))
						{
							OnLogMessage(TraceEventType.Verbose,
													String.Format("Reactor threw an uncaught exception: {0}", e.FormatForLogging()));
						}
						if (OnUncaughtException(e))
						{
							throw;
						}
					}
				} while (!IsCanceled
					&& itemsHandled <= dispatchesPerSequential
					&& _queue.TryDequeue(out it));
			}
			finally
			{
				var remaining = Interlocked.Decrement(ref _backgroundWorkersActive);
				try
				{
					if (!IsCanceled
						&& remaining == 0
						&& _queue.Count > 0)
					{
						CheckBackgroundReactorState();
					}
					if (AllowLogEvent(SourceLevels.Verbose))
					{
						OnLogMessage(TraceEventType.Verbose,
												String.Format(
																		 "Exiting foreground reactor; handled {0} items, {1} remaining workers", itemsHandled,
																		Thread.VolatileRead(ref _backgroundWorkers) - 1)
							);
					}
				}
				finally
				{
					Interlocked.Decrement(ref _backgroundWorkers);
				}
			}
		}
	}
}