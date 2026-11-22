using MediaForge.Models;
using MediaForge.Services;

var failures = 0;
var episodeMatcher = new EpisodeIdentificationService();
var structureMatcher = new EpisodeStructureService();

void Check(string name, bool passed, string detail)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
    if (!passed) failures++;
}

var narutoEpisodes = Enumerable.Range(1, 100)
    .Select(number => new TmdbEpisode { SeasonNumber = 2, EpisodeNumber = number, Name = $"Naruto episode {number}", RuntimeMinutes = 22 })
    .ToList();
var naruto83 = new MediaJob { SourceName = "Naruto S02E083 - Naruto episode 83.mkv", SourcePath = @"D:\DVD\Naruto HD Set Disc 1\A1_t00.mkv" };
var exact83 = episodeMatcher.Match(naruto83, 2, narutoEpisodes, "Naruto");
Check("Naruto explicit S02E83", exact83.SuggestedEpisode?.EpisodeNumber == 83, $"suggested {exact83.SuggestedEpisode?.Display ?? "none"}");

var naruto89 = new MediaJob { SourceName = "Naruto S02E089.mkv", SourcePath = @"D:\DVD\Naruto HD Set Disc 1\A1_t06.mkv" };
var exact89 = episodeMatcher.Match(naruto89, 2, narutoEpisodes, "Naruto");
Check("Naruto explicit S02E89", exact89.SuggestedEpisode?.EpisodeNumber == 89, $"suggested {exact89.SuggestedEpisode?.Display ?? "none"}");

var mislabeledNaruto = new MediaJob
{
    SourceName = "A1_t00.mkv",
    SourcePath = @"D:\DVD\Naruto HD Set Disc 1\A1_t00.mkv",
    EpisodeTitle = "Naruto episode 1"
};
var noGuess = episodeMatcher.Match(mislabeledNaruto, 2, narutoEpisodes, "Naruto");
Check("Naruto disc label does not imply episodes 1-7", noGuess.SuggestedEpisode == null, $"suggested {noGuess.SuggestedEpisode?.Display ?? "none"}");

var cardMatcher = new EpisodeTitleCardMatcher();
var cardEpisodes = new[]
{
    new TmdbEpisode { SeasonNumber=3, EpisodeNumber=83, Name="A Fight of the Rock Lee Style!" },
    new TmdbEpisode { SeasonNumber=3, EpisodeNumber=84, Name="Roar, Chidori! Brother vs. Brother!" },
    new TmdbEpisode { SeasonNumber=3, EpisodeNumber=85, Name="Hate Among the Uchihas: The Last of the Clan" }
};
var titleCardMatch = cardMatcher.Match("ROAR CHIDORI BROTHER VS BROTHER", cardEpisodes);
Check("Title-card OCR identifies a distinctive Naruto episode title", titleCardMatch?.Episode.EpisodeNumber == 84, $"matched {titleCardMatch?.Episode.Display ?? "none"}");
Check("Uninformative title-card OCR is left unresolved", cardMatcher.Match("NARUTO", cardEpisodes) == null, "one generic word cannot assign an episode");
var plannedCardTimes = TitleCardOcrService.PlanSampleTimes(
    new[] { 600d, 600d, 600d, 600d, 600d },
    new[] { new ChapterRangeSuggestion(1, 1, 3), new ChapterRangeSuggestion(2, 4, 5) });
Check("Title cards are sampled 45 seconds into each chapter segment",
    plannedCardTimes.SequenceEqual(new[] { 45d, 1845d }),
    $"planned {string.Join(", ", plannedCardTimes)} seconds");

var pawEpisodes = new[]
{
    new TmdbEpisode { SeasonNumber=11, EpisodeNumber=5, Name="Pups Get Your Goat", RuntimeMinutes=11 },
    new TmdbEpisode { SeasonNumber=11, EpisodeNumber=6, Name="Pups Save the Crop Circles", RuntimeMinutes=11 },
    new TmdbEpisode { SeasonNumber=11, EpisodeNumber=13, Name="Pups Save the Mountain Surfers", RuntimeMinutes=11 },
    new TmdbEpisode { SeasonNumber=11, EpisodeNumber=14, Name="Pups Save Humdinger's Home Away from Home", RuntimeMinutes=11 },
    new TmdbEpisode { SeasonNumber=11, EpisodeNumber=28, Name="Air Rescue: Pups Save the Airport Opening", RuntimeMinutes=22 },
    new TmdbEpisode { SeasonNumber=11, EpisodeNumber=29, Name="Air Rescue: Pups Stop a Rocket-Powered Pogo Stick", RuntimeMinutes=11 },
    new TmdbEpisode { SeasonNumber=11, EpisodeNumber=30, Name="Air Rescue: Pups Save the Sky-High Stunt Show", RuntimeMinutes=11 },
    new TmdbEpisode { SeasonNumber=11, EpisodeNumber=31, Name="Air Rescue: Pups vs. the Hum-Cloud", RuntimeMinutes=11 },
    new TmdbEpisode { SeasonNumber=11, EpisodeNumber=32, Name="Air Rescue: Pups Save a Hum-stronaut", RuntimeMinutes=22 },
    new TmdbEpisode { SeasonNumber=11, EpisodeNumber=33, Name="Air Rescue: Pups Stop the Mid-Air Magnet", RuntimeMinutes=11 },
};
var pawRip = new MediaJob { SourceName = "A1_t00.mkv", SourcePath = @"D:\DVD\Paw Patrol Air Rescue\A1_t00.mkv" };
var pawGuess = episodeMatcher.Match(pawRip, 11, pawEpisodes, "Paw Patrol");
Check("Paw Patrol opaque DVD title stays unresolved", pawGuess.SuggestedEpisode == null, $"suggested {pawGuess.SuggestedEpisode?.Display ?? "none"}; top score {pawGuess.Candidates.FirstOrDefault()?.Score ?? 0}");

var pawExpected = new (int Number, string Title)[]
{
    (28, "Airport Opening"),
    (32, "Hum-stronaut"),
    (29, "Rocket-Powered Pogo Stick"),
    (30, "Sky-High Stunt Show"),
    (31, "Hum-Cloud"),
    (33, "Mid-Air Magnet"),
    (13, "Mountain Surfers"),
    (14, "Humdinger's Home Away from Home"),
    (5, "Pups Get Your Goat"),
    (6, "Pups Save the Crop Circles"),
};
foreach (var (number, title) in pawExpected)
{
    var fileTitle = title.Replace("/", "-");
    var namedRip = new MediaJob
    {
        SourceName = $"A1_t00 - {fileTitle}.mkv",
        SourcePath = $"D:\\DVD\\Paw Patrol Air Rescue\\A1_t00 - {fileTitle}.mkv"
    };
    var result = episodeMatcher.Match(namedRip, 11, pawEpisodes, "Paw Patrol");
    Check($"Paw Patrol title maps to TMDB S11E{number:00}", result.SuggestedEpisode?.EpisodeNumber == number,
        $"suggested {result.SuggestedEpisode?.Display ?? "none"}; best {result.Candidates.FirstOrDefault()?.Episode.Display ?? "none"} ({result.Candidates.FirstOrDefault()?.Score ?? 0}%)");
}
var dvdMatcher = new DvdEpisodeGroupMatcher();
var pawDvdEpisodes = new[]
{
    new TmdbEpisodeGroupEpisodeDto { Id=11028, Name="Airport Opening", SeasonNumber=11, EpisodeNumber=28, RuntimeMinutes=22, Order=1 },
    new TmdbEpisodeGroupEpisodeDto { Id=11032, Name="Hum-stronaut", SeasonNumber=11, EpisodeNumber=32, RuntimeMinutes=22, Order=2 },
    new TmdbEpisodeGroupEpisodeDto { Id=11029, Name="Rocket-Powered Pogo Stick", SeasonNumber=11, EpisodeNumber=29, RuntimeMinutes=11, Order=3 },
    new TmdbEpisodeGroupEpisodeDto { Id=11030, Name="Sky-High Stunt Show", SeasonNumber=11, EpisodeNumber=30, RuntimeMinutes=11, Order=4 },
    new TmdbEpisodeGroupEpisodeDto { Id=11031, Name="Hum-Cloud", SeasonNumber=11, EpisodeNumber=31, RuntimeMinutes=11, Order=5 },
    new TmdbEpisodeGroupEpisodeDto { Id=11033, Name="Mid-Air Magnet", SeasonNumber=11, EpisodeNumber=33, RuntimeMinutes=11, Order=6 },
    new TmdbEpisodeGroupEpisodeDto { Id=11013, Name="Mountain Surfers", SeasonNumber=11, EpisodeNumber=13, RuntimeMinutes=11, Order=7 },
    new TmdbEpisodeGroupEpisodeDto { Id=11014, Name="Humdinger's Home Away from Home", SeasonNumber=11, EpisodeNumber=14, RuntimeMinutes=11, Order=8 },
    new TmdbEpisodeGroupEpisodeDto { Id=11005, Name="Get Your Goat", SeasonNumber=11, EpisodeNumber=5, RuntimeMinutes=11, Order=9 },
    new TmdbEpisodeGroupEpisodeDto { Id=11006, Name="Crop Circles", SeasonNumber=11, EpisodeNumber=6, RuntimeMinutes=11, Order=10 },
};
var pawGroupCatalog = new TmdbEpisodeGroupDetailsDto
{
    Id = "nick-dvds", Name = "Nickelodeon US DVDs", Type = 3,
    Groups = new List<TmdbEpisodeGroupSetDto> { new() { Id="air-rescue", Name="Air Rescue", Episodes=pawDvdEpisodes.ToList() } }
};
var matchedPawGroup = dvdMatcher.FindDiscGroup("Paw Patrol Air Rescue", "Paw Patrol", new[] { pawGroupCatalog });
Check("Paw Patrol disc label selects DVD release group", matchedPawGroup?.Name == "Air Rescue", $"matched {matchedPawGroup?.Name ?? "none"}");
Check("Paw Patrol DVD release season overrides stale season 13", dvdMatcher.GetUniqueSeason(matchedPawGroup) == 11, $"disc group season {dvdMatcher.GetUniqueSeason(matchedPawGroup)?.ToString() ?? "unknown"}");
var pawDiscJobs = Enumerable.Range(0, 6).Select(index => new MediaJob
{
    Id = 100 + index,
    SourceName = $"A1_t{index:00}.mkv",
    SourcePath = $"D:\\DVD\\Paw Patrol Air Rescue\\A1_t{index:00}.mkv",
    SourceDurationSeconds = 22 * 60,
    SourceKind = MediaSourceKind.Dvd
}).ToList();
var pawOrderAssignments = matchedPawGroup == null ? new Dictionary<long, IReadOnlyList<TmdbEpisode>>() : dvdMatcher.MatchTitles(pawDiscJobs, matchedPawGroup);
var pawOrderExpected = new Dictionary<int, int[]> { [0]=new[]{28}, [1]=new[]{32}, [2]=new[]{29,30}, [3]=new[]{31,33}, [4]=new[]{13,14}, [5]=new[]{5,6} };
var pawOrderPassed = pawOrderAssignments.Count == 6 && pawDiscJobs.Select((job, index) => pawOrderAssignments.TryGetValue(job.Id, out var assigned) && assigned.Select(x => x.EpisodeNumber).SequenceEqual(pawOrderExpected[index])).All(x => x);
Check("Paw Patrol DVD tracks map to aired TMDB episodes", pawOrderPassed, pawOrderPassed ? "t00–t05 mapped to the expected 1/1/2/2/2/2 episode sequence" : "DVD track order/runtime alignment failed");

var narutoDvdGroup = new TmdbEpisodeGroupSetDto
{
    Id="naruto-disc-1", Name="Naruto HD Set Disc 1", Episodes=Enumerable.Range(83, 7)
        .Select((number, index) => new TmdbEpisodeGroupEpisodeDto { Id=2000+number, Name=$"Naruto episode {number}", SeasonNumber=2, EpisodeNumber=number, RuntimeMinutes=22, Order=index+1 }).ToList()
};
var chapterSignature = new[] { 90d, 300d, 300d, 300d, 300d };
var narutoDisc = new MediaJob
{
    Id=200, SourceName="A1_t00.mkv", SourcePath=@"D:\DVD\Naruto HD Set Disc 1\A1_t00.mkv",
    SourceDurationSeconds=(long)(chapterSignature.Sum()*7), SourceChapterCount=chapterSignature.Length*7,
    ChapterDurationsSeconds=Enumerable.Range(0,7).SelectMany(_=>chapterSignature).ToList(), SourceKind=MediaSourceKind.BluRay
};
var matchedNarutoGroup = dvdMatcher.FindDiscGroup("Naruto HD Set Disc 1", "Naruto", new[] { new TmdbEpisodeGroupDetailsDto { Type=3, Groups=new(){narutoDvdGroup} } });
var narutoAssignments = matchedNarutoGroup == null ? new Dictionary<long, IReadOnlyList<TmdbEpisode>>() : dvdMatcher.MatchTitles(new[]{narutoDisc}, matchedNarutoGroup);
var narutoSegments = narutoAssignments.TryGetValue(narutoDisc.Id, out var assignedNaruto) ? structureMatcher.Analyze(narutoDisc, assignedNaruto) : null;
var narutoSet4Label = new MediaJob { SourceName="Naruto HD Set Disc 4.mkv", SourcePath=@"D:\\DVD\\Rips\\Naruto HD Set Disc 4.mkv" };
Check("Naruto release number is read from episode filename", EpisodeIdentificationService.GetDiscNumber(narutoSet4Label) == 4, $"release number {EpisodeIdentificationService.GetDiscNumber(narutoSet4Label)?.ToString() ?? "unknown"}");
var narutoSet4Episodes = Enumerable.Range(83, 28).Select((number, index) => new TmdbEpisodeGroupEpisodeDto { Id=3000+number, Name=$"Naruto episode {number}", SeasonNumber=number <= 83 ? 2 : 3, EpisodeNumber=number, RuntimeMinutes=22, Order=index+1 }).ToList();
var narutoSet4Group = new TmdbEpisodeGroupSetDto { Id="naruto-set-4", Name="Naruto Set 4", Episodes=narutoSet4Episodes };
var matchedSet4 = dvdMatcher.FindDiscGroup(EpisodeIdentificationService.GetDiscLabel(narutoSet4Label), "Naruto", new[] { new TmdbEpisodeGroupDetailsDto { Type=3, Groups=new(){narutoSet4Group} } });
Check("Naruto HD Set Disc 4 label matches online Set 4 catalog", matchedSet4?.Id == "naruto-set-4", $"matched {matchedSet4?.Name ?? "none"}");
Check("Naruto Set 4 metadata narrows the episode range to 83–110", matchedSet4?.Episodes.Select(x => x.EpisodeNumber).SequenceEqual(Enumerable.Range(83, 28)) == true, $"episodes {matchedSet4?.Episodes.FirstOrDefault()?.EpisodeNumber}-{matchedSet4?.Episodes.LastOrDefault()?.EpisodeNumber}; count {matchedSet4?.Episodes.Count ?? 0}");
Check("Naruto disc metadata maps the 83–89 compilation", narutoSegments?.IsMultiEpisode == true && assignedNaruto!.Select(x=>x.EpisodeNumber).SequenceEqual(Enumerable.Range(83,7)), $"mapped {string.Join(",", assignedNaruto?.Select(x=>x.EpisodeNumber) ?? Array.Empty<int>())}");
Check("Naruto DVD mapping retains chapter boundaries", narutoSegments?.ChapterRanges.Select(x=>$"{x.Start}-{x.End}").SequenceEqual(new[]{"1-5","6-10","11-15","16-20","21-25","26-30","31-35"}) == true, $"chapters [{string.Join(", ", narutoSegments?.ChapterRanges.Select(x=>$"{x.Start}-{x.End}") ?? Array.Empty<string>())}]");
var sevenEpisodes = Enumerable.Range(83, 7)
    .Select(number => new TmdbEpisode { SeasonNumber = 2, EpisodeNumber = number, Name = $"Naruto episode {number}", RuntimeMinutes = 22 })
    .ToList();

var compilation = new MediaJob
{
    SourceName = "A1_t00.mkv",
    SourcePath = @"D:\DVD\Naruto HD Set Disc 1\A1_t00.mkv",
    SourceDurationSeconds = (long)(chapterSignature.Sum() * 7),
    SourceChapterCount = chapterSignature.Length * 7,
    ChapterDurationsSeconds = Enumerable.Range(0, 7).SelectMany(_ => chapterSignature).ToList(),
    SourceKind = MediaSourceKind.BluRay
};
var structure = structureMatcher.Analyze(compilation, sevenEpisodes);
var boundaries = structure.ChapterRanges.Select(x => $"{x.Start}-{x.End}").ToArray();
Check("Naruto seven-part runtime/chapter segmentation", structure.IsMultiEpisode && structure.EstimatedEpisodeCount == 7 && boundaries.SequenceEqual(new[] { "1-5", "6-10", "11-15", "16-20", "21-25", "26-30", "31-35" }), $"count {structure.EstimatedEpisodeCount}; chapters [{string.Join(", ", boundaries)}]");

Console.WriteLine(failures == 0 ? "All episode-identification checks passed." : $"{failures} episode-identification check(s) failed.");
return failures == 0 ? 0 : 1;





