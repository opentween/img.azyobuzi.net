﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ImgAzyobuziNet.TestFramework;
using Jil;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImgAzyobuziNet.Core.Resolvers
{
    public class FlickrProvider : PatternProviderBase<FlickrResolver>
    {
        public override string ServiceId => "Flickr";

        public override string ServiceName => "Flickr";

        public override string Pattern => @"^https?://(?:www\.)?(?:flickr\.com/photos/(?:[\w\-_@]+)/(?:(albums|galleries)/)?(\d+)(?:/(?:in|with|sizes)(?:/.*)?)?|flic\.kr/p/([1-9a-zA-Z]+))/?(?:[\?#]|$)";

        #region Tests
        [TestMethod(TestCategory.Static)]
        private void RegexPhotoTest()
        {
            var match = this.GetRegex().Match("https://www.flickr.com/photos/85669226@N02/23816761306/in/datetaken/");
            Assert.True(() => match.Success);
            Assert.True(() => !match.Groups[1].Success);
            match.Groups[2].Value.Is("23816761306");
            Assert.True(() => !match.Groups[3].Success);
        }

        [TestMethod(TestCategory.Static)]
        private void RegexAlbumTest()
        {
            var match = this.GetRegex().Match("https://www.flickr.com/photos/takeshik/albums/72157658404341455");
            Assert.True(() => match.Success);
            match.Groups[1].Value.Is("albums");
            match.Groups[2].Value.Is("72157658404341455");
            Assert.True(() => !match.Groups[3].Success);
        }

        [TestMethod(TestCategory.Static)]
        private void RegexShortenedTest()
        {
            var match = this.GetRegex().Match("https://flic.kr/p/ChB8nC");
            Assert.True(() => match.Success);
            Assert.True(() => !match.Groups[1].Success && !match.Groups[2].Success);
            match.Groups[3].Value.Is("ChB8nC");
        }
        #endregion
    }

    public class FlickrResolver : IResolver
    {
        private readonly ImgAzyobuziNetOptions _options;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger _logger;

        public FlickrResolver(IOptions<ImgAzyobuziNetOptions> options, IMemoryCache memoryCache, ILogger<FlickrResolver> logger)
        {
            this._options = options.Value;
            this._memoryCache = memoryCache;
            this._logger = logger;
        }

        public async Task<ImageInfo[]> GetImages(Match match)
        {
            try
            {
                if (!match.Groups[1].Success)
                {
                    var id = match.Groups[3].Success
                        ? DecodeBase58(match.Groups[3].Value).ToString("D")
                        : match.Groups[2].Value;

                    var result = await this._memoryCache.GetOrSet(
                       "flickrphoto-" + id,
                       () => this.FetchPhoto(id)
                    ).ConfigureAwait(false);

                    return new[] { result };
                }
                else
                {
                    var id = match.Groups[2].Value;
                    var task = match.Groups[1].Value == "albums"
                        ? this._memoryCache.GetOrSet("flickralbum-" + id, () => this.FetchAlbum(id))
                        : this._memoryCache.GetOrSet("flickrgallery-" + id, () => this.FetchGallery(id));
                    return await task.ConfigureAwait(false);
                }
            }
            catch (FlickrException ex) when (ex.Code == 1)
            {
                throw new ImageNotFoundException();
            }
        }

        private static long DecodeBase58(string s)
        {
            // 123456789abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ
            // l, I, O に注意
            var result = 0L;
            var m = 1L;
            for (var i = s.Length - 1; i >= 0; i--)
            {
                var c = s[i];
                // IndexOf を簡略化
                var value =
                    c <= '9' ? c - '1'
                    : c <= 'H' ? c - 'A' + 34
                    : c <= 'N' ? c - 'J' + 42
                    : c <= 'Z' ? c - 'P' + 47
                    : c <= 'k' ? c - 'a' + 9
                    : c - 'm' + 20;
                result += value * m;
                m *= 58;
            }
            return result;
        }

        #region Objects

        public class FlickrException : Exception
        {
            public FlickrException(int code, string message)
                : base(message)
            {
                this.Code = code;
            }

            public int Code { get; }
        }

        private abstract class FlickrResponseBase
        {
            public string stat;
            public int code;
            public string message;
        }

        private sealed class GetSizesResponse : FlickrResponseBase
        {
            public Sizes sizes;
        }

        private struct Sizes
        {
            public List<Size> size;
        }

        private struct Size
        {
            public string label;
            public string source;
        }

        private sealed class PhotosetsGetPhotosResponse : FlickrResponseBase
        {
            public Photoset photoset;
        }

        private struct Photoset
        {
            public IReadOnlyList<Photo> photo;
            public string owner;
        }

        private struct Photo
        {
            public string id;
            public string owner;
            public string secret;
            public string url_o;
            public string url_l;
            public string url_m;
            public string url_s;
            public string media;
        }

        private class GalleriesGetPhotosResponse : FlickrResponseBase
        {
            public Photos photos;
        }

        private struct Photos
        {
            public IReadOnlyList<Photo> photo;
        }

        #endregion

        private async Task<T> CallApi<T>(string method, string query)
            where T : FlickrResponseBase
        {
            string json;
            using (var hc = new HttpClient())
            {
                var requestUri = "https://api.flickr.com/services/rest/?format=json&nojsoncallback=1&"
                    + "&api_key=" + this._options.FlickrApiKey
                    + "&method=" + method
                    + "&" + query;
                ResolverUtils.RequestingMessage(this._logger, requestUri, null);

                json = await hc.GetStringAsync(requestUri).ConfigureAwait(false);
            }

            ResolverUtils.HttpResponseMessage(this._logger, json, null);
            var result = JSON.Deserialize<T>(json, Jil.Options.IncludeInherited);

            if (result.stat != "ok")
                throw new FlickrException(result.code, result.message);

            return result;
        }

        private async Task<ImageInfo> FetchPhoto(string id)
        {
            var sizes = (await this.CallApi<GetSizesResponse>("flickr.photos.getSizes", "photo_id=" + id))
                .sizes.size.ToDictionary(x => x.label, x => x.source);
            return new ImageInfo(
                sizes.GetOrDefault("Original") ?? sizes.GetOrDefault("Large") ?? sizes["Medium"],
                sizes.GetOrDefault("Large") ?? sizes["Medium"],
                sizes["Small"],
                sizes.GetOrDefault("HD MP4") ?? sizes.GetOrDefault("Site MP4"),
                sizes.GetOrDefault("Site MP4"),
                sizes.GetOrDefault("Mobile MP4")
            );
        }

        private async Task<ImageInfo[]> FetchAlbum(string id)
        {
            var res = (await this.CallApi<PhotosetsGetPhotosResponse>(
                "flickr.photosets.getPhotos",
                "extras=url_o,url_l,url_m,url_s,media&photoset_id=" + id
            ).ConfigureAwait(false)).photoset;

            return res.photo.ConvertAll(x =>
            {
                var isVideo = x.media == "video";
                var site = isVideo ? CreateVideoUri("site", x.id, res.owner, x.secret) : null;
                return new ImageInfo(
                    x.url_o ?? x.url_l ?? x.url_m,
                    x.url_l ?? x.url_m,
                    x.url_s,
                    site,
                    site,
                    isVideo ? CreateVideoUri("mobile", x.id, res.owner, x.secret) : null
                );
            });
        }

        private async Task<ImageInfo[]> FetchGallery(string id)
        {
            var res = await this.CallApi<GalleriesGetPhotosResponse>(
                "flickr.galleries.getPhotos",
                "extras=url_o,url_l,url_m,url_s,media&gallery_id=" + id
            ).ConfigureAwait(false);

            return res.photos.photo.ConvertAll(x =>
            {
                var isVideo = x.media == "video";
                var site = isVideo ? CreateVideoUri("site", x.id, x.owner, x.secret) : null;
                return new ImageInfo(
                    x.url_o ?? x.url_l ?? x.url_m,
                    x.url_l ?? x.url_m,
                    x.url_s,
                    site,
                    site,
                    isVideo ? CreateVideoUri("mobile", x.id, x.owner, x.secret) : null
                );
            });
        }

        private static string CreateVideoUri(string size, string id, string owner, string secret)
        {
            return "https://www.flickr.com/photos/" + owner + "/" + id + "/play/" + size + "/" + secret + "/";
        }

        #region Tests

        [TestMethod(TestCategory.Static)]
        private static void DecodeBase58Test()
        {
            DecodeBase58("y8hQ95").Is(21085915780);
        }

        [TestMethod(TestCategory.Network)]
        private async Task FetchPhotoTest()
        {
            // https://www.flickr.com/photos/85669226@N02/23816761306
            var result = await this.FetchPhoto("23816761306").ConfigureAwait(false);
            result.Full.Is("https://farm6.staticflickr.com/5725/23816761306_d95dabb2be_o.jpg");
            result.Large.Is("https://farm6.staticflickr.com/5725/23816761306_123ded3e11_b.jpg");
            result.Thumb.Is("https://farm6.staticflickr.com/5725/23816761306_123ded3e11_m.jpg");
            result.VideoFull.Is("https://www.flickr.com/photos/85669226@N02/23816761306/play/hd/123ded3e11/");
        }

        [TestMethod(TestCategory.Network)]
        private async Task FetchAlbumTest()
        {
            // https://www.flickr.com/photos/85669226@N02/albums/72157661860595319
            var result = await this.FetchAlbum("72157661860595319").ConfigureAwait(false);
            result.Length.Is(2);
            foreach (var x in result)
                Assert.True(() => !string.IsNullOrEmpty(x.Full) && !string.IsNullOrEmpty(x.Large) && !string.IsNullOrEmpty(x.Thumb));
            Assert.True(() => !string.IsNullOrEmpty(result[0].VideoFull) && !string.IsNullOrEmpty(result[0].VideoLarge) && !string.IsNullOrEmpty(result[0].VideoMobile));
            result[0].VideoFull.Is(result[0].VideoLarge);
            Assert.True(() => result[0].VideoLarge != result[0].VideoMobile);
        }

        [TestMethod(TestCategory.Network)]
        private async Task FetchGalleryTest()
        {
            // https://www.flickr.com/photos/flickr/galleries/72157662518421935/
            var result = await this.FetchGallery("72157662518421935").ConfigureAwait(false);
            result.Length.Is(25);
            foreach (var x in result)
                Assert.True(() => !string.IsNullOrEmpty(x.Full) && !string.IsNullOrEmpty(x.Large) && !string.IsNullOrEmpty(x.Thumb));
        }

        #endregion
    }
}
