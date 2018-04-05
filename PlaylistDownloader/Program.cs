using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IniParser;
using IniParser.Model;
using WhetStone.Looping;
using WhetStone.Streams;
using WhetStone.WordPlay;
using YoutubeExplode;
using YoutubeExplode.Models;
using YoutubeExplode.Models.MediaStreams;

namespace PlaylistDownloader
{
    enum dlmode
    {
        Video,
        Audio
    }
    class todo
    {
        public static dlmode Mode;
        public todo(string id, string dest, string ext, int ind)
        {
            this.id = id;
            this.dest = dest;
            this.ind = ind;
            this.ext = ext;
        }
        public string id { get; }
        public string dest { get; }
        public int ind { get; }
        public string ext { get; }
        public override string ToString()
        {
            return $"{dest}(#{ind}) id:{id}";
        }
        public async Task Do(YoutubeClient client)
        {
            MediaStreamInfoSet streamInfoSet;
            try
            {
                streamInfoSet = await client.GetVideoMediaStreamInfosAsync(id);
            }
            catch (YoutubeExplode.Exceptions.VideoUnavailableException e)
            {
                Console.WriteLine($"Error @ {this}:");
                Console.WriteLine(e);
                return;
            }

            MediaStreamInfo streamInfo;
            switch (Mode)
            {
                case dlmode.Audio:
                    streamInfo = streamInfoSet.Audio.WithHighestBitrate();
                    break;
                case dlmode.Video:
                    streamInfo = streamInfoSet.Muxed.WithHighestVideoQuality();
                    break;
                default:
                    throw new Exception();
            }

            var fileName = dest+ "." + ext;
            await client.DownloadMediaStreamAsync(streamInfo, fileName);
        }
    }
    class Batcher
    {
        public YoutubeClient client { get; }
        private readonly ConcurrentBag<todo> _todos = new ConcurrentBag<todo>();
        private readonly EventWaitHandle _notEmptyHandle = new AutoResetEvent(false);
        public readonly EventWaitHandle doneHandle = new AutoResetEvent(true);

        public bool kill = false;
        public Batcher(YoutubeClient client)
        {
            this.client = client;
        }
        public void enqueue(todo t)
        {
            _todos.Add(t);
            updateTitle();
            _notEmptyHandle.Set();
        }
        public void updateTitle(int offset = 0)
        {
            var num = _todos.Count;
            num += offset;
            Console.Title = $"{num} pending downloads";
        }
        public async void run()
        {
            bool waitOne(int timeoutMillis = 1000)
            {
                while (true)
                {
                    var win = _notEmptyHandle.WaitOne(timeoutMillis);
                    if (win)
                        return true;
                    if (kill)
                        return false;
                }
            }
            while (waitOne())
            {
                //Console.WriteLine("queue resumed");
                doneHandle.WaitOne();

                while (true)
                {
                    if (!_todos.TryTake(out todo t))
                    {
                        _notEmptyHandle.Reset();
                        //Console.WriteLine("queue is empty");
                        break;
                    }

                    await t.Do(client);
                    updateTitle(1);
                }

                doneHandle.Set();
                if (kill)
                    break;
            }
        }
    }
    class Program
    {
        
        enum rule { Ask, Y, N, Skip, Do}
        static rule ParseRule(string s)
        {
            switch (s.ToLower())
            {
                case "ask":
                    return rule.Ask;
                case "skip":
                    return rule.Skip;
                case "y":
                    return rule.Y;
                case "n":
                    return rule.N;
                case "do":
                    return rule.Do;
                default:
                    throw new NotSupportedException();
            }
        }
        static string legalify(string s)
        {
            string regexSearch = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            Regex r = new Regex(string.Format("[{0}]", Regex.Escape(regexSearch)));
            return r.Replace(s, "");
        }
        static bool existsExtensionInvariant(string path)
        {
            var dir = Path.GetDirectoryName(path);
            var filename = Path.GetFileName(path);

            var candidates = Directory.GetFiles(dir, $"{filename}.*", SearchOption.TopDirectoryOnly);
            return candidates.Any();
        }
        static ISet<string> getblacklisted(string path)
        {
            using (var reader = new StreamReader(path))
            {
                return new HashSet<string>(reader.Loop());
            }
        }
        static (rule,string) getRule(string name, Video vid, ISet<string> blacklist, KeyDataCollection ruleSection)
        {
            var str = "default";
            if (existsExtensionInvariant(name))
            {
                str = "exists";
            }
            else if (blacklist.Contains(vid.Title))
            {
                str = "blacklisted";
            }
            return (ParseRule(ruleSection[str]), str);
        }
        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.GetEncoding("Windows-1255");

            Console.WriteLine("enter configuration file:");
            var configPath = Console.ReadLine();
            if (configPath.StartsWith("\"") && configPath.EndsWith("\""))
                configPath = configPath.Substring(1, configPath.Length - 2);
            var parser = new FileIniDataParser();

            var configDir = Path.GetDirectoryName(configPath);
            IniData configData = parser.ReadFile(configPath);

            var destDir = configData["Paths"]["destination"];
            destDir = Path.Combine(configDir, destDir);
            var source = configData["Paths"]["source"];
            int startIndex = int.Parse(configData["Paths"]["startIndex"]);
            var blacklistPath = configData["Paths"]["blacklist"];
            blacklistPath = Path.Combine(configDir, blacklistPath);
            var ext = configData["Paths"]["extension"];
            var modestr = configData["Paths"]["mode"];
            dlmode mode;
            switch (modestr)
            {
                case "audio":
                    mode = dlmode.Audio;
                    break;
                case "video":
                    mode = dlmode.Video;
                    break;
                default:
                    throw new Exception("mode mist be either video or audio");
            }

            todo.Mode = mode;

            Directory.CreateDirectory(destDir);

            var blacklisted = getblacklisted(blacklistPath);

            using (var blackListWriter = new StreamWriter(blacklistPath, true){AutoFlush = true})
            {
                var client = new YoutubeClient();
                source = YoutubeClient.ParsePlaylistId(source);
                var videos = client.GetPlaylistAsync(source).GetAwaiter().GetResult().Videos;

                var batcher = new Batcher(client);

                Thread dlThread = new Thread(() => batcher.run());
                dlThread.Start();

                string prevanswer = "Y";

                foreach (var (video, i) in videos.Skip(startIndex).CountBind(startIndex+1))
                {
                    var title = video.Title;
                    var fileName = Path.Combine(destDir, legalify(video.Title));
                    var (activeRule, reason) = getRule(fileName, video, blacklisted, configData["Rules"]);
                    IList<string> titles = new List<string> {$"#{i}/{videos.Count} ({reason}): {title}"};
                    if (video.Title.Any(a => a >= 128))
                        titles.Insert(0, title.Reverse().ConvertToString());
                    titles.Do(Console.WriteLine);



                    bool? @do;
                    string defAns = null;

                    switch (activeRule)
                    {
                        case rule.Ask:
                            @do = null;
                            defAns = prevanswer;
                            break;
                        case rule.N:
                            @do = null;
                            defAns = "N";
                            break;
                        case rule.Y:
                            @do = null;
                            defAns = "Y";
                            break;
                        case rule.Do:
                            @do = true;
                            break;
                        case rule.Skip:
                            @do = false;
                            break;
                        default:
                            throw new NotSupportedException();
                    }

                    if (@do == null)
                    {
                        string message = $"Enter for {defAns} ({reason})";

                        Console.WriteLine($"Y/N? {message}");

                        var answer = Console.ReadLine().ToUpper();
                        if (answer == "")
                            answer = defAns;
                        else
                            prevanswer = answer;
                        if (answer == "N")
                        {
                            @do = false;
                            blackListWriter.WriteLine(video.Title);
                        }
                        else
                        {
                            @do = true;
                        }

                    }

                    if (@do.Value)
                    {
                        var t = new todo(video.Id, fileName, ext, i);

                        batcher.enqueue(t);
                    }
                }

                Console.WriteLine("waiting for downloads to finish...");
                batcher.kill = true;
                batcher.doneHandle.WaitOne();
                batcher.doneHandle.Reset();
                Console.WriteLine("Done!, press enter to exit!");
                Console.ReadLine();
            }
        }
    }
}
