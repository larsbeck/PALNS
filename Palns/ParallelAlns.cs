using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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
        private readonly Func<TSolution, bool> _abort;
        private readonly double _alpha; //>0 and < 1
        private readonly AsyncLock _bestLock = new AsyncLock();
        private readonly AsyncLock _cloneLock = new AsyncLock();
        private readonly Func<TInput, TSolution> _constructionHeuristic;

        private readonly List<Func<TSolution, Task<TSolution>>> _destroyOperators;
        private readonly List<double> _weights;

        private readonly double _newGlobalBestWeight,
                                _betterSolutionWeight,
                                _acceptedSolution,
                                _rejectedSolution,
                                _decay,
                                _precision;

        private readonly int? _numberOfThreads;

        /// <summary>
        /// Called after every iteration with the current best solution as input.
        /// </summary>
        private readonly Action<TSolution> _progressUpdate;

        private readonly Random _randomizer;
        private readonly List<Func<TSolution, Task<TSolution>>> _repairOperators;
        //private readonly List<double> _repairWeights;
        private readonly double _temperature; //>0
        private readonly AsyncLock _weightLock = new AsyncLock();
        private List<double> _cumulativeWeights;
        //private List<double> _cumulativeRepairWeights;
        private TSolution _x;

        public ParallelAlns(Func<TInput, TSolution> constructionHeuristic,
            List<Func<TSolution, Task<TSolution>>> destroyOperators,
            List<Func<TSolution, Task<TSolution>>> repairOperators, double temperature, double alpha, Random randomizer,
            double newGlobalBestWeight, double betterSolutionWeight, double acceptedSolution, double rejectedSolution,
            double decay, double initialWeight = 1,
            double precision = 1e-5,
            int? numberOfThreads = null,
            Func<TSolution, bool> abort = null, Action<TSolution> progressUpdate = null)
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
            _precision = precision;
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
        public int ProcessorCores => Environment.ProcessorCount / 2;

        /// <summary>
        /// Holds the current best solution
        /// </summary>
        public TSolution BestSolution { get; private set; }

        /// <summary>
        /// Gets a text describing the methods' weights.
        /// </summary>
        public string MethodWeightLog
        {
            get
            {
                return WeightLog(
                    "Operators' weights",
                    _weights.ToArray(),
                    idx => $"{_destroyOperators[idx / _repairOperators.Count].Method.Name}, {_repairOperators[idx % _repairOperators.Count].Method.Name}");
            }
        }

        /// <summary>
        /// Gets a text describing the repair operations' weight: 
        /// The weight of one repair operation is the average of the weights of all operations where the repair is used.
        /// </summary>
        public string RepairWeightLog
        {
            get
            {
                /* Create an array of doubles describing the weight of each repair operation */
                var repairWeights = new double[_repairOperators.Count];
                List<int> destroyOperatorChangeIdx =
                    Enumerable.Range(0, _destroyOperators.Count).Select(i => i * _repairOperators.Count).ToList();
                for (int repairIdx = 0; repairIdx < repairWeights.Length; repairIdx++)
                {
                    repairWeights[repairIdx] = destroyOperatorChangeIdx.Average(destroyIdx => _weights[destroyIdx + repairIdx]);
                }

                /* Create and return description */
                return WeightLog("Total repair weights", repairWeights, idx => _repairOperators[idx].Method.Name);
            }
        }

        /// <summary>
        /// Gets a text describing the destroy operations' weight: 
        /// The weight of one destroy operation is the average of the weights of all operations where the destroy is used.
        /// </summary>
        public string DestroyWeightLog
        {
            get
            {
                /* Create an array of doubles describing the weight of each destroy operation */
                var destroyWeights = new double[_destroyOperators.Count];
                List<int> repairOperatorSummands = Enumerable.Range(0, _repairOperators.Count).ToList();
                for (int destroyIdx = 0; destroyIdx < destroyWeights.Length; destroyIdx++)
                {
                    destroyWeights[destroyIdx] =
                        repairOperatorSummands.Average(repairIdx => _weights[destroyIdx * _repairOperators.Count + repairIdx]);
                }

                /* Create and return description */
                return WeightLog("Total destroy weights", destroyWeights, idx => _destroyOperators[idx].Method.Name);
            }
        }

        /// <summary>
        /// Returns a nicely formatted overview over weights for operations, including both total and relative weights.
        /// </summary>
        /// <param name="title">The overview's title.</param>
        /// <param name="weights">Weights to use.</param>
        /// <param name="operationNameFromIdx">Given a (weight) index, returns the correct operation.</param>
        /// <returns>A string describing the distribution of weight between operations.</returns>
        private string WeightLog(string title, double[] weights, Func<int, string> operationNameFromIdx)
        {
            var log = $"{title}\n{"Weight",-10} {"Probability",-15} Operation\n\n";
            double sum = weights.Sum();
            foreach (var idx in Enumerable.Range(0, weights.Length).OrderBy(i => weights[i]))
            {
                log += $"{weights[idx],-10:0.#####} {weights[idx] / sum,-15:#0.##%} {operationNameFromIdx.Invoke(idx)}\n";
            }

            return log;
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
                    .Select(i => Task.Run(async () => await ApplyOperation()))
                    .ToArray());
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
                using (await _weightLock.LockAsync())
                {
                    operatorIndex = await SelectOperatorIndex(_cumulativeWeights);
                    d = _destroyOperators[operatorIndex / _repairOperators.Count];
                    r = _repairOperators[operatorIndex % _repairOperators.Count];
                }
                TSolution xTemp;
                using (await _cloneLock.LockAsync())
                {
                    xTemp = _x.Clone();
                }
                xTemp = await r(await d(xTemp));
                WeightSelection weightSelection;
                using (await _cloneLock.LockAsync())
                {
                    weightSelection = await UpdateCurrentSolution(xTemp, temperature);
                }
                using (await _bestLock.LockAsync())
                {
                    weightSelection = UpdateBestSolution(xTemp, weightSelection);
                }
                using (await _weightLock.LockAsync())
                {
                    UpdateWeights(operatorIndex, weightSelection);
                }
                temperature *= _alpha;

                _progressUpdate?.Invoke(BestSolution);
            } while (!_abort.Invoke(BestSolution));
        }

        private WeightSelection UpdateBestSolution(TSolution xTemp, WeightSelection weightSelection)
        {
            if (BestSolution.Objective - xTemp.Objective > _precision)
            {
                BestSolution = xTemp;
                weightSelection = WeightSelection.NewGlobalBest;
            }
            return weightSelection;
        }

        private async Task<WeightSelection> UpdateCurrentSolution(TSolution xTemp, double temperature)
        {
            var weightSelection = await Accept(xTemp, temperature);
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

        private readonly AsyncLock _randomizerLock = new AsyncLock();
        private async Task<int> SelectOperatorIndex(IReadOnlyList<double> cumulativeWeights)
        {
            double randomValue;
            using (await _randomizerLock.LockAsync())
            {
                randomValue = _randomizer.NextDouble();
            }
            for (var i = 0; i < cumulativeWeights.Count; i++)
            {
                if (cumulativeWeights[i] > randomValue)
                    return i;
            }
            return cumulativeWeights.Count - 1;
        }

        private async Task<WeightSelection> Accept(TSolution newSolution, double temperature)
        {
            if (_x.Objective - newSolution.Objective > _precision)
                return WeightSelection.BetterThanCurrent;
            var probability = Math.Exp(-(newSolution.Objective - _x.Objective) / temperature);
            bool accepted;
            using (await _randomizerLock.LockAsync())
            {
                accepted = _randomizer.NextDouble() <= probability;
            }
            return accepted ? WeightSelection.Accepted : WeightSelection.Rejected;
        }
    }
}