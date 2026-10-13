using System;

namespace EventImplementedInduceInterface
{
	public class EiiiEventArgs<T> : EventArgs
	{
		public T Value { get; private set; }

		public EiiiEventArgs(T val)
		{
			Value = val;
		}
	}
}
