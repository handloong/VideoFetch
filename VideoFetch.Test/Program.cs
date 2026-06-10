using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VideoFetch.Services;
using VideoFetch.Services.Sites;

// Quick integration test: verify all 3 sites' search works
var keyword = "teen";

async Task TestSearch(ISiteParser parser)
{
    try
    {
        var results = await parser.SearchAsync(keyword);
        Console.WriteLine($"[{parser.Site}] Found {results.Count} results");
        
        if (results.Count > 0)
        {
            foreach (var r in results.Take(3))
                Console.WriteLine($"    -> {r.Title?.Substring(0, Math.Min(50, r.Title?.Length ?? 0))} | {r.Url?.Substring(0, 60)} | {r.Duration}");
            
            if (results.Count > 3) 
                Console.WriteLine($"    ... +{results.Count - 3} more results");
                
            // Test: click (fetch info) on first result URL
            if (!string.IsNullOrEmpty(results[0].Url))
            {
                Console.WriteLine($"\n[{parser.Site}] Fetching video info from first result...");
                var info = await parser.FetchVideoInfoAsync(results[0].Url);
                if (info != null)
                {
                    Console.WriteLine($"    Title: {info.Title}");
                    Console.WriteLine($"    Duration: {info.Duration}");
                    Console.WriteLine($"    Author: {info.Author}");
                    Console.WriteLine($"    Qualities: {info.Qualities.Count}");
                    
                    // Test stream resolution
                    if (info.Qualities.Count > 0)
                    {
                        foreach (var q in info.Qualities)
                            Console.WriteLine($"      - {q.Label} => {q.Value}");
                    }
                }
                else
                    Console.WriteLine("    FAILED: could not fetch video info");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{parser.Site}] ERROR: {ex.Message}");
    }
}

Console.WriteLine($"=== Search Integration Test: \"{keyword}\" ===\n");

await Task.WhenAll(
    TestSearch(new PornHubParser()),
    TestSearch(new XVideosParser()),
    TestSearch(new XNxxParser())
);

Console.WriteLine("\nDone!");
