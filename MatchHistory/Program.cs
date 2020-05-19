using Microsoft.Data.Sqlite;
using MingweiSamuel.Camille;
using MingweiSamuel.Camille.Enums;
using MingweiSamuel.Camille.MatchV4;
using MingweiSamuel.Camille.SummonerV4;
using Newtonsoft.Json;
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
            internal double GoldAtTenDiff;
            internal double GoldEarlyMidGameDiff;
            internal double TotalGoldDiff;
            internal double AllyGoldAtTenDiff;
        }

        private RiotApi riotApi;
        private SqliteConnection db;

        public Program(string apiKey, SqliteConnection db)
        {
            this.riotApi = RiotApi.NewInstance(apiKey);
            this.db = db;
        }

        public async Task<Summoner> GetSummoner(Region region, string summonerName, CancellationToken? cancellationToken = null)
        {
            Summoner summoner = await riotApi.SummonerV4.GetBySummonerNameAsync(region, summonerName, cancellationToken);

            Console.WriteLine("Resolved {0}", summoner.Name);
            return summoner;
        }

        public async Task<List<MatchStats>> Analyze(Region region, Summoner target, Summoner[] alliesRequired = null, Summoner[] alliesExcluded = null, string[] roleFilter = null, string[] laneFilter = null, string[] allyFilter = null)
        {
            var matchList = await riotApi.MatchV4.GetMatchlistAsync(region, target.AccountId, null, new int[] { (int)QueueType.RANKED_SOLO_5x5 }, null, null, (long)SeasonTimestamp.SEASON_2020);
            Console.WriteLine("Retrieved match list with {0} entries", matchList.TotalGames);

            var matchTasks = matchList.Matches.Select(
                                async matchDesc =>
                                {
                                    var selectCmd = db.CreateCommand();
                                    selectCmd.CommandText = "select serialized from matches where id = $id";
                                    selectCmd.Parameters.AddWithValue("$id", matchDesc.GameId);

                                    using (var reader = await selectCmd.ExecuteReaderAsync())
                                    {
                                        if (await reader.ReadAsync() && reader.HasRows)
                                        {
                                            Console.WriteLine("Loaded game {0} from cache", matchDesc.GameId);
                                            return JsonConvert.DeserializeObject<Match>(reader.GetString(0));
                                        }
                                        else
                                        {
                                            var match = await riotApi.MatchV4.GetMatchAsync(Region.NA, matchDesc.GameId);
                                            var insertCmd = db.CreateCommand();
                                            insertCmd.CommandText = "insert into matches (id, serialized) values ($id, $serialized)";
                                            insertCmd.Parameters.AddWithValue("$id", matchDesc.GameId);
                                            insertCmd.Parameters.AddWithValue("$serialized", JsonConvert.SerializeObject(match));
                                            await insertCmd.ExecuteNonQueryAsync();
                                            Console.WriteLine("Cached game {0}", matchDesc.GameId);
                                            return match;
                                        }
                                    }
                                }).ToArray();
 
            // Wait for all task requests to complete asynchronously.
            var matches = await Task.WhenAll(matchTasks);

            List<MatchStats> statsList = new List<MatchStats>(matches.Length);

            for (var i = 0; i < matches.Count(); i++)
            {
                var match = matches[i];
                // Get this summoner's participant ID info.
                var participantId = match.ParticipantIdentities
                    .First(pi => target.Id.Equals(pi.Player.SummonerId));
                // Find the corresponding participant.
                var participant = match.Participants
                    .First(p => p.ParticipantId == participantId.ParticipantId);
                var teamId = participant.TeamId;
                var role = participant.Timeline.Role;
                var lane = participant.Timeline.Lane;

                bool missingRequired = false;
                foreach (Summoner required in alliesRequired)
                {
                    if (null == match.ParticipantIdentities.FirstOrDefault(pi => required.Id.Equals(pi.Player.SummonerId)))
                    {
                        Console.WriteLine("Skipping game without summoner {0} ({1})", match.GameId, required.Name);
                        missingRequired = true;
                        break;
                    }
                }
                if (missingRequired)
                {
                    continue;
                }

                bool hasExclude = false;
                foreach (Summoner exclude in alliesExcluded)
                {
                    if (null != match.ParticipantIdentities.FirstOrDefault(pi => exclude.Id.Equals(pi.Player.SummonerId)))
                    {
                        Console.WriteLine("Skipping game with summoner {0} ({1})", match.GameId, exclude.Name);
                        hasExclude = true;
                        break;
                    }
                }
                if (hasExclude)
                {
                    continue;
                }

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

                if (laneFilter != null && !laneFilter.Contains(lane))
                {
                    Console.WriteLine("Skipping game {0} with lane {1} ({2})", match.GameId, lane, ((Champion)participant.ChampionId).Name());
                    continue;
                }

                var opposingLaner = match.Participants.FirstOrDefault(p => p.TeamId != teamId && p.Timeline.Role == role && p.Timeline.Lane == lane);
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
                double allyGoldAtTenDiff = 0;
                long allyCountDelta = 0;

                foreach (var player in match.Participants)
                {
                    if (player == participant || player == opposingLaner) continue;
                    if (allyFilter != null && !allyFilter.Contains(player.Timeline.Lane)) continue;

                    if (player.TeamId == teamId)
                    {
                        allyDmg += player.Stats.TotalDamageDealtToChampions;
                        allyGoldAtTenDiff += player.Timeline.GoldPerMinDeltas[Deltas.D0_10];
                        allyCountDelta++;
                    }
                    else
                    {
                        enemyDmg += player.Stats.TotalDamageDealtToChampions;
                        allyGoldAtTenDiff -= player.Timeline.GoldPerMinDeltas[Deltas.D0_10];
                        allyCountDelta--;
                    }
                }

                MatchStats stats = new MatchStats();
                stats.GameId = match.GameId;
                stats.IsScrub = !participant.Stats.Win;
                stats.LaneDamageRatio = dmg / (double)opposingDmg;
                stats.AllyDamageRatio = allyDmg / (double)enemyDmg;
                stats.GoldAtTenDiff = participant.Timeline.GoldPerMinDeltas[Deltas.D0_10] - opposingLaner.Timeline.GoldPerMinDeltas[Deltas.D0_10];
                try
                {
                    stats.GoldEarlyMidGameDiff = participant.Timeline.GoldPerMinDeltas[Deltas.D10_20] - opposingLaner.Timeline.GoldPerMinDeltas[Deltas.D10_20];
                }
                catch
                {
                    Console.WriteLine("Warning game {0} with role {1} ({2}) failed to find Deltas.D10_20", match.GameId, role, ((Champion)participant.ChampionId).Name());
                    stats.GoldEarlyMidGameDiff = 0;
                }
                stats.TotalGoldDiff = participant.Stats.GoldEarned - opposingLaner.Stats.GoldEarned;

                if (allyCountDelta != 0)
                {
                    Console.WriteLine("Warning game {0} with role {1} ({2}) has imbalanced ally/enemy laner ratio", match.GameId, role, ((Champion)participant.ChampionId).Name());
                }
                else
                {
                    stats.AllyGoldAtTenDiff = allyGoldAtTenDiff;
                }

                statsList.Add(stats);
            }

            long gamesCucked = 0;
            long gamesTrolling = 0;
            long gamesTilted = 0;
            long gamesThrown = 0;
            long gamesHeavy = 0;
            long wins = 0;
            long throws = 0;
            foreach (MatchStats stats in statsList)
            {
                Console.WriteLine("Game {2} LaneDR {0:0.00} AllyDR {1:0.00} G10 {3:0.00} AllyG10 {3:0.00}", stats.LaneDamageRatio, stats.AllyDamageRatio, stats.GameId, stats.GoldAtTenDiff, stats.AllyGoldAtTenDiff);

                if (stats.LaneDamageRatio > stats.AllyDamageRatio)
                {
                    gamesCucked++;
                }

                if (stats.AllyGoldAtTenDiff > 0)
                {
                    gamesHeavy++;
                }

                if (stats.GoldAtTenDiff <= 0)
                {
                    gamesTrolling++;
                }

                if (stats.GoldEarlyMidGameDiff <= 0)
                {
                    gamesTilted++;
                }

                if (stats.TotalGoldDiff <= 0)
                {
                    gamesThrown++;
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
            Console.WriteLine("{0} of {1} ({2:0.00}%) had gold lead at 10 minutes", statsList.Count - gamesTrolling, statsList.Count, 100 * (statsList.Count - gamesTrolling) / (double)statsList.Count);
            Console.WriteLine("{0} of {1} ({2:0.00}%) had higher GPM 10-20", statsList.Count - gamesTilted, statsList.Count, 100 * (statsList.Count - gamesTilted) / (double)statsList.Count);
            Console.WriteLine("{0} of {1} ({2:0.00}%) had higher total gold", statsList.Count - gamesThrown, statsList.Count, 100 * (statsList.Count - gamesThrown) / (double)statsList.Count);
            Console.WriteLine("{0} of {1} ({2:0.00}%) allies had gold lead at 10 minutes", gamesHeavy, statsList.Count, 100 * (gamesHeavy) / (double)statsList.Count);
            Console.WriteLine("{0}W {1}L ({2:0.00}%) won", wins, throws, 100 * wins / (double)statsList.Count);
            Console.WriteLine("Player is boosted: {0}", throws != 0);

            return statsList;
        }

        private static async Task MainAsync(string[] args)
        {
            string apiKey = File.ReadAllText("apikey.txt").Trim();

            Region region = Region.NA;
            string summonerName = "WHATSHENANIGANS";
            string[] requiredAllies = new string[] { summonerName, "TheFreezer" };
            string[] excludedAllies = new string[] {  };

            string[] lane =
                new string[] { "BOTTOM" };
                //null;
            string[] role =
                new string[] { "DUO_CARRY" };     // e.g. DUO_CARRY, DUO_SUPPORT 
                //null;
            string[] allyLanes = new string[] { "TOP", "JUNGLE", "MIDDLE", "NONE" };

            using (var db = new SqliteConnection("Data Source=cache.sqlite"))
            {
                db.Open();
                var command = db.CreateCommand();
                command.CommandText = "create table if not exists matches (id integer, serialized text)";
                command.ExecuteNonQuery();

                Program p = new Program(apiKey, db);
                Summoner target = await p.GetSummoner(region, summonerName);

                var summonersRequired = await Task.WhenAll(requiredAllies.Select(async ally => { return await p.GetSummoner(region, ally); }).ToArray());
                var summonersExcluded = await Task.WhenAll(excludedAllies.Select(async ally => { return await p.GetSummoner(region, ally); }).ToArray());

                await p.Analyze(Region.NA, target, summonersRequired, summonersExcluded, role, lane, allyLanes);
            }

        }

        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }
    }
}
