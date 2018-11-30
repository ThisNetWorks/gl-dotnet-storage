﻿namespace GeekLearning.Storage.Azure
{
    using GeekLearning.Storage.Azure.Configuration;
    using GeekLearning.Storage.Configuration;
    using Microsoft.WindowsAzure.Storage;
    using Microsoft.WindowsAzure.Storage.Blob;
    using Microsoft.WindowsAzure.Storage.Core;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Threading.Tasks;

    public class AzureStore : IStore
    {
        private AzureStoreOptions _storeOptions;
        private readonly Lazy<CloudBlobClient> _client;
        private Lazy<CloudBlobContainer> _container;

        public AzureStore(AzureStoreOptions storeOptions)
        {
            storeOptions.Validate();

            this._storeOptions = storeOptions;
            this._client = new Lazy<CloudBlobClient>(() => CloudStorageAccount.Parse(storeOptions.ConnectionString).CreateCloudBlobClient());
        }

        public string Name => this._storeOptions.Name;

        public void Dispose()
        {
            this._container = null;
        }

        public Task InitAsync(IStoreOptions individualStoreOptions)
        {
            //gives us the option to create a container per store, adhoc on the fly
            if (individualStoreOptions != null)
            {
                individualStoreOptions.Validate();
                this._storeOptions = (AzureStoreOptions)individualStoreOptions;
            }
            //regardless we need to build a container now
            this._container = new Lazy<CloudBlobContainer>(() => this._client.Value.GetContainerReference(this._storeOptions.FolderName));

            BlobContainerPublicAccessType accessType;
            switch (this._storeOptions.AccessLevel)
            {
                case Storage.Configuration.AccessLevel.Public:
                    accessType = BlobContainerPublicAccessType.Container;
                    break;
                case Storage.Configuration.AccessLevel.Confidential:
                    accessType = BlobContainerPublicAccessType.Blob;
                    break;
                case Storage.Configuration.AccessLevel.Private:
                default:
                    accessType = BlobContainerPublicAccessType.Off;
                    break;
            }

            return this._container.Value.CreateIfNotExistsAsync(accessType, null, null);
        }

        public async ValueTask<IFileReference[]> ListAsync(string path, bool recursive, bool withMetadata)
        {
            if (this._container == null)
            {
                throw new Exception("Must init store with a container");
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                path = null;
            }
            else
            {
                if (!path.EndsWith("/"))
                {
                    path = path + "/";
                }
            }

            BlobContinuationToken continuationToken = null;
            List<IListBlobItem> results = new List<IListBlobItem>();

            do
            {
                var response = await this._container.Value.ListBlobsSegmentedAsync(path, recursive, withMetadata ? BlobListingDetails.Metadata : BlobListingDetails.None, null, continuationToken, new BlobRequestOptions(), new OperationContext());
                continuationToken = response.ContinuationToken;
                results.AddRange(response.Results);
            }
            while (continuationToken != null);

            return results.OfType<ICloudBlob>().Select(blob => new Internal.AzureFileReference(blob, withMetadata: withMetadata)).ToArray();
        }

        public async ValueTask<IFileReference[]> ListAsync(string path, string searchPattern, bool recursive, bool withMetadata)
        {
            if (this._container == null)
            {
                throw new Exception("Must init store with a container");
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                path = null;
            }
            else
            {
                if (!path.EndsWith("/"))
                {
                    path = path + "/";
                }
            }

            string prefix = path;
            var firstWildCard = searchPattern.IndexOf('*');
            if (firstWildCard >= 0)
            {
                prefix += searchPattern.Substring(0, firstWildCard);
                searchPattern = searchPattern.Substring(firstWildCard);
            }

            Microsoft.Extensions.FileSystemGlobbing.Matcher matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher(StringComparison.Ordinal);
            matcher.AddInclude(searchPattern);

            var operationContext = new OperationContext();
            BlobContinuationToken continuationToken = null;
            List<IListBlobItem> results = new List<IListBlobItem>();

            do
            {
                var response = await this._container.Value.ListBlobsSegmentedAsync(prefix, recursive, withMetadata ? BlobListingDetails.Metadata : BlobListingDetails.None, null, continuationToken, new BlobRequestOptions(), new OperationContext());
                continuationToken = response.ContinuationToken;
                results.AddRange(response.Results);
            }
            while (continuationToken != null);

            var pathMap = results.OfType<ICloudBlob>().Select(blob => new Internal.AzureFileReference(blob, withMetadata: withMetadata)).ToDictionary(x => x.Path);

            var filteredResults = matcher.Execute(
                new Internal.AzureListDirectoryWrapper(path,
                pathMap));

            return filteredResults.Files.Select(x => pathMap[path + x.Path]).ToArray();
        }

        public async ValueTask<IFileReference> GetAsync(IPrivateFileReference file, bool withMetadata)
        {
            return await this.InternalGetAsync(file, withMetadata);
        }

        public async ValueTask<IFileReference> GetAsync(Uri uri, bool withMetadata)
        {
            return await this.InternalGetAsync(uri, withMetadata);
        }

        public async Task DeleteAsync(IPrivateFileReference file)
        {
            var fileReference = await this.InternalGetAsync(file);
            await fileReference.DeleteAsync();
        }

        public async ValueTask<Stream> ReadAsync(IPrivateFileReference file)
        {
            var fileReference = await this.InternalGetAsync(file);
            return await fileReference.ReadInMemoryAsync();
        }

        public async ValueTask<byte[]> ReadAllBytesAsync(IPrivateFileReference file)
        {
            var fileReference = await this.InternalGetAsync(file);
            return await fileReference.ReadAllBytesAsync();
        }

        public async ValueTask<string> ReadAllTextAsync(IPrivateFileReference file)
        {
            var fileReference = await this.InternalGetAsync(file);
            return await fileReference.ReadAllTextAsync();
        }

        public async ValueTask<IFileReference> SaveAsync(byte[] data, IPrivateFileReference file, string contentType, OverwritePolicy overwritePolicy = OverwritePolicy.Always)
        {
            using (var stream = new SyncMemoryStream(data, 0, data.Length))
            {
                return await this.SaveAsync(stream, file, contentType, overwritePolicy);
            }
        }

        public async ValueTask<IFileReference> SaveAsync(Stream data, IPrivateFileReference file, string contentType, OverwritePolicy overwritePolicy = OverwritePolicy.Always)
        {
            if (this._container == null)
            {
                throw new Exception("Must init store with a container");
            }
            var uploadBlob = true;
            var blockBlob = this._container.Value.GetBlockBlobReference(file.Path);
            var blobExists = await blockBlob.ExistsAsync();

            if (blobExists)
            {
                if (overwritePolicy == OverwritePolicy.Never)
                {
                    throw new Exceptions.FileAlreadyExistsException(this.Name, file.Path);
                }

                await blockBlob.FetchAttributesAsync();

                if (overwritePolicy == OverwritePolicy.IfContentModified)
                {
                    using (var md5 = MD5.Create())
                    {
                        data.Seek(0, SeekOrigin.Begin);
                        var contentMD5 = Convert.ToBase64String(md5.ComputeHash(data));
                        data.Seek(0, SeekOrigin.Begin);
                        uploadBlob = (contentMD5 != blockBlob.Properties.ContentMD5);
                    }
                }
            }

            if (uploadBlob)
            {
                await blockBlob.UploadFromStreamAsync(data);
            }

            var reference = new Internal.AzureFileReference(blockBlob, withMetadata: true);

            if (reference.Properties.ContentType != contentType)
            {
                reference.Properties.ContentType = contentType;
                await reference.SavePropertiesAsync();
            }

            return reference;
        }

        public ValueTask<string> GetSharedAccessSignatureAsync(ISharedAccessPolicy policy)
        {
            if (this._container == null)
            {
                throw new Exception("Must init store with a container");
            }
            var adHocPolicy = new SharedAccessBlobPolicy()
            {
                SharedAccessStartTime = policy.StartTime,
                SharedAccessExpiryTime = policy.ExpiryTime,
                Permissions = FromGenericToAzure(policy.Permissions),
            };

            return new ValueTask<string>(this._container.Value.GetSharedAccessSignature(adHocPolicy));
        }

        internal static SharedAccessBlobPermissions FromGenericToAzure(SharedAccessPermissions permissions)
        {
            var result = SharedAccessBlobPermissions.None;

            if (permissions.HasFlag(SharedAccessPermissions.Add))
            {
                result |= SharedAccessBlobPermissions.Add;
            }

            if (permissions.HasFlag(SharedAccessPermissions.Create))
            {
                result |= SharedAccessBlobPermissions.Create;
            }

            if (permissions.HasFlag(SharedAccessPermissions.Delete))
            {
                result |= SharedAccessBlobPermissions.Delete;
            }

            if (permissions.HasFlag(SharedAccessPermissions.List))
            {
                result |= SharedAccessBlobPermissions.List;
            }

            if (permissions.HasFlag(SharedAccessPermissions.Read))
            {
                result |= SharedAccessBlobPermissions.Read;
            }

            if (permissions.HasFlag(SharedAccessPermissions.Write))
            {
                result |= SharedAccessBlobPermissions.Write;
            }

            return result;
        }

        private ValueTask<Internal.AzureFileReference> InternalGetAsync(IPrivateFileReference file, bool withMetadata = false)
        {
            return this.InternalGetAsync(new Uri(file.Path, UriKind.Relative), withMetadata);
        }

        private async ValueTask<Internal.AzureFileReference> InternalGetAsync(Uri uri, bool withMetadata)
        {
            try
            {
                ICloudBlob blob;

                if (uri.IsAbsoluteUri)
                {
                    // When the URI is absolute, we cannot get a simple reference to the blob, so the
                    // properties and metadata are fetched, even if it was not asked.

                    blob = await this._client.Value.GetBlobReferenceFromServerAsync(uri);
                    withMetadata = true;
                }
                else
                {
                    if (withMetadata)
                    {
                        if (this._container == null)
                        {
                            throw new Exception("Must init store with a container");
                        }
                        blob = await this._container.Value.GetBlobReferenceFromServerAsync(uri.ToString());
                    }
                    else
                    {
                        if (this._container == null)
                        {
                            throw new Exception("Must init store with a container");
                        }
                        blob = this._container.Value.GetBlockBlobReference(uri.ToString());
                        if (!(await blob.ExistsAsync()))
                        {
                            return null;
                        }
                    }
                }

                return new Internal.AzureFileReference(blob, withMetadata);
            }
            catch (StorageException storageException)
            {
                if (storageException.RequestInformation.HttpStatusCode == 404)
                {
                    return null;
                }

                throw;
            }
        }

    }
}
