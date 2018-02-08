using System;
using System.Threading;
using System.Threading.Tasks;

namespace EventImplementedInduceInterface
{
	/// <summary>
	/// EIII(EventImplementedInduceInterface)
	/// </summary>

	/// <summary>
	/// (目的の)型
	/// </summary>
	internal class MyItem
	{
		public MyItem(int n, string s)
		{
			Number = n;
			TextMessage = s;
		}

		public MyItem(TSKCTRL t, int n, string s)
		{
			tskctl = t;
			Number = n;
			TextMessage = s;
		}

		public TSKCTRL tskctl { get; set; }
		public int Number { get; set; }
		public string TextMessage { get; set; }
	}

	/// <summary>
	/// EiiiTask
	/// </summary>
	internal class MyTask : EiiiTask<MyItem>
	{
		public MyTask(TaskToCallback cb, CancellationTokenSource cs, int freq, int tlc) : base(cb, cs, freq, tlc)
		{
		}

		internal override void eventFunc<T>(object sender, EiiiEventArgs<T> e)
		{
			base.eventFunc(sender, e);

			// override
			var itm = e.Value as MyItem;
			var bak = Console.ForegroundColor;
			var color = bak;
			switch (itm.tskctl)
			{
				case TSKCTRL.NEUTRAL:
					color = ConsoleColor.Gray;
					break;

				case TSKCTRL.RESTART:
					color = ConsoleColor.Red;
					break;

				default:
					break;
			}
			Console.ForegroundColor = color;
			Console.Write("OnEvent("
					+ DateTime.Now.ToString()
					+ "),TSKCTRL=" + itm.tskctl.ToString()
					+ ",Number=" + itm.Number.ToString()
					+ ",Text=" + itm.TextMessage);
			Console.ForegroundColor = bak;
			switch (itm.tskctl)
			{
				case TSKCTRL.NEUTRAL:
					break;

				case TSKCTRL.RESTART:

					restartTask();
					break;

				default:
					break;
			}
		}

		internal override Task<int> restartTask()
		{
			return base.restartTask();
		}

		internal override Task<string> starterTask(CancellationToken ct)
		{
			// override
			var color = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Green;
			Console.Write("Task, "
					+ DateTime.Now.ToString()
					+ ", StarterStart");
			Console.ForegroundColor = color;
			return base.starterTask(ct);
		}

		internal override Task<Task<int>> mainTask(CancellationToken ct)
		{
			// override
			var color = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.Write("Task, "
					+ DateTime.Now.ToString()
					+ ", MainStart");
			Console.ForegroundColor = color;

			return base.mainTask(ct);
		}
	}

	/// <summary>
	/// (目的の)メイン
	/// </summary>
	internal class Test
	{
		public TaskDbContext<MyItem> taskDbContext { get; set; }
		private static Test _instance { get; set; }

		public static Test GetInstance()
		{
			return _instance;
		}

		private static void Main(string[] args)
		{
			_instance = new Test();
			_instance.createTask();
			var loop = true;
			while (loop)
			{
				var readkey = Console.ReadKey().Key;
				switch (readkey)
				{
					case ConsoleKey.R:
						_instance.taskDbContext.GetTaskByName("aTsk").SetSubscribe(new EiiiLauncher<MyItem>());
						_instance.taskDbContext.GetTaskByName("aTsk").Trigger(new MyItem(TSKCTRL.RESTART, 111, "Item111"));
						break;

					case ConsoleKey.Spacebar:
						_instance.taskDbContext.GetTaskByName("aTsk").SetSubscribe(new EiiiLauncher<MyItem>());
						_instance.taskDbContext.GetTaskByName("aTsk").Trigger(new MyItem(TSKCTRL.NEUTRAL, 333, "Item333"));
						break;

					case ConsoleKey.Escape:
						// ESCキーで終了
						loop = false;
						break;

					default:
						break;
				}
				WaitForSeconds(1);
				Console.WriteLine(" ");
			}
			// Keep the console window open in debug mode.
			System.Console.WriteLine("Press any key to exit.");
			System.Console.ReadKey();
		}

		/// <summary>
		/// Wait
		/// </summary>
		private static async void WaitForSeconds(double val)
		{
			await Task.Delay(TimeSpan.FromSeconds(val));
		}

		/// <summary>
		/// Task Creations
		/// </summary>
		private async void createTask()
		{
			taskDbContext = new TaskDbContext<MyItem>();
			taskDbContext.Add("aTsk", new MyTask(Cb[0], Cs[0], 100, 1), Cs[0]);
			var tsks = taskDbContext.GetAll();

			// start TASK
			var tsk = taskDbContext.GetTaskByName("aTsk").GetTask();
			await taskDbContext.GetTaskByName("aTsk").GetTask().Start();
		}

		public static CancellationTokenSource[] Cs = new CancellationTokenSource[4];

		/// <summary>
		/// Array of Callback delegates
		/// </summary>
		public static TaskToCallback[] Cb = {
			new TaskToCallback( (s) => {
			// callback method 0
			GetInstance().aCallbacks(s);
			}),
		};

		private void aCallbacks(string s)
		{
			Console.Write("." + s);
		}
	}
}
