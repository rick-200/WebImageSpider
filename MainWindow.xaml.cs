using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Timers;
using System.Net;
using HtmlAgilityPack;
using System.Threading;
using Microsoft.Win32;
using ICSharpCode.SharpZipLib.Zip;
using System.Reflection;
namespace WebImageSpider {
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public class HttpImageBfsSpider {
        private string ctl_savepath;
        private Uri uri, proxy;
        private bool flag_running;
        private bool ctl_externhost;
        private bool flag_pause;
        private int ctl_maxurlcnt, ctl_parallelcnt, ctl_timeout, ctl_maxtrycount, state_completeurlcnt, state_errorcnt;
        private HttpClient http;
        private Queue<Uri> q = new Queue<Uri>();
        private List<Uri> failed_url = new List<Uri>();
        private HashSet<Uri> set_uri = new HashSet<Uri>();
        Mutex mtx_set_uri = new Mutex(), mtx_failed_url = new Mutex(), mtx_try_cnt = new Mutex(), mtx_state = new Mutex();
        public delegate void DeleOutPut(string s);
        DeleOutPut dele_log = (string s) => { Console.WriteLine(s); }, dele_err = (string s) => { Console.WriteLine(s); };
        private class UriComprer : IComparer<Uri> {
            public int Compare(Uri a, Uri b) {
                return a.ToString().CompareTo(b.ToString());
            }
        }
        private SortedList<Uri, int> try_cnt = new SortedList<Uri, int>(new UriComprer());
        Task task;
        public bool ExternHost { get { return ctl_externhost; } set { CtlSetCheck(); ctl_externhost = value; } }
        public bool IsRunning { get { return flag_running; } }
        public int MaxUrlCount { get { return ctl_maxurlcnt; } set { CtlSetCheck(); ctl_maxurlcnt = value; } }
        public int ParalleCount { get { return ctl_parallelcnt; } set { CtlSetCheck(); ctl_parallelcnt = value; } }
        public Uri StartUri { get { return uri; } set { CtlSetCheck(); uri = value; } }
        public Uri Proxy { get { return proxy; } set { CtlSetCheck(); proxy = value; InitClient(); } }
        public string SavePath { get { return ctl_savepath; } set { CtlSetCheck(); ctl_savepath = value; } }
        public int TimeOut { get { return ctl_timeout; } set { CtlSetCheck(); ctl_timeout = value; } }
        public int MaxTryCount { get { return ctl_maxtrycount; } set { CtlSetCheck(); ctl_maxtrycount = value; } }
        public int NowUrlCount { get { return q.Count; } }
        public int CompleteUrlCount { get { mtx_state.WaitOne(); var ret = state_completeurlcnt; mtx_state.ReleaseMutex(); return ret; } }
        public int ErrorCount { get { mtx_state.WaitOne(); var ret = state_errorcnt; mtx_state.ReleaseMutex(); return ret; } }
        public DeleOutPut DeleLog { get { return dele_log; } set { CtlSetCheck(); dele_log = value; } }
        public DeleOutPut DeleErr { get { return dele_err; } set { CtlSetCheck(); dele_err = value; } }
        private void CtlSetCheck() {
            if (flag_running) throw new Exception("can't set a control property when spider running.");
        }
        private bool SetUriContains(Uri uri) {
            mtx_set_uri.WaitOne();
            bool ret = set_uri.Contains(uri);
            mtx_set_uri.ReleaseMutex();
            return ret;
        }
        private void SetUriAdd(Uri uri) {
            mtx_set_uri.WaitOne();
            set_uri.Add(uri);
            mtx_set_uri.ReleaseMutex();
        }
        private static ulong HashString(string s) {
            decimal ret = 0;
            foreach (char c in s) {
                ret = (ret * 19260817 + c) % ulong.MaxValue;
            }
            return (ulong)ret;
        }
        static void DfsAnalizeWebPageLink(HtmlNode node, Uri baseuri, List<Uri> link_page, List<Uri> link_img) {
            switch (node.Name) {
                case "img":
                    if (node.Attributes.Contains("src"))
                        link_img.Add(new Uri(baseuri, node.Attributes["src"].Value));
                    break;
                case "a":
                    if (node.Attributes.Contains("href"))
                        link_page.Add(new Uri(baseuri, node.Attributes["href"].Value));
                    break;
            }
            foreach (HtmlNode x in node.ChildNodes) {
                DfsAnalizeWebPageLink(x, baseuri, link_page, link_img);
            }
        }
        private async Task<List<Uri>> Work(Uri uri, bool no_recursion = false) {
            dele_log("work: " + uri);
            List<Uri> list = new List<Uri>();
            List<Uri> imglist = new List<Uri>();
            try {
                var res = await http.GetAsync(uri);
                string type = res.Content.Headers.ContentType.MediaType;
                if (type == "text/html") {
                    string html = await res.Content.ReadAsStringAsync();
                    HtmlDocument hd = new HtmlDocument();
                    hd.LoadHtml(html);
                    DfsAnalizeWebPageLink(hd.DocumentNode, uri, list, imglist);
                    if (!no_recursion) {
                        foreach (var v in imglist) {
                            if (!SetUriContains(v)) {
                                SetUriAdd(v);
                                await Work(v, true);
                            }
                        }
                    }
                } else if (type.StartsWith("image")) {
                    dele_log("save: " + uri);
                    type = type.Substring(6);
                    if (!(type == "png" || type == "jpg" || type == "jpeg" || type == "bmp")) return list;
                    byte[] resbyte = await res.Content.ReadAsByteArrayAsync();
                    FileStream f = new FileStream(ctl_savepath + "/" + HashString(uri.ToString()) + "." + type, FileMode.Create);
                    f.Write(resbyte, 0, resbyte.Length);
                }
                mtx_try_cnt.WaitOne();
                if (try_cnt.ContainsKey(uri)) try_cnt.Remove(uri);
                mtx_try_cnt.ReleaseMutex();
                mtx_state.WaitOne();
                ++state_completeurlcnt;
                mtx_state.ReleaseMutex();
            } catch (Exception e) {
                mtx_state.WaitOne();
                ++state_errorcnt;
                mtx_state.ReleaseMutex();
                mtx_try_cnt.WaitOne();
                if (!try_cnt.ContainsKey(uri))
                    try_cnt[uri] = 0;
                try_cnt[uri]++;
                if (try_cnt[uri] < ctl_maxtrycount)
                    failed_url.Add(uri);
                dele_err(e.Message);
                mtx_try_cnt.ReleaseMutex();
            }
            return list;
        }
        private void Bfs() {
            Task<List<Uri>>[] taskpool = new Task<List<Uri>>[ctl_parallelcnt];
            Uri[] uri = new Uri[ctl_parallelcnt];
            while (q.Count != 0) {
                if (flag_pause) return;
                int cnt = Math.Min(ctl_parallelcnt, q.Count);
                for (int i = 0; i < cnt; i++) {
                    uri[i] = q.Dequeue();
                    taskpool[i] = Work(uri[i]);
                }
                for (int i = 0; i < cnt; i++) {
                    taskpool[i].Wait();
                    if (q.Count > ctl_maxurlcnt) continue;
                    foreach (var s in taskpool[i].Result) {
                        if (q.Count > ctl_maxurlcnt) continue;
                        if (SetUriContains(s)) continue;
                        if (s.Host != this.uri.Host && !ctl_externhost) continue;
                        Uri newurl = new Uri(uri[i], s);
                        SetUriAdd(newurl);
                        q.Enqueue(newurl);
                    }
                }
                foreach (var s in failed_url) {
                    q.Enqueue(s);
                }
                failed_url.Clear();
            }
            flag_running = false;
        }
        private void InitClient() {
            HttpClientHandler hch = new HttpClientHandler();
            if (proxy != null) {
                WebProxy wp = new WebProxy();
                wp.Address = proxy;
                hch.Proxy = wp;
                http = new HttpClient(hch);
            } else {
                http = new HttpClient();
            }
            http.Timeout = new TimeSpan(0, 0, 0, ctl_timeout);
            http.BaseAddress = uri;
        }
        public HttpImageBfsSpider() {
            ctl_maxurlcnt = 100000;
            ctl_parallelcnt = 10;
            ctl_externhost = false;
            ctl_timeout = 10;
            ctl_maxtrycount = 3;
        }
        public void Start() {
            InitClient();
            q.Enqueue(uri);
            flag_running = true;
            task = Task.Run(() => { Bfs(); });
        }
        public void Wait() {
            task.Wait();
        }
        public void Pause() {
            flag_pause = true;
            Wait();
            flag_pause = false;
            flag_running = false;
        }
        public void GetTaskState(out string[] queue_urls, out string[] complete_urls) {
            if (flag_running) throw new Exception("can't get taskstate when spider running.");
            queue_urls = new string[q.Count];
            complete_urls = new string[set_uri.Count];
            Uri[] uris = new Uri[Math.Max(q.Count, set_uri.Count)];
            q.CopyTo(uris, 0);
            for (int i = 0; i < q.Count; i++) {
                queue_urls[i] = uris[i].ToString();
            }
            set_uri.CopyTo(uris);
            for (int i = 0; i < set_uri.Count; i++) {
                complete_urls[i] = uris[i].ToString();
            }
        }
        public void ApplyTaskState(string[] queue_urls, string[] complete_urls) {
            state_completeurlcnt = complete_urls.Length;
            state_errorcnt = 0;
            q.Clear(); set_uri.Clear(); try_cnt.Clear();
            foreach (var s in queue_urls)
                q.Enqueue(new Uri(s));
            foreach (var s in complete_urls)
                set_uri.Add(new Uri(s));
        }
    }
    public partial class MainWindow : Window {
        HttpImageBfsSpider spider = new HttpImageBfsSpider();
        System.Timers.Timer timer = new System.Timers.Timer();
        FileStream fs_log, fs_err;
        StreamWriter sw_log, sw_err;
        System.Reflection.Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args) {
            string dllName = args.Name.Contains(",") ? args.Name.Substring(0, args.Name.IndexOf(',')) : args.Name.Replace(".dll", "");
            //MessageBox.Show(dllName);
            dllName = dllName.Replace(".", "_");
            if (dllName.EndsWith("_resources")) {
                return null;
            }
            System.Resources.ResourceManager rm = new System.Resources.ResourceManager(GetType().Namespace + ".Properties.Resources", System.Reflection.Assembly.GetExecutingAssembly());
            byte[] bytes = (byte[])rm.GetObject(dllName);
            return System.Reflection.Assembly.Load(bytes);
        }
        public MainWindow() {
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);
            InitializeComponent();
            timer.Interval = 1000;
            timer.Elapsed += (object timer_sender, ElapsedEventArgs ee) => {
                this.Dispatcher.Invoke(() => {
                    if (!spider.IsRunning) {
                        timer.Stop();
                        MessageBox.Show("已完成爬取");
                        Button_Click_5(null, null);
                    }
                    text_completecnt.Text = spider.CompleteUrlCount.ToString();
                    text_queuecnt.Text = spider.NowUrlCount.ToString();
                    text_errorcnt.Text = spider.ErrorCount.ToString();
                });
            };
            fs_log = new FileStream("./log.txt", FileMode.Create);
            fs_err = new FileStream("./err.txt", FileMode.Create);
            sw_log = new StreamWriter(fs_log);
            sw_err = new StreamWriter(fs_err);
            SetStateStop();
        }
        

        private void Button_Click(object sender, RoutedEventArgs e) {
        }

        private void Rectangle_MouseMove(object sender, MouseEventArgs e) {
            if (e.LeftButton == MouseButtonState.Pressed)
                this.DragMove();
        }
        private void Rectangle_MouseDown(object sender, MouseButtonEventArgs e) {
        }
        private void Rectangle_MouseUp(object sender, MouseButtonEventArgs e) {
        }

        private void Button_Click_1(object sender, RoutedEventArgs e) {
            this.Close();
        }

        private void Button_Click_2(object sender, RoutedEventArgs e) {
            SystemCommands.MinimizeWindow(this);
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {//timeout
            if (text_timeout == null) return;
            text_timeout.Text = ((int)e.NewValue).ToString();
        }

        private void Slider_ValueChanged_1(object sender, RoutedPropertyChangedEventArgs<double> e) {//trycnt
            if (text_trycnt == null) return;
            text_trycnt.Text = ((int)e.NewValue).ToString();
        }

        private void Slider_ValueChanged_2(object sender, RoutedPropertyChangedEventArgs<double> e) {//parallecnt
            if (text_parallecnt == null) return;
            text_parallecnt.Text = ((int)e.NewValue).ToString();
        }
        private void EnabledAll(bool val) {
            slider_timeout.IsEnabled = val;
            slider_trycnt.IsEnabled = val;
            slider_parallecnt.IsEnabled = val;
            edit_url.IsEnabled = val;
            edit_savepath.IsEnabled = val;
            edit_proxy.IsEnabled = val;
            check_externhost.IsEnabled = val;
            btn_choosefloder.IsEnabled = val;
        }
        private void SetStateRunning() {
            EnabledAll(false);
            btn_start.IsEnabled = false;
            btn_pause.IsEnabled = true;
            btn_stop.IsEnabled = false;
            btn_opentask.IsEnabled = false;
            btn_savetask.IsEnabled = false;
        }
        private void SetStatePause() {
            EnabledAll(true);
            btn_start.IsEnabled = true;
            btn_pause.IsEnabled = false;
            btn_stop.IsEnabled = true;
            btn_opentask.IsEnabled = false;
            btn_savetask.IsEnabled = true;
        }
        private void SetStateStop() {
            EnabledAll(true);
            btn_start.IsEnabled = true;
            btn_pause.IsEnabled = false;
            btn_stop.IsEnabled = false;
            btn_opentask.IsEnabled = true;
            btn_savetask.IsEnabled = false;
        }

        private void Button_Click_5(object sender, RoutedEventArgs e) {//stop
            SetStateStop();
            spider = new HttpImageBfsSpider();
            text_completecnt.Text = "0";
            text_queuecnt.Text = "0";
            text_errorcnt.Text = "0";
        }

        private void Button_Click_6(object sender, RoutedEventArgs e) {//save task
            string[] que_url, com_url;
            spider.GetTaskState(out que_url, out com_url);
            int id = 0;
            while (File.Exists("./SpiderTask" + id + ".task")) ++id;
            string filepath = "./SpiderTask" + id + ".task";
            MemoryStream ms = new MemoryStream();

            StreamWriter sw = new StreamWriter(ms);
            sw.WriteLine(edit_url.Text);
            sw.WriteLine(edit_savepath.Text);
            sw.WriteLine(edit_proxy.Text);
            sw.WriteLine(((int)slider_timeout.Value).ToString());
            sw.WriteLine(((int)slider_trycnt.Value).ToString());
            sw.WriteLine(((int)slider_parallecnt.Value).ToString());
            sw.WriteLine(que_url.Length);
            sw.WriteLine(com_url.Length);

            foreach (var s in que_url)
                sw.WriteLine(s);
            foreach (var s in com_url)
                sw.WriteLine(s);

            sw.Flush();

            FileStream fs = new FileStream(filepath, FileMode.Create);
            ZipOutputStream zos = new ZipOutputStream(fs);

            ZipEntry ze = new ZipEntry("task");

            zos.PutNextEntry(ze);
            zos.Write(ms.ToArray(), 0, (int)ms.Length);
            zos.Close();
            fs.Close();
            sw.Close();
            ms.Close();
            MessageBox.Show(filepath);
        }

        private void Button_Click_7(object sender, RoutedEventArgs e) {//open task
            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Filter = "任务|*.task";
            ofd.DefaultExt = "task";
            bool ret = ofd.ShowDialog(this) ?? false;
            if (!ret) return;
            spider = new HttpImageBfsSpider();


            FileStream fs = new FileStream(ofd.FileName, FileMode.Open);
            ZipInputStream zis = new ZipInputStream(fs);
            ZipEntry ze = zis.GetNextEntry();

            MemoryStream ms = new MemoryStream();

            byte[] buff = new byte[1024 * 128];

            while (true) {
                int readlen = zis.Read(buff, 0, 1024 * 128);
                if (readlen == 0) break;
                ms.Write(buff, 0, readlen);
            }
            zis.Close();
            fs.Close();

            ms.Seek(0, SeekOrigin.Begin);

            StreamReader sr = new StreamReader(ms);
            edit_url.Text = sr.ReadLine();
            edit_savepath.Text = sr.ReadLine();
            edit_proxy.Text = sr.ReadLine();
            slider_timeout.Value = int.Parse(sr.ReadLine());
            slider_trycnt.Value = int.Parse(sr.ReadLine());
            slider_parallecnt.Value = int.Parse(sr.ReadLine());
            string[] que_url, com_url;
            int que_cnt = int.Parse(sr.ReadLine());
            int com_cnt = int.Parse(sr.ReadLine());
            text_completecnt.Text = com_cnt.ToString();
            text_queuecnt.Text = que_cnt.ToString();
            text_errorcnt.Text = "0";
            que_url = new string[que_cnt];
            com_url = new string[com_cnt];
            for (int i = 0; i < que_cnt; i++)
                que_url[i] = sr.ReadLine();
            for (int i = 0; i < com_cnt; i++)
                com_url[i] = sr.ReadLine();
            spider.ApplyTaskState(que_url, com_url);
            sr.Close();
            ms.Close();
        }

        private void Button_Click_8(object sender, RoutedEventArgs e) {//chose
            System.Windows.Forms.FolderBrowserDialog fbd = new System.Windows.Forms.FolderBrowserDialog();
            if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                edit_savepath.Text = fbd.SelectedPath;
            }
        }

        private void Button_Click_4(object sender, RoutedEventArgs e) {//start
            try {
                SetStateRunning();
                spider.StartUri = new Uri(edit_url.Text);
                spider.SavePath = edit_savepath.Text;
                spider.TimeOut = int.Parse(text_timeout.Text);
                spider.MaxTryCount = int.Parse(text_trycnt.Text);
                spider.ParalleCount = int.Parse(text_parallecnt.Text);
                spider.ExternHost = check_externhost.IsChecked ?? false;
                if (edit_proxy.Text == "") {
                    spider.Proxy = null;
                } else {
                    spider.Proxy = new Uri(edit_proxy.Text);
                }

                spider.DeleLog = (string s) => { sw_log.WriteLine(s); };
                spider.DeleErr = (string s) => { sw_err.WriteLine(s); sw_err.Flush(); };

                spider.Start();
                timer.Start();
            } catch (Exception exc) {
                MessageBox.Show("Exception: " + exc.Message);
                SetStateStop();
            }
        }
        private void Button_Click_3(object sender, RoutedEventArgs e) {//pause
            SetStatePause();
            timer.Stop();
            spider.Pause();
        }
    }
}
