﻿using System;
using System.Collections.Generic;
using System.Threading;
using FlitBit.Core.Parallel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlitBit.Core.Tests.Parallel
{
	[TestClass]
	public class DemuxProducerTests
	{
		[TestMethod]
		public void DemuxProducer_CanDemultiplexOps()
		{
			var test = new
			{
				Threads = 12,
				Iterations = 1000,
				Max = 100
			};
			var originators = 0;
			var observers = 0;
			var blanks = 0;
			var fails = 0;
			var demux = new TestDemuxer();
			var threads = new List<Thread>();
			Exception ex = null;
			for (var i = 0; i < test.Threads; i++)
			{
				var thread = new Thread(() =>
				{
					var rand = new Random();

					for (var j = 0; j < test.Iterations; j++)
					{
						var observed = false;
						var item = rand.Next(test.Max);
						demux.TryConsume(item, (e, res) =>
						{
							if (e != null && ex == null)
							{
								ex = e;
							}
							else if (res == null)
							{
								Interlocked.Increment(ref blanks);
							}
							else
							{
								switch (res.Item1)
								{
									case DemuxResultKind.None:
										Interlocked.Increment(ref fails);
										break;
									case DemuxResultKind.Observed:
										Interlocked.Increment(ref observers);
										break;
									case DemuxResultKind.Originated:
										Interlocked.Increment(ref originators);
										break;
								}
							}
							observed = true;
						});

						while (!observed)
						{
							Thread.Sleep(0);
						}
					}
				});
				thread.Start();
				threads.Add(thread);
			}

			foreach (var th in threads)
			{
				th.Join();
			}
			if (ex != null)
			{
				throw ex;
			}

			Console.Out.WriteLine(String.Concat("Originators: ", originators));
			Console.Out.WriteLine(String.Concat("Observers: ", observers));
			Console.Out.WriteLine(String.Concat("Blanks: ", blanks));
			Console.Out.WriteLine(String.Concat("Fails: ", fails));
			Assert.AreEqual(0, fails);
		}

		[TestMethod]
		public void DemuxProducer_ExceptionsThrownByProducerArePropagatedToDemuxedThreads()
		{
			var producer = new ErrorProducer();

			var sync = new Object();
			var threads = new Tuple<Thread, Object>[10];
			for (var i = 0; i < threads.Length; i++)
			{
				var tuple = new Tuple<Thread, Object>(new Thread(n =>
				{
					var idx = (int) n;
					var completed = false;
					producer.TryConsume(idx, (e, res) =>
					{
						if (e != null)
						{
							lock (sync)
							{
								var tpl = threads[idx];
								threads[idx] = new Tuple<Thread, object>(tpl.Item1, e);
							}
						}
						else
						{
							lock (sync)
							{
								var tpl = threads[idx];
								threads[idx] = new Tuple<Thread, object>(tpl.Item1, tpl.Item1);
							}
						}
						completed = true;
					});
					while (!completed)
					{
						Thread.Sleep(0);
					}
				}), null);
				threads[i] = tuple;
				tuple.Item1.Start(i);
			}
			foreach (var tpl in threads)
			{
				tpl.Item1.Join();
			}
			lock (sync)
			{
				foreach (var t in threads)
				{
					Assert.IsNotNull(t.Item2);
				}
			}
		}

		class ErrorProducer : DemuxProducer<int, object>
		{
			protected override bool ProduceResult(int arg, out object value)
			{
				Thread.Sleep(15);
				throw new InvalidOperationException("Yo, this be da bomb!");
			}
		}

		class Observation
		{
			internal int Latency { get; set; }
			internal int Producer { get; set; }
			internal int Sequence { get; set; }
		}

		class TestDemuxer : DemuxProducer<int, Observation>
		{
			static int __sequence;

			protected override bool ProduceResult(int arg, out Observation value)
			{
				var wait = new Random().Next(10);
				Thread.Sleep(wait);
				value = new Observation
				{
					Sequence = Interlocked.Increment(ref __sequence),
					Producer = Thread.CurrentThread.ManagedThreadId,
					Latency = wait
				};
				return true;
			}
		}
	}
}