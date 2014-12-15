﻿using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using ccMonitor.Api;

namespace ccMonitor
{
    public class RigController
    {
        public BindingList<RigInfo> RigLogs { get; set; }
        public class RigInfo
        {
            public string Name { get; set; }
            public string IpAddress { get; set; }
            public int Port { get; set; }

            public string UserFriendlyName { get; set; }

            public List<GpuLogger> GpuLogs { get; set; }

            public RigStat CurrentStatistic { get; set; }
            public class RigStat
            {
                public string Algorithm { get; set; }
                public double TotalHashCount { get; set; }
                public double TotalHashRate { get; set; }
                public double AverageHashRate { get; set; }
                public double AverageMeanHashRate { get; set; }
                public double AverageStandardDeviation { get; set; }
                public double[] AverageGaussianPercentiles { get; set; }
                public double Accepts { get; set; }
                public double Rejects { get; set; }
                public double AverageTemperature { get; set; }
                public double ShareAnswerPing { get; set; }
            }

            public RigInfo()
            {
                GpuLogs = new List<GpuLogger>();
                CurrentStatistic = new RigStat();
            }
        }
        

        public RigController(BindingList<RigInfo> rigLogs)
        {
            RigLogs = rigLogs;

            Update();
        }

        // No logs available? What a pity, must be a fresh start
        // This will get all local nvidia cards and load them in
        public RigController()
        {
            InitDefaultRig();

            Update();
        }

        private void InitDefaultRig()
        {
            RigInfo localRig = new RigInfo
            {
                Name = "Local rig",
                IpAddress = "127.0.0.1",
                Port = 4068, 
                GpuLogs = new List<GpuLogger>(),
            };

            RigLogs = new BindingList<RigInfo> {localRig};
        }

        public void Update()
        {
            for (int index = 0; index < RigLogs.Count; index++)
            {
                RigInfo rig = RigLogs[index];

                // Makes sure the rigname the user sees is always correct
                StringBuilder sb = new StringBuilder();
                sb.Append("Rig #").Append(index + 1).Append(", ").Append(rig.Name)
                    .Append(": ").Append(rig.IpAddress).Append(":").Append(rig.Port);
                rig.UserFriendlyName = sb.ToString();
                
                //Gets all API information here first and passes it on
                Dictionary<string, string>[][] allApiResults = new Dictionary<string, string>[4][];
                allApiResults[0] = PruvotApi.GetSummary(rig.IpAddress, rig.Port);
                allApiResults[1] = PruvotApi.GetPoolInfo(rig.IpAddress, rig.Port);
                allApiResults[2] = PruvotApi.GetHwInfo(rig.IpAddress, rig.Port);

                if (allApiResults[0] != null && allApiResults[1] != null && allApiResults[2] != null)
                {
                    // Ping times measuring needs only to be done once too
                    int[] pingTimes = GetPingTimes(allApiResults[1], rig);

                    // If it's a new rig or hardware has been added/removed, it should add/remove it in the logs too
                    CheckLiveGpus(allApiResults, rig);

                    // Prepping for total rig statistics
                    double totalHashCount = 0,totalAverageHashRate = 0,
                        totalMeanHashRate = 0,totalStandardDeviation = 0,
                        totalAverageTemperature = 0;
                    double[] totalGaussianPercentiles = {0.0, 0.0, 0.0, 0.0};

                    foreach (GpuLogger gpu in rig.GpuLogs)
                    {
                        allApiResults[3] = gpu.Info.MinerMap != -1
                            ? PruvotApi.GetHistory(rig.IpAddress, rig.Port, gpu.Info.MinerMap)
                            : new Dictionary<string, string>[0];

                        if (allApiResults[3] != null)
                        {
                            gpu.Update(allApiResults, pingTimes);


                            // While we're at it, calculate the total stats for the rig
                            totalHashCount += gpu.CurrentBenchmark.Statistic.TotalHashCount;
                            totalAverageHashRate += gpu.CurrentBenchmark.Statistic.AverageHashRate;
                            totalMeanHashRate += gpu.CurrentBenchmark.Statistic.MeanHashRate;
                            totalStandardDeviation += gpu.CurrentBenchmark.Statistic.StandardDeviation;
                            totalAverageTemperature += gpu.CurrentBenchmark.Statistic.AverageTemperature;

                            for (int i = 0; i < 4; i++)
                            {
                                if (gpu.CurrentBenchmark.Statistic.GaussianPercentiles != null)
                                    totalGaussianPercentiles[i] += gpu.CurrentBenchmark.Statistic.GaussianPercentiles[i];
                            }
                        }
                    }

                    rig.CurrentStatistic = new RigInfo.RigStat
                    {
                        Algorithm = rig.GpuLogs[rig.GpuLogs.Count -1].CurrentBenchmark.Algorithm,
                        TotalHashCount = totalHashCount,
                        TotalHashRate = totalAverageHashRate,
                        AverageHashRate = totalAverageHashRate/rig.GpuLogs.Count,
                        AverageMeanHashRate = totalMeanHashRate/rig.GpuLogs.Count,
                        AverageStandardDeviation = totalStandardDeviation/rig.GpuLogs.Count,
                        Accepts = PruvotApi.GetDictValue<int>(allApiResults[0][0], "ACC"),
                        Rejects = PruvotApi.GetDictValue<int>(allApiResults[0][0], "REJ"),
                        AverageTemperature = totalAverageTemperature/rig.GpuLogs.Count,
                        ShareAnswerPing = pingTimes[0],
                        AverageGaussianPercentiles = new double[4]
                    };

                    for (int i = 0; i < 4; i++)
                    {
                        rig.CurrentStatistic.AverageGaussianPercentiles[i] = totalGaussianPercentiles[i]/rig.GpuLogs.Count;
                    }
                }
            }
        }

        private static void CheckLiveGpus(Dictionary<string, string>[][] allApiResults, RigInfo rig)
        {
            int apiGpuCount = PruvotApi.GetDictValue<int>(allApiResults[0][0], "GPUS");
            if (rig.GpuLogs.Count < apiGpuCount)
            {
                foreach (Dictionary<string, string> hwInfo in allApiResults[2])
                {
                    int apiBus = PruvotApi.GetDictValue<int>(hwInfo, "BUS");
                    if (apiBus >= 0)
                    {
                        bool found = false;

                        foreach (GpuLogger gpu in rig.GpuLogs)
                        {
                            if (gpu.Info.Bus == apiBus)
                            {
                                found = true;
                            }
                        }

                        if (!found)
                        {
                            GpuLogger gpu = new GpuLogger
                            {
                                Info = new GpuLogger.GpuInfo
                                {
                                    Name = PruvotApi.GetDictValue(hwInfo, "CARD"),
                                    Bus = apiBus,
                                    MinerMap = PruvotApi.GetDictValue<int>(hwInfo, "GPU"),
                                    NvapiId = PruvotApi.GetDictValue<int>(hwInfo, "NVAPI"),
                                    Available = true
                                },
                                BenchLogs = new List<GpuLogger.Benchmark>(),
                            };
                            rig.GpuLogs.Add(gpu);
                        }
                    }
                }
            }

            if (rig.GpuLogs.Count > apiGpuCount)
            {
                foreach (GpuLogger gpu in rig.GpuLogs)
                {
                    bool found = false;
                    foreach (Dictionary<string, string> hwInfo in allApiResults[2])
                    {
                        int apiBus = PruvotApi.GetDictValue<int>(hwInfo, "BUS");
                        if (gpu.Info.Bus == apiBus)
                        {
                            found = true;
                        }
                    }

                    gpu.Info.Available = found;
                }
            }
        }

        private static int[] GetPingTimes(Dictionary<string, string>[] poolInfo, RigInfo rig)
        {
            if (poolInfo == null || poolInfo.Length == 0) return new[] {999, 999, 999};

            string[] stratum = PruvotApi.GetDictValue<string>(poolInfo[0], "URL").Split('/');
            string[] stratumUrlPort = stratum[stratum.Length - 1].Split(':');

            int[] pingTimes = new int[3];
            Ping pinger = new Ping();
            PingReply miningUrlPing = pinger.Send(stratumUrlPort[0], 500);
            PingReply networkRigPing = pinger.Send(rig.IpAddress, 500);

            // Will try to just ping the URL without subdomain if it failed, 
            // May give bad results but it's just a small stat for fun ^^
            // Ping by Pruvotapi is more accurate
            // PS: Let's hope it's not a .co.uk :D
            if (miningUrlPing != null && miningUrlPing.Status != IPStatus.Success)
            {
                string[] stratumDomains = stratumUrlPort[0].Split('.');
                string lastParts = stratumDomains[stratumDomains.Length - 2] + '.' +
                                   stratumDomains[stratumDomains.Length - 1];
                miningUrlPing = pinger.Send(lastParts, 500);
            }

            pingTimes[0] = PruvotApi.GetDictValue<int>(poolInfo[0], "PING");
            pingTimes[1] = (int) (miningUrlPing != null && miningUrlPing.Status == IPStatus.Success
                            ? miningUrlPing.RoundtripTime : 999);
            pingTimes[2] = (int) (networkRigPing != null && networkRigPing.Status == IPStatus.Success
                            ? networkRigPing.RoundtripTime : 999);

            return pingTimes;
        }
    }
}