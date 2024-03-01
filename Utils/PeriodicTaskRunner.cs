using System.Timers;
namespace Adminbot.Utils
{
    public class PeriodicTaskRunner
    {
        private static System.Timers.Timer _timer;

        // private static Dictionary<long, string> _allAccounts;
        public static CredentialsDbContext _credentialsDbContext;


        public static void Start()
        {

            // Set up the timer for 5 minutes (5 minutes * 60 seconds * 1000 milliseconds)
            _timer = new System.Timers.Timer(5 * 60 * 1000);

            // Hook up the Elapsed event for the timer
            _timer.Elapsed += OnTimedEvent;

            // Enable the timer
            _timer.Enabled = true;

            // Optional: If you want the timer to start immediately
            _timer.AutoReset = true;
            _timer.Start();
        }

        private static async void OnTimedEvent(Object source, ElapsedEventArgs e)
        {
            // Call the method that does the work
            await DoPeriodicWork();
        }

        private static async Task DoPeriodicWork()
        {

            var col_Users = _credentialsDbContext.Users.Where(u => u.IsColleague);
            await _credentialsDbContext.SaveChangesAsync();

            // Perform the calculation or work here
            Console.WriteLine("Periodic work is being performed.");
            //return Task.CompletedTask;
        }


        // public static  void StartAsync()
        // {
        //     // Set up the timer for 5 minutes (5 minutes * 60 seconds * 1000 milliseconds)
        //     _timer = new System.Timers.Timer(5 * 60 * 1000);

        //     // Hook up the Elapsed event for the timer
        //     _timer.Elapsed += OnTimedEvent;

        //     // Enable the timer
        //     _timer.Enabled = true;

        //     // Optional: If you want the timer to start immediately
        //     _timer.AutoReset = true;
        //     _timer.Start();
        // }



    }

}