namespace EventImplementedInduceInterface
{
	public delegate void EventHandler<TSender, TEventArgs>(TSender sender, TEventArgs e);

	public interface ILauncher<T>
	{
		event EventHandler<object, EiiiEventArgs<T>> Raise;
	}
}
