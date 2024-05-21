using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

Console.WriteLine("Web scrapper has been started...");

(string Tag, string UrlAttribute)[] elementsToDownload = { ("a", "href"), ("link", "href"), ("script", "src"), ("img", "src") };
CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(30));

string directoryToSave = "Page";
Directory.Delete(directoryToSave, true);
Directory.CreateDirectory(directoryToSave);

uint filesCount = 0;

Console.CancelKeyPress += (sender, eventArgs) =>
{
    cancellationTokenSource.Cancel();
};

await FetchAndSaveWebPage("http://books.toscrape.com/", 
    directoryToSave, 
    filePath => {
        Console.WriteLine($"File {filePath} has been saved");
        Interlocked.Increment(ref filesCount);
        Console.WriteLine($"Downloaded {filesCount} files");
    },
    cancellationTokenSource.Token);

async Task FetchAndSaveWebPage(string url, string directoryPath, Action<string> progressDisplay, CancellationToken cancellationToken)
{
    try
    {
        IConfiguration config = Configuration.Default.WithDefaultLoader();
        IBrowsingContext context = BrowsingContext.New(config);
        
        // I supposed, 2 options of task implementation: 1 - using tasks array and await completion all of them, 2 - using Parallel class
        
        // 1st option
        await Task.WhenAll(ProcessPageUsingTasks(context, new Url(url), directoryPath, progressDisplay, cancellationToken));

        // 2nd option
        //await ProcessPageUsingParallel(context, new Url(url), directoryPath, progressDisplay, cancellationToken);
        
        Console.WriteLine($"Web page content saved to {directoryPath} folder");
    }
    catch (Exception e)
    {
        Console.WriteLine($"Error: {e.Message}");
    }
}

/*
 * 1st implementation - using tasks collection.
 */
async Task<IEnumerable<Task>> ProcessPageUsingTasks(IBrowsingContext context, Uri pageUrl, string directoryPath, Action<string> progressDisplay, CancellationToken cancellationToken)
{
    Url docUrl = new Url(pageUrl.ToString());
    
    IDocument? document = await context.OpenAsync(docUrl, cancellationToken);
    if (document == null || !await SavePage(directoryPath, docUrl.Path, document.DocumentElement.OuterHtml, cancellationToken)) return [];

    List<Task> tasks = new(document.All.Length/2);
    
    foreach (IElement element in document.All)
    {
        if(cancellationToken.IsCancellationRequested) break;
        
        foreach (var downloadable in elementsToDownload)
        {
            string? resourceUrl = ExtractResourceUrl(element, downloadable.Tag, downloadable.UrlAttribute);
            if (!string.IsNullOrEmpty(resourceUrl))
            {
                var absoluteUrl = new Uri(pageUrl, resourceUrl);
                
                if (element is IHtmlAnchorElement)
                {
                    tasks.AddRange(await ProcessPageUsingTasks(context, absoluteUrl, Path.Combine(directoryPath, Path.GetDirectoryName(resourceUrl) ?? ""), progressDisplay, cancellationToken));
                }
                else
                {
                    tasks.Add(DownloadResource(absoluteUrl, 
                        Path.Combine(directoryPath, resourceUrl), 
                        progressDisplay, 
                        cancellationToken));
                }
            }
            
            if(cancellationToken.IsCancellationRequested) break;
        }
    }

    return tasks;
}

/*
 * 2nd implementation - using tasks class Parallel.
 */
async Task ProcessPageUsingParallel(IBrowsingContext context, Uri pageUrl, string directoryPath, Action<string> progressDisplay, CancellationToken cancellationToken)
{
    Url docUrl = new Url(pageUrl.ToString());
    
    IDocument? document = await context.OpenAsync(docUrl, cancellationToken);
    if (document == null || !await SavePage(directoryPath, docUrl.Path, document.DocumentElement.OuterHtml, cancellationToken)) return;

    await Parallel.ForEachAsync(document.All,
        new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 },
        async (element, token) =>
        {
            foreach (var downloadable in elementsToDownload)
            {
                string? resourceUrl = ExtractResourceUrl(element, downloadable.Tag, downloadable.UrlAttribute);
                if (!string.IsNullOrEmpty(resourceUrl))
                {
                    var absoluteUrl = new Uri(pageUrl, resourceUrl);

                    if (element is IHtmlAnchorElement)
                    {
                        await ProcessPageUsingParallel(context, absoluteUrl,
                            Path.Combine(directoryPath, Path.GetDirectoryName(resourceUrl) ?? ""), progressDisplay,
                            token);
                    }
                    else
                    {
                        await DownloadResource(absoluteUrl,
                            Path.Combine(directoryPath, resourceUrl),
                            progressDisplay,
                            token);
                    }
                }

                if (token.IsCancellationRequested) break;
            }
        });
}

async Task DownloadResource(Uri url, string filePath, Action<string> displayProgress, CancellationToken cancellationToken)
{
    try
    {
        using HttpClient client = new HttpClient();
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var directory = Path.GetDirectoryName(filePath);

        CreateDirectoryIfNotExist(directory);

        await File.WriteAllBytesAsync(filePath, content, cancellationToken);

        displayProgress(filePath);
    }
    catch (Exception e)
    {
        Console.WriteLine($"Error downloading {url}: {e.Message}");
    }
}

void CreateDirectoryIfNotExist(string? directoryPath)
{
    if(string.IsNullOrEmpty(directoryPath) 
       || string.IsNullOrWhiteSpace(directoryPath)
       || Directory.Exists(directoryPath)) return;
    
    string[] directories = directoryPath.Split("/");

    foreach (var directory in directories)
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}

string? ExtractResourceUrl(IElement element, string tagName, string attributeName)
{
    if (element.TagName.Equals(tagName, StringComparison.OrdinalIgnoreCase)
        && (!element.TagName.Equals("link", StringComparison.OrdinalIgnoreCase) || element.GetAttribute("rel")?.Equals("stylesheet", StringComparison.OrdinalIgnoreCase) == true))
    {
        string? attributeValue = element.GetAttribute(attributeName);
        if(!string.IsNullOrEmpty(attributeValue)
           && !string.IsNullOrWhiteSpace(attributeValue)
           && new Url(attributeValue).IsRelative)
            return attributeValue;
    }
        
    return null;
}

async Task<bool> SavePage(string directoryPath, string urlPath, string content, CancellationToken cancellationToken)
{
    string htmlFilePath = Path.Combine(directoryPath, !string.IsNullOrEmpty(urlPath) && !string.IsNullOrWhiteSpace(urlPath) ? Path.GetFileName(urlPath): "index.html");
    if (File.Exists(htmlFilePath)) return false;
    
    CreateDirectoryIfNotExist(directoryPath);
    await File.WriteAllTextAsync(htmlFilePath, content, cancellationToken);
    
    return true;
}