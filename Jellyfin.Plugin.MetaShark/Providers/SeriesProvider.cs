﻿using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMDbLib.Objects.Find;
using TMDbLib.Objects.TvShows;
using MetadataProvider = MediaBrowser.Model.Entities.MetadataProvider;

namespace Jellyfin.Plugin.MetaShark.Providers
{
    public class SeriesProvider : BaseProvider, IRemoteMetadataProvider<Series, SeriesInfo>
    {
        public SeriesProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<SeriesProvider>(), libraryManager, doubanApi, tmdbApi, omdbApi)
        {
        }

        public string Name => Plugin.PluginName;


        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo info, CancellationToken cancellationToken)
        {
            this.Log($"GetSearchResults of [name]: {info.Name}");
            var result = new List<RemoteSearchResult>();
            if (string.IsNullOrEmpty(info.Name))
            {
                return result;
            }

            // 从douban搜索
            var res = await this._doubanApi.SearchAsync(info.Name, cancellationToken).ConfigureAwait(false);
            result.AddRange(res.Take(this.config.MaxSearchResult).Select(x =>
            {
                return new RemoteSearchResult
                {
                    SearchProviderName = DoubanProviderName,
                    ProviderIds = new Dictionary<string, string> { { DoubanProviderId, x.Sid } },
                    ImageUrl = this.GetProxyImageUrl(x.Img),
                    ProductionYear = x.Year,
                    Name = x.Name,
                };
            }));

            // 尝试从tmdb搜索
            if (this.config.EnableTmdbSearch)
            {
                var tmdbList = await this._tmdbApi.SearchSeriesAsync(info.Name, info.MetadataLanguage, cancellationToken).ConfigureAwait(false);
                result.AddRange(tmdbList.Take(this.config.MaxSearchResult).Select(x =>
                {
                    return new RemoteSearchResult
                    {
                        SearchProviderName = TmdbProviderName,
                        ProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), x.Id.ToString(CultureInfo.InvariantCulture) } },
                        Name = x.Name ?? x.OriginalName,
                        ImageUrl = this._tmdbApi.GetPosterUrl(x.PosterPath),
                        Overview = x.Overview,
                        ProductionYear = x.FirstAirDate?.Year,
                    };
                }));
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            this.Log($"GetSeriesMetadata of [name]: {info.Name} [providerIds]: {info.ProviderIds.ToJson()}");
            var result = new MetadataResult<Series>();

            var sid = info.GetProviderId(DoubanProviderId);
            var tmdbId = info.GetProviderId(MetadataProvider.Tmdb);
            var metaSource = info.GetProviderId(Plugin.ProviderId); // 刷新元数据时会有值
            if (string.IsNullOrEmpty(sid) && string.IsNullOrEmpty(tmdbId))
            {
                // 刷新元数据自动匹配搜索
                sid = await this.GuessByDoubanAsync(info, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(sid))
                {
                    tmdbId = await this.GuestByTmdbAsync(info, cancellationToken).ConfigureAwait(false);
                }
            }

            if (metaSource != MetaSource.Tmdb && !string.IsNullOrEmpty(sid))
            {
                this.Log($"GetSeriesMetadata of douban [sid]: \"{sid}\"");
                var subject = await this._doubanApi.GetMovieAsync(sid, cancellationToken).ConfigureAwait(false);
                if (subject == null)
                {
                    return result;
                }
                subject.Celebrities = await this._doubanApi.GetCelebritiesBySidAsync(sid, cancellationToken).ConfigureAwait(false);

                var item = new Series
                {
                    ProviderIds = new Dictionary<string, string> { { DoubanProviderId, subject.Sid }, { Plugin.ProviderId, MetaSource.Douban } },
                    Name = subject.Name,
                    OriginalTitle = subject.OriginalName,
                    CommunityRating = subject.Rating,
                    Overview = subject.Intro,
                    ProductionYear = subject.Year,
                    HomePageUrl = "https://www.douban.com",
                    Genres = subject.Genres,
                    // ProductionLocations = [x?.Country],
                    PremiereDate = subject.ScreenTime,
                };

                // 通过imdb获取tmdbId
                if (!string.IsNullOrEmpty(subject.Imdb))
                {
                    item.SetProviderId(MetadataProvider.Imdb, subject.Imdb);
                    if (string.IsNullOrEmpty(tmdbId))
                    {
                        tmdbId = await this.GetTmdbIdByImdbAsync(subject.Imdb, info.MetadataLanguage, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(tmdbId))
                        {
                            item.SetProviderId(MetadataProvider.Tmdb, tmdbId);
                        }
                    }
                }

                // 尝试通过搜索匹配获取tmdbId
                if (string.IsNullOrEmpty(tmdbId))
                {
                    tmdbId = await this.GuestByTmdbAsync(info, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(tmdbId))
                    {
                        item.SetProviderId(MetadataProvider.Tmdb, tmdbId);
                    }
                }


                result.Item = item;
                result.QueriedById = true;
                result.HasMetadata = true;
                subject.LimitDirectorCelebrities.Take(this.config.MaxCastMembers).ToList().ForEach(c => result.AddPerson(new PersonInfo
                {
                    Name = c.Name == "" ? "未知" : c.Name,
                    Type = c.RoleType == "" ? "未知" : c.Name,
                    Role = c.Role == "" ? "未知" : c.Name,
                    ImageUrl = c.Img,
                    ProviderIds = new Dictionary<string, string> { { DoubanProviderId, c.Id } },
                }));

                return result;
            }


            if (!string.IsNullOrEmpty(tmdbId))
            {
                this.Log($"GetSeriesMetadata of tmdb [id]: \"{tmdbId}\"");
                var tvShow = await _tmdbApi
                    .GetSeriesAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                    .ConfigureAwait(false);

                if (tvShow == null)
                {
                    return result;
                }

                result = new MetadataResult<Series>
                {
                    Item = MapTvShowToSeries(tvShow, info.MetadataCountryCode),
                    ResultLanguage = info.MetadataLanguage ?? tvShow.OriginalLanguage
                };

                foreach (var person in GetPersons(tvShow))
                {
                    result.AddPerson(person);
                }

                result.QueriedById = true;
                result.HasMetadata = true;
                return result;
            }

            return result;
        }

        private Series MapTvShowToSeries(TvShow seriesResult, string preferredCountryCode)
        {
            var series = new Series
            {
                Name = seriesResult.Name,
                OriginalTitle = seriesResult.OriginalName
            };

            series.SetProviderId(MetadataProvider.Tmdb, seriesResult.Id.ToString(CultureInfo.InvariantCulture));

            series.CommunityRating = (float)System.Math.Round(seriesResult.VoteAverage, 2);

            series.Overview = seriesResult.Overview;

            if (seriesResult.Networks != null)
            {
                series.Studios = seriesResult.Networks.Select(i => i.Name).ToArray();
            }

            if (seriesResult.Genres != null)
            {
                series.Genres = seriesResult.Genres.Select(i => i.Name).ToArray();
            }

            if (seriesResult.Keywords?.Results != null)
            {
                for (var i = 0; i < seriesResult.Keywords.Results.Count; i++)
                {
                    series.AddTag(seriesResult.Keywords.Results[i].Name);
                }
            }
            series.HomePageUrl = seriesResult.Homepage;

            series.RunTimeTicks = seriesResult.EpisodeRunTime.Select(i => TimeSpan.FromMinutes(i).Ticks).FirstOrDefault();

            if (string.Equals(seriesResult.Status, "Ended", StringComparison.OrdinalIgnoreCase))
            {
                series.Status = SeriesStatus.Ended;
                series.EndDate = seriesResult.LastAirDate;
            }
            else
            {
                series.Status = SeriesStatus.Continuing;
            }

            series.PremiereDate = seriesResult.FirstAirDate;

            var ids = seriesResult.ExternalIds;
            if (ids != null)
            {
                if (!string.IsNullOrWhiteSpace(ids.ImdbId))
                {
                    series.SetProviderId(MetadataProvider.Imdb, ids.ImdbId);
                }

                if (!string.IsNullOrEmpty(ids.TvrageId))
                {
                    series.SetProviderId(MetadataProvider.TvRage, ids.TvrageId);
                }

                if (!string.IsNullOrEmpty(ids.TvdbId))
                {
                    series.SetProviderId(MetadataProvider.Tvdb, ids.TvdbId);
                }
            }
            series.SetProviderId(Plugin.ProviderId, MetaSource.Tmdb);
            var contentRatings = seriesResult.ContentRatings.Results ?? new List<ContentRating>();

            var ourRelease = contentRatings.FirstOrDefault(c => string.Equals(c.Iso_3166_1, preferredCountryCode, StringComparison.OrdinalIgnoreCase));
            var usRelease = contentRatings.FirstOrDefault(c => string.Equals(c.Iso_3166_1, "US", StringComparison.OrdinalIgnoreCase));
            var minimumRelease = contentRatings.FirstOrDefault();

            if (ourRelease != null)
            {
                series.OfficialRating = ourRelease.Rating;
            }
            else if (usRelease != null)
            {
                series.OfficialRating = usRelease.Rating;
            }
            else if (minimumRelease != null)
            {
                series.OfficialRating = minimumRelease.Rating;
            }

            return series;
        }

        private IEnumerable<PersonInfo> GetPersons(TvShow seriesResult)
        {
            // 演员
            if (seriesResult.Credits?.Cast != null)
            {
                foreach (var actor in seriesResult.Credits.Cast.OrderBy(a => a.Order).Take(this.config.MaxCastMembers))
                {
                    var personInfo = new PersonInfo
                    {
                        Name = actor.Name.Trim(),
                        Role = actor.Character,
                        Type = PersonType.Actor,
                        SortOrder = actor.Order,
                    };

                    if (!string.IsNullOrWhiteSpace(actor.ProfilePath))
                    {
                        personInfo.ImageUrl = this._tmdbApi.GetProfileUrl(actor.ProfilePath);
                    }

                    if (actor.Id > 0)
                    {
                        personInfo.SetProviderId(MetadataProvider.Tmdb, actor.Id.ToString(CultureInfo.InvariantCulture));
                    }


                    yield return personInfo;
                }
            }

            // 导演
            if (seriesResult.Credits?.Crew != null)
            {
                var keepTypes = new[]
                {
                    PersonType.Director,
                    PersonType.Writer,
                    PersonType.Producer
                };

                foreach (var person in seriesResult.Credits.Crew)
                {
                    // Normalize this
                    var type = MapCrewToPersonType(person);

                    if (!keepTypes.Contains(type, StringComparer.OrdinalIgnoreCase)
                        && !keepTypes.Contains(person.Job ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var personInfo = new PersonInfo
                    {
                        Name = person.Name.Trim(),
                        Role = person.Job,
                        Type = type
                    };

                    if (!string.IsNullOrWhiteSpace(person.ProfilePath))
                    {
                        personInfo.ImageUrl = this._tmdbApi.GetPosterUrl(person.ProfilePath);
                    }

                    if (person.Id > 0)
                    {
                        personInfo.SetProviderId(MetadataProvider.Tmdb, person.Id.ToString(CultureInfo.InvariantCulture));
                    }

                    yield return personInfo;
                }
            }

        }


        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            this.Log("GetImageResponse url: {0}", url);
            return this._httpClientFactory.CreateClient().GetAsync(new Uri(url), cancellationToken);
        }

        private void Log(string? message, params object?[] args)
        {
            this._logger.LogInformation($"[MetaShark] {message}", args);
        }
    }
}
