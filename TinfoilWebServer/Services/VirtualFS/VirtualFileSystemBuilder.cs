﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using TinfoilWebServer.Logging;
using TinfoilWebServer.Utils;

namespace TinfoilWebServer.Services.VirtualFS;

public class VirtualFileSystemBuilder : IVirtualFileSystemBuilder
{

    private readonly IFileFilter _fileFilter;
    private readonly ILogger<VirtualFileSystemBuilder> _logger;

    public VirtualFileSystemBuilder(IFileFilter fileFilter, ILogger<VirtualFileSystemBuilder> logger)
    {
        _fileFilter = fileFilter ?? throw new ArgumentNullException(nameof(fileFilter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Helper to ensure we never have duplicated key
    /// </summary>
    private class KeyGenerator
    {
        private readonly string _baseFileNameWithoutExt;
        private readonly string _fileExt;
        private int? _num;

        public KeyGenerator(string baseFileName)
        {
            _baseFileNameWithoutExt = Path.GetFileNameWithoutExtension(baseFileName);
            _fileExt = Path.GetExtension(baseFileName);
            _num = null;
        }

        public string GetNextKey()
        {
            string nextKey;
            if (_num == null)
            {
                nextKey = $"{_baseFileNameWithoutExt}{_fileExt}";
                _num = 1;
            }
            else
            {
                nextKey = $"{_baseFileNameWithoutExt}_{_num}{_fileExt}";
                _num++;
            }

            return nextKey;
        }
    }


    private static void SafePopulateSubDir(VirtualDirectory parent, string subDirPath)
    {
        var dirName = Path.GetFileName(subDirPath);

        // Can be null or empty when serving the root of a drive, but in this case we really need to define a name to avoid empty URI path segment
        dirName = string.IsNullOrEmpty(dirName) ? "root" : dirName;

        var keyGenerator = new KeyGenerator(dirName);

        string key;
        do
        {
            key = keyGenerator.GetNextKey();
        } while (parent.ChildExists(key));

        parent.AddDirectory(new VirtualDirectory(key, subDirPath));
    }

    private static void SafePopulateFile(VirtualDirectory parent, string subFilePath)
    {
        var fileInfo = new FileInfo(subFilePath);

        var keyGenerator = new KeyGenerator(fileInfo.Name);

        string key;
        do
        {
            key = keyGenerator.GetNextKey();
        } while (parent.ChildExists(key));

        parent.AddFile(new VirtualFile(key, subFilePath, fileInfo.Length));
    }


    public VirtualFileSystemRoot BuildFlat(IReadOnlyList<string> servedDirectories)
    {
        var virtualFileSystemRoot = new VirtualFileSystemRoot();

        var remainingDirsToBrowse = new List<string>();

        foreach (var servedDirectoryFullPath in servedDirectories.Select(ToFullPath))
        {
            if (Directory.Exists(servedDirectoryFullPath))
                remainingDirsToBrowse.Add(servedDirectoryFullPath);
            else
                _logger.LogError($"Served directory \"{servedDirectoryFullPath}\" not found (or access denied).");
        }

        while (remainingDirsToBrowse.TryRemoveFirst(out var dirPath))
        {
            string[]? subFilePaths = null;
            try
            {
                subFilePaths = Directory.GetFiles(dirPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to list files of \"{dirPath}\": {ex.Message}");
            }

            if (subFilePaths != null)
            {
                foreach (var subFilePath in subFilePaths)
                {
                    if (_fileFilter.IsFileAllowed(subFilePath))
                        SafePopulateFile(virtualFileSystemRoot, subFilePath);
                }
            }

            string[]? subDirectoryPaths = null;
            try
            {
                subDirectoryPaths = Directory.GetDirectories(dirPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to list directories of \"{dirPath}\": {ex.Message}");
            }

            if (subDirectoryPaths != null)
                remainingDirsToBrowse.AddRange(subDirectoryPaths);
        }

        return virtualFileSystemRoot;
    }


    public VirtualFileSystemRoot BuildHierarchical(IReadOnlyList<string> servedDirectories, bool excludeEmptyDirectories)
    {
        var virtualFileSystemRoot = new VirtualFileSystemRoot();

        foreach (var servedDirectoryFullPath in servedDirectories.Select(ToFullPath))
        {
            // Trims end separator to avoid having an empty name while calling Path.GetFileName
            var safeDirPath = servedDirectoryFullPath.TrimEnd(Path.DirectorySeparatorChar);

            if (Directory.Exists(safeDirPath))
                SafePopulateSubDir(virtualFileSystemRoot, safeDirPath);
            else
                _logger.LogError($"Served directory \"{servedDirectoryFullPath}\" not found.");
        }

        var remainingDirsToBrowse = new List<VirtualDirectory>(virtualFileSystemRoot.Directories);
        while (remainingDirsToBrowse.TryRemoveFirst(out var virtualDir))
        {

            string[]? subDirectoryPaths = null;
            try
            {
                subDirectoryPaths = Directory.GetDirectories(virtualDir.FullLocalPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to list directories of \"{virtualDir.FullLocalPath}\": {ex.Message}");
            }

            if (subDirectoryPaths != null)
            {
                foreach (var subDirectory in subDirectoryPaths)
                {
                    SafePopulateSubDir(virtualDir, subDirectory);
                }

                remainingDirsToBrowse.AddRange(virtualDir.Directories);
            }

            string[]? subFilePaths = null;
            try
            {
                subFilePaths = Directory.GetFiles(virtualDir.FullLocalPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to list files of \"{virtualDir.FullLocalPath}\": {ex.Message}");
            }

            if (subFilePaths != null)
            {
                foreach (var subFilePath in subFilePaths)
                {
                    if (_fileFilter.IsFileAllowed(subFilePath))
                        SafePopulateFile(virtualDir, subFilePath);
                }
            }
        }

        if (excludeEmptyDirectories)
        {
            var removedDirectories = virtualFileSystemRoot.RemoveDirectoriesWithoutFile();

            if (_logger.IsEnabled(LogLevel.Debug) && removedDirectories.Count > 0)
                _logger.LogDebug($"Empty served directories removed:{removedDirectories.Select(directory => directory.FullLocalPath).ToMultilineString()}");
        }

        return virtualFileSystemRoot;

    }


    private static string ToFullPath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        return Path.GetFullPath(path);
    }

}