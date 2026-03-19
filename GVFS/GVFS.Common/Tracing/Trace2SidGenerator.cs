using System;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace GVFS.Common.Tracing
{
    /// <summary>
    /// Generates Trace2-compatible Session IDs (SIDs) with a monotonic
    /// timestamp guarantee to prevent collisions when multiple sessions
    /// are created from the same process.
    ///
    /// Format: yyyyMMddTHHmmss.ffffffZ-H{hostname_hash}-P{pid_hex}
    /// With optional parent SID prefix for hierarchical traces:
    ///   {parent_sid}/{this_sid}
    /// </summary>
    public class Trace2SidGenerator
    {
        private readonly string hostComponent;
        private readonly string pidComponent;
        private readonly object lockObj = new object();
        private long lastTimestampTicks;

        public Trace2SidGenerator()
        {
            this.hostComponent = ComputeHostComponent();
            this.pidComponent = string.Format("P{0:x8}", (uint)Process.GetCurrentProcess().Id);
        }

        /// <summary>
        /// Generate a unique SID. If parentSid is provided, it is prepended
        /// to create a hierarchical SID for parent-child trace correlation.
        /// </summary>
        public string Generate(string parentSid = null)
        {
            string mySid = this.GenerateComponent();
            return parentSid != null ? parentSid + "/" + mySid : mySid;
        }

        private string GenerateComponent()
        {
            long ticks;
            lock (this.lockObj)
            {
                ticks = DateTime.UtcNow.Ticks;
                if (ticks <= this.lastTimestampTicks)
                {
                    // Advance by 10 ticks (1 microsecond) to guarantee
                    // a different value in the 6-digit microsecond field.
                    ticks = this.lastTimestampTicks + 10;
                }

                this.lastTimestampTicks = ticks;
            }

            DateTime dt = new DateTime(ticks, DateTimeKind.Utc);
            long microseconds = (dt.Ticks / 10) % 1000000;

            return string.Format(
                "{0:yyyyMMdd}T{0:HHmmss}.{1:D6}Z-{2}-{3}",
                dt,
                microseconds,
                this.hostComponent,
                this.pidComponent);
        }

        private static string ComputeHostComponent()
        {
            try
            {
                string hostname = Dns.GetHostName();
                using (SHA1 sha1 = SHA1.Create())
                {
                    byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(hostname));
                    return "H" + BitConverter.ToString(hash, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
                }
            }
            catch
            {
                return "Localhost";
            }
        }
    }
}
