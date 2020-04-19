using MingweiSamuel.Camille;
using MingweiSamuel.Camille.Enums;
using MingweiSamuel.Camille.SummonerV4;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MatchHistory
{
    class Program
    {
        internal struct MatchStats
        {
            internal long GameId;
            internal bool IsScrub;
            internal double LaneDamageRatio;
            internal double AllyDamageRatio;
        }

        private RiotApi riotApi;

        public Program(string apiKey)
        {
            riotApi = RiotApi.NewInstance(apiKey);
        }

        public async Task<Summoner> GetSummoner(Region region, string summonerName, CancellationToken? cancellationToken = null)
        {
            return await riotApi.SummonerV4.GetBySummonerNameAsync(region, summonerName, cancellationToken);
        }

        public async Task Analyze(Region region, Summoner summoner, string[] roleFilter = null, string[] allyFilter = null)
        {
            var matchList = await riotApi.MatchV4.GetMatchlistAsync(region, summoner.AccountId, null, new int[] { (int)QueueType.RANKED_SOLO_5x5 }, null, null, (long)SeasonTimestamp.SEASON_2020);
            Console.WriteLine("Retrieved match list with {0} entries", matchList.TotalGames);
            var matchTasks = matchList.Matches.Select(
                    matchMetadata => riotApi.MatchV4.GetMatchAsync(Region.NA, matchMetadata.GameId)
                ).ToArray();
            // Wait for all task requests to complete asynchronously.
            var matches = await Task.WhenAll(matchTasks);

            List<MatchStats> statsList = new List<MatchStats>(matches.Length);

            for (var i = 0; i < matches.Count(); i++)
            {
                var match = matches[i];
                // Get this summoner's participant ID info.
                var participantId = match.ParticipantIdentities
                    .First(pi => summoner.Id.Equals(pi.Player.SummonerId));
                // Find the corresponding participant.
                var participant = match.Participants
                    .First(p => p.ParticipantId == participantId.ParticipantId);
                var teamId = participant.TeamId;
                var role = participant.Timeline.Role;

                if (match.GameDuration < 5 * 60)
                {
                    Console.WriteLine("Skipping remake {0} ({1})", match.GameId, ((Champion)participant.ChampionId).Name());
                    continue;
                }

                if (role == "NONE" || role == "DUO")
                {
                    Console.WriteLine("Encountered ambiguous role {0} in game {1}", role, match.GameId);
                }

                if (roleFilter != null && !roleFilter.Contains(role))
                {
                    Console.WriteLine("Skipping game {0} with role {1} ({2})", match.GameId, role, ((Champion)participant.ChampionId).Name());
                    continue;
                }

                var opposingLaner = match.Participants.FirstOrDefault(p => p.TeamId != teamId && p.Timeline.Role == role);
                if (opposingLaner == null)
                {
                    Console.WriteLine("Skipping game {0} with role {1} ({2}) failed to find opposing player", match.GameId, role, ((Champion)participant.ChampionId).Name());
                    continue;
                }

                var win = participant.Stats.Win;
                var dmg = participant.Stats.TotalDamageDealtToChampions;
                var opposingDmg = opposingLaner.Stats.TotalDamageDealtToChampions;
                long allyDmg = 0;
                long enemyDmg = 0;

                foreach (var player in match.Participants)
                {
                    if (player == participant || player == opposingLaner) continue;
                    if (allyFilter != null && !allyFilter.Contains(player.Timeline.Lane)) continue;

                    if (player.TeamId == teamId)
                    {
                        allyDmg += player.Stats.TotalDamageDealtToChampions;
                    }
                    else
                    {
                        enemyDmg += player.Stats.TotalDamageDealtToChampions;
                    }
                }

                MatchStats stats = new MatchStats();
                stats.GameId = match.GameId;
                stats.IsScrub = !participant.Stats.Win;
                stats.LaneDamageRatio = dmg / (double)opposingDmg;
                stats.AllyDamageRatio = allyDmg / (double)enemyDmg;
                statsList.Add(stats);
            }

            long gamesCucked = 0;
            long wins = 0;
            long throws = 0;
            foreach (MatchStats stats in statsList)
            {
                Console.WriteLine("Game {2} lane ratio: {0:0.00} ally ratio: {1:0.00}", stats.LaneDamageRatio, stats.AllyDamageRatio, stats.GameId);
                if (stats.LaneDamageRatio > stats.AllyDamageRatio)
                {
                    gamesCucked++;
                }

                if (!stats.IsScrub)
                {
                    wins++;
                } else
                {
                    throws++;
                }
            }

            Console.WriteLine("{0} of {1} ({2:0.00}%) had higher damage ratio than allies", gamesCucked, statsList.Count, 100 * gamesCucked / (double)statsList.Count);
            Console.WriteLine("{0}W {1}L ({2:0.00}%) won", wins, throws, 100 * wins / (double)statsList.Count);
            Console.WriteLine("Player is boosted: {0}", throws != 0);
        }

        private static async Task MainAsync(string[] args)
        {
            string apiKey = File.ReadAllText("apikey.txt").Trim();

            Program p = new Program(apiKey);
            Summoner target = await p.GetSummoner(Region.NA, "WHATSHENANIGANS");
            Console.WriteLine("Resolved {0}", target.Name);
            await p.Analyze(Region.NA, target, new string[] {"DUO_CARRY"}, new string[] { "TOP", "JUNGLE", "MIDDLE" });
        }

        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }
    }
}
