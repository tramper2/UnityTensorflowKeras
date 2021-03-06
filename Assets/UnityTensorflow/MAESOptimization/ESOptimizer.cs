﻿using ICM;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ESOptimizer : MonoBehaviour
{

    public int iterationPerUpdate = 10;
    public int populationSize = 16;
    public ESOptimizerType optimizerType;
    public float initialStepSize = 1;
    public OptimizationModes mode;
    public int maxIteration = 100;
    public double targetValue = 2;

    public int evaluationBatchSize = 1;

    protected IESOptimizable optimizable = null;

    [ReadOnly]
    [SerializeField]
    protected int iteration;
    public int Iteration { get { return iteration; } }

    protected OptimizationSample[] samples;
    protected IMAES optimizer;
    protected Action<double[]> onReady = null;


    public double BestScore { get; private set; }
    public double[] BestParams { get; private set; }
    public bool IsOptimizing { get; private set; } = false;

    public enum ESOptimizerType
    {
        MAES,
        LMMAES
    }


    private void Update()
    {
        if (IsOptimizing)
        {
            for (int it = 0; it < iterationPerUpdate; ++it)
            {
                optimizer.generateSamples(samples);
                for (int s = 0; s <= samples.Length / evaluationBatchSize; ++s)
                {
                    List<double[]> paramList = new List<double[]>();
                    for (int b = 0; b < evaluationBatchSize; ++b)
                    {
                        int ind = s * evaluationBatchSize + b;
                        if (ind < samples.Length)
                        {
                            paramList.Add(samples[ind].x);
                        }
                    }

                    var values = optimizable.Evaluate(paramList);

                    for (int b = 0; b < evaluationBatchSize; ++b)
                    {
                        int ind = s * evaluationBatchSize + b;
                        if (ind < samples.Length)
                        {
                            samples[ind].objectiveFuncVal = values[b];
                        }
                    }

                }

                iteration++;
                /*foreach (OptimizationSample s in samples)
                {
                    float value = optimizable.Evaluate(new List<double[]>() { s.x })[0];
                    s.objectiveFuncVal = value;
                }*/
                optimizer.update(samples);
                BestScore = optimizer.getBestObjectiveFuncValue();

                BestParams = optimizer.getBest();

                if ((iteration >= maxIteration && maxIteration > 0) ||
                    (BestScore <= targetValue && mode == OptimizationModes.minimize) ||
                    (BestScore >= targetValue && mode == OptimizationModes.maximize))
                {
                    //optimizatoin is done
                    if (onReady != null)
                        onReady.Invoke(BestParams);
                    IsOptimizing = false;
                }
            }
        }
    }

    /// <summary>
    /// Start to optimize asynchronized. It is actaually not running in another thread, but running in Update() in each frame of your game.
    /// This way the optimization will not block your game.
    /// </summary>
    /// <param name="optimizeTarget">Target to optimize</param>
    /// <param name="onReady">Action to call when optmization is ready. THe input is the best solution found.</param>
    /// <param name="initialMean">initial mean guess.</param>
    public void StartOptimizingAsync(IESOptimizable optimizeTarget, Action<double[]> onReady = null, double[] initialMean = null)
    {
        optimizable = optimizeTarget;

        optimizer = optimizerType == ESOptimizerType.LMMAES ? (IMAES)new LMMAES() : (IMAES)new MAES();

        samples = new OptimizationSample[populationSize];
        for (int i = 0; i < populationSize; ++i)
        {
            samples[i] = new OptimizationSample(optimizable.GetParamDimension());
        }
        iteration = 0;

        //initial mean
        double[] actualInitMean = null;
        if (initialMean != null && initialMean.Length != optimizeTarget.GetParamDimension())
            Debug.LogError("Init mean has a wrong dimension " + initialMean.Length + " rather than " + optimizeTarget.GetParamDimension() + ".");
        if (initialMean == null)
            actualInitMean = new double[optimizeTarget.GetParamDimension()];
        else
            actualInitMean = initialMean;


        optimizer.init(optimizable.GetParamDimension(), populationSize, actualInitMean, initialStepSize, mode);

        IsOptimizing = true;

        this.onReady = onReady;
    }


    /// <summary>
    /// Optimize and return the solution immediately.
    /// </summary>
    /// <param name="optimizeTarget">Target to optimize</param>
    /// <param name="initialMean">initial mean guess.</param>
    /// <returns>The best solution found</returns>
    public double[] Optimize(IESOptimizable optimizeTarget,  double[] initialMean = null)
    {

        var tempOptimizer = (optimizerType == ESOptimizerType.LMMAES ? (IMAES)new LMMAES() : (IMAES)new MAES());

        var tempSamples = new OptimizationSample[populationSize];
        for (int i = 0; i < populationSize; ++i)
        {
            tempSamples[i] = new OptimizationSample(optimizeTarget.GetParamDimension());
        }

        //initial mean
        double[] actualInitMean = null;
        if (initialMean != null && initialMean.Length != optimizeTarget.GetParamDimension())
            Debug.LogError("Init mean has a wrong dimension " + initialMean.Length + " rather than " + optimizeTarget.GetParamDimension() + ".");
        if (initialMean == null)
            actualInitMean = new double[optimizeTarget.GetParamDimension()];
        else
            actualInitMean = initialMean;

        //initialize the optimizer
        tempOptimizer.init(optimizeTarget.GetParamDimension(), populationSize, actualInitMean, initialStepSize, mode);

        //iteration
        double[] bestParams = null;

        //bool hasInvokeReady = false;
        iteration = 0;
        for (int it = 0; it < maxIteration; ++it)
        {
            tempOptimizer.generateSamples(tempSamples);
            for (int s = 0; s <= tempSamples.Length / evaluationBatchSize; ++s)
            {
                List<double[]> paramList = new List<double[]>();
                for (int b = 0; b < evaluationBatchSize; ++b)
                {
                    int ind = s * evaluationBatchSize + b;
                    if (ind < tempSamples.Length)
                    {
                        paramList.Add(tempSamples[ind].x);
                    }
                }

                var values = optimizeTarget.Evaluate(paramList);

                for (int b = 0; b < evaluationBatchSize; ++b)
                {
                    int ind = s * evaluationBatchSize + b;
                    if (ind < tempSamples.Length)
                    {
                        tempSamples[ind].objectiveFuncVal = values[b];
                    }
                }

            }

            tempOptimizer.update(tempSamples);
            BestScore = tempOptimizer.getBestObjectiveFuncValue();

            iteration++;
            bestParams = tempOptimizer.getBest();

            if ((BestScore <= targetValue && mode == OptimizationModes.minimize) ||
                (BestScore >= targetValue && mode == OptimizationModes.maximize))
            {
                //optimizatoin is done
                /*if (onReady != null)
                {
                    onReady.Invoke(bestParams);
                    hasInvokeReady = true;
                }*/
                break;
            }
        }

        /*if (onReady != null && !hasInvokeReady)
        {
            onReady.Invoke(bestParams);
        }*/
        return bestParams;

    }


    public void StopOptimizing(Action<double[]> onReady = null)
    {
        if (IsOptimizing == false)
            return;
        IsOptimizing = false;
        if (onReady != null)
        {
            onReady.Invoke(BestParams);
        }
    }

}
