// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Float = System.Single;

using System;
using System.Linq;
using System.IO;
using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.CommandLine;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.EntryPoints;
using Microsoft.ML.Runtime.Internal.CpuMath;
using Microsoft.ML.Runtime.Internal.Utilities;
using Microsoft.ML.Runtime.Model;
using Microsoft.ML.Runtime.Numeric;
using Microsoft.ML.Runtime.PCA;
using Microsoft.ML.Runtime.Training;
using Microsoft.ML.Runtime.Internal.Internallearn;

[assembly: LoadableClass(RandomizedPcaTrainer.Summary, typeof(RandomizedPcaTrainer), typeof(RandomizedPcaTrainer.Arguments),
    new[] { typeof(SignatureAnomalyDetectorTrainer), typeof(SignatureTrainer) },
    RandomizedPcaTrainer.UserNameValue,
    RandomizedPcaTrainer.LoadNameValue,
    RandomizedPcaTrainer.ShortName)]

[assembly: LoadableClass(typeof(PcaPredictor), null, typeof(SignatureLoadModel),
    "PCA Anomaly Executor", PcaPredictor.LoaderSignature)]

[assembly: LoadableClass(typeof(void), typeof(RandomizedPcaTrainer), null, typeof(SignatureEntryPointModule), RandomizedPcaTrainer.LoadNameValue)]

namespace Microsoft.ML.Runtime.PCA
{
    // REVIEW: make RFF transformer an option here.

    /// <summary>
    /// This trainer trains an approximate PCA using Randomized SVD algorithm
    /// Reference: http://web.stanford.edu/group/mmds/slides2010/Martinsson.pdf
    /// </summary>
    /// <remarks>
    /// This PCA can be made into Kernel PCA by using Random Fourier Features transform
    /// </remarks>
    public sealed class RandomizedPcaTrainer : TrainerBase<RoleMappedData, PcaPredictor>
    {
        public const string LoadNameValue = "pcaAnomaly";
        internal const string UserNameValue = "PCA Anomaly Detector";
        internal const string ShortName = "pcaAnom";
        internal const string Summary = "This algorithm trains an approximate PCA using Randomized SVD algorithm. "
            + "This PCA can be made into Kernel PCA by using Random Fourier Features transform.";

        public class Arguments : UnsupervisedLearnerInputBaseWithWeight
        {
            [Argument(ArgumentType.AtMostOnce, HelpText = "The number of components in the PCA", ShortName = "k", SortOrder = 50)]
            [TGUI(SuggestedSweeps = "10,20,40,80")]
            [TlcModule.SweepableDiscreteParam("Rank", new object[] { 10, 20, 40, 80 })]
            public int Rank = 20;

            [Argument(ArgumentType.AtMostOnce, HelpText = "Oversampling parameter for randomized PCA training", SortOrder = 50)]
            [TGUI(SuggestedSweeps = "10,20,40")]
            [TlcModule.SweepableDiscreteParam("Oversampling", new object[] { 10, 20, 40 })]
            public int Oversampling = 20;

            [Argument(ArgumentType.AtMostOnce, HelpText = "If enabled, data is centered to be zero mean", ShortName = "center")]
            [TlcModule.SweepableDiscreteParam("Center", null, isBool: true)]
            public bool Center = true;

            [Argument(ArgumentType.AtMostOnce, HelpText = "The seed for random number generation", ShortName = "seed")]
            public int? Seed;
        }

        private int _dimension;
        private readonly int _rank;
        private readonly int _oversampling;
        private readonly bool _center;
        private readonly int _seed;
        private VBuffer<Float>[] _eigenvectors; // top eigenvectors of the covariance matrix
        private VBuffer<Float> _mean;

        public RandomizedPcaTrainer(IHostEnvironment env, Arguments args)
            : base(env, LoadNameValue)
        {
            Host.CheckValue(args, nameof(args));
            Host.CheckUserArg(args.Rank > 0, nameof(args.Rank), "Rank must be positive");
            Host.CheckUserArg(args.Oversampling >= 0, nameof(args.Oversampling), "Oversampling must be non-negative");

            _rank = args.Rank;
            _center = args.Center;
            _oversampling = args.Oversampling;
            _seed = args.Seed ?? Host.Rand.Next();
        }

        public override bool NeedNormalization
        {
            get { return true; }
        }

        public override bool NeedCalibration
        {
            get { return false; }
        }

        public override bool WantCaching
        {
            // Two passes, only. Probably not worth caching.
            get { return false; }
        }

        public override PcaPredictor CreatePredictor()
        {
            return new PcaPredictor(Host, _rank, _eigenvectors, ref _mean);
        }

        public override PredictionKind PredictionKind { get { return PredictionKind.AnomalyDetection; } }

        //Note: the notations used here are the same as in http://web.stanford.edu/group/mmds/slides2010/Martinsson.pdf (pg. 9)
        public override void Train(RoleMappedData data)
        {
            Host.CheckValue(data, nameof(data));

            data.CheckFeatureFloatVector(out _dimension);

            using (var ch = Host.Start("Training"))
            {
                TrainCore(ch, data);
                ch.Done();
            }
        }

        private void TrainCore(IChannel ch, RoleMappedData data)
        {
            Host.AssertValue(ch);
            ch.AssertValue(data);

            if (_rank > _dimension)
                throw ch.Except("Rank ({0}) cannot be larger than the original dimension ({1})", _rank, _dimension);
            int oversampledRank = Math.Min(_rank + _oversampling, _dimension);

            //exact: (size of the 2 big matrices + other minor allocations) / (2^30)
            Double memoryUsageEstimate = 2.0 * _dimension * oversampledRank * sizeof(Float) / 1e9;
            if (memoryUsageEstimate > 2)
                ch.Info("Estimate memory usage: {0:G2} GB. If running out of memory, reduce rank and oversampling factor.", memoryUsageEstimate);

            var y = Zeros(oversampledRank, _dimension);
            _mean = _center ? VBufferUtils.CreateDense<Float>(_dimension) : VBufferUtils.CreateEmpty<Float>(_dimension);

            var omega = GaussianMatrix(oversampledRank, _dimension, _seed);

            var cursorFactory = new FeatureFloatVectorCursor.Factory(data, CursOpt.Features | CursOpt.Weight);
            long numBad;
            Project(Host, cursorFactory, ref _mean, omega, y, out numBad);
            if (numBad > 0)
                ch.Warning("Skipped {0} instances with missing features/weights during training", numBad);

            //Orthonormalize Y in-place using stabilized Gram Schmidt algorithm.
            //Ref: http://en.wikipedia.org/wiki/Gram-Schmidt#Algorithm
            for (var i = 0; i < oversampledRank; ++i)
            {
                var v = y[i];
                VectorUtils.ScaleBy(ref v, 1 / VectorUtils.Norm(y[i]));

                // Make the next vectors in the queue orthogonal to the orthonormalized vectors.
                for (var j = i + 1; j < oversampledRank; ++j) //subtract the projection of y[j] on v.
                    VectorUtils.AddMult(ref v, -VectorUtils.DotProduct(ref v, ref y[j]), ref y[j]);
            }
            var q = y; // q in QR decomposition.

            var b = omega; // reuse the memory allocated by Omega.
            Project(Host, cursorFactory, ref _mean, q, b, out numBad);

            //Compute B2 = B' * B
            var b2 = new Float[oversampledRank * oversampledRank];
            for (var i = 0; i < oversampledRank; ++i)
            {
                for (var j = i; j < oversampledRank; ++j)
                    b2[i * oversampledRank + j] = b2[j * oversampledRank + i] = VectorUtils.DotProduct(ref b[i], ref b[j]);
            }

            Float[] smallEigenvalues;// eigenvectors and eigenvalues of the small matrix B2.
            Float[] smallEigenvectors;
            EigenUtils.EigenDecomposition(b2, out smallEigenvalues, out smallEigenvectors);
            PostProcess(b, smallEigenvalues, smallEigenvectors, _dimension, oversampledRank);
            _eigenvectors = b;
        }

        private static VBuffer<Float>[] Zeros(int k, int d)
        {
            var rv = new VBuffer<Float>[k];
            for (var i = 0; i < k; ++i)
                rv[i] = VBufferUtils.CreateDense<Float>(d);
            return rv;
        }

        private static VBuffer<Float>[] GaussianMatrix(int k, int d, int seed)
        {
            var rv = Zeros(k, d);
            var rng = new SysRandom(seed);

            // REVIEW: use a faster Gaussian random matrix generator
            //MKL has a fast vectorized random number generation.
            for (var i = 0; i < k; ++i)
            {
                for (var j = 0; j < d; ++j)
                    rv[i].Values[j] = (Float)Stats.SampleFromGaussian(rng); // not fast for large matrix generation
            }
            return rv;
        }

        //Project the covariance matrix A on to Omega: Y <- A * Omega
        //A = X' * X / n, where X = data - mean
        //Note that the covariance matrix is not computed explicitly
        private static void Project(IHost host, FeatureFloatVectorCursor.Factory cursorFactory, ref VBuffer<Float> mean, VBuffer<Float>[] omega, VBuffer<Float>[] y, out long numBad)
        {
            Contracts.AssertValue(host, "host");
            host.AssertNonEmpty(omega);
            host.Assert(Utils.Size(y) == omega.Length); // Size of Y and Omega: dimension x oversampled rank
            int numCols = omega.Length;

            for (int i = 0; i < y.Length; ++i)
                VBufferUtils.Clear(ref y[i]);

            bool center = mean.IsDense;
            Float n = 0;
            long count = 0;
            using (var pch = host.StartProgressChannel("Project covariance matrix"))
            using (var cursor = cursorFactory.Create())
            {
                pch.SetHeader(new ProgressHeader(new[] { "rows" }), e => e.SetProgress(0, count));
                while (cursor.MoveNext())
                {
                    if (center)
                        VectorUtils.AddMult(ref cursor.Features, cursor.Weight, ref mean);
                    for (int i = 0; i < numCols; i++)
                    {
                        VectorUtils.AddMult(
                            ref cursor.Features,
                            cursor.Weight * VectorUtils.DotProduct(ref omega[i], ref cursor.Features),
                            ref y[i]);
                    }
                    n += cursor.Weight;
                    count++;
                }
                pch.Checkpoint(count);
                numBad = cursor.SkippedRowCount;
            }

            Contracts.Check(n > 0, "Empty training data");
            Float invn = 1 / n;

            for (var i = 0; i < numCols; ++i)
                VectorUtils.ScaleBy(ref y[i], invn);

            if (center)
            {
                VectorUtils.ScaleBy(ref mean, invn);
                for (int i = 0; i < numCols; i++)
                    VectorUtils.AddMult(ref mean, -VectorUtils.DotProduct(ref omega[i], ref mean), ref y[i]);
            }
        }

        /// <summary>
        /// Modifies <paramref name="y"/> in place so it becomes <paramref name="y"/> * eigenvectors / eigenvalues.
        /// </summary>
        // REVIEW: improve
        private static void PostProcess(VBuffer<Float>[] y, Float[] sigma, Float[] z, int d, int k)
        {
            Contracts.Assert(y.All(v => v.IsDense));
            var pinv = new Float[k];
            var tmp = new Float[k];

            for (int i = 0; i < k; i++)
                pinv[i] = (Float)(1.0) / ((Float)(1e-6) + sigma[i]);

            for (int i = 0; i < d; i++)
            {
                for (int j = 0; j < k; j++)
                {
                    tmp[j] = 0;
                    for (int l = 0; l < k; l++)
                        tmp[j] += y[l].Values[i] * z[j * k + l];
                }
                for (int j = 0; j < k; j++)
                    y[j].Values[i] = pinv[j] * tmp[j];
            }
        }

        [TlcModule.EntryPoint(Name = "Trainers.PcaAnomalyDetector",
            Desc = "Train an PCA Anomaly model.",
            Remarks = PcaPredictor.Remarks,
            UserName = UserNameValue,
            ShortName = ShortName)]
        public static CommonOutputs.AnomalyDetectionOutput TrainPcaAnomaly(IHostEnvironment env, Arguments input)
        {
            Contracts.CheckValue(env, nameof(env));
            var host = env.Register("TrainPCAAnomaly");
            host.CheckValue(input, nameof(input));
            EntryPointUtils.CheckInputArgs(host, input);

            return LearnerEntryPointsUtils.Train<Arguments, CommonOutputs.AnomalyDetectionOutput>(host, input,
                () => new RandomizedPcaTrainer(host, input),
                getWeight: () => LearnerEntryPointsUtils.FindColumn(host, input.TrainingData.Schema, input.WeightColumn));
        }
    }

    /// <summary>
    /// An anomaly detector using PCA.
    /// - The algorithm uses the top eigenvectors to approximate the subspace containing the normal class
    /// - For each new instance, it computes the norm difference between the raw feature vector and the projected feature on that subspace.
    /// - - If the error is close to 0, the instance is considered normal (non-anomaly).
    /// </summary>
    // REVIEW: move the predictor to a different file and fold EigenUtils.cs to this file.
    public sealed class PcaPredictor : PredictorBase<Float>,
        IValueMapper,
        ICanGetSummaryAsIDataView,
        ICanSaveInTextFormat, ICanSaveModel, ICanSaveSummary
    {
        public const string LoaderSignature = "pcaAnomExec";
        public const string RegistrationName = "PCAPredictor";
        internal const string Remarks = @"<remarks>
<a href='https://en.wikipedia.org/wiki/Principal_component_analysis'>Principle Component Analysis (PCA)</a> is a dimensionality-reduction transform which computes the projection of the feature vector to onto a low-rank subspace.
Its training is done using the technique described in the paper: <a href='https://arxiv.org/pdf/1310.6304v2.pdf'>Combining Structured and Unstructured Randomness in Large Scale PCA</a>, 
and the paper <see href='https://arxiv.org/pdf/0909.4061v2.pdf'>Finding Structure with Randomness: Probabilistic Algorithms for Constructing Approximate Matrix Decompositions</see>
<a href='http://web.stanford.edu/group/mmds/slides2010/Martinsson.pdf'>Randomized Methods for Computing the Singular Value Decomposition (SVD) of very large matrices</a>
<a href='https://arxiv.org/abs/0809.2274'>A randomized algorithm for principal component analysis</a>
<a href='http://users.cms.caltech.edu/~jtropp/papers/HMT11-Finding-Structure-SIREV.pdf'>Finding Structure with Randomness: Probabilistic Algorithms for Constructing Approximate Matrix Decompositions</a>
</remarks>";

        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "PCA ANOM",
                verWrittenCur: 0x00010001, // Initial
                verReadableCur: 0x00010001,
                verWeCanReadBack: 0x00010001,
                loaderSignature: LoaderSignature);
        }

        private readonly int _dimension;
        private readonly int _rank;
        private readonly VBuffer<Float>[] _eigenVectors; // top-k eigenvectors of the train data's covariance matrix
        private readonly Float[] _meanProjected; // for centering
        private readonly VBuffer<Float> _mean; // used to compute (x-mu)^2
        private readonly Float _norm2Mean;

        private readonly ColumnType _inputType;

        public override PredictionKind PredictionKind
        {
            get { return PredictionKind.AnomalyDetection; }
        }

        internal PcaPredictor(IHostEnvironment env, int rank, VBuffer<Float>[] eigenVectors, ref VBuffer<Float> mean)
            : base(env, RegistrationName)
        {
            _dimension = eigenVectors[0].Length;
            _rank = rank;
            _eigenVectors = new VBuffer<Float>[rank];
            _meanProjected = new Float[rank];

            for (var i = 0; i < rank; ++i) // Only want first k
            {
                _eigenVectors[i] = eigenVectors[i];
                _meanProjected[i] = VectorUtils.DotProduct(ref eigenVectors[i], ref mean);
            }

            _mean = mean;
            _norm2Mean = VectorUtils.NormSquared(mean);

            _inputType = new VectorType(NumberType.Float, _dimension);
        }

        private PcaPredictor(IHostEnvironment env, ModelLoadContext ctx)
            : base(env, RegistrationName, ctx)
        {
            // *** Binary format ***
            // int: dimension (aka. number of features)
            // int: rank
            // bool: center
            // If (center)
            //  Float[]: mean vector
            // Float[][]: eigenvectors
            _dimension = ctx.Reader.ReadInt32();
            Host.CheckDecode(FloatUtils.IsFinite(_dimension));

            _rank = ctx.Reader.ReadInt32();
            Host.CheckDecode(FloatUtils.IsFinite(_rank));

            bool center = ctx.Reader.ReadBoolByte();
            if (center)
            {
                var meanArray = ctx.Reader.ReadFloatArray(_dimension);
                Host.CheckDecode(meanArray.All(FloatUtils.IsFinite));
                _mean = new VBuffer<Float>(_dimension, meanArray);
                _norm2Mean = VectorUtils.NormSquared(_mean);
            }
            else
            {
                _mean = VBufferUtils.CreateEmpty<Float>(_dimension);
                _norm2Mean = 0;
            }

            _eigenVectors = new VBuffer<Float>[_rank];
            _meanProjected = new Float[_rank];
            for (int i = 0; i < _rank; ++i)
            {
                var vi = ctx.Reader.ReadFloatArray(_dimension);
                Host.CheckDecode(vi.All(FloatUtils.IsFinite));
                _eigenVectors[i] = new VBuffer<Float>(_dimension, vi);
                _meanProjected[i] = VectorUtils.DotProduct(ref _eigenVectors[i], ref _mean);
            }
            WarnOnOldNormalizer(ctx, GetType(), Host);

            _inputType = new VectorType(NumberType.Float, _dimension);
        }

        protected override void SaveCore(ModelSaveContext ctx)
        {
            base.SaveCore(ctx);
            ctx.SetVersionInfo(GetVersionInfo());
            var writer = ctx.Writer;

            // *** Binary format ***
            // int: dimension (aka. number of features)
            // int: rank
            // bool: center
            // If (center)
            //  Float[]: mean vector
            // Float[][]: eigenvectors
            writer.Write(_dimension);
            writer.Write(_rank);

            if (_mean.IsDense) // centered
            {
                writer.WriteBoolByte(true);
                writer.WriteFloatsNoCount(_mean.Values, _dimension);
            }
            else
                writer.WriteBoolByte(false);

            for (int i = 0; i < _rank; ++i)
                writer.WriteFloatsNoCount(_eigenVectors[i].Values, _dimension);
        }

        public static PcaPredictor Create(IHostEnvironment env, ModelLoadContext ctx)
        {
            Contracts.CheckValue(env, nameof(env));
            env.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel(GetVersionInfo());
            return new PcaPredictor(env, ctx);
        }

        public void SaveSummary(TextWriter writer, RoleMappedSchema schema)
        {
            SaveAsText(writer, schema);
        }

        public void SaveAsText(TextWriter writer, RoleMappedSchema schema)
        {
            writer.WriteLine("Dimension: {0}", _dimension);
            writer.WriteLine("Rank: {0}", _rank);

            if (_mean.IsDense)
            {
                writer.Write("Mean vector:");
                foreach (var value in _mean.Items(all: true))
                    writer.Write(" {0}", value.Value);

                writer.WriteLine();
                writer.Write("Projected mean vector:");
                foreach (var value in _meanProjected)
                    writer.Write(" {0}", value);
            }

            writer.WriteLine();
            writer.WriteLine("# V");
            for (var i = 0; i < _rank; ++i)
            {
                VBufferUtils.ForEachDefined(ref _eigenVectors[i],
                    (ind, val) => { if (val != 0) writer.Write(" {0}:{1}", ind, val); });
                writer.WriteLine();
            }
        }

        public IDataView GetSummaryDataView(RoleMappedSchema schema)
        {
            var bldr = new ArrayDataViewBuilder(Host);

            var cols = new VBuffer<Float>[_rank + 1];
            var names = new string[_rank + 1];
            for (var i = 0; i < _rank; ++i)
            {
                names[i] = "EigenVector" + i;
                cols[i] = _eigenVectors[i];
            }
            names[_rank] = "MeanVector";
            cols[_rank] = _mean;

            bldr.AddColumn("VectorName", names);
            bldr.AddColumn("VectorData", NumberType.R4, cols);

            return bldr.GetDataView();
        }

        public ColumnType InputType
        {
            get { return _inputType; }
        }

        public ColumnType OutputType
        {
            get { return NumberType.Float; }
        }

        public ValueMapper<TIn, TOut> GetMapper<TIn, TOut>()
        {
            Host.Check(typeof(TIn) == typeof(VBuffer<Float>));
            Host.Check(typeof(TOut) == typeof(Float));

            ValueMapper<VBuffer<Float>, Float> del =
                (ref VBuffer<Float> src, ref Float dst) =>
                {
                    Host.Check(src.Length == _dimension);
                    dst = Score(ref src);
                };
            return (ValueMapper<TIn, TOut>)(Delegate)del;
        }

        private Float Score(ref VBuffer<Float> src)
        {
            Host.Assert(src.Length == _dimension);

            // REVIEW: Can this be done faster in a single pass over src and _mean?
            var mean = _mean;
            Float norm2X = VectorUtils.NormSquared(src) -
                2 * VectorUtils.DotProduct(ref mean, ref src) + _norm2Mean;
            // Because the distance between src and _mean is computed using the above expression, the result
            // may be negative due to round off error. If this happens, we let the distance be 0.
            if (norm2X < 0)
                norm2X = 0;

            Float norm2U = 0;
            for (int i = 0; i < _rank; i++)
            {
                Float component = VectorUtils.DotProduct(ref _eigenVectors[i], ref src) - _meanProjected[i];
                norm2U += component * component;
            }

            return MathUtils.Sqrt((norm2X - norm2U) / norm2X); // normalized error
        }

        /// <summary>
        /// Copies the top eigenvectors of the covariance matrix of the training data
        /// into a set of buffers.
        /// </summary>
        /// <param name="vectors">A possibly reusable set of vectors, which will
        /// be expanded as necessary to accomodate the data.</param>
        /// <param name="rank">Set to the rank, which is also the logical length
        /// of <paramref name="vectors"/>.</param>
        public void GetEigenVectors(ref VBuffer<Float>[] vectors, out int rank)
        {
            rank = _eigenVectors.Length;
            Utils.EnsureSize(ref vectors, _eigenVectors.Length, _eigenVectors.Length);
            for (int i = 0; i < _eigenVectors.Length; i++)
                _eigenVectors[i].CopyTo(ref vectors[i]);
        }

        /// <summary>
        /// Copies the mean vector of the training data.
        /// </summary>
        public void GetMean(ref VBuffer<Float> mean)
        {
            _mean.CopyTo(ref mean);
        }
    }
}
