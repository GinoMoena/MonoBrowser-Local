////////////////////////////////////////////////////////////////
// MonoBrowser Core
////////////////////////////////////////////////////////////////
// Author: Yury Romanov
//
// https://monobrowser.org
//
// https://github.com/romanov/monobrowser
//
// (c) 2024
////////////////////////////////////////////////////////////////

module Book

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open AngleSharp.Html.Parser
open BasicData
open Markdig



let private pages = ConcurrentDictionary<string, RenderElement>()

let private convertMarkdownToRender(text:string, isHtml:bool) =

    let markdownToHtml =
        if isHtml then text
        else Markdown.ToHtml(text)

    let doc = HtmlParser().ParseDocument(markdownToHtml)

    let nodes =
            Builder.CreateElement (doc.Body, [| "default"; "header1"; "header2" |])
            |> Builder.AddMarginNodes
            |> Builder.AddTextNodes None
            |> Builder.AddSize

    // TODO functional chain
    Builder.RefreshPosition nodes None
    nodes


/// A page fetched off the game thread, ready to be laid out (font-measured) on it.
/// Either it carries the raw text still to be built, or an already-laid-out page from the cache.
type FetchedPage =
    { Text: string                  // raw markdown/html; ignored when Cached is Some
      IsHtml: bool
      BasePath: string option       // image base dir, applied just before layout
      CacheKey: string option       // Some -> store the built page in the cache; None -> never cache
      Cached: RenderElement option } // Some -> the cache already held a laid-out page


/// Background-safe: performs the slow file/network read and a cache lookup only. Does NOT
/// touch any DynamicSpriteFont (no MeasureString), so it is safe to run off the game thread.
let Fetch(url:string, isLocal:bool, isHtml:bool, forceRefresh:bool) : FetchedPage =

    let name = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(url))

    if isLocal then
        // local files are intentionally not cached, so edits on disk show up on reload
        let path = Path.Combine(url)
        { Text = File.ReadAllText(path)
          IsHtml = true
          BasePath = Some(Path.GetDirectoryName(path))
          CacheKey = None
          Cached = None }

    elif not forceRefresh && pages.ContainsKey(name) then
        { Text = ""; IsHtml = isHtml; BasePath = None
          CacheKey = Some name; Cached = Some pages[name] }

    else
        // cache miss (or forced refresh): drop any stale copy and do the slow read now
        if forceRefresh then pages.TryRemove(name) |> ignore

        let text, basePath =
            match url with
            | path when path.Contains("content://") ->
                let full = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Content", path.Replace("content://", ""))
                // base path lets images in the markdown load from the same directory as the file
                File.ReadAllText(full), Some(Path.GetDirectoryName(full))
            | _ ->
                use client = new HttpClient()
                client.GetStringAsync(url).Result, None

        { Text = text; IsHtml = isHtml; BasePath = basePath
          CacheKey = Some name; Cached = None }


/// Background-safe: a page whose text is already in hand (no I/O, never cached).
let FetchString(text:string) : FetchedPage =
    { Text = text; IsHtml = false; BasePath = None; CacheKey = None; Cached = None }


/// Game-thread only: lays the page out, which measures glyphs through DynamicSpriteFont.
/// Because that font cache is not thread-safe, this MUST run on the same thread that draws.
let Build(fetched:FetchedPage) : RenderElement =
    match fetched.Cached with
    | Some data -> data
    | None ->
        fetched.BasePath |> Option.iter ImageLoader.SetBasePath
        let data = convertMarkdownToRender(fetched.Text, fetched.IsHtml)
        fetched.CacheKey |> Option.iter (fun key -> pages.TryAdd(key, data) |> ignore)
        data
