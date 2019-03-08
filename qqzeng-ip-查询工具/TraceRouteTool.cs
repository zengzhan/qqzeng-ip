using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Net.Sockets;
using System.Linq;
namespace TraceRouteTool
{
    public partial class qqzengTraceRoute : Form
    {
        CancellationTokenSource tokenSource = new CancellationTokenSource();
       
        public qqzengTraceRoute()
        {
            InitializeComponent();
        }

        private async void button1_Click(object sender, EventArgs e)
        {
           
            if (!string.IsNullOrEmpty(textBox1.Text))
            {
                tokenSource = new CancellationTokenSource();
                var token = tokenSource.Token;
                listBox1.Items.Clear();
                await Task.Run(() => TraceRoute(30, textBox1.Text, token), token);
            }
         
          
        }


        private async void TraceRoute(int hopLimit, string hostNameOrIp, CancellationToken ct)
        {
            if (ct.IsCancellationRequested == true)
            {
                return;
                //ct.ThrowIfCancellationRequested();
            }


            for (int hopIndex = 0; hopIndex < hopLimit; hopIndex++)
            {
                int ttl = hopIndex + 1;
                int timeout = 200; 

               // string data = Guid.NewGuid().ToString();
                byte[] dataBytes =new byte[] { 0 };//Encoding.ASCII.GetBytes(data);
                Ping ping = new Ping();


                string ip = " ", local = " ", hostname = " ", reply_time = " ";
                IPStatus status= IPStatus.Unknown;
                Stopwatch pingReplyTime = new Stopwatch();
                List<string> times = new List<string>();
                List<IPAddress> ips = new List<IPAddress>();
                for (int i = 0; i < 3; i++)
                {
                    pingReplyTime.Start();
                    PingReply pingReply = await ping.SendPingAsync(hostNameOrIp, timeout, dataBytes, new PingOptions(ttl, true));
                                 
                    times.Add(pingReplyTime.ElapsedMilliseconds.ToString());
                    pingReplyTime.Reset();

                    ips.Add(pingReply.Address);
                    status = pingReply.Status;
                     // reply_time = pingReplyTime.ElapsedMilliseconds.ToString() + "ms";
                }



               var ipa= ips.FirstOrDefault(t => t != null);

                reply_time = string.Join(" / ", times);// + " ms";
                if (reply_time.Length <12)
                {
                    reply_time = "      "+ reply_time;
                }

                if (ipa != null)
                {
                    ip = ipa.ToString();


                    if (ip == "0.0.0.0")
                    {
                        ip = "*";
                        local = "";
                        hostname = "";
                    }
                    else
                    {
                        string t = IPSearch3Fast.Instance.Find(ip);
                        string[] r = t.Split('|');
                        string pcd = "";
                        if (r.Length > 3)
                        {
                            pcd = r[1] + r[2] + r[3] + r[4] + (r[5] == "" ? "" : "(" + r[5] + ")");
                        }
                        local = pcd;

                        if (r[5] != "保留")
                        {
                            hostname = GetReverseDNS(ip, r[1] == "中国" ? 60 : 200);
                        }

                    }
                }

                var entity = new TracertEntry()
                {
                    HopIndex = hopIndex + 1,
                    Ip = ip,
                    Hostname = hostname,
                    Local = local,
                    ReplyTime = reply_time,
                    ReplyStatus = status
                };
                UpdateUI(entity);


                if (status == IPStatus.Success)
                    break;

                if (ct.IsCancellationRequested)
                {
                    break;

                }
            }

           
        }


        private delegate IPHostEntry GetHostEntryHandler(string ip);

        public string GetReverseDNS(string ip, int timeout)
        {
            try
            {
                Func<IPAddress, IPHostEntry> callback = s => Dns.GetHostEntry(s);
                var result = callback.BeginInvoke(IPAddress.Parse(ip), null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeout, false))
                {
                    return "";
                }
                return callback.EndInvoke(result).HostName;
            }
            catch (Exception)
            {

                return "";
            }
                
          
             
        }

        private void UpdateUI(TracertEntry r)
        {
            if (this.InvokeRequired)
                this.Invoke(new Action<TracertEntry>(UpdateUI), r);
            else
            {

                listBox1.Items.Add(r.ToString());
              
            }

        }

        private void listBox1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            int index = this.listBox1.IndexFromPoint(e.Location);
            if (index != System.Windows.Forms.ListBox.NoMatches)
            {
                string s = listBox1.SelectedItem.ToString();
                Clipboard.SetText(s.Split('\t')[3]);
            }
        }

        private void textBox1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                button1_Click(this, new EventArgs());
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            tokenSource.Cancel();
        }

       
    }



    public class TracertEntry
    {
        /// <summary>
        /// The hop id. Represents the number of the hop.
        /// </summary>
        public int HopIndex { get; set; }

        /// <summary>
        /// The IP address.
        /// </summary>
        public string Ip { get; set; }

        public string Hostname { get; set; }
        /// <summary>
        /// The hostname
        /// </summary>
        public string Local { get; set; }

        /// <summary>
        /// The reply time it took for the host to receive and reply to the request in milliseconds.
        /// </summary>
        public string ReplyTime { get; set; }

        /// <summary>
        /// The reply status of the request.
        /// </summary>
        public IPStatus ReplyStatus { get; set; }

        public override string ToString()
        {
            return string.Format("{0}\t{1}\t\t{2}\t{3}\t\t\t{4}", HopIndex, ReplyTime, Ip, Local, Hostname);
        }
    }
}
