namespace EventImplementedInduceInterface
{
	public class EiiiLauncher<T> : ILauncher<T>
	{
		private T arg = default(T);

		public EiiiLauncher()
		{
		}

		public EiiiLauncher(T a)
		{
			this.arg = a;
		}

		public T Trigger
		{
			get
			{
				return this.arg;
			}
			set
			{
				this.arg = value;
				OnChanged(this, new EiiiEventArgs<T>(this.arg));
			}
		}

		// Delegate
		public event EventHandler<object, EiiiEventArgs<T>> Raise;

		// Trigger
		public void OnChanged(object sender, EiiiEventArgs<T> e)
		{
			if (Raise != null)
			{
				// Call the Event
				Raise(this, e);
			}
		}
	}
}
