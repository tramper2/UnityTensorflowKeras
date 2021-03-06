﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Accord.Statistics.Distributions.Univariate;
using System;
using System.Linq;
using Accord;
using Accord.Math;
using Accord.Statistics;
using System.Runtime.Serialization.Formatters.Binary;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif


using static KerasSharp.Backends.Current;
using KerasSharp.Backends;

using MLAgents;
using KerasSharp.Models;
using KerasSharp.Optimizers;
using KerasSharp.Engine.Topology;
using KerasSharp.Initializers;
using KerasSharp;
using KerasSharp.Losses;


public interface IRLModelPPO
{
    float EntropyLossWeight { get; set; }
    float ValueLossWeight { get; set; }
    float ClipEpsilon { get; set; }

    float[] EvaluateValue(float[,] vectorObservation, List<float[,,,]> visualObservation);
    float[,] EvaluateAction(float[,] vectorObservation, out float[,] actionProbs, List<float[,,,]> visualObservation, bool useProbability = true);
    float[,] EvaluateProbability(float[,] vectorObservation, float[,] actions, List<float[,,,]> visualObservation);
    float[] TrainBatch(float[,] vectorObservations, List<float[,,,]> visualObservations, float[,] actions, float[,] actionProbs, float[] targetValues, float[] oldValues, float[] advantages);
}


public class RLModelPPO : LearningModelBase, IRLModelPPO, INeuralEvolutionModel, ISupervisedLearningModel
{


    protected Function ValueFunction { get; set; }
    protected Function ActionFunction { get; set; }
    protected Function UpdatePPOFunction { get; set; }
    protected Function UpdateSLFunction { get; set; }
    protected Function ActionProbabilityFunction { get; set; }

    protected Function UpdateNormalizerFunction { get; set; }

    [ShowAllPropertyAttr]
    public RLNetworkAC network;

    public OptimizerCreator optimizer;
    public bool useInputNormalization = false;
    public float EntropyLossWeight { get; set; }
    public float ValueLossWeight { get; set; }
    public float ClipEpsilon { get; set; }

    //the variables for normalization
    protected Tensor runningMean = null;
    protected Tensor runningVariance = null;
    protected Tensor stepCount = null;

    public enum Mode
    {
        PPO,
        SupervisedLearning
    }
    public Mode mode = Mode.PPO;

    /// <summary>
    /// Initialize the model without training parts
    /// </summary>
    /// <param name="brainParameters"></param>
    public override void InitializeInner(BrainParameters brainParameters, Tensor stateTensor, List<Tensor> visualTensors, TrainerParams trainerParams)
    {

        Tensor inputStateTensorToNetwork = stateTensor;

        if (useInputNormalization && HasVectorObservation)
        {
            inputStateTensorToNetwork = CreateRunninngNormalizer(inputStateTensorToNetwork, StateSize);
        }


        //build the network
        Tensor outputValue = null; Tensor outputAction = null; Tensor outputVariance = null;
        network.BuildNetwork(inputStateTensorToNetwork, visualTensors, null, null, ActionSize, ActionSpace, out outputAction, out outputValue, out outputVariance);

        if (trainerParams is TrainerParamsPPO  || mode == Mode.PPO)
        {
            mode = Mode.PPO;
            InitializePPOStructures(trainerParams, stateTensor, visualTensors, outputValue, outputAction, outputVariance, network.GetWeights());
        }
        else if (mode == Mode.SupervisedLearning || trainerParams is TrainerParamsMimic)
        {
            mode = Mode.SupervisedLearning;
            InitializeSLStructures(trainerParams, stateTensor, visualTensors, outputAction, outputVariance, network.GetActorWeights());
        }
    }




    /// <summary>
    /// Initialize the model for PPO
    /// </summary>
    /// <param name="trainerParams"></param>
    /// <param name="stateTensor"></param>
    /// <param name="inputVisualTensors"></param>
    /// <param name="outputValueFromNetwork"></param>
    /// <param name="outputActionFromNetwork"></param>
    /// <param name="outputVarianceFromNetwork"></param>
    /// <param name="weightsToUpdate"></param>
    protected void InitializePPOStructures(TrainerParams trainerParams, Tensor stateTensor, List<Tensor> inputVisualTensors, Tensor outputValueFromNetwork, Tensor outputActionFromNetwork, Tensor outputVarianceFromNetwork, List<Tensor> weightsToUpdate)
    {
        List<Tensor> allobservationInputs = new List<Tensor>();
        if (HasVectorObservation)
        {
            allobservationInputs.Add(stateTensor);
        }
        if (HasVisualObservation)
        {
            allobservationInputs.AddRange(inputVisualTensors);
        }

        ValueFunction = K.function(allobservationInputs, new List<Tensor> { outputValueFromNetwork }, null, "ValueFunction");

        Tensor outputActualAction = null; Tensor actionProb = null;
        if (ActionSpace == SpaceType.continuous)
        {
            using (K.name_scope("SampleAction"))
            {
                outputActualAction = K.standard_normal(K.shape(outputActionFromNetwork), DataType.Float) * K.sqrt(outputVarianceFromNetwork) + outputActionFromNetwork;

            }
            using (K.name_scope("ActionProbs"))
            {
                actionProb = K.normal_probability(K.stop_gradient(outputActualAction), outputActionFromNetwork, outputVarianceFromNetwork);
            }
            ActionFunction = K.function(allobservationInputs, new List<Tensor> { outputActualAction, actionProb, outputActionFromNetwork, outputVarianceFromNetwork }, null, "ActionFunction");

            var probInputs = new List<Tensor>(); probInputs.AddRange(allobservationInputs); probInputs.Add(outputActualAction);
            ActionProbabilityFunction = K.function(probInputs, new List<Tensor> { actionProb }, null, "ActionProbabilityFunction");
        }
        else
        {

            ActionFunction = K.function(allobservationInputs, new List<Tensor> { outputActionFromNetwork }, null, "ActionFunction");
        }

        TrainerParamsPPO trainingParams = trainerParams as TrainerParamsPPO;
        if (trainingParams != null)
        {
            //training needed inputs

            var inputOldProb = UnityTFUtils.Input(new int?[] { ActionSpace == SpaceType.continuous ? ActionSize : 1 }, name: "InputOldProb")[0];
            var inputAdvantage = UnityTFUtils.Input(new int?[] { 1 }, name: "InputAdvantage")[0];
            var inputTargetValue = UnityTFUtils.Input(new int?[] { 1 }, name: "InputTargetValue")[0];
            var inputOldValue = UnityTFUtils.Input(new int?[] { 1 }, name: "InputOldValue")[0];

            ClipEpsilon = trainingParams.clipEpsilon;
            ValueLossWeight = trainingParams.valueLossWeight;
            EntropyLossWeight = trainingParams.entropyLossWeight;

            var inputClipEpsilon = UnityTFUtils.Input(batch_shape: new int?[] { }, name: "ClipEpsilon", dtype: DataType.Float)[0];
            var inputValuelossWeight = UnityTFUtils.Input(batch_shape: new int?[] { }, name: "ValueLossWeight", dtype: DataType.Float)[0];
            var inputEntropyLossWeight = UnityTFUtils.Input(batch_shape: new int?[] { }, name: "EntropyLossWeight", dtype: DataType.Float)[0];

            // action probability from input action
            Tensor outputEntropy;
            Tensor inputActionDiscrete = null, onehotInputAction = null;    //for discrete action space

            if (ActionSpace == SpaceType.continuous)
            {
                using (K.name_scope("Entropy"))
                {
                    var temp = K.mul(outputVarianceFromNetwork, 2 * Mathf.PI * 2.7182818285);
                    temp = K.mul(K.log(temp), 0.5);
                    if (outputVarianceFromNetwork.shape.Length == 2)
                    {
                        outputEntropy = K.mean(K.mean(temp, 0, false), name: "OutputEntropy");
                    }
                    else
                    {
                        outputEntropy = K.mean(temp, 0, false, name: "OutputEntropy");
                    }
                }

            }
            else
            {
                using (K.name_scope("ActionProbAndEntropy"))
                {
                    inputActionDiscrete = UnityTFUtils.Input(new int?[] { 1 }, name: "InputAction", dtype: DataType.Int32)[0];
                    onehotInputAction = K.one_hot(inputActionDiscrete, K.constant<int>(ActionSize, dtype: DataType.Int32), K.constant(1.0f), K.constant(0.0f));
                    onehotInputAction = K.reshape(onehotInputAction, new int[] { -1, ActionSize });
                    outputEntropy = K.mean((-1.0f) * K.sum(outputActionFromNetwork * K.log(outputActionFromNetwork + 0.00000001f), axis: 1), 0);
                    actionProb = K.reshape(K.sum(outputActionFromNetwork * onehotInputAction, 1), new int[] { -1, 1 });
                }
            }

            // value loss   
            Tensor outputValueLoss = null;
            using (K.name_scope("ValueLoss"))
            {
                var clippedValueEstimate = inputOldValue + K.clip(outputValueFromNetwork - inputOldValue, 0.0f - inputClipEpsilon, inputClipEpsilon);
                var valueLoss1 = new MeanSquareError().Call(outputValueFromNetwork, inputTargetValue);
                var valueLoss2 = new MeanSquareError().Call(clippedValueEstimate, inputTargetValue);
                outputValueLoss = K.mean(K.maximum(valueLoss1, valueLoss2));
            }
            //var outputValueLoss = K.mean(valueLoss1);

            // Clipped Surrogate loss
            Tensor outputPolicyLoss;
            using (K.name_scope("ClippedCurreogateLoss"))
            {
                //Debug.LogWarning("testnew");
                //var probStopGradient = K.stop_gradient(actionProb);
                var probRatio = actionProb / (inputOldProb + 0.0000000001f);
                var p_opt_a = probRatio * inputAdvantage;
                var p_opt_b = K.clip(probRatio, 1.0f - inputClipEpsilon, 1.0f + inputClipEpsilon) * inputAdvantage;

                outputPolicyLoss = (-1f) * K.mean(K.mean(K.minimun(p_opt_a, p_opt_b)), name: "ClippedCurreogateLoss");
            }
            //final weighted loss
            var outputLoss = outputPolicyLoss + inputValuelossWeight * outputValueLoss;
            outputLoss = outputLoss - inputEntropyLossWeight * outputEntropy;
            outputLoss = K.identity(outputLoss, "OutputLoss");

            //add inputs, outputs and parameters to the list
            List<Tensor> allInputs = new List<Tensor>();
            if (HasVectorObservation)
            {
                allInputs.Add(stateTensor);
            }
            if (HasVisualObservation)
            {
                allInputs.AddRange(inputVisualTensors);
            }
            if (ActionSpace == SpaceType.continuous)
            {
                allInputs.Add(outputActualAction);
            }
            else
            {
                allInputs.Add(inputActionDiscrete);
            }

            allInputs.Add(inputOldProb);
            allInputs.Add(inputTargetValue);
            allInputs.Add(inputOldValue);
            allInputs.Add(inputAdvantage);
            allInputs.Add(inputClipEpsilon);
            allInputs.Add(inputValuelossWeight);
            allInputs.Add(inputEntropyLossWeight);

            //create optimizer and create necessary functions
            var updates = AddOptimizer(weightsToUpdate, outputLoss, optimizer);
            UpdatePPOFunction = K.function(allInputs, new List<Tensor> { outputLoss, outputValueLoss, outputPolicyLoss, outputEntropy, actionProb }, updates, "UpdateFunction");



        }
    }

    /// <summary>
    /// Initialize the model for supervised learning
    /// </summary>
    /// <param name="trainerParams"></param>
    /// <param name="stateTensor"></param>
    /// <param name="inputVisualTensors"></param>
    /// <param name="outputActionFromNetwork"></param>
    /// <param name="outputVarianceFromNetwork"></param>
    /// <param name="weightsToUpdate"></param>
    protected void InitializeSLStructures(TrainerParams trainerParams, Tensor stateTensor, List<Tensor> inputVisualTensors, Tensor outputActionFromNetwork, Tensor outputVarianceFromNetwork, List<Tensor> weightsToUpdate)
    {
        List<Tensor> allobservationInputs = new List<Tensor>();
        if (HasVectorObservation)
        {
            allobservationInputs.Add(stateTensor);
        }
        if (HasVisualObservation)
        {
            allobservationInputs.AddRange(inputVisualTensors);
        }

        if (ActionSpace == SpaceType.continuous)
        {
            ActionFunction = K.function(allobservationInputs, new List<Tensor> { outputActionFromNetwork, outputVarianceFromNetwork }, null, "ActionFunction");
        }
        else
        {

            ActionFunction = K.function(allobservationInputs, new List<Tensor> { outputActionFromNetwork }, null, "ActionFunction");
        }



        ///created losses for supervised learning part
        Tensor supervisedLearingLoss = null;
        var inputActionLabel = UnityTFUtils.Input(new int?[] { ActionSpace == SpaceType.continuous ? ActionSize : 1 }, name: "InputAction", dtype: ActionSpace == SpaceType.continuous ? DataType.Float : DataType.Int32)[0];
        if (ActionSpace == SpaceType.discrete)
        {
            var onehotInputAction = K.one_hot(inputActionLabel, K.constant<int>(ActionSize, dtype: DataType.Int32), K.constant(1.0f), K.constant(0.0f));
            onehotInputAction = K.reshape(onehotInputAction, new int[] { -1, ActionSize });
            supervisedLearingLoss = K.mean(K.categorical_crossentropy(onehotInputAction, outputActionFromNetwork, false));
        }
        else
        {
            supervisedLearingLoss = K.mean(K.mean(0.5 * K.square(inputActionLabel - outputActionFromNetwork) / outputVarianceFromNetwork + 0.5 * K.log(outputVarianceFromNetwork)));
        }

        var updates = AddOptimizer(weightsToUpdate, supervisedLearingLoss, optimizer);
        var slInputs = new List<Tensor>();
        slInputs.AddRange(allobservationInputs); slInputs.Add(inputActionLabel);
        UpdateSLFunction = K.function(slInputs, new List<Tensor>() { supervisedLearingLoss }, updates, "UpdateSLFunction");
    }

    /// <summary>
    /// evaluate the value of current states
    /// </summary>
    /// <param name="vectorObservation">current vector observation. The first dimension of the array is the batch dimension.</param>
    /// <param name="visualObservation">current visual observation. The first dimension of the array is the batch dimension.</param>
    /// <returns>values of current states</returns>
    public virtual float[] EvaluateValue(float[,] vectorObservation, List<float[,,,]> visualObservation)
    {
        Debug.Assert(mode == Mode.PPO, "This method is for PPO mode only");
        List<Array> inputLists = new List<Array>();
        if (HasVectorObservation)
        {
            Debug.Assert(vectorObservation != null, "Must Have vector observation inputs!");
            inputLists.Add(vectorObservation);
        }
        if (HasVisualObservation)
        {
            Debug.Assert(visualObservation != null, "Must Have visual observation inputs!");
            inputLists.AddRange(visualObservation);
        }

        var result = ValueFunction.Call(inputLists);
        //return new float[] { ((float[,])result[0].eval())[0,0] };
        var value = ((float[,])result[0].eval()).Flatten();
        return value;
    }

    /// <summary>
    /// Query actions based on curren states. The first dimension of the array must be batch dimension
    /// </summary>
    /// <param name="vectorObservation">current vector states. Can be batch input</param>
    /// <param name="actionProbs">output actions' probabilities</param>
    /// <param name="useProbability">when true, the output actions are sampled based on output mean and variance. Otherwise it uses mean directly.</param>
    /// <returns></returns>
    public virtual float[,] EvaluateAction(float[,] vectorObservation, out float[,] actionProbs, List<float[,,,]> visualObservation, bool useProbability = true)
    {
        Debug.Assert(mode == Mode.PPO, "This method is for PPO mode only");

        List<Array> inputLists = new List<Array>();
        if (HasVectorObservation)
        {
            Debug.Assert(vectorObservation != null, "Must Have vector observation inputs!");
            inputLists.Add(vectorObservation);
        }
        if (HasVisualObservation)
        {
            Debug.Assert(visualObservation != null, "Must Have visual observation inputs!");
            inputLists.AddRange(visualObservation);
        }

        var result = ActionFunction.Call(inputLists);

        var outputAction = ((float[,])result[0].eval());
        float[,] actions = new float[outputAction.GetLength(0), ActionSpace == SpaceType.continuous ? outputAction.GetLength(1) : 1];
        actionProbs = new float[outputAction.GetLength(0), ActionSpace == SpaceType.continuous ? outputAction.GetLength(1) : 1];

        if (ActionSpace == SpaceType.continuous)
        {
            actions = outputAction;
            actionProbs = ((float[,])result[1].eval());
            //var actionsMean = (float[,])(result[2].eval());
            //var actionsVars = (float[])(result[3].eval());
            //print("actual vars" + actions.GetColumn(0).Variance()+"," + actions.GetColumn(1).Variance() + "," + actions.GetColumn(2).Variance() + "," + actions.GetColumn(3).Variance());
        }
        else if (ActionSpace == SpaceType.discrete)
        {
            for (int j = 0; j < outputAction.GetLength(0); ++j)
            {
                if (useProbability)
                    actions[j, 0] = MathUtils.IndexByChance(outputAction.GetRow(j));
                else
                    actions[j, 0] = outputAction.GetRow(j).ArgMax();

                actionProbs[j, 0] = outputAction.GetRow(j)[Mathf.RoundToInt(actions[j, 0])];
            }
        }

        if (useInputNormalization && HasVectorObservation)
        {
            UpdateNormalizerFunction.Call(new List<Array>() { vectorObservation });
            //var runningMean = (float[])runningData[0].eval();
            //var runningVar = (float[])runningData[1].eval();
            //var steps = (float)runningData[2].eval();
            //var normalized = (float[,])runningData[3].eval();
        }


        return actions;

    }


    /// <summary>
    /// Query actions' probabilities based on curren states. The first dimension of the array must be batch dimension
    /// </summary>
    public virtual float[,] EvaluateProbability(float[,] vectorObservation, float[,] actions, List<float[,,,]> visualObservation)
    {
        Debug.Assert(mode == Mode.PPO, "This method is for PPO mode only");
        Debug.Assert(TrainingEnabled == true, "The model needs to initalized with Training enabled to use EvaluateProbability()");

        List<Array> inputLists = new List<Array>();
        if (HasVectorObservation)
        {
            Debug.Assert(vectorObservation != null, "Must Have vector observation inputs!");
            inputLists.Add(vectorObservation);
        }
        if (HasVisualObservation)
        {
            Debug.Assert(visualObservation != null, "Must Have visual observation inputs!");
            inputLists.AddRange(visualObservation);
        }

        var actionProbs = new float[actions.GetLength(0), ActionSpace == SpaceType.continuous ? actions.GetLength(1) : 1];

        if (ActionSpace == SpaceType.continuous)
        {
            inputLists.Add(actions);
            var result = ActionProbabilityFunction.Call(inputLists);
            actionProbs = ((float[,])result[0].eval());
        }
        else if (ActionSpace == SpaceType.discrete)
        {
            var result = ActionFunction.Call(inputLists);

            var outputAction = ((float[,])result[0].eval());
            for (int j = 0; j < outputAction.GetLength(0); ++j)
            {
                actionProbs[j, 0] = outputAction.GetRow(j)[Mathf.RoundToInt(actions[j, 0])];
            }
        }

        return actionProbs;

    }



    public virtual float[] TrainBatch(float[,] vectorObservations, List<float[,,,]> visualObservations, float[,] actions, float[,] actionProbs, float[] targetValues, float[] oldValues, float[] advantages)
    {
        Debug.Assert(mode == Mode.PPO, "This method is for PPO mode only");
        Debug.Assert(TrainingEnabled == true, "The model needs to initalized with Training enabled to use TrainBatch()");

        List<Array> inputs = new List<Array>();
        if (vectorObservations != null)
            inputs.Add(vectorObservations);
        if (visualObservations != null)
            inputs.AddRange(visualObservations);
        if (ActionSpace == SpaceType.continuous)
            inputs.Add(actions);
        else if (ActionSpace == SpaceType.discrete)
        {
            int[,] actionsInt = actions.Convert(t => Mathf.RoundToInt(t));
            inputs.Add(actionsInt);
        }

        inputs.Add(actionProbs);
        inputs.Add(targetValues);
        inputs.Add(oldValues);
        inputs.Add(advantages);
        inputs.Add(new float[] { ClipEpsilon });
        inputs.Add(new float[] { ValueLossWeight });
        inputs.Add(new float[] { EntropyLossWeight });

        var loss = UpdatePPOFunction.Call(inputs);
        var result = new float[] { (float)loss[0].eval(), (float)loss[1].eval(), (float)loss[2].eval(), (float)loss[3].eval() };
        //float[,] outActionProbs = (float[,])loss[4].eval();
        return result;
        //Debug.LogWarning("test save graph");
        //((UnityTFBackend)K).ExportGraphDef("SavedGraph/PPOTest.pb");
        //return new float[] { 0, 0, 0 }; //test for memeory allocation
    }

    public override List<Tensor> GetAllModelWeights()
    {
        List<Tensor> result = new List<Tensor>();
        if (mode == Mode.PPO)
            result.AddRange(network.GetWeights());
        else
            result.AddRange(network.GetActorWeights());
        if (runningMean != null)
        {
            result.Add(runningMean); result.Add(runningVariance); result.Add(stepCount);
        }
        return result;
    }

    public float[,] EvaluateAction(float[,] vectorObservation, List<float[,,,]> visualObservation)
    {
        float[,] outActionProbs;
        return EvaluateAction(vectorObservation, out outActionProbs, visualObservation, true);
    }

    public List<Tensor> GetWeightsForNeuralEvolution()
    {
        return network.GetActorWeights();
    }


    protected Tensor CreateRunninngNormalizer(Tensor vectorInput, int size)
    {
        using (K.name_scope("InputNormalizer"))
        {
            stepCount = K.variable(0, DataType.Float, "NormalizationStep");

            runningMean = K.zeros(new int[] { size }, DataType.Float, "RunningMean");
            float[] initialVariance = new float[size];
            for (int i = 0; i < size; ++i)
            {
                initialVariance[i] = 1;
            }
            runningVariance = K.variable((Array)initialVariance, DataType.Float, "RunningVariance");

            var meanCurrentObs = K.mean(vectorInput, 0);

            var newMean = runningMean + (meanCurrentObs - runningMean) / (stepCount + 1);
            var newVariance = runningVariance + (meanCurrentObs - newMean) * (meanCurrentObs - runningMean);
            var normalized = K.clip((vectorInput - runningMean) / K.sqrt(runningVariance / (stepCount + 1.0f)), -5.0f, 5.0f);
            //var varCurrentObs = K.mean((vectorInput - meanCurrentObs) * (vectorInput - runningMean), 0);
            //var newMean = 0.95f*runningMean + 0.05f* meanCurrentObs;
            //var newVariance = runningVariance + varCurrentObs;
            //var normalized = K.clip((vectorInput - runningMean) / K.sqrt(runningVariance / (stepCount + 1.0f)), -5.0f, 5.0f);
            UpdateNormalizerFunction = K.function(new List<Tensor>() { vectorInput },
                new List<Tensor> { },
                new List<List<Tensor>>() {
                                new List<Tensor>() { K.update(runningMean,newMean) },
                                new List<Tensor>() { K.update(runningVariance,newVariance) },
                                new List<Tensor>(){K.update_add(stepCount,1.0f) },
            }, "UpdateNormalization");

            return normalized;

        }
    }

    /// <summary>
    /// THis is implemented for ISupervisedLearingModel so that this model can also be used for TrainerMimic
    /// </summary>
    /// <param name="vectorObservation"></param>
    /// <param name="visualObservation"></param>
    /// <returns>(mean, var) var will be null for discrete</returns>
    ValueTuple<float[,], float[,]> ISupervisedLearningModel.EvaluateAction(float[,] vectorObservation, List<float[,,,]> visualObservation)
    {
        Debug.Assert(mode == Mode.SupervisedLearning, "This method is for SupervisedLearning mode only. Please set the mode of RLModePPO to SupervisedLearning in the editor.");
        List<Array> inputLists = new List<Array>();
        if (HasVectorObservation)
        {
            Debug.Assert(vectorObservation != null, "Must Have vector observation inputs!");
            inputLists.Add(vectorObservation);
        }
        if (HasVisualObservation)
        {
            Debug.Assert(visualObservation != null, "Must Have visual observation inputs!");
            inputLists.AddRange(visualObservation);
        }

        var result = ActionFunction.Call(inputLists);

        var outputAction = ((float[,])result[0].eval());

        float[,] actions = new float[outputAction.GetLength(0), ActionSpace == SpaceType.continuous ? outputAction.GetLength(1) : 1];
        float[,] outputVar = null;
        if (ActionSpace == SpaceType.continuous)
        {
            actions = outputAction;
            outputVar = (float[,])result[1].eval();
        }
        else if (ActionSpace == SpaceType.discrete)
        {
            for (int j = 0; j < outputAction.GetLength(0); ++j)
            {
                actions[j, 0] = outputAction.GetRow(j).ArgMax();
            }
        }

        return ValueTuple.Create(actions, outputVar);
    }
    /// <summary>
    /// Training for supervised learning
    /// </summary>
    /// <param name="vectorObservations"></param>
    /// <param name="visualObservations"></param>
    /// <param name="actions"></param>
    /// <returns></returns>
    public float TrainBatch(float[,] vectorObservations, List<float[,,,]> visualObservations, float[,] actions)
    {
        Debug.Assert(mode == Mode.SupervisedLearning, "This method is for SupervisedLearning mode only. Please set the mode of RLModePPO to SupervisedLearning in the editor.");
        Debug.Assert(TrainingEnabled == true, "The model needs to initalized with Training enabled to use TrainBatch()");


        List<Array> inputs = new List<Array>();
        if (vectorObservations != null)
            inputs.Add(vectorObservations);
        if (visualObservations != null)
            inputs.AddRange(visualObservations);
        if (ActionSpace == SpaceType.continuous)
            inputs.Add(actions);
        else if (ActionSpace == SpaceType.discrete)
        {
            int[,] actionsInt = actions.Convert(t => Mathf.RoundToInt(t));
            inputs.Add(actionsInt);
        }

        var loss = UpdateSLFunction.Call(inputs);
        var result = (float)loss[0].eval();

        return result;
    }
    
}