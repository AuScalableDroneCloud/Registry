﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeMapping;
using Minio.Exceptions;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters
{
    public class ObjectsManager : IObjectsManager
    {
        private readonly ILogger<ObjectsManager> _logger;
        private readonly IObjectSystem _objectSystem;
        private readonly IChunkedUploadManager _chunkedUploadManager;
        private readonly IDdbManager _ddbManager;
        private readonly IUtils _utils;
        private readonly IAuthManager _authManager;
        private readonly ICacheManager _cacheManager;
        private readonly RegistryContext _context;
        private readonly AppSettings _settings;

        // TODO: Could be moved to config
        private const int DefaultThumbnailSize = 512;

        private const string BucketNameFormat = "{0}-{1}";

        public ObjectsManager(ILogger<ObjectsManager> logger,
            RegistryContext context,
            IObjectSystem objectSystem,
            IChunkedUploadManager chunkedUploadManager,
            IOptions<AppSettings> settings,
            IDdbManager ddbManager,
            IUtils utils, IAuthManager authManager, ICacheManager cacheManager)
        {
            _logger = logger;
            _context = context;
            _objectSystem = objectSystem;
            _chunkedUploadManager = chunkedUploadManager;
            _ddbManager = ddbManager;
            _utils = utils;
            _authManager = authManager;
            _cacheManager = cacheManager;
            _settings = settings.Value;
        }

        public async Task<IEnumerable<ObjectDto>> List(string orgSlug, string dsSlug, string path = null, bool recursive = false)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Searching in '{path}'");

            var files = ddb.Search(path, recursive).Select(file => file.ToDto()).ToArray();

            _logger.LogInformation($"Found {files.Length} objects");

            return files;
        }

        public async Task<ObjectRes> Get(string orgSlug, string dsSlug, string path)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path should not be null");

            return await InternalGet(orgSlug, ds.InternalRef, path);
        }

        private async Task<ObjectRes> InternalGet(string orgSlug, Guid internalRef, string path)
        {
            var ddb = _ddbManager.Get(orgSlug, internalRef);

            var res = ddb.Search(path).FirstOrDefault();

            if (res == null)
                throw new NotFoundException($"Cannot find '{path}'");

            var bucketName = GetBucketName(orgSlug, internalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
            {
                _logger.LogInformation("Bucket does not exist, creating it");

                await _objectSystem.MakeBucketAsync(bucketName, SafeGetRegion());

                _logger.LogInformation("Bucket created");
            }

            var objInfo = await _objectSystem.GetObjectInfoAsync(bucketName, res.Path);

            if (objInfo == null)
                throw new NotFoundException($"Cannot find '{res.Path}' in storage provider");

            await using var memory = new MemoryStream();

            _logger.LogInformation($"Getting object '{res.Path}' in bucket '{bucketName}'");

            await _objectSystem.GetObjectAsync(bucketName, res.Path, stream => stream.CopyTo(memory));

            return new ObjectRes
            {
                ContentType = objInfo.ContentType,
                Name = objInfo.ObjectName,
                Data = memory.ToArray(),
                // TODO: We can add more fields from DDB if we need them
                Type = res.Type,
                Hash = res.Hash,
                Size = res.Size
            };
        }

        private string SafeGetRegion()
        {
            if (_settings.StorageProvider.Type != StorageType.S3) return null;

            var settings = _settings.StorageProvider.Settings.ToObject<S3ProviderSettings>();
            if (settings == null)
            {
                _logger.LogWarning("No S3 settings loaded, shouldn't this be a problem?");
                return null;
            }

            if (settings.Region == null)
                _logger.LogWarning("No region specified in storage provider config");

            return settings.Region;
        }

        public async Task<UploadedObjectDto> AddNew(string orgSlug, string dsSlug, string path, byte[] data)
        {
            await using var stream = new MemoryStream(data);
            stream.Reset();
            return await AddNew(orgSlug, dsSlug, path, stream);
        }

        public async Task<UploadedObjectDto> AddNew(string orgSlug, string dsSlug, string path, Stream stream)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            var bucketName = GetBucketName(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            // If the bucket does not exist, let's create it
            if (!await _objectSystem.BucketExistsAsync(bucketName))
            {

                _logger.LogInformation($"Bucket '{bucketName}' does not exist, creating it");

                await _objectSystem.MakeBucketAsync(bucketName, SafeGetLocation());

                _logger.LogInformation("Bucket created");
            }

            // TODO: I highly doubt the robustness of this 
            var contentType = MimeTypes.GetMimeType(path);

            _logger.LogInformation($"Uploading '{path}' (size {stream.Length}) to bucket '{bucketName}'");

            // TODO: No metadata / encryption ?
            await _objectSystem.PutObjectAsync(bucketName, path, stream, stream.Length, contentType);

            _logger.LogInformation("File uploaded, adding to DDB");

            // Add to DDB
            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            ddb.Add(path, stream);

            _logger.LogInformation("Added to DDB");

            // Refresh objects count and total size
            ds.UpdateStatistics(ddb);
            await _context.SaveChangesAsync();

            var obj = new UploadedObjectDto
            {
                Path = path,
                ContentType = contentType,
                Size = stream.Length
            };

            return obj;
        }

        private string SafeGetLocation()
        {
            if (_settings.StorageProvider.Type != StorageType.S3) return null;

            var settings = _settings.StorageProvider.Settings.ToObject<S3ProviderSettings>();
            if (settings == null)
            {
                _logger.LogWarning("No S3 settings loaded, shouldn't this be a problem?");
                return null;
            }

            if (settings.Region == null)
                _logger.LogWarning("No region specified in storage provider config");

            return settings.Region;
        }

        public async Task Delete(string orgSlug, string dsSlug, string path)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            var bucketName = GetBucketName(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
                throw new BadRequestException($"Cannot find bucket '{bucketName}'");

            _logger.LogInformation($"Deleting '{path}'");

            await _objectSystem.RemoveObjectAsync(bucketName, path);

            _logger.LogInformation($"File deleted, removing from DDB");

            // Remove from DDB
            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            ddb.Remove(path);
            ds.UpdateStatistics(ddb);

            // Refresh objects count and total size
            await _context.SaveChangesAsync();

            _logger.LogInformation("Removed from DDB");

        }

        public async Task DeleteAll(string orgSlug, string dsSlug)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In DeleteAll('{orgSlug}/{dsSlug}')");

            var bucketName = GetBucketName(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
            {
                _logger.LogWarning($"Asked to remove non-existing bucket '{bucketName}'");
            }
            else
            {

                _logger.LogInformation("Deleting bucket");
                await _objectSystem.RemoveBucketAsync(bucketName);
                _logger.LogInformation("Bucket deleted");

            }

            _logger.LogInformation("Removing DDB");

            _ddbManager.Delete(orgSlug, ds.InternalRef);

        }

        #region Sessions
        public async Task<int> AddNewSession(string orgSlug, string dsSlug, int chunks, long size)
        {
            await _utils.GetDataset(orgSlug, dsSlug);

            var fileName = $"{orgSlug.ToSlug()}-{dsSlug.ToSlug()}-{CommonUtils.RandomString(16)}";

            _logger.LogDebug($"Generated '{fileName}' as temp file name");

            var sessionId = _chunkedUploadManager.InitSession(fileName, chunks, size);

            return sessionId;
        }

        public async Task AddToSession(string orgSlug, string dsSlug, int sessionId, int index, Stream stream)
        {
            await _utils.GetDataset(orgSlug, dsSlug);

            await _chunkedUploadManager.Upload(sessionId, stream, index);
        }

        public async Task AddToSession(string orgSlug, string dsSlug, int sessionId, int index, byte[] data)
        {
            await using var memory = new MemoryStream(data);
            memory.Reset();
            await AddToSession(orgSlug, dsSlug, sessionId, index, memory);
        }

        public async Task<UploadedObjectDto> CloseSession(string orgSlug, string dsSlug, int sessionId, string path)
        {
            await _utils.GetDataset(orgSlug, dsSlug);

            var tempFilePath = _chunkedUploadManager.CloseSession(sessionId, false);

            UploadedObjectDto newObj;

            await using (var fileStream = File.OpenRead(tempFilePath))
            {
                newObj = await AddNew(orgSlug, dsSlug, path, fileStream);
            }

            _chunkedUploadManager.CleanupSession(sessionId);

            File.Delete(tempFilePath);

            return newObj;
        }
        #endregion

        public async Task<FileDescriptorDto> GenerateThumbnail(string orgSlug, string dsSlug, string path, int? size, bool recreate = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            EnsurePathsValidity(orgSlug, ds.InternalRef, new[] { path });

            var fileName = Path.GetFileName(path);
            if (fileName == null)
                throw new ArgumentException("Path is not valid");

            var destFilePath = Path.Combine(Path.GetTempPath(), "out-" + Path.ChangeExtension(fileName, ".jpg"));
            var sourceFilePath = Path.Combine(Path.GetTempPath(), fileName);

            try
            {

                var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

                var entry = ddb.Search(path).FirstOrDefault();
                if (entry == null)
                    throw new ArgumentException($"Cannot find entry '{path}' in ddb");

                await _cacheManager.GenerateThumbnail(ddb, sourceFilePath, entry.Hash, size ?? DefaultThumbnailSize, destFilePath, async () =>
                {
                    var obj = await InternalGet(orgSlug, ds.InternalRef, path);
                    await File.WriteAllBytesAsync(sourceFilePath, obj.Data);
                });

                var memory = new MemoryStream(await File.ReadAllBytesAsync(destFilePath));
                memory.Reset();

                return new FileDescriptorDto
                {
                    ContentStream = memory,
                    ContentType = "image/jpeg",
                    Name = Path.ChangeExtension(fileName, ".jpg")
                };
            }
            finally
            {
                if (File.Exists(destFilePath)) File.Delete(destFilePath);
                if (File.Exists(sourceFilePath)) File.Delete(sourceFilePath);
            }

        }


        #region Downloads
        public async Task<string> GetDownloadPackage(string orgSlug, string dsSlug, string[] paths, DateTime? expiration = null, bool isPublic = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            EnsurePathsValidity(orgSlug, ds.InternalRef, paths);

            var currentUser = await _authManager.GetCurrentUser();

            var downloadPackage = new DownloadPackage
            {
                CreationDate = DateTime.Now,
                Dataset = ds,
                ExpirationDate = expiration,
                Paths = paths,
                UserName = currentUser.UserName,
                IsPublic = isPublic
            };

            await _context.DownloadPackages.AddAsync(downloadPackage);
            await _context.SaveChangesAsync();

            return downloadPackage.Id.ToString();
        }

        private void EnsurePathsValidity(string orgSlug, Guid internalRef, string[] paths)
        {

            if (paths == null || !paths.Any())
                // Everything
                return;

            if (paths.Any(path => path.Contains("*") || path.Contains("?") || string.IsNullOrWhiteSpace(path)))
                throw new ArgumentException("Wildcards or empty paths are not supported");

            if (paths.Length != paths.Distinct().Count())
                throw new ArgumentException("Duplicate paths");

            var ddb = _ddbManager.Get(orgSlug, internalRef);

            foreach (var path in paths)
            {
                var res = ddb.Search(path)?.ToArray();

                if (res == null || !res.Any())
                    throw new ArgumentException($"Invalid path: '{path}'");
            }
        }

        public async Task<FileDescriptorDto> DownloadPackage(string orgSlug, string dsSlug, string packageId)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug, checkOwnership: false);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            if (packageId == null)
                throw new ArgumentException("No package id provided");

            if (!Guid.TryParse(packageId, out var packageGuid))
                throw new ArgumentException("Invalid package id: expected guid");

            var package = _context.DownloadPackages.FirstOrDefault(item => item.Id == packageGuid);

            if (package == null)
                throw new ArgumentException($"Cannot find package with id '{packageId}'");

            var user = await _authManager.GetCurrentUser();

            // If we are not logged-in and this is not a public package
            if (user == null && !package.IsPublic)
                throw new UnauthorizedException("Download not allowed");

            // If it has and expiration date
            if (package.ExpirationDate != null)
            {
                // If expired
                if (DateTime.Now > package.ExpirationDate)
                {
                    _context.DownloadPackages.Remove(package);
                    await _context.SaveChangesAsync();

                    throw new ArgumentException("This package is expired");
                }
            }
            // It's a one-time download
            else
            {
                _context.DownloadPackages.Remove(package);
                await _context.SaveChangesAsync();
            }

            return await GetFileDescriptor(orgSlug, dsSlug, ds.InternalRef, package.Paths);

        }


        public async Task<FileDescriptorDto> Download(string orgSlug, string dsSlug, string[] paths)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            EnsurePathsValidity(orgSlug, ds.InternalRef, paths);

            return await GetFileDescriptor(orgSlug, dsSlug, ds.InternalRef, paths);
        }

        private async Task<FileDescriptorDto> GetFileDescriptor(string orgSlug, string dsSlug, Guid internalRef, string[] paths)
        {
            var ddb = _ddbManager.Get(orgSlug, internalRef);

            string[] filePaths = null;

            if (paths != null)
            {
                var temp = new List<string>();

                foreach (var path in paths)
                {
                    // We are in recursive mode because the paths could contain other folders that we need to expand
                    var items = ddb.Search(path, true)?
                        .Where(entry => entry.Type != EntryType.Directory)
                        .Select(entry => entry.Path).ToArray();

                    if (items == null || !items.Any())
                        throw new ArgumentException($"Cannot find any file path matching '{path}'");

                    temp.AddRange(items);
                }

                // Get rid of possible duplicates and sort
                filePaths = temp.Distinct().OrderBy(item => item).ToArray();

            }
            else
            {
                // Select everything and sort
                filePaths = ddb.Search(null, true)?
                    .Where(entry => entry.Type != EntryType.Directory)
                    .Select(entry => entry.Path)
                    .OrderBy(path => path)
                    .ToArray();

                if (filePaths == null)
                    throw new InvalidOperationException("Ddb is empty, what should I get?");
            }

            _logger.LogInformation($"Found {filePaths.Length} paths");

            FileDescriptorDto descriptor;

            // If there is just one file we return it
            if (filePaths.Length == 1)
            {
                var filePath = filePaths.First();

                _logger.LogInformation($"Only one path found: '{filePath}'");

                descriptor = new FileDescriptorDto
                {
                    ContentStream = new MemoryStream(),
                    Name = Path.GetFileName(filePath),
                    ContentType = MimeUtility.GetMimeMapping(filePath)
                };

                await WriteObjectContentStream(orgSlug, internalRef, filePath, descriptor.ContentStream);

                descriptor.ContentStream.Reset();
            }
            // Otherwise we zip everything together and return the package
            else
            {
                descriptor = new FileDescriptorDto
                {
                    Name = $"{orgSlug}-{dsSlug}-{CommonUtils.RandomString(8)}.zip",
                    ContentStream = new MemoryStream(),
                    ContentType = "application/zip"
                };

                using (var archive = new ZipArchive(descriptor.ContentStream, ZipArchiveMode.Create, true))
                {
                    foreach (var path in filePaths)
                    {
                        _logger.LogInformation($"Zipping: '{path}'");

                        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                        await using var entryStream = entry.Open();
                        await WriteObjectContentStream(orgSlug, internalRef, path, entryStream);
                    }
                }

                descriptor.ContentStream.Reset();
            }

            return descriptor;
        }

        private async Task WriteObjectContentStream(string orgSlug, Guid internalRef, string path, Stream stream)
        {
            var bucketName = GetBucketName(orgSlug, internalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
            {
                _logger.LogInformation("Bucket does not exist, creating it");

                await _objectSystem.MakeBucketAsync(bucketName, SafeGetRegion());

                _logger.LogInformation("Bucket created");
            }

            var objInfo = await _objectSystem.GetObjectInfoAsync(bucketName, path);

            if (objInfo == null)
                throw new NotFoundException($"Cannot find '{path}' in storage provider");

            _logger.LogInformation($"Getting object '{path}' in bucket '{bucketName}'");

            await _objectSystem.GetObjectAsync(bucketName, path, s => s.CopyTo(stream));
        }

        #endregion

        private string GetBucketName(string orgSlug, Guid internalRef)
        {
            return string.Format(BucketNameFormat, orgSlug, internalRef.ToString()).ToLowerInvariant();
        }
    }
}
