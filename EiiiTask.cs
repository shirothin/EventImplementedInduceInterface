using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace EventImplementedInduceInterface
{
	public class EiiiTask<T>
	{
		#region Fields

		private const int TIM_TSK_STRIDE = 100;
		protected Task task;
		protected string starter;
		protected TaskToCallback cb;
		protected int frequency;
		protected TaskLifeSpan life;
		private List<ILauncher<T>> launcher;
		private CancellationTokenSource cancelsource;
		private CancellationTokenSource cancelsourceclients;

		#endregion Fields

		#region Event

		public void Subscribe(ILauncher<T> lchr)
		{
			//pub.Raised += OnEvent;
			lchr.Raise += new EventHandler<object, EiiiEventArgs<T>>(OnEvent);
			this.launcher.Add(lchr);
		}

		internal virtual void OnEvent(object sender, EiiiEventArgs<T> e)
		{
			Debug.WriteLine("OnEvent(" + DateTime.Now.ToString() + ")" + e.ToString());
			eventFunc(sender, e);
		}

		/// <summary>
		/// イベント関数 virtual
		/// </summary>
		internal virtual void eventFunc<T>(object sender, EiiiEventArgs<T> e)
		{
			// override してここに目的の処理を記述
		}

		async internal void start()
		{
			await Start();
		}

		#endregion Event

		#region Cancellation

		protected void OnTaskCancelerInitialize()
		{
			initCancelResouces();
		}

		private void initCancelResouces()
		{
			if (cancelsource == null)
				cancelsource = new CancellationTokenSource();
			if (cancelsource.IsCancellationRequested)
			{
				cancelsource.Dispose();
				cancelsource = new CancellationTokenSource();
			}
		}

		#endregion Cancellation

		#region Constructor

		/// <summary>
		///
		/// </summary>
		public EiiiTask(TaskToCallback cb, CancellationTokenSource cs, int freq, int tlc)
		{
			this.launcher = new List<ILauncher<T>>();
			this.cb = cb;
			this.cancelsource = cs;
			initCancelResouces();
			this.frequency = (freq > 0) ? freq : 100;
			this.life = (tlc != 0) ? TaskLifeSpan.Infinite : TaskLifeSpan.Single;
			this.starter = null;
		}

		#endregion Constructor

		#region Externals

		public async Task<string> Start()
		{
			if (this.starter != null)
				return null;
			if (startOptions() == null)
				return null;
			await Task.Delay(0);
			this.starter = await starterTask(cancelsource.Token);
			return this.starter;
		}

		/// <summary>
		/// タスクを再起動する
		/// </summary>
		internal virtual Task<int> restartTask()
		{
			Task.Run(async () =>
			{
				cancelsource.Cancel();
				await Task.Delay(TIM_TSK_STRIDE * 2);
				await Task.Delay(1000);
				var lifebak = this.life;
				this.life = TaskLifeSpan.Single;
				this.starter = null;
				this.life = lifebak;
				start();
			});
			return Task.FromResult(0);
		}

		protected virtual string startOptions()
		{
			// additional coe here
			return "";
		}

		public TaskStatus GetStatus()
		{
			if (this.task == null)
				return TaskStatus.Faulted;
			return this.task.Status;
		}

		#endregion Externals

		#region Tasks

		internal virtual Task<string> starterTask(CancellationToken ct)
		{
			// override ere

			var res = "";
			Task.Run(async () =>
			{
				this.task = await mainTask(ct);
				this.starter = null;
			});
			return Task.FromResult<string>(res);
		}

		internal virtual async Task<Task<int>> mainTask(CancellationToken ct)
		{
			// override here

			int t = 0;
			while (this.life == TaskLifeSpan.Infinite)
			{
				try
				{
					await Task.Delay(TIM_TSK_STRIDE);
					// キャンセル待機可
					ct.ThrowIfCancellationRequested();
					t++;
					mainFunc(t);
				}
				catch (Exception ex)
				{
					// キャンセル
					Debug.WriteLine(ex.Message);
					if (ct.IsCancellationRequested == true)
						OnTaskCancelerInitialize();
					else
					{
						throw new Exception("throw at ", ex);
					}
					return Task.FromResult<int>(t);
				}
			}
			return Task.FromResult<int>(t);
		}

		/// <summary>
		/// Task to
		/// </summary>
		internal virtual void mainFunc(int t)
		{
			this.cb(t.ToString());
		}

		#endregion Tasks
	}
}
