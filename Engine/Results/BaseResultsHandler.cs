﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.TransactionHandlers;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Statistics;

namespace QuantConnect.Lean.Engine.Results
{
    /// <summary>
    /// Provides base functionality to the implementations of <see cref="IResultHandler"/>
    /// </summary>
    public abstract class BaseResultsHandler
    {
        /// <summary>
        /// True if the exit has been triggered
        /// </summary>
        protected volatile bool ExitTriggered;

        /// <summary>
        /// The log store instance
        /// </summary>
        protected List<LogEntry> LogStore { get; }

        /// <summary>
        /// Algorithms performance related chart names
        /// </summary>
        /// <remarks>Used to calculate the probabilistic sharpe ratio</remarks>
        protected List<string> AlgorithmPerformanceCharts { get; } = new List<string> { "Strategy Equity", "Benchmark" };

        /// <summary>
        /// Lock to be used when accessing the chart collection
        /// </summary>
        protected object ChartLock { get; }

        /// <summary>
        /// The algorithm unique compilation id
        /// </summary>
        protected string CompileId { get; set; }

        /// <summary>
        /// The algorithm job id.
        /// This is the deploy id for live, backtesting id for backtesting
        /// </summary>
        protected string JobId { get; set; }

        /// <summary>
        /// The result handler start time
        /// </summary>
        protected DateTime StartTime { get; }

        /// <summary>
        /// Customizable dynamic statistics <see cref="IAlgorithm.RuntimeStatistics"/>
        /// </summary>
        protected Dictionary<string, string> RuntimeStatistics { get; }

        /// <summary>
        /// The handler responsible for communicating messages to listeners
        /// </summary>
        protected IMessagingHandler MessagingHandler;

        /// <summary>
        /// The transaction handler used to get the algorithms Orders information
        /// </summary>
        protected ITransactionHandler TransactionHandler;

        /// <summary>
        /// The algorithms starting portfolio value.
        /// Used to calculate the portfolio return
        /// </summary>
        protected decimal StartingPortfolioValue { get; set; }

        /// <summary>
        /// The algorithm instance
        /// </summary>
        protected IAlgorithm Algorithm { get; set; }

        /// <summary>
        /// The data manager, used to access current subscriptions
        /// </summary>
        protected IDataFeedSubscriptionManager DataManager;

        /// <summary>
        /// Gets or sets the current alpha runtime statistics
        /// </summary>
        protected AlphaRuntimeStatistics AlphaRuntimeStatistics { get; set; }

        /// <summary>
        /// Closing portfolio value. Used to calculate daily performance.
        /// </summary>
        protected decimal DailyPortfolioValue;

        /// <summary>
        /// Last time the <see cref="IResultHandler.Sample(DateTime, bool)"/> method was called in UTC
        /// </summary>
        protected DateTime PreviousUtcSampleTime;

        /// <summary>
        /// Creates a new instance
        /// </summary>
        protected BaseResultsHandler()
        {
            RuntimeStatistics = new Dictionary<string, string>();
            StartTime = DateTime.UtcNow;
            CompileId = "";
            JobId = "";
            ChartLock = new object();
            LogStore = new List<LogEntry>();
        }

        /// <summary>
        /// Returns the location of the logs
        /// </summary>
        /// <param name="id">Id that will be incorporated into the algorithm log name</param>
        /// <param name="logs">The logs to save</param>
        /// <returns>The path to the logs</returns>
        public virtual string SaveLogs(string id, List<LogEntry> logs)
        {
            var path = $"{id}-log.txt";
            File.WriteAllLines(path, logs.Select(x => x.Message));
            return Path.Combine(Directory.GetCurrentDirectory(), path);
        }

        /// <summary>
        /// Save the results to disk
        /// </summary>
        /// <param name="name">The name of the results</param>
        /// <param name="result">The results to save</param>
        public virtual void SaveResults(string name, Result result)
        {
            File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), name), JsonConvert.SerializeObject(result, Formatting.Indented));
        }

        /// <summary>
        /// Sets the current alpha runtime statistics
        /// </summary>
        /// <param name="statistics">The current alpha runtime statistics</param>
        public virtual void SetAlphaRuntimeStatistics(AlphaRuntimeStatistics statistics)
        {
            AlphaRuntimeStatistics = statistics;
        }

        /// <summary>
        /// Sets the current Data Manager instance
        /// </summary>
        public virtual void SetDataManager(IDataFeedSubscriptionManager dataManager)
        {
            DataManager = dataManager;
        }

        /// <summary>
        /// Gets the algorithm net return
        /// </summary>
        protected decimal GetNetReturn()
        {
            //Some users have $0 in their brokerage account / starting cash of $0. Prevent divide by zero errors
            return StartingPortfolioValue > 0 ?
                (Algorithm.Portfolio.TotalPortfolioValue - StartingPortfolioValue) / StartingPortfolioValue
                : 0;
        }

        /// <summary>
        /// Samples portfolio equity, benchmark, and daily performance
        /// </summary>
        /// <param name="time">Current time in the AlgorithmManager loop</param>
        /// <param name="force">Force sampling of equity, benchmark, and performance to be </param>
        public virtual void Sample(DateTime time, bool force = false)
        {
            var dayChanged = PreviousUtcSampleTime.Date != time.Date;

            if (dayChanged || force)
            {
                if (force)
                {
                    // For any forced sampling, we need to sample at the time we provide to this method.
                    PreviousUtcSampleTime = time;
                }

                var currentPortfolioValue = Algorithm.Portfolio.TotalPortfolioValue;
                var portfolioPerformance = DailyPortfolioValue == 0 ? 0 : Math.Round((currentPortfolioValue - DailyPortfolioValue) * 100 / DailyPortfolioValue, 10);

                SampleEquity(PreviousUtcSampleTime, currentPortfolioValue);
                SampleBenchmark(PreviousUtcSampleTime, Algorithm.Benchmark.Evaluate(PreviousUtcSampleTime).SmartRounding());
                SamplePerformance(PreviousUtcSampleTime, portfolioPerformance);

                // If the day changed, set the closing portfolio value. Otherwise, we would end up
                // with skewed statistics if a processing event was forced.
                if (dayChanged)
                {
                    DailyPortfolioValue = currentPortfolioValue;
                }
            }

            PreviousUtcSampleTime = time;
        }

        /// <summary>
        /// Sample the current equity of the strategy directly with time-value pair.
        /// </summary>
        /// <param name="time">Time of the sample.</param>
        /// <param name="value">Current equity value.</param>
        protected virtual void SampleEquity(DateTime time, decimal value)
        {
            Sample("Strategy Equity", "Equity", 0, SeriesType.Candle, time, value);
        }

        /// <summary>
        /// Sample the current daily performance directly with a time-value pair.
        /// </summary>
        /// <param name="time">Time of the sample.</param>
        /// <param name="value">Current daily performance value.</param>
        protected virtual void SamplePerformance(DateTime time, decimal value)
        {
            if (Log.DebuggingEnabled)
            {
                Log.Debug("BaseResultsHandler.SamplePerformance(): " + time.ToShortTimeString() + " >" + value);
            }
            Sample("Strategy Equity", "Daily Performance", 1, SeriesType.Bar, time, value, "%");
        }

        /// <summary>
        /// Sample the current benchmark performance directly with a time-value pair.
        /// </summary>
        /// <param name="time">Time of the sample.</param>
        /// <param name="value">Current benchmark value.</param>
        /// <seealso cref="IResultHandler.Sample"/>
        protected virtual void SampleBenchmark(DateTime time, decimal value)
        {
            Sample("Benchmark", "Benchmark", 0, SeriesType.Line, time, value);
        }

        /// <summary>
        /// Add a sample to the chart specified by the chartName, and seriesName.
        /// </summary>
        /// <param name="chartName">String chart name to place the sample.</param>
        /// <param name="seriesName">Series name for the chart.</param>
        /// <param name="seriesIndex">Series chart index - which chart should this series belong</param>
        /// <param name="seriesType">Series type for the chart.</param>
        /// <param name="time">Time for the sample</param>
        /// <param name="value">Value for the chart sample.</param>
        /// <param name="unit">Unit for the chart axis</param>
        /// <remarks>Sample can be used to create new charts or sample equity - daily performance.</remarks>
        protected abstract void Sample(string chartName,
            string seriesName,
            int seriesIndex,
            SeriesType seriesType,
            DateTime time,
            decimal value,
            string unit = "$");

        /// <summary>
        /// Gets the algorithm runtime statistics
        /// </summary>
        protected Dictionary<string, string> GetAlgorithmRuntimeStatistics(Dictionary<string, string> summary,
            Dictionary<string, string> runtimeStatistics = null)
        {
            if (runtimeStatistics == null)
            {
                runtimeStatistics = new Dictionary<string, string>();
            }

            if (summary.ContainsKey("Probabilistic Sharpe Ratio"))
            {
                runtimeStatistics["Probabilistic Sharpe Ratio"] = summary["Probabilistic Sharpe Ratio"];
            }
            else
            {
                runtimeStatistics["Probabilistic Sharpe Ratio"] = "0%";
            }

            runtimeStatistics["Unrealized"] = "$" + Algorithm.Portfolio.TotalUnrealizedProfit.ToStringInvariant("N2");
            runtimeStatistics["Fees"] = "-$" + Algorithm.Portfolio.TotalFees.ToStringInvariant("N2");
            runtimeStatistics["Net Profit"] = "$" + Algorithm.Portfolio.TotalProfit.ToStringInvariant("N2");
            runtimeStatistics["Return"] = GetNetReturn().ToStringInvariant("P");
            runtimeStatistics["Equity"] = "$" + Algorithm.Portfolio.TotalPortfolioValue.ToStringInvariant("N2");
            runtimeStatistics["Holdings"] = "$" + Algorithm.Portfolio.TotalHoldingsValue.ToStringInvariant("N2");
            runtimeStatistics["Volume"] = "$" + Algorithm.Portfolio.TotalSaleVolume.ToStringInvariant("N2");

            return runtimeStatistics;
        }

        /// <summary>
        /// Will generate the statistics results and update the provided runtime statistics
        /// </summary>
        protected StatisticsResults GenerateStatisticsResults(Dictionary<string, Chart> charts,
            SortedDictionary<DateTime, decimal> profitLoss = null)
        {
            var statisticsResults = new StatisticsResults();
            if (profitLoss == null)
            {
                profitLoss = new SortedDictionary<DateTime, decimal>();
            }

            try
            {
                //Generates error when things don't exist (no charting logged, runtime errors in main algo execution)
                const string strategyEquityKey = "Strategy Equity";
                const string equityKey = "Equity";
                const string dailyPerformanceKey = "Daily Performance";
                const string benchmarkKey = "Benchmark";

                // make sure we've taken samples for these series before just blindly requesting them
                if (charts.ContainsKey(strategyEquityKey) &&
                    charts[strategyEquityKey].Series.ContainsKey(equityKey) &&
                    charts[strategyEquityKey].Series.ContainsKey(dailyPerformanceKey) &&
                    charts.ContainsKey(benchmarkKey) &&
                    charts[benchmarkKey].Series.ContainsKey(benchmarkKey))
                {
                    var equity = charts[strategyEquityKey].Series[equityKey].Values;
                    var performance = charts[strategyEquityKey].Series[dailyPerformanceKey].Values;
                    var totalTransactions = Algorithm.Transactions.GetOrders(x => x.Status.IsFill()).Count();
                    var benchmark = charts[benchmarkKey].Series[benchmarkKey].Values;

                    var trades = Algorithm.TradeBuilder.ClosedTrades;

                    statisticsResults = StatisticsBuilder.Generate(trades, profitLoss, equity, performance, benchmark,
                        StartingPortfolioValue, Algorithm.Portfolio.TotalFees, totalTransactions);
                }
            }
            catch (Exception err)
            {
                Log.Error(err, "BaseResultsHandler.GenerateStatisticsResults(): Error generating statistics packet");
            }

            return statisticsResults;
        }
    }
}
