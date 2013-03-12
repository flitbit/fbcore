﻿#region COPYRIGHT© 2009-2013 Phillip Clark. All rights reserved.

// For licensing information see License.txt (MIT style licensing).

#endregion

using System;

namespace FlitBit.Core.Parallel
{
	/// <summary>
	///   EventArgs issued when a reactor encounters an uncaught exception.
	/// </summary>
	[Serializable]
	public sealed class ReactorExceptionArgs : EventArgs
	{
		/// <summary>
		///   Creates a new instance.
		/// </summary>
		/// <param name="err">the uncaught exception</param>
		public ReactorExceptionArgs(Exception err) { UncaughtException = err; }

		/// <summary>
		///   The uncaught exception.
		/// </summary>
		public Exception UncaughtException { get; private set; }

		/// <summary>
		///   Whether the exception should be rethrown.
		/// </summary>
		public bool Rethrow { get; set; }
	}
}