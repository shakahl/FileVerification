﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace TE.FileVerification
{
    enum VerifyFileLayout
    {
        NAME,
        HASH_ALGORITHM,
        HASH
    }

    public class FileSystemCrawlerSO
    {
        const string VERIFY_FILE_NAME = "__fv.txt";

        public int NumFolders { get; set; }

        public int NumFiles { get; set; }

        public string FolderPath { get; set; }

        private int processorCount;

        private int threadCount;
        
        private List<DirectoryInfo> directories = new List<DirectoryInfo>();

        private readonly ConcurrentBag<Task> tasks = new ConcurrentBag<Task>();
        //private readonly ConcurrentBag<Task> fileTasks = new ConcurrentBag<Task>();

        public FileSystemCrawlerSO()
        {
            processorCount = Environment.ProcessorCount;
            threadCount = processorCount - 1;

            Logger.WriteLine($"Processors:    {processorCount}");
            Logger.WriteLine($"Threads:       {threadCount}");
        }

        public void CollectFolders(string path)
        {
            FolderPath = path;
            DirectoryInfo directoryInfo = new DirectoryInfo(path);
            tasks.Add(Task.Run(() => CrawlFolder(directoryInfo)));

            Task taskToWaitFor;
            while (tasks.TryTake(out taskToWaitFor))
            {
                NumFolders++;
                taskToWaitFor.Wait();
            }
        }

        public void CollectFiles()
        {  
            foreach (var dir in directories)
            {
                GetFiles(dir);
            }
        }

        private void CrawlFolder(DirectoryInfo dir)
        {
            try
            {
                DirectoryInfo[] directoryInfos = dir.GetDirectories();
                foreach (DirectoryInfo childInfo in directoryInfos)
                {
                    // here may be dragons using enumeration variable as closure!!
                    DirectoryInfo di = childInfo;
                    tasks.Add(Task.Run(() => CrawlFolder(di)));
                }
                directories.Add(dir);                
            }
            catch (Exception ex)
                when (ex is DirectoryNotFoundException || ex is System.Security.SecurityException || ex is UnauthorizedAccessException)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
            }
        }

        private void GetFiles(DirectoryInfo dir)
        {            
            FileInfo[] files = dir.GetFiles();
            VerifyFile verifyFile = new VerifyFile(VERIFY_FILE_NAME, dir);

            // Read the verify file, if it exists, but if the read method
            // returns null, indicating an exception, and the verify file file
            // exists, then assume there is an issue and don't continue with
            // the hashing and verification
            Dictionary<string, HashInfo> verifyFileData = verifyFile.Read();
            if (verifyFileData == null && verifyFile.Exists())
            {
                return;
            }

            ConcurrentDictionary<string, HashInfo> folderFileData = new ConcurrentDictionary<string, HashInfo>();
            
            ParallelOptions options = new ParallelOptions();
            options.MaxDegreeOfParallelism = threadCount;
            Parallel.ForEach(files, options, file =>
            {
                // Ignore the verification file and system files
                if (file.Name.Equals(VERIFY_FILE_NAME) || file.Attributes == FileAttributes.System)
                {
                    return;
                }

                folderFileData.TryAdd(file.Name, new HashInfo(file, Algorithm.SHA256));
                NumFiles++;
            });

            int count = 0;
            foreach (var file in folderFileData)
            {
                if (verifyFileData.TryGetValue(file.Key, out HashInfo hashInfo))
                {
                    if (!hashInfo.IsHashEqual(file.Value.Value))
                    {
                        Logger.WriteLine($"Hash mismatch: {dir.FullName}{Path.DirectorySeparatorChar}{file.Key}");
                        count++;
                    }
                }
                else
                {
                    verifyFileData.Add(file.Key, file.Value);
                }
            }

            if (verifyFileData.Count > 0)
            {
                verifyFile.Write(verifyFileData, dir);
            }

            if (count > 0)
            {
                Logger.WriteLine($"Number failed: {count}");
            }
        }
    }
}
