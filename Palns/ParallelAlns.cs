using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

[module: SuppressMessage("StyleCop.CSharp.DocumentationRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.NamingRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.OrderingRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.ReadabilityRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "*")]

namespace Palns
{
    /// <summary>
    ///     Parallel adaptive large neighborhood search based on
    ///     http://orbit.dtu.dk/fedora/objects/orbit:56703/datastreams/file_4129408/content
    /// </summary>
    /// <typeparam Name="TInput">The input problem</typeparam>
    /// <typeparam Name="TSolution">An initial feasible solution</typeparam>
    /// <typeparam name="TSolution">The solution type</typeparam>
    /// <typeparam name="TInput">The input type</typeparam>
    public class ParallelAlns<TInput, TSolution> : ISolve<TInput, TSolution> where TSolution : ISolution<TSolution>
    {
        private readonly Func<bool> _abort;
        private readonly double _alpha; //>0 and < 1
        private readonly object _bestLock = new object();
        private readonly object _cloneLock = new object();
        private readonly Func<TInput, TSolution> _constructionHeuristic;

        private readonly List<Func<TSolution, Task<TSolution>>> _destroyOperators;
        private readonly List<double> _weights;

        private readonly double _newGlobalBestWeight,
            _betterSolutionWeight,
            _acceptedSolution,
            _rejectedSolution,
            _decay;

        private readonly int? _numberOfThreads;

        /// <summary>
        ///     Called after every iteration with the current best solution as input.
        /// </summary>
        private readonly Action<TSolution> _progressUpdate;

        private readonly Random _randomizer;
        private readonly List<Func<TSolution, Task<TSolution>>> _repairOperators;
        //private readonly List<double> _repairWeights;
        private readonly double _temperature; //>0
        private readonly object _weightLock = new object();
        private List<double> _cumulativeWeights;
        //private List<double> _cumulativeRepairWeights;
        private TSolution _x;

        public ParallelAlns(Func<TInput, TSolution> constructionHeuristic,
            List<Func<TSolution, Task<TSolution>>> destroyOperators,
            List<Func<TSolution, Task<TSolution>>> repairOperators, double temperature, double alpha, Random randomizer,
            double newGlobalBestWeight, double betterSolutionWeight, double acceptedSolution, double rejectedSolution,
            double decay, double initialWeight = 1, int? numberOfThreads = null,
            Func<bool> abort = null, Action<TSolution> progressUpdate = null)
        {
            _destroyOperators = destroyOperators;
            _repairOperators = repairOperators;
            _temperature = temperature;
            _alpha = alpha;
            _randomizer = randomizer;
            _newGlobalBestWeight = newGlobalBestWeight;
            _betterSolutionWeight = betterSolutionWeight;
            _acceptedSolution = acceptedSolution;
            _rejectedSolution = rejectedSolution;
            _decay = decay;
            _abort = abort;
            _numberOfThreads = numberOfThreads;
            _progressUpdate = progressUpdate;
            _constructionHeuristic = constructionHeuristic;
            _weights = _destroyOperators.SelectMany(destroy => _repairOperators.Select(repair => initialWeight)).ToList();
            _cumulativeWeights = _weights.ToCumulativEnumerable().ToList();
            if (_numberOfThreads == null)
            {
                _numberOfThreads = ProcessorCores;
            }
        }

        /// <summary>
        /// The number of physical processors
        /// </summary>
        public int ProcessorCores
        {
            get
            {
                return Environment.ProcessorCount / 2;
            }
        }

        /// <summary>
        /// Holds the current best solution
        /// </summary>
        public TSolution BestSolution { get; private set; }

        /// <summary>
        /// Gets a text describing the repair and destroy methods' weights.
        /// </summary>
        public string MethodWeightLog
        {
            get
            {
                var log = string.Format("Operators' weights\n{0,-10}|Operator\n\n", "Weight");
                foreach (var combinationIdx in Enumerable.Range(0, _weights.Count).OrderBy(i => _weights[i]))
                {
                    log += string.Format(
                        "{0,-10:0.#####} {1}, {2}\n",
                        _weights[combinationIdx],
                        _destroyOperators[combinationIdx / _repairOperators.Count].Method.Name,
                        _repairOperators[combinationIdx % _repairOperators.Count].Method.Name);
                }

                return log;
            }
        }

        /// <summary>
        /// Solves the given input and produces a solution
        /// </summary>
        /// <param name="input">The problem to solve</param>
        /// <returns>A solution to the input</returns>
        public TSolution Solve(TInput input)
        {
            _x = _constructionHeuristic(input);
            BestSolution = _x;
            Task.WaitAll(
                Enumerable.Range(0, _numberOfThreads ?? ProcessorCores)
                    .Select(i => Task.Run(() => ApplyOperation()))
                    .ToArray());
            return BestSolution;
        }

        public TSolution SolveUsingTplDataflow(TInput input)
        {
            _x = _constructionHeuristic(input);
            BestSolution = _x;
            var iterationCounter = 0;
            var weightPair = new ConcurrentExclusiveSchedulerPair();
            var clonePair = new ConcurrentExclusiveSchedulerPair();
            var weightOptions = new ExecutionDataflowBlockOptions
            {
                SingleProducerConstrained = true,
                TaskScheduler = weightPair.ExclusiveScheduler
            };
            var cloneOptions = new ExecutionDataflowBlockOptions
            {
                SingleProducerConstrained = true,
                TaskScheduler = clonePair.ExclusiveScheduler
            };
            var destroyAndRepairOptions = new ExecutionDataflowBlockOptions
            {
                SingleProducerConstrained = true,
                MaxDegreeOfParallelism = _numberOfThreads ?? ProcessorCores,
                TaskScheduler = clonePair.ConcurrentScheduler
            };
            var executionDataflowBlockOptions = new ExecutionDataflowBlockOptions { SingleProducerConstrained = true };

            var inputBlock = new BufferBlock<Message<TSolution>>();
            var selectOperators = new TransformBlock<Message<TSolution>, Message<TSolution>>(
                x =>
                {
                    x.OperatorIndex = SelectOperatorIndex(_cumulativeWeights);
                    x.DestroyOperator = _destroyOperators[x.OperatorIndex / _repairOperators.Count];
                    x.RepairOperator = _repairOperators[x.OperatorIndex % _repairOperators.Count];
                    return x;
                }, weightOptions);
            var copyCurrentSolution = new TransformBlock<Message<TSolution>, Message<TSolution>>(
                x =>
                {
                    x.Solution = _x.Clone();
                    return x;
                }, cloneOptions);
            var destroyAndRepair =
                new TransformBlock<Message<TSolution>, Message<TSolution>>(async x =>
                {
                    await x.RepairOperator(await x.DestroyOperator(x.Solution));
                    return x;
                }, destroyAndRepairOptions);
            var updateCurrentSolution =
                new TransformBlock<Message<TSolution>, Message<TSolution>>(x =>
                {
                    x.WeightSelection = UpdateCurrentSolution(x.Solution, x.Temperature);
                    return x;
                }, cloneOptions);
            var updateBestSolution =
                new TransformBlock<Message<TSolution>, Message<TSolution>>(x =>
                {
                    x.WeightSelection = UpdateBestSolution(x.Solution, x.WeightSelection);
                    return x;
                }, executionDataflowBlockOptions);
            var updateWeights =
                new TransformBlock<Message<TSolution>, Message<TSolution>>(x =>
                {
                    UpdateWeights(x.OperatorIndex, x.WeightSelection);
                    return x;
                }, weightOptions);

            var updateCounters =
                new ActionBlock<Message<TSolution>>(x =>
                {
                    x.Temperature *= _alpha;
                    Interlocked.Increment(ref iterationCounter);
                    if (!_abort.Invoke())
                    {
                        inputBlock.Post(x);
                    }
                    else
                    {
                        inputBlock.Complete();
                    }
                }, executionDataflowBlockOptions);

            var dataflowLinkOptions = new DataflowLinkOptions { PropagateCompletion = true };
            using (inputBlock.LinkTo(selectOperators, dataflowLinkOptions))
            using (selectOperators.LinkTo(copyCurrentSolution, dataflowLinkOptions))
            using (copyCurrentSolution.LinkTo(destroyAndRepair, dataflowLinkOptions))
            using (destroyAndRepair.LinkTo(updateCurrentSolution, dataflowLinkOptions))
            using (updateCurrentSolution.LinkTo(updateBestSolution, dataflowLinkOptions))
            using (updateBestSolution.LinkTo(updateWeights, dataflowLinkOptions))
            using (updateWeights.LinkTo(updateCounters, dataflowLinkOptions))
            {
                foreach (var core in Enumerable.Range(0, ProcessorCores))
                {
                    inputBlock.Post(new Message<TSolution>());
                }
                updateCounters.Completion.Wait();
            }
            return BestSolution;
        }

        private async Task ApplyOperation()
        {
            var temperature = _temperature;
            do
            {
                Func<TSolution, Task<TSolution>> d;
                Func<TSolution, Task<TSolution>> r;
                int operatorIndex;
                lock (_weightLock)
                {
                    operatorIndex = SelectOperatorIndex(_cumulativeWeights);
                    d = _destroyOperators[operatorIndex / _repairOperators.Count];
                    r = _repairOperators[operatorIndex % _repairOperators.Count];
                }
                TSolution xTemp;
                //if there is just one thread, there is no need for copying
                if (_numberOfThreads != null && _numberOfThreads.Value == 1)
                {
                    xTemp = _x;
                }
                else
                {
                    lock (_cloneLock)
                    {
                        xTemp = _x.Clone();
                    }
                }
                xTemp = await r(await d(xTemp));
                WeightSelection weightSelection;
                lock (_cloneLock)
                {
                    weightSelection = UpdateCurrentSolution(xTemp, temperature);
                }
                lock (_bestLock)
                {
                    weightSelection = UpdateBestSolution(xTemp, weightSelection);
                }
                lock (_weightLock)
                {
                    UpdateWeights(operatorIndex, weightSelection);
                }
                temperature *= _alpha;

                if (_progressUpdate != null)
                {
                    _progressUpdate.Invoke(BestSolution);
                }
            } while (!_abort.Invoke());
        }

        private WeightSelection UpdateBestSolution(TSolution xTemp, WeightSelection weightSelection)
        {
            if (xTemp.Objective < BestSolution.Objective)
            {
                BestSolution = xTemp;
                weightSelection = WeightSelection.NewGlobalBest;
            }
            return weightSelection;
        }

        private WeightSelection UpdateCurrentSolution(TSolution xTemp, double temperature)
        {
            var weightSelection = Accept(xTemp, temperature);
            if (weightSelection >= WeightSelection.Accepted)
            {
                _x = xTemp;
            }
            return weightSelection;
        }

        private void UpdateWeights(int operatorIndex, WeightSelection weightSelection)
        {
            double weight;
            switch (weightSelection)
            {
                case WeightSelection.Rejected:
                    weight = _rejectedSolution;
                    break;
                case WeightSelection.Accepted:
                    weight = _acceptedSolution;
                    break;
                case WeightSelection.BetterThanCurrent:
                    weight = _betterSolutionWeight;
                    break;
                case WeightSelection.NewGlobalBest:
                    weight = _newGlobalBestWeight;
                    break;
                default:
                    throw new ArgumentException("WeightSelection has unexpected status");
            }
            _weights[operatorIndex] = _decay * _weights[operatorIndex] + (1 - _decay) * weight;
            _cumulativeWeights = _weights.ToCumulativEnumerable().ToList();
        }

        private int SelectOperatorIndex(IReadOnlyList<double> cumulativeWeights)
        {
            var randomValue = _randomizer.NextDouble();
            for (var i = 0; i < cumulativeWeights.Count; i++)
            {
                if (cumulativeWeights[i] > randomValue)
                    return i;
            }
            return cumulativeWeights.Count - 1;
        }

        private WeightSelection Accept(TSolution newSolution, double temperature)
        {
            if (newSolution.Objective < _x.Objective)
                return WeightSelection.BetterThanCurrent;
            var probability = Math.Exp(-(newSolution.Objective - _x.Objective) / temperature);
            var accepted = _randomizer.NextDouble() <= probability;
            return accepted ? WeightSelection.Accepted : WeightSelection.Rejected;
        }
    }
}