using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace EventImplementedInduceInterface
{
	public class TaskDbContext<T>
	{
		private Dictionary<string, int> tdic;
		private List<TaskEntity<T>> tlst;

		public TaskDbContext()
		{
			this.tdic = new Dictionary<string, int>();
			this.tlst = new List<TaskEntity<T>>();
		}

		public IEnumerable<TaskEntity<T>> GetAll()
		{
			return this.tlst;
		}

		public TaskEntity<T> GetTaskByName(string s)
		{
			try
			{
				var tsk = from t in this.tlst
						  where t.GetId() == this.tdic[s]
						  select t;
				return tsk.First();
			}
			catch { return null; }
		}

		public TaskEntity<T> GetTaskById(int n)
		{
			try
			{
				var tsk = from t in this.tlst
						  where t.GetId() == n
						  select t;
				return tsk.First();
			}
			catch { return null; }
		}

		public TaskEntity<T> Add(string s, EiiiTask<T> tsk, CancellationTokenSource cs)
		{
			this.tlst.Add(new TaskEntity<T>(s, tsk));
			try
			{
				this.tdic.Add(s, this.tlst.Last().GetId());
			}
			catch
			{
				return null;
			}
			return this.tlst.Last();
		}
	}
}
