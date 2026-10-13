namespace EventImplementedInduceInterface
{
	public class TaskEntity<T>
	{
		private static int lastId = 0;
		private int teId;
		private string teName;
		private EiiiTask<T> teTask;
		private EiiiLauncher<T> teLauncher;

		public TaskEntity(string n, EiiiTask<T> tsk)
		{
			this.teName = n;
			this.teTask = tsk;
			this.teId = lastId++;
		}

		public string GetName()
		{
			return this.teName;
		}

		public int GetId()
		{
			return this.teId;
		}

		public EiiiTask<T> GetTask()
		{
			return this.teTask;
		}

		public void Trigger(T obj)
		{
			this.teLauncher.Trigger = obj;
		}

		public void SetSubscribe(ILauncher<T> lchr)
		{
			if (lchr == null) return;
			teLauncher = (EiiiLauncher<T>)lchr;
			teTask.Subscribe(lchr);
		}
	}
}
