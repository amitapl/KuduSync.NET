﻿using CommandLine;
using CommandLine.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KuduSync.NET
{
    public class KuduSync
    {
        private static readonly string[] _projectFileExtensions = new[] { ".csproj", ".vbproj" };
        private static readonly List<string> _emptyList = Enumerable.Empty<string>().ToList();

        private string _from;
        private string _to;
        private DeploymentManifest _nextManifest;
        private HashSet<string> _previousManifest;
        private HashSet<string> _ignoreList;
        private bool _whatIf;

        public KuduSync(KuduSyncOptions options)
        {
            _from = Path.GetFullPath(options.From);
            _to = Path.GetFullPath(options.To);
            _nextManifest = new DeploymentManifest(options.NextManifestFilePath);
            _previousManifest = new HashSet<string>(DeploymentManifest.LoadManifestFile(options.PreviousManifestFilePath).Paths, StringComparer.OrdinalIgnoreCase);
            _ignoreList = BuildIgnoreList(options.Ignore);
            _whatIf = options.WhatIf;
        }

        private HashSet<string> BuildIgnoreList(string ignore)
        {
            if (!String.IsNullOrEmpty(ignore))
            {
                var ignoreList = ignore.Split(';').Select(s => s.Trim());
                return new HashSet<string>(ignoreList, StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>();
        }

        public void Run()
        {
            Logger.Log("Kudu sync from: \"{0}\" to: \"{1}\"", _from, _to);

            SmartCopy(_from, _to, new DirectoryInfoWrapper(new DirectoryInfo(_from)), new DirectoryInfoWrapper(new DirectoryInfo(_to)));

            _nextManifest.SaveManifestFile();
        }

        private void SmartCopy(string sourcePath,
                               string destinationPath,
                               DirectoryInfoBase sourceDirectory,
                               DirectoryInfoBase destinationDirectory)
        {
            // Skip source control folder
            if (IgnorePath(sourceDirectory))
            {
                return;
            }

            if (!destinationDirectory.Exists)
            {
                destinationDirectory.Create();
            }

            var destFilesLookup = FileSystemHelpers.GetFiles(destinationDirectory);
            var sourceFilesLookup = FileSystemHelpers.GetFiles(sourceDirectory);

            foreach (var destFile in destFilesLookup.Values)
            {
                // If the file doesn't exist in the source, only delete if:
                // 1. We have no previous directory
                // 2. We have a previous directory and the file exists there

                // Trim the start path
                string previousPath = destFile.FullName.Substring(destinationPath.Length).TrimStart('\\');
                if (!sourceFilesLookup.ContainsKey(destFile.Name) && DoesPathExistsInManifest(previousPath))
                {
                    Logger.Log("Deleting file: {0}", destFile.FullName);
                    destFile.Delete();
                }
            }

            foreach (var sourceFile in sourceFilesLookup.Values)
            {
                // Skip the .deployment file
                if (IgnorePath(sourceFile))
                {
                    continue;
                }

                _nextManifest.AddPath(sourcePath, sourceFile.FullName);

                // if the file exists in the destination then only copy it again if it's
                // last write time is different than the same file in the source (only if it changed)
                FileInfoBase targetFile;
                if (destFilesLookup.TryGetValue(sourceFile.Name, out targetFile) &&
                    sourceFile.LastWriteTimeUtc == targetFile.LastWriteTimeUtc)
                {
                    continue;
                }

                // Otherwise, copy the file
                string path = FileSystemHelpers.GetDestinationPath(sourcePath, destinationPath, sourceFile);

                Logger.Log("Copying file from: {0} to: {1}", sourceFile.FullName, path);
                OperationManager.Attempt(() => sourceFile.CopyTo(path, overwrite: true));
            }

            var sourceDirectoryLookup = FileSystemHelpers.GetDirectories(sourceDirectory);
            var destDirectoryLookup = FileSystemHelpers.GetDirectories(destinationDirectory);

            foreach (var destSubDirectory in destDirectoryLookup.Values)
            {
                // If the directory doesn't exist in the source, only delete if:
                // 1. We have no previous directory
                // 2. We have a previous directory and the file exists there

                string previousPath = destSubDirectory.FullName.Substring(destinationPath.Length).TrimStart('\\');
                if (!sourceDirectoryLookup.ContainsKey(destSubDirectory.Name) && DoesPathExistsInManifest(previousPath))
                {
                    Logger.Log("Deleting directory: {0}", destSubDirectory.FullName);
                    destSubDirectory.Delete(recursive: true);
                }
            }

            foreach (var sourceSubDirectory in sourceDirectoryLookup.Values)
            {
                DirectoryInfoBase targetSubDirectory;
                if (!destDirectoryLookup.TryGetValue(sourceSubDirectory.Name, out targetSubDirectory))
                {
                    string path = FileSystemHelpers.GetDestinationPath(sourcePath, destinationPath, sourceSubDirectory);
                    targetSubDirectory = CreateDirectoryInfo(path);
                }

                _nextManifest.AddPath(sourcePath, sourceSubDirectory.FullName);

                // Sync all sub directories
                SmartCopy(sourcePath, destinationPath, sourceSubDirectory, targetSubDirectory);
            }
        }

        private DirectoryInfoBase CreateDirectoryInfo(string path)
        {
            return new DirectoryInfoWrapper(new DirectoryInfo(path));
        }

        private bool IgnorePath(FileSystemInfoBase fileName)
        {
            return _ignoreList.Contains(fileName.Name);
        }

        private bool DoesPathExistsInManifest(string path)
        {
            return _previousManifest.Contains(path);
        }
    }
}
