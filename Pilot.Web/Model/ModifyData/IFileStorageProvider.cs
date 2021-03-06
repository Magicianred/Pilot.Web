﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Ascon.Pilot.Common.Utils;
using Ascon.Pilot.DataModifier;

namespace Pilot.Web.Model.ModifyData
{
    public interface IFileStorage : IFileStorageProvider
    {
        string CreateFile(Guid fileId);
        void DeleteFile(Guid fileId);
    }

    public class FileStorageProvider : IFileStorage
    {
        private readonly string _fileArchiveFolder;
        private static readonly ConcurrentDictionary<Guid, string> Files = new ConcurrentDictionary<Guid, string>();

        public FileStorageProvider(string fileArchiveFolder)
        {
            _fileArchiveFolder = fileArchiveFolder;
            if (!Directory.Exists(fileArchiveFolder))
                Directory.CreateDirectory(fileArchiveFolder);
        }

        public Stream Create(Guid fileId)
        {
            var filePath = Path.Combine(_fileArchiveFolder, fileId + ".dat");
            Files[fileId] = filePath;
            return new FileStream(filePath, FileMode.OpenOrCreate);
        }

        public Stream Open(Guid id)
        {
            if (Files.TryGetValue(id, out var path))
                return File.Open(path, FileMode.OpenOrCreate);

            throw new FileNotFoundException("File not found", id.ToString());
        }

        public void ClearActualFileId(Guid objId)
        {
            throw new NotImplementedException();
        }

        public string CreateFile(Guid fileId)
        {
            var filePath = Path.Combine(_fileArchiveFolder, fileId.ToString());
            Files[fileId] = filePath;
            return filePath;
        }

        public void DeleteFile(Guid fileId)
        {
            if (!Files.TryGetValue(fileId, out var path))
                return;

            FileHelper.DeleteFile(path);
            Files.Remove(fileId, out _);
        }
    }
}
