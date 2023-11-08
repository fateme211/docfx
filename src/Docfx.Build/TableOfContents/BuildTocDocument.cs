﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Composition;
using System.Web;
using Docfx.Build.Common;
using Docfx.Common;
using Docfx.DataContracts.Common;
using Docfx.Plugins;

namespace Docfx.Build.TableOfContents;

[Export(nameof(TocDocumentProcessor), typeof(IDocumentBuildStep))]
class BuildTocDocument : BaseDocumentBuildStep
{
    public override string Name => nameof(BuildTocDocument);

    public override int BuildOrder => 0;

    /// <summary>
    /// 1. Expand the TOC reference
    /// 2. Resolve homepage
    /// </summary>
    public override IEnumerable<FileModel> Prebuild(ImmutableList<FileModel> models, IHostService host)
    {
        var (resolvedTocModels, includedTocs) = TocHelper.ResolveToc(models, host);

        ReportPreBuildDependency(resolvedTocModels, host, 8, includedTocs);

        return resolvedTocModels;
    }

    public override void Build(FileModel model, IHostService host)
    {
        var toc = (TocItemViewModel)model.Content;
        TocRestructureUtility.Restructure(toc, host.TableOfContentRestructions);
        BuildCore(toc, model, host);
        // todo : metadata.
    }

    private static void BuildCore(TocItemViewModel item, FileModel model, IHostService hostService, string includedFrom = null)
    {
        if (item == null)
        {
            return;
        }

        var linkToUids = new HashSet<string>();
        var linkToFiles = new HashSet<string>();
        var uidLinkSources = new Dictionary<string, ImmutableList<LinkSourceInfo>>();
        var fileLinkSources = new Dictionary<string, ImmutableList<LinkSourceInfo>>();

        if (Utility.IsSupportedRelativeHref(item.Href))
        {
            UpdateDependencies(linkToFiles, fileLinkSources, item.Href);
        }
        if (Utility.IsSupportedRelativeHref(item.Homepage))
        {
            UpdateDependencies(linkToFiles, fileLinkSources, item.Homepage);
        }
        if (!string.IsNullOrEmpty(item.TopicUid))
        {
            UpdateDependencies(linkToUids, uidLinkSources, item.TopicUid);
        }

        model.LinkToUids = model.LinkToUids.Union(linkToUids);
        model.LinkToFiles = model.LinkToFiles.Union(linkToFiles);
        model.UidLinkSources = model.UidLinkSources.Merge(uidLinkSources);
        model.FileLinkSources = model.FileLinkSources.Merge(fileLinkSources);

        includedFrom = item.IncludedFrom ?? includedFrom;
        if (item.Items != null)
        {
            foreach (var i in item.Items)
            {
                BuildCore(i, model, hostService, includedFrom);
            }
        }

        void UpdateDependencies(HashSet<string> linkTos, Dictionary<string, ImmutableList<LinkSourceInfo>> linkSources, string link)
        {
            var path = HttpUtility.UrlDecode(UriUtility.GetPath(link));
            var anchor = UriUtility.GetFragment(link);
            linkTos.Add(path);
            AddOrUpdate(linkSources, path, GetLinkSourceInfo(path, anchor, model.File, includedFrom));
        }
    }

    private static string ParseFile(string link)
    {
        var queryIndex = link.IndexOfAny(new[] { '?', '#' });
        return queryIndex == -1 ? link : link.Remove(queryIndex);
    }

    private static void AddOrUpdate(Dictionary<string, ImmutableList<LinkSourceInfo>> dict, string path, LinkSourceInfo source)
        => dict[path] = dict.TryGetValue(path, out var sources) ? sources.Add(source) : ImmutableList.Create(source);

    private static LinkSourceInfo GetLinkSourceInfo(string path, string anchor, string source, string includedFrom)
    {
        return new LinkSourceInfo
        {
            SourceFile = includedFrom ?? source,
            Anchor = anchor,
            Target = path,
        };
    }

    private static void ReportPreBuildDependency(List<FileModel> models, IHostService host, int parallelism, HashSet<string> includedTocs)
    {
        var nearest = new ConcurrentDictionary<string, RelativeInfo>(FilePathComparer.OSPlatformSensitiveStringComparer);
        models.RunAll(model =>
        {
            var item = (TocItemViewModel)model.Content;
            UpdateNearestToc(host, item, model, nearest);
        },
        parallelism);

        // handle not-in-toc items
        UpdateNearestTocForNotInTocItem(models, host, nearest, parallelism);
    }

    private static void UpdateNearestToc(IHostService host, TocItemViewModel item, FileModel toc, ConcurrentDictionary<string, RelativeInfo> nearest)
    {
        var tocHref = item.TocHref;
        var type = Utility.GetHrefType(tocHref);
        if (type == HrefType.MarkdownTocFile || type == HrefType.YamlTocFile)
        {
            UpdateNearestTocCore(host, UriUtility.GetPath(tocHref), toc, nearest);
        }
        else if (item.TopicUid == null && Utility.IsSupportedRelativeHref(item.Href))
        {
            UpdateNearestTocCore(host, UriUtility.GetPath(item.Href), toc, nearest);
        }

        if (item.Items != null && item.Items.Count > 0)
        {
            foreach (var i in item.Items)
            {
                UpdateNearestToc(host, i, toc, nearest);
            }
        }
    }

    private static void UpdateNearestTocForNotInTocItem(List<FileModel> models, IHostService host, ConcurrentDictionary<string, RelativeInfo> nearest, int parallelism)
    {
        var allSourceFiles = host.SourceFiles;
        var tocInfos = models.Select(m => new TocInfo(m)).ToArray();
        Parallel.ForEach(
            EnumerateNotInTocArticles(),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            item =>
            {
                var near = (from tocInfo in tocInfos
                            let rel = new RelativeInfo(tocInfo, allSourceFiles[item])
                            where rel.TocPathRelativeToArticle.SubdirectoryCount == 0
                            orderby rel.TocPathRelativeToArticle.ParentDirectoryCount
                            select rel).FirstOrDefault();
                if (near != null)
                {
                    nearest[item] = near;
                }
            });

        IEnumerable<string> EnumerateNotInTocArticles()
        {
            return from pair in allSourceFiles
                   where pair.Value.Type == DocumentType.Article && !nearest.ContainsKey(pair.Key)
                   select pair.Key;
        }
    }

    private static void UpdateNearestTocCore(IHostService host, string item, FileModel toc, ConcurrentDictionary<string, RelativeInfo> nearest)
    {
        if (!host.SourceFiles.TryGetValue(item, out var itemSource))
        {
            return;
        }

        var tocInfo = new RelativeInfo(toc, itemSource);
        nearest.AddOrUpdate(
            item,
            k => tocInfo,
            (k, v) => Compare(tocInfo, v) < 0 ? tocInfo : v);
    }

    private static RelativePath GetOutputPath(FileAndType file)
    {
        if (file.SourceDir != file.DestinationDir)
        {
            return (RelativePath)file.DestinationDir + (((RelativePath)file.File) - (RelativePath)file.SourceDir);
        }
        else
        {
            return (RelativePath)file.File;
        }
    }

    private static int Compare(RelativeInfo infoA, RelativeInfo infoB)
    {
        var relativePathA = infoA.TocPathRelativeToArticle;
        var relativePathB = infoB.TocPathRelativeToArticle;

        int subDirCompareResult = relativePathA.SubdirectoryCount - relativePathB.SubdirectoryCount;
        if (subDirCompareResult != 0)
        {
            return subDirCompareResult;
        }

        int parentDirCompareResult = relativePathA.ParentDirectoryCount - relativePathB.ParentDirectoryCount;
        if (parentDirCompareResult != 0)
        {
            return parentDirCompareResult;
        }

        var tocA = infoA.TocInfo;
        var tocB = infoB.TocInfo;

        var outputPathCompareResult = StringComparer.OrdinalIgnoreCase.Compare(tocA.OutputPath, tocB.OutputPath);
        if (outputPathCompareResult != 0)
        {
            return outputPathCompareResult;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(tocA.FilePath, tocB.FilePath);
    }

    private class TocInfo
    {
        public FileModel Model { get; set; }

        public string FilePath { get; set; }

        public RelativePath OutputPath { get; set; }

        public TocInfo(FileModel tocModel)
        {
            Model = tocModel;
            FilePath = tocModel.FileAndType.File;
            OutputPath = GetOutputPath(tocModel.FileAndType);
        }
    }

    private class RelativeInfo
    {
        public TocInfo TocInfo { get; set; }

        public RelativePath TocPathRelativeToArticle { get; set; }

        public RelativeInfo(TocInfo tocInfo, FileAndType article)
        {
            TocInfo = tocInfo;
            TocPathRelativeToArticle = tocInfo.OutputPath.RemoveWorkingFolder().MakeRelativeTo(GetOutputPath(article));
        }

        public RelativeInfo(FileModel tocModel, FileAndType article)
            : this(new TocInfo(tocModel), article) { }
    }
}
