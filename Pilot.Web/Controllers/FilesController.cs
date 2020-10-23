﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Ascon.Pilot.Common;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.DataModifier;
using DocumentRender;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Pilot.Web.Model;
using Pilot.Web.Model.DataObjects;
using Pilot.Web.Model.FileStorage;
using Pilot.Web.Model.ModifyData;
using Pilot.Web.Tools;

namespace Pilot.Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class FilesController : ControllerBase
    {
        private readonly IContextService _contextService;
        private readonly IDocumentRender _documentRender;
        private readonly IFileStorageProvider _fileStorageProvider;
        private readonly IFilesStorage _filesStorage;
        private readonly IFileSaver _fileSaver;

        public FilesController(IContextService contextService, IDocumentRender documentRender, 
            IFilesStorage filesStorage, IFileSaver fileSaver, IFileStorageProvider fileStorageProvider)
        {
            _contextService = contextService;
            _documentRender = documentRender;
            _fileStorageProvider = fileStorageProvider;
            _filesStorage = filesStorage;
            _fileSaver = fileSaver;
        }

        [Authorize]
        [HttpGet("[action]")]
        public int GetDocumentPagesCount(string fileId, long size, int scale)
        {
            var guid = Guid.Parse(fileId);
            var pages = _filesStorage.GetPages(guid).ToList();
            if (pages.Any())
                return pages.Count;

            var actor = HttpContext.GetTokenActor();
            var fileLoader = _contextService.GetFileLoader(actor);
            var file = fileLoader.Download(guid, size);

            pages = _documentRender.RenderPages(file, scale).ToList();
            if (pages.Any())
            {
                _fileSaver.PutFilesAsync(guid, pages);
            }

            return pages.Count;
        }

        [Authorize]
        [HttpGet("[action]")]
        public ActionResult GetDocumentPageContent(string fileId, int page)
        {
            var guid = Guid.Parse(fileId);
            var image = _filesStorage.GetImageFile(guid, page);
            return File(image ?? new byte[0], "image/png");
        }

        [Authorize]
        [HttpGet("[action]")]
        public ActionResult GetFile(string fileId, long size)
        {
            var guid = Guid.Parse(fileId);
            var actor = HttpContext.GetTokenActor();
            var fileLoader = _contextService.GetFileLoader(actor);
            var bytes = fileLoader.Download(guid, size);
            return File(bytes, "application/octet-stream");
        }

        [Authorize]
        [HttpGet("[action]")]
        public ActionResult GetThumbnail(string fileId, long size)
        {
            var guid = Guid.Parse(fileId);
            var thumbnail = _filesStorage.GetThumbnail(guid);
            if (thumbnail != null)
                return File(thumbnail, "image/png");

            var actor = HttpContext.GetTokenActor();
            var fileLoader = _contextService.GetFileLoader(actor);
            var fileContent = fileLoader.Download(guid, size);
            thumbnail = _documentRender.RenderPage(fileContent, 1, 0.2);
            if (thumbnail != null)
                _fileSaver.PutThumbnailAsync(guid, thumbnail);

            return File(thumbnail, "image/png");
        }

        [Authorize]
        [HttpPost("[action]")]
        public IActionResult GetFileArchive([FromBody] string[] ids)
        {
            if (ids.Length == 0)
                throw new Exception("ids are empty");

            var actor = HttpContext.GetTokenActor();
            var api = _contextService.GetServerApi(actor);
            var loader = _contextService.GetFileLoader(actor);

            var list = ids.Select(Guid.Parse).ToArray();
            var objects = api.GetObjects(list);

            using (var compressedFileStream = new MemoryStream())
            {
                using (var zipArchive = new ZipArchive(compressedFileStream, ZipArchiveMode.Update, true))
                {
                    AddObjectsToArchive(api, loader, objects, zipArchive, "");
                }

                var data = compressedFileStream.ToArray();
                return File(data, "application/octet-stream");
            }
        }

        /// <summary>
        /// Загрузка файлов на сервер
        /// </summary>
        /// <param name="parentId"></param>
        /// <returns></returns>
        [Authorize]
        [HttpPost("[action]/{parentId}")]
        [DisableRequestSizeLimit]
        public ActionResult<Guid[]> UploadFiles(Guid parentId)
        {
            const string fileTitleAttribute = "Title 4C281306-E329-423A-AF45-7B39EC30273F";
            
            try
            {
                if (Request.Form.Files.Count == 0)
                {
                    return BadRequest("There are not files");
                }
                
                if (IsBadFileExtensions(Request.Form.Files))
                {
                    return BadRequest("Bad file extension");
                }
            
                var actor = HttpContext.GetTokenActor();
                var api = _contextService.GetServerApi(actor);
            
                var parent = api.GetObjects(new []{parentId})?.FirstOrDefault();
                if (parent == null)
                {
                    throw new Exception("Parent is not found");
                }

                if (!parent.Type.IsMountable && parent.Type.Name != "Project_folder")
                {
                    throw new Exception("Parent is not mountable");
                }

                var fileType = api.GetMetadata().Types.FirstOrDefault(t => t.Name == "File");
                if (fileType == null)
                {
                    throw new Exception("File type is not found");
                }

                var existingFileObjectIds = parent.Children
                    .Where(c => c.TypeId == fileType.Id)
                    .Select(c => c.ObjectId)
                    .ToArray();

                var existingFileNames = new HashSet<string>(
                    api.GetObjects(existingFileObjectIds)
                        .Select(o => o.Attributes.TryGetValue(fileTitleAttribute, out var title) ? title as string : null)
                        .Where(t => !string.IsNullOrEmpty(t)));

                var result = new Guid[Request.Form.Files.Count];
                var modifier = api.NewModifier();
                var i = 0;
                foreach (var formFile in Request.Form.Files)
                {
                    var fileName = existingFileNames.Contains(formFile.Name) 
                        ? $"{Path.GetFileNameWithoutExtension(formFile.Name)} [{DateTime.Now:yyyy-MM-dd HH-mm-ss}]{Path.GetExtension(formFile.Name)}"
                        : formFile.Name;
                    
                    var newObjectId = Guid.NewGuid();
                    var builder = modifier.CreateObject(newObjectId, parentId, fileType);
                    builder.SetAttribute(fileTitleAttribute, fileName);
                    builder.AddFile(
                        new DocumentInfo(formFile.Name, formFile.OpenReadStream, DateTime.Now, DateTime.Now,
                            DateTime.Now), _fileStorageProvider);
                    result[i++] = modifier.Apply();
                }
                    
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        private static void AddObjectsToArchive(IServerApiService apiService, IFileLoader fileLoader, IEnumerable<PObject> objects, ZipArchive archive, string currentPath)
        {
            var stack = new Stack<PObject>();
            foreach (var child in objects)
            {
                stack.Push(child);
            }
                
            while (stack.Any())
            {
                var pObject = stack.Pop();
                if (pObject.Children.Any())
                {
                    var childrenIds = pObject.Children.Select(c => c.ObjectId).ToArray();
                    var children = apiService.GetObjects(childrenIds);
                    foreach (var child in children)
                    {
                        stack.Push(child);
                    }
                }

                if (pObject.Type.HasFiles)
                {
                    INFile dFile = null;
                    var entryName = string.Empty;
                    if (pObject.Type.Name == SystemTypes.PROJECT_FILE)
                    {
                        dFile = pObject.ActualFileSnapshot.Files.FirstOrDefault(f => !FileExtensionHelper.IsSystemFile(f.Name));
                        if (dFile == null)
                            continue;

                        entryName = dFile.Name;
                    }
                    else
                    {
                        dFile = pObject.ActualFileSnapshot.Files.FirstOrDefault(f => FileExtensionHelper.IsXpsAlike(f.Name) || FileExtensionHelper.IsPdfAlike(f.Name));
                        if (dFile == null)
                            continue;

                        entryName = $"{pObject.Title}{Path.GetExtension(dFile.Name)}";
                    }

                    var fileBody = fileLoader.Download(dFile.Id, dFile.Size);
                    if (archive.Entries.Any(x => x.Name == entryName))
                        entryName += " Conflicted";
                    
                    var zipEntry = archive.CreateEntry(Path.Combine(currentPath, entryName), CompressionLevel.NoCompression);

                    //Get the stream of the attachment
                    using (var originalFileStream = new MemoryStream(fileBody))
                    using (var zipEntryStream = zipEntry.Open())
                    {
                        //Copy the attachment stream to the zip entry stream
                        originalFileStream.CopyTo(zipEntryStream);
                    }
                }
                //else
                //{
                //    var directoryPath = Path.Combine(currentPath, dir.Title);
                //    if (archive.Entries.Any(x => x.Name == dir.Title))
                //        directoryPath += " Conflicted";

                //    var entry = archive.GetEntry(currentPath);
                //    entry?.Archive?.CreateEntry(directoryPath);
                //}
            }
        }
        
        /// <summary>
        /// Проверка расширения документа на допустимое к загрузке и скачиванию
        /// </summary>
        /// <param name="fileCollection">список загружаемых файлов</param>
        /// <returns></returns>
        private static bool IsBadFileExtensions(IFormFileCollection fileCollection)
        {
            var badExtensions = new List<string>()
            {
                ".exe",
                ".cmd",
                ".com",
                ".vbs",
                ".dll"
            };
            
            foreach (var file in fileCollection)
            {
                if (!badExtensions.Any(file.FileName.Contains))
                {
                    continue;
                }
                
                return true;
            }

            return false;
        }
    }
}