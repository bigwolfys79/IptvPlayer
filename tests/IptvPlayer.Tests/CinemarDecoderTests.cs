using System.Collections.Generic;
using IptvPlayer.Services;

namespace IptvPlayer.Tests;

public class CinemarDecoderTests
{
    // Real "#2" payload from a live embed page (8 voiceover tracks),
    // captured 2026-09-25
    private const string Encoded = "#236GV3T2szMUbb2e416d5W3siaWQiOiJiNTY4Y2JjZjU1MTYyNjRmZmU5ZDQ0MzNhZjNjNzJiNiIsInRpdGxlIjoiXHUwNDE0XHUwNDQzXHUwNDMxXHUwNDNiXHUwNDRmXHUwNDM2IChIRHJlemthIFN0dWRpbykiLCJ0aXRsZTIiOiIiLCJkYXRhIjoidjZ3UjlnM2VIek5qdGtPa0MxZlBiOVNPSzhjODZTa0JXNXBoMUNsdDdUT1F3WDZBWkx0c2IweURJSmR1YnE1WTNKb293enptTHdkVGdIdVhibWJcL1c0NmJKTUUxNzMxUVZJVjJrRzQwK2x6Y2pqM1VhXC93bEVRdmFNSXBtWkxwWG5ZQXpoU1wva2NVWVAybStHYjNYMVdZZWZLZG92ckQwSlVvWjdsQ2QxdTAyRm5UM1VlXC93bEFsS0RkWk1uZGFGTmhmZE0yaSt4UFFsQnpDYldaRFNyQVoyQU01OHY1RDBDVkk1dGxUbG40VnFPZ2lUSExcL0k5VUVHTWNwTXlaXC94YmlwOG14UT09IiwiZmlsZSI6IiMifSx7ImlkIjoiNTJlMTkxZGY1MmU1YTdlMzRlMjg3Mzk1NGVjNWRkYWQiLCJ0aXRsZSI6Ilx1MDQxNFx1MDQ0M1x1MDQzMVx1MDQzYlx1MDQ0Zlx1MDQzNiAoVmlkZW9maWxtIEludC4pIiwidGl0bGUyIjoiIiwiZGF0YSI6IkI1YnhnciswM3JZUGxwR0pRbjg3aDJ5MHk3T09nK2lFTjdxeitXQkZHZHNvKzU3MDF0R3Q2aUN2b0x4eUhsaTNOXC9MRXNvclc3WVU3clwvU3djVVlEdG1Ha3hyZlpqZW5UTjZYMXVuTWJYYkl3dE4yZzJaYmtsR2Y2NHFjdlRFNlwvSmJyVDhaMk9cL01SNjViM3NMQmdYOG16azA2NmQwUHlNT2E2anNXNWRTYVU5cDhHNmo1ajh3aTJzb0tWZ0NSbTlOcWZFdElpWVwvTmd0ck1yVWJsMVVwVDIwaStmTjI3M1NZYlM5cXl0ZEFhVTJvY21zam9idW1EcW52N3h6WFJlbFpMVExzNGlON29VN282SytjUT09IiwiZmlsZSI6IiMifSx7ImlkIjoiZTdhNDkxYjg4MjNhOWYyNTI0MDkxZmNiZmU5MjM3ZTQiLCJ0aXRsZSI6IkhEcmV6a2EgU3R1ZGlvIChcdTA0MWZcdTA0NDBcdTA0M2VcdTA0NDQuIFx1MDQzY1x1MDQzZFx1MDQzZVx1MDQzM1x1MDQzZVx1MDQzM1x1MDQzZVx1MDQzYlx1MDQzZVx1MDQ0MVx1MDQ0Ylx1MDQzOSkiLCJ0aXRsZTIiOiIiLCJkYXRhIjoiMFFpXC9lc2ZiU1h2bnJnREZMV0lsRnJvcWhVdjI3SDlKMzRJaXRROVlCMHIrWmRBTXJyNDZKOGlhTlB3YVVSRWs2VzJKR2ZDNExVM1J6RFR6R2dOREllRnNqRU9pN0g5UDBjc3hveGxSRkhEaktwTllvZmx6V1lcL0NjK3RBVVZBdTh5U2RDZVhoSnc2THdpem5TVUFmSU9rN2gxYmxxV3RCMXA0NDlRRkFVVFRyT1pOWXNmbHpTdGFiTnZJQlFFczA2MVBpVnVXMGEwSEYxR1czUWdGQmVQTWtuUlBsNFd0SzBKWXU5QjlTQ3lQZ0pvcEw1ZmRyR01XVU1mSVVVaFlpNUR1SVNRPT0iLCJmaWxlIjoiIyJ9LHsiaWQiOiIyY2FlNTNlNDQ1YmY3ZmZkYTJkNGFkMjNhZTg1NWE1NSIsInRpdGxlIjoiSERyZXprYSBTdHVkaW8gMTgrIChcdTA0MWZcdTA0NDBcdTA0M2VcdTA0NDQuIFx1MDQzY1x1MDQzZFx1MDQzZVx1MDQzM1x1MDQzZVx1MDQzM1x1MDQzZVx1MDQzYlx1MDQzZVx1MDQ0MVx1MDQ0Ylx1MDQzOSkiLCJ0aXRsZTIiOiIiLCJkYXRhIjoiTldjcTBiM2xUZmROR1diMlUzenlyMTVGRU9DTTBudkZkVFZFaG5GRzBQTWFDa1duMUlBK3EySjRCY2MySFpiTERWVkp0OWlCZGNSOGZGV1NNaDNMbWdkV1R1WGVobjdFZW44RHoyUWR5NXdFUlFiejI4ZDMxU1YxRmRnK1Q0ZVhGMHNJb3BcL2ZJNEloZFVyVU4xN0ltUTFVRXYyZmwyXC9OZkNsZXhuOWVobzBQVmdienk4ZDN4bndzVU1GXC9YcHlORHp4M1wvWitLYjgxdll3T0VQQitXd1JkTENMaWYzMlwvR2VpRkl4MkZNM0pvRVNSXC9nbjhsdmxHOGpWOEZxVE1HYkFGUWQ0Zz09IiwiZmlsZSI6IiMifSx7ImlkIjoiM2JjYWI0MTA2N2E0MWNmOWEyN2E3ODNjMmMwMDZjZjIiLCJ0aXRsZSI6IlRWU2hvd3MgKFx1MDQxZlx1MDQ0MFx1MDQzZVx1MDQ0NC4gXHUwNDNjXHUwNDNkXHUwNDNlXHUwNDMzXHUwNDNlXHUwNDMzXHUwNDNlXHUwNDNiXHUwNDNlXHUwNDQxXHUwNDRiXHUwNDM5KSIsInRpdGxlMiI6IiIsImRhdGEiOiJ4eGhyMDIzK2RPbWlqQUhsZm9LUEk2dzZVZUpjeVVMYm1xQWpsVnk0clhcL29kUVNsQkpzSHRZM3FZOUJMdHUwVW9ub1A1bHZLVGR6RHVURFdTN2VcL0ZcL01yQ2VkVXpVR4bbd2561e9$iMifV0bb2e41VuaDZ4T2lPa2Z4Qzl4T3k4cmdjc3NUc2ZvYjVUUkpvRVwvRVZwdlhcL3kyQUVPV3REK1Y4U2VsYnhrYlJqcTV6eDBTenZ4djNORW1uVDhSRnhZRDZJOTlQczdvVjhEUkp2VVwvRUw3U09ybTdIUktEMVJyVjNDTGNEM0ZqTHk2NDd4MCsxdHczMktsdjlXTTlhM0pPdUxjY2RvTFVTOENGYjRGbkxSOTZSIiwiZmlsZSI6IiMifSx7ImlkIjoiNWYwZDhiMTJjNGZhNjY4NmEwYThkZmQ0Y2RmM2EzMDEiLCJ0aXRsZSI6IkdvTFRGaWxtIChcdTA0M2NcdTA0M2RcdTA0M2VcdTA0MzNcdTA0M2VcdTA0MzNcdTA0M2VcdTA0M2JcdTA0M2VcdTA0NDFcdTA0NGJcdTA0MzkpIiwidGl0bGUyIjoiIiwiZGF0YSI6Ikh3bDMzcXdRQ1NtZDNyWWJibnpDWTNRclRlK2RKejhicGZLVWEweEc0RDh3WkJpb3hYVjZkYkxtZ0NoWVJQdFJLV29UN1pvZ2JCcjR1OVVxRDB1aFV5MDZUK3pKY3pGS3FyelZLRmtkOFZBcUsxdjh5akl6Q1wvV3l4VFVEVDdkYlBTVlZyWTRxWjF6eHNwbzVDbDc0VlNjNlFmS09ZaXNUck82T0swSmV0a0VsT0Z2ODJqSXpHS3pyZ0N4Q1hxeEJKVklxOG81XC9LeE9cL3BOTnBBUittRFQwbFZiZU9LaXNZcXVhWUtseE03Rll1SjBMdmpqd3JTclwva2h5eFhUUEZYS2pwQTdRPT0iLCJmaWxlIjoiIyJ9LHsiaWQiOiJkMjQ4YmJkYmY0YmRlYzM2NWIyYjFlMjBiMTAzOGQ3NiIsInRpdGxlIjoiPGltZyBzcmM9XCJcL2ZsYWdzXC91YS5wbmdcIiBhbHQ9XCJcIj4gXHUwNDIzXHUwNDNhXHUwNDQwXHUwNDMwXHUwNDM4XHUwNDNkXHUwNDQxXHUwNDNhXHUwNDM4XHUwNDM5IChcdTA0MzRcdTA0NDNcdTA0MzFcdTA0M2JcdTA0NGZcdTA0MzYpIiwidGl0bGUyIjoiIiwiZGF0YSI6InpuZWxlSUIyaWE0XC9QYnFhK0JmWVdxVlZuMG14UWIrY0J4R1k2dG90K2diaEdzb082UlA2OGhBRWk2XC9JZHJ0cVwvaE9RU0xVVXVwMExCTitqeXk3Z2E2aEZrazNtVDc3TEJ3N2VxY2x6dm1cLzVWWWxhNWxTempGZFJ5YmVPSnZVN1wvRm5JU1wvVk9xNElkVHBpZzJtV3RLZUlTeXgrc0ErTGNIUkdZXC90b3Q3bUw4VDRsYThsU3pudzhGaXJiYVlcL3BnXC8xdUhEcUpNdUo4S0M0MjIybm42WUpVcWlWcnZWTE9NUlZqSTladHp0bmppVmN4YXVsUzRtUWNUaTZqSU9lMXI0RUtVV3F4VTZvd0ZESTJqeUNUc2JcLzFBbGc9PSIsImZpbGUiOiIjIn0seyJpZCI6IjUwNTM5ODcxNGMyODIwMDcyNmQ3OGIxZjI4NGUwZjc4IiwidGl0bGUiOiI8aW1nIHNyYz1cIlwvZmxhZ3NcL3VzLnBuZ1wiIGFsdD1cIlwiPiBFbmdsaXNoIChPcmlnaW5hbCkiLCJ0aXRsZTIiOiIiLCJkYXRhIjoiUW14TUlHRGxhUEZ0XC9kdmN1U0tWaXlsT2RoRlIwbDdEVmRINXJKc1l0OWR0QVNOV0NZQWJyVUxFNnVtSlFcL2E3Y2doNUVGV0hXOEpaeEw3bGlodXR1aVJlZXhVRzNGK1VWYzZcLzc0aEc4NzUxVG1BQ0JzZFMwd1dScVBIUEU3anFjVUloRXhYZFN0MVBqdm5tbTFEZytHNEpJa2RNa0FPRFQ5SDV1SnNZbzdOd1ZHQUNFc2RTd0YzRjZcL0NiVnJleGMwQnVWa0xmV2NCWXkrendtMHkzc1JreFlBSVB4MUxURjVpcHM5cEcrNmx1VGlVQ1dzZFp4bFhUNnU2SkRLQzZiRmw5QWt6SEM5Tlh6T3psaVJHaHZuRmJmdz09IiwiZmlsZSI6Ibb21e46";

    [Fact]
    public void DecodePlaylist_PortsPlayerJsAlgorithm()
    {
        var tracks = CinemarDecoder.DecodePlaylist(Encoded);

        Assert.Equal(8, tracks.Count);
        Assert.All(tracks, t => Assert.False(string.IsNullOrWhiteSpace(t.Id)));
        Assert.All(tracks, t => Assert.False(string.IsNullOrWhiteSpace(t.Data)));
        Assert.Contains(tracks, t => t.Title.Contains("Дубляж"));
    }

    [Fact]
    public void DecodePlaylist_RejectsWrongPrefix()
    {
        Assert.Throws<ArgumentException>(() => CinemarDecoder.DecodePlaylist("#1abc"));
    }

    [Fact]
    public void FlattenLeaves_Film_ListsEveryVoiceover()
    {
        var nodes = CinemarDecoder.DecodePlaylist(Encoded);
        var leaves = CinemarDecoder.FlattenLeaves(nodes);

        Assert.Equal(8, leaves.Count);
        Assert.Contains(leaves, l => l.Label.Contains("Дубляж"));
    }

    [Fact]
    public void FlattenLeaves_Series_NestsSeasonEpisodeKeepsFirstVoiceover()
    {
        // Series shape: season → episode → voiceovers (data on the innermost)
        var nodes = new List<CinemarNode>
        {
            new()
            {
                Title = "Сезон 1",
                Folder =
                [
                    new CinemarNode
                    {
                        Title = "Серия 1",
                        Title2 = "1 сезон 1 серия",
                        Folder =
                        [
                            new CinemarNode { Title = "LostFilm", Title2 = "1 сезон 1 серия", Data = "payload-e1-lost" },
                            new CinemarNode { Title = "Кубик в Кубе", Title2 = "1 сезон 1 серия", Data = "payload-e1-kubik" }
                        ]
                    },
                    new CinemarNode
                    {
                        Title = "Серия 2",
                        Title2 = "1 сезон 2 серия",
                        Folder =
                        [
                            new CinemarNode { Title = "LostFilm", Title2 = "1 сезон 2 серия", Data = "payload-e2-lost" }
                        ]
                    }
                ]
            }
        };

        var leaves = CinemarDecoder.FlattenLeaves(nodes);

        Assert.Equal(2, leaves.Count);
        Assert.Contains("Сезон 1 · Серия 1 · LostFilm", leaves[0].Label);
        Assert.Contains("LostFilm", leaves[0].Label);
        Assert.Equal("payload-e1-lost", leaves[0].Data);
        Assert.Equal("payload-e2-lost", leaves[1].Data);
    }

    [Fact]
    public void ExtractEmbedUrls_SkipsAdsAndYoutube_PutsCinemarFirst()
    {
        var html = @"<iframe data-src=""https://cvt-s1.agl010.pro/iframe/1000/x""></iframe>
<iframe class=""lazy"" data-src=""https://cinemar.cc/embed/117628/+NTI""></iframe>
<iframe src=""https://www.youtube.com/embed/abc""></iframe>
<iframe data-src=""/uploads/mini/poster.webp""></iframe>";

        var urls = KinogoSite.ExtractEmbedUrls(html);

        var embed = Assert.Single(urls);
        Assert.Equal("https://cinemar.cc/embed/117628/+NTI", embed);
    }
}
