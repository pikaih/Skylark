﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using Mygod.Skylark.BackgroundRunner;
using Mygod.Xml.Linq;
using SevenZip;

namespace Mygod.Skylark
{
    public static partial class FileHelper
    {
        public static string GetFilePath(string path)
        {
            if (path.Contains("%", StringComparison.InvariantCultureIgnoreCase)
                || path.Contains("#", StringComparison.InvariantCultureIgnoreCase)) throw new FormatException();
            return Path.Combine("Files", path);
        }
        public static string GetDataPath(string path)
        {
            if (path.Contains("%", StringComparison.InvariantCultureIgnoreCase)
                || path.Contains("#", StringComparison.InvariantCultureIgnoreCase)) throw new FormatException();
            return Path.Combine("Data", path);
        }
        public static string GetDataFilePath(string path)
        {
            return GetDataPath(path) + ".data";
        }
        public static string GetTaskPath(string id)
        {
            return GetDataPath(id) + ".task";
        }

        public static IEnumerable<string> GetAllSources(this IMultipleSources task)
        {
            foreach (var file in task.Sources)
            {
                var path = GetFilePath(file);
                if (IsFile(path)) yield return file;
                else foreach (var sub in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
                        yield return sub.Substring(6);
            }
        }
    }

    public abstract partial class CloudTask
    {
        public void Execute()
        {
            try
            {
                PID = Process.GetCurrentProcess().Id;
                Save();
                ExecuteCore();
                if (!string.IsNullOrWhiteSpace(ErrorMessage)) Finish();
            }
            catch (Exception exc)
            {
                ErrorMessage = exc.GetMessage();
                Save();
            }
        }
        protected abstract void ExecuteCore();
    }
    public abstract partial class MultipleFilesTask
    {
        protected void StartFile(string relativePath)
        {
            FileHelper.WriteAllText(FileHelper.GetDataFilePath(relativePath),
                                    string.Format("<file state=\"{0}\" id=\"{1}\" pid=\"{2}\" />", Type, ID, PID));
            CurrentFile = relativePath;
            Save();
        }
        protected void FinishFile(string relativePath)
        {
            FileHelper.WriteAllText(FileHelper.GetDataFilePath(relativePath),
                                    string.Format("<file mime=\"{0}\" state=\"ready\" />", relativePath));
            Save();
        }
    }
    public abstract partial class MultipleToOneFileTask
    {
        public override sealed void Finish()
        {
            CurrentSource = null;
            base.Finish();
        }
    }
    public abstract partial class OneToMultipleFilesTask
    {
        public override sealed void Finish()
        {
            CurrentFile = null;
            base.Finish();
        }
    }

    public sealed partial class OfflineDownloadTask
    {
        public OfflineDownloadTask(string url, string relativePath)
            : base(relativePath, TaskType.OfflineDownloadTask)
        {
            Url = url;
        }

        protected override void ExecuteCore()
        {
            throw new NotSupportedException();
        }
    }
    public sealed partial class CompressTask
    {
        protected override void ExecuteCore()
        {
            SevenZipCompressor compressor = null;
            try
            {
                var files = this.GetAllSources().ToList();
                foreach (var file in files) FileHelper.WaitForReady(FileHelper.GetDataFilePath(file));
                long nextLength = 0, nextFile = 0;
                FileLength = files.Sum(file => new FileInfo(FileHelper.GetFilePath(file)).Length);
                compressor = new SevenZipCompressor
                {
                    CompressionLevel = (CompressionLevel)Enum.Parse(typeof(CompressionLevel),
                        TaskXml.GetAttributeValue("compressionLevel"), true)
                };
                switch (Path.GetExtension(RelativePath).ToLowerInvariant())
                {
                    case ".7z":
                        compressor.ArchiveFormat = OutArchiveFormat.SevenZip;
                        break;
                    case ".zip":
                        compressor.ArchiveFormat = OutArchiveFormat.Zip;
                        break;
                    case ".tar":
                        compressor.ArchiveFormat = OutArchiveFormat.Tar;
                        break;
                }
                var filesStart = Path.GetFullPath(FileHelper.GetFilePath(string.Empty)).Length + 1;
                compressor.FileCompressionStarted += (sender, e) =>
                {
                    ProcessedSourceCount += nextFile;
                    ProcessedFileLength += nextLength;
                    nextFile = 1;
                    nextLength = new FileInfo(e.FileName).Length;
                    CurrentSource = e.FileName.Substring(filesStart);
                    Save();
                };
                compressor.CompressFiles(FileHelper.GetFilePath(RelativePath),
                    Path.GetFullPath(FileHelper.GetFilePath(BaseFolder)).Length + 1,
                    files.Select(file => Path.GetFullPath(FileHelper.GetFilePath(file))).ToArray());
                ProcessedSourceCount += nextFile;
                ProcessedFileLength += nextLength;
                Finish();
            }
            catch (SevenZipException)
            {
                if (compressor == null) throw;
                throw new AggregateException(compressor.Exceptions);
            }
        }
    }
    public sealed partial class ConvertTask
    {
        private static readonly Regex TimeParser = new Regex(@"size=(.*)kB time=(.*)bitrate=", RegexOptions.Compiled);
        protected override void ExecuteCore()
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo("plugins/ffmpeg/ffmpeg.exe",
                    string.Format("-i \"{0}\"{2}{3} \"{1}\" -y", FileHelper.GetFilePath(Source),
                                  FileHelper.GetFilePath(RelativePath), string.IsNullOrWhiteSpace(AudioPath)
                                    ? string.Empty
                                    : " -i \"" + FileHelper.GetFilePath(AudioPath) + "\" -map 0:v -map 1:a",
                                  Arguments ?? string.Empty)) { UseShellExecute = false, RedirectStandardError = true }
            };
            process.Start();
            while (!process.StandardError.EndOfStream)
            {
                var line = process.StandardError.ReadLine();
                var match = TimeParser.Match(line);
                if (!match.Success) continue;
                try
                {
                    ProcessedFileLength = long.Parse(match.Groups[1].Value.Trim()) << 10;
                    ProcessedDuration = TimeSpan.Parse(match.Groups[2].Value.Trim());
                    Save();
                }
                catch { }
            }
            Finish();
        }
    }
    public sealed partial class DecompressTask
    {
        protected override void ExecuteCore()
        {
            SevenZipExtractor extractor = null;
            try
            {
                string directory = Target.Replace('/', '\\'),
                       filePath = FileHelper.GetFilePath(directory), dataPath = FileHelper.GetDataPath(directory);
                if (!Directory.Exists(filePath)) Directory.CreateDirectory(filePath);
                if (!Directory.Exists(dataPath)) Directory.CreateDirectory(dataPath);
                extractor = new SevenZipExtractor(FileHelper.GetFilePath(Source));
                var singleFileName = extractor.ArchiveFileNames.Count == 1
                                        && extractor.ArchiveFileNames[0] == "[no name]"
                                        ? Path.GetFileNameWithoutExtension(Source) : null;
                FileCount = extractor.FilesCount;
                FileLength = extractor.ArchiveFileData.Sum(data => (long)data.Size);
                long nextLength = 0, nextFile = 0;
                extractor.FileExtractionStarted += (sender, e) =>
                {
                    ProcessedFileCount += nextFile;
                    ProcessedFileLength += nextLength;
                    nextLength = (long)e.FileInfo.Size;
                    nextFile = 1;
                    StartFile(FileHelper.Combine(directory, singleFileName ?? e.FileInfo.FileName));
                };
                extractor.FileExtractionFinished +=
                    (sender, e) => FinishFile(FileHelper.Combine(directory, singleFileName ?? e.FileInfo.FileName));
                extractor.ExtractArchive(filePath);
                Finish();
            }
            catch (SevenZipException)
            {
                if (extractor == null) throw;
                throw new AggregateException(extractor.Exceptions);
            }
        }
    }
    public sealed partial class CrossAppCopyTask
    {
        private readonly CookieAwareWebClient client = new CookieAwareWebClient();
        private bool CopyFile(string domain, string source, string target, bool logging = true)
        {
            var targetFile = FileHelper.Combine(target, Path.GetFileName(source));
            CurrentFile = targetFile;
            try
            {
                var root = XDocument.Parse(client.DownloadString(
                    string.Format("http://{0}/Api/Details/{1}", domain, source))).Root;
                if (root.GetAttributeValue("status") != "ok")
                    throw new ExternalException(root.GetAttributeValue("message"));
                Save();
                Program.OfflineDownload(string.Format("http://{0}/Download/{1}", domain, source), target, client);
                var file = root.Element("file");
                FileHelper.SetDefaultMime(FileHelper.GetDataFilePath(targetFile), file.GetAttributeValue("mime"));
                ProcessedFileCount++;
                ProcessedFileLength += file.GetAttributeValue<long>("size");
                Save();
                return true;
            }
            catch (Exception exc)
            {
                if (logging)
                {
                    ErrorMessage += string.Format("复制 /{0} 时发生了错误：{2}{1}{2}", target, exc.GetMessage(),
                                                  Environment.NewLine);
                    Save();
                }
                return false;
            }
        }
        private void CopyDirectory(string domain, string source, string target)
        {
            CurrentFile = FileHelper.Combine(target, Path.GetFileName(source));
            Save();
            try
            {
                var root = XDocument.Parse(client.DownloadString(
                    string.Format("http://{0}/Api/List/{1}", domain, source))).Root;
                if (root.GetAttributeValue("status") != "ok")
                    throw new ExternalException(root.GetAttributeValue("message"));
                foreach (var element in root.Elements())
                {
                    var name = element.GetAttributeValue("name");
                    switch (element.Name.LocalName)
                    {
                        case "directory":
                            var dir = FileHelper.Combine(target, name);
                            Directory.CreateDirectory(FileHelper.GetFilePath(dir));
                            Directory.CreateDirectory(FileHelper.GetDataPath(dir));
                            CopyDirectory(domain, FileHelper.Combine(source, name), dir);
                            break;
                        case "file":
                            CopyFile(domain, FileHelper.Combine(source, name), target);
                            break;
                    }
                }
            }
            catch (Exception exc)
            {
                ErrorMessage += string.Format("复制 /{0} 时发生了错误：{2}{1}{2}", target, exc.GetMessage(),
                                              Environment.NewLine);
                Save();
            }
        }

        protected override void ExecuteCore()
        {
            ErrorMessage = string.Empty;
            client.CookieContainer.Add(new Cookie("Password", Password, "/", Domain));
            Password = null;
            Save();
            if (!CopyFile(Domain, Source, Target, false)) CopyDirectory(Domain, Source, Target);
            Finish();
        }
    }
    public sealed partial class FtpUploadTask
    {
        protected override void ExecuteCore()
        {
            var url = UrlFull;
            Url = Url;  // clear the user account data!!!
            Save();
            var sources = this.GetAllSources().ToArray();
            SourceCount = sources.Length;
            FileLength = sources.Sum(source => new FileInfo(FileHelper.GetFilePath(source)).Length);
            foreach (var file in sources) FileHelper.WaitForReady(FileHelper.GetDataFilePath(file));
            var set = new HashSet<string>(new[] { url });
            foreach (var file in sources)
            {
                CurrentSource = file;
                Save();
                string targetUrl = Path.Combine(url, string.IsNullOrEmpty(BaseFolder)
                                                        ? file : file.Substring(BaseFolder.Length + 1)),
                       targetDir = Path.GetDirectoryName(targetUrl);
                FtpWebRequest request;
                if (!set.Contains(targetDir))
                {
                    request = (FtpWebRequest)WebRequest.Create(targetDir);
                    request.Timeout = Timeout.Infinite;
                    request.UseBinary = true;
                    request.UsePassive = true;
                    request.KeepAlive = true;
                    request.Method = WebRequestMethods.Ftp.MakeDirectory;
                    request.GetResponse().Close();
                }
                request = (FtpWebRequest)WebRequest.Create(Path.Combine(url, file));
                request.Timeout = Timeout.Infinite;
                request.UseBinary = true;
                request.UsePassive = true;
                request.KeepAlive = true;
                request.Method = WebRequestMethods.Ftp.UploadFile;
                using (var src = File.OpenRead(FileHelper.GetFilePath(file)))
                using (var dst = request.GetRequestStream())
                {
                    var byteBuffer = new byte[1048576];
                    var bytesSent = src.Read(byteBuffer, 0, 1048576);
                    while (bytesSent != 0)
                    {
                        dst.Write(byteBuffer, 0, bytesSent);
                        ProcessedFileLength += bytesSent;
                        Save();
                        bytesSent = src.Read(byteBuffer, 0, 1048576);
                    }
                }
                request.GetResponse().Close();
                ProcessedSourceCount++;
            }
            Finish();
        }
    }

    public static partial class FFmpeg
    {
        static FFmpeg()
        {
            Root = Path.GetFullPath(".");
            var dirPath = Path.Combine(Root, "plugins/ffmpeg");
            Ffprobe = Path.Combine(dirPath, "ffprobe.exe");
        }
    }

    public class CookieAwareWebClient : WebClient
    {
        public CookieContainer CookieContainer { get; private set; }

        public CookieAwareWebClient(CookieContainer cookies = null)
        {
            CookieContainer = (cookies ?? new CookieContainer());
        }

        protected override WebRequest GetWebRequest(Uri address)
        {
            var request = base.GetWebRequest(address);
            ProcessRequest(request);
            return request;
        }
        public void ProcessRequest(WebRequest request)
        {
            var httpRequest = request as HttpWebRequest;
            if (httpRequest == null) return;
            httpRequest.CookieContainer = CookieContainer;
            httpRequest.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            httpRequest.ServicePoint.Expect100Continue = false;
        }
    }
}

namespace Mygod.Skylark.BackgroundRunner
{
    public static class Program
    {
        private static void Main()
        {
            try
            {
                var lines = Rbase64.Decode(Console.ReadLine()).Split('\n');
                switch (lines[0].ToLowerInvariant())
                {
                    case TaskType.OfflineDownloadTask:
                        OfflineDownload(lines[1], lines[2]);
                        break;
                    case TaskType.FtpUploadTask:
                        new FtpUploadTask(lines[1]).Execute();
                        break;
                    case TaskType.DecompressTask:
                        new DecompressTask(lines[1]).Execute();
                        break;
                    case TaskType.CompressTask:
                        new CompressTask(lines[1]).Execute();
                        break;
                    case TaskType.ConvertTask:
                        new ConvertTask(lines[1]).Execute();
                        break;
                    case TaskType.CrossAppCopyTask:
                        new CrossAppCopyTask(lines[1]).Execute();
                        break;
                    case TaskType.BatchMergeVATask:
                        var splitter = new[] {'\t'};
                        BatchMergeVA(lines[1], "true".Equals(lines[2], StringComparison.InvariantCultureIgnoreCase),
                                     lines[3], lines[4].Split(splitter, StringSplitOptions.RemoveEmptyEntries),
                                     lines[5].Split(splitter, StringSplitOptions.RemoveEmptyEntries));
                        break;
                    default:
                        Console.WriteLine("无法识别。");
                        break;
                }
            }
            catch (Exception exc)
            {
                File.AppendAllText(@"Data\error.log", string.Format("[{0}] {1}{2}{2}", DateTime.UtcNow,
                                                                    exc.GetMessage(), Environment.NewLine));
            }
        }

        private static string GetFileName(string url)
        {
            url = url.TrimEnd('/', '\\');
            int i = url.IndexOf('?'), j = url.IndexOf('#');
            if (j >= 0 && (i < 0 || i > j)) i = j;
            if (i >= 0) url = url.Substring(0, i);
            return Path.GetFileName(url);
        }
        public static void OfflineDownload(string url, string path, CookieAwareWebClient client = null)
        {
            FileStream fileStream = null;
            OfflineDownloadTask task = null;
            try
            {
                var retried = false;
            retry:
                var request = WebRequest.Create(url);
                var httpWebRequest = request as HttpWebRequest;
                if (httpWebRequest != null)
                {
                    httpWebRequest.Referer = url;
                    httpWebRequest.ReadWriteTimeout = Timeout.Infinite;
                    if (client != null) client.ProcessRequest(request);
                }
                request.Timeout = Timeout.Infinite;
                var response = request.GetResponse();
                if (!retried && url.StartsWith("http://goo.im", true, CultureInfo.InvariantCulture)
                    && response.ContentType == "text/html")
                {
                    retried = true;
                    Thread.Sleep(15000);
                    goto retry;
                }
                var stream = response.GetResponseStream();
                var disposition = response.Headers["Content-Disposition"] ?? string.Empty;
                var pos = disposition.IndexOf("filename=", StringComparison.Ordinal);
                long? fileLength;
                if (stream.CanSeek) fileLength = stream.Length;
                else
                    try
                    {
                        fileLength = response.ContentLength;
                    }
                    catch
                    {
                        fileLength = null;
                    }
                if (fileLength < 0) fileLength = null;

                var fileName = (pos >= 0 ? disposition.Substring(pos + 9).Trim('"', '\'').UrlDecode()
                                     : GetFileName(url)).ToValidPath();
                string mime, extension;
                try
                {
                    mime = Helper.GetMime(response.ContentType);
                    extension = Helper.GetDefaultExtension(mime);
                }
                catch
                {
                    extension = Path.GetExtension(fileName);
                    mime = Helper.GetDefaultExtension(extension);
                }
                if (Directory.Exists(FileHelper.GetFilePath(path)))
                {
                    if (!string.IsNullOrEmpty(extension) && !fileName.EndsWith(extension, StringComparison.Ordinal))
                        fileName += extension;
                    path = FileHelper.Combine(path, fileName);
                }

                task = new OfflineDownloadTask(url, path) { PID = Process.GetCurrentProcess().Id };
                if (!string.IsNullOrWhiteSpace(mime)) task.Mime = mime;
                if (fileLength != null) task.FileLength = fileLength;
                task.Save();
                stream.CopyTo(fileStream = File.Create(FileHelper.GetFilePath(path)));
                task.Finish();
            }
            catch (Exception exc)
            {
                if (task == null) throw;
                task.ErrorMessage = exc.Message;
                task.Save();
                if (client != null) throw;
            }
            finally
            {
                if (fileStream != null) fileStream.Close();
            }
        }

        private static void BatchMergeVA(string path, bool deleteSource, string videoPattern,
                                         string[] audioPatterns, string[] resultPatterns)
        {
            var videoMatcher = new Regex(videoPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var queue = new LinkedList<Tuple<string, string, string>>
                (from p in Directory.EnumerateFiles(FileHelper.GetFilePath(path), "*", SearchOption.AllDirectories)
                 let input = p.Substring(6) let match = videoMatcher.Match(input) where match.Success
                 let audio = (from ap in audioPatterns let ar = match.Result(ap)
                     where File.Exists(FileHelper.GetFilePath(ar)) select ar).FirstOrDefault() where audio != null
                 let output = (from op in resultPatterns let or = match.Result(op)
                     where !File.Exists(FileHelper.GetFilePath(or)) select or).FirstOrDefault() where output != null
                 select new Tuple<string, string, string>(input, audio, output));
            while (queue.Count > 0)
            {
                var node = queue.First;
                while (node != null)
                {
                    if (!FileHelper.IsReady(FileHelper.GetDataFilePath(node.Value.Item1)) ||
                        !FileHelper.IsReady(FileHelper.GetDataFilePath(node.Value.Item2)))
                    {
                        node = node.Next;
                        continue;
                    }
                    ConvertTask.Create(node.Value.Item1, node.Value.Item3, null, "copy", "copy", null,
                                       node.Value.Item2).Execute();
                    if (deleteSource)
                    {
                        FileHelper.Delete(node.Value.Item1);
                        FileHelper.Delete(node.Value.Item2);
                    }
                    var previous = node;
                    node = node.Next;
                    queue.Remove(previous);
                }
                Thread.Sleep(1000);
            }
        }
    }
}
