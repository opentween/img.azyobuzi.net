﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ImgAzyobuziNet.Core.SupportServices;
using Jil;
using NX;

namespace ImgAzyobuziNet
{
    internal class ApiV2Interoperation
    {
        private static readonly string[] s_serviceNameWhitelist =
        {
            "携帯百景",
            "飯テロ.in",
            "My365",
            "MyPix",
            "ニコニコ動画",
            "ニコニコ静画",
            "OneDrive",
            "Ow.ly",
            "Path",
            "Pckles",
            "PHOTOHITO",
            "Photomemo",
            "Big Canvas PhotoShare",
            "フォト蔵",
            "PIAPRO",
            "Pikubo",
            "pixiv",
            "Shamoji",
            "SkyDrive",
            "Streamzoo",
            "TINAMI",
            "Tumblr",
            "つなビィ",
            "ついっぷるフォト",
            "TwitCasting",
            "Twitgoo",
            "TwitrPix",
            "Twitter",
            "Ustream.tv",
            "Via.Me",
            "Vimeo",
            "Vine",
            "yfrog",
            "YouTube"
        };

        private readonly Uri _oldApiUri;
        private readonly IHttpClient _httpClient;

        public ApiV2Interoperation(InteroperationOptions options, IHttpClient httpClient)
        {
            this._oldApiUri = new Uri(options.OldApiUri);
            this._httpClient = httpClient;
        }

        private Uri CreateRequestUri(string apiName)
        {
            return new Uri(this._oldApiUri, apiName);
        }

        private Task<HttpResponseMessage> SendRequest(HttpRequestMessage request)
        {
            // AutoRedirect は常にオフ
            return this._httpClient.SendAsync(request, false);
        }

        public async Task<IReadOnlyList<ApiV2NameRegexPair>> GetRegex()
        {
            string json;
            var req = new HttpRequestMessage(HttpMethod.Get, this.CreateRequestUri("regex.json"));
            using (var res = await this.SendRequest(req).ConfigureAwait(false))
            {
                res.EnsureSuccessStatusCode();
                json = await res.EnsureSuccessStatusCode().Content.ReadAsStringAsync().ConfigureAwait(false);
            }

            return JSON.Deserialize<IEnumerable<ApiV2NameRegexPair>>(json)
                .Where(x => s_serviceNameWhitelist.Contains(x.Name))
                .ToArray();
        }

        public async Task<(int StatusCode, byte[] Content)> AllSizes(string query)
        {
            var req = new HttpRequestMessage(
                HttpMethod.Get,
                this.CreateRequestUri("all_sizes.json" + query)
            );

            using (var res = await this.SendRequest(req).ConfigureAwait(false))
            {
                return ((int)res.StatusCode, await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            }
        }

        public async Task<Either<Uri, (int StatusCode, byte[] Content)>> Redirect(string query)
        {
            var req = new HttpRequestMessage(
                HttpMethod.Get,
                this.CreateRequestUri("redirect" + query)
            );

            using (var res = await this.SendRequest(req).ConfigureAwait(false))
            {
                switch (res.StatusCode)
                {
                    case HttpStatusCode.MovedPermanently:
                    case HttpStatusCode.Found:
                    case HttpStatusCode.SeeOther:
                        return res.Headers.Location.Inl();
                }

                return ((int)res.StatusCode, await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Inr();
            }
        }
    }
}